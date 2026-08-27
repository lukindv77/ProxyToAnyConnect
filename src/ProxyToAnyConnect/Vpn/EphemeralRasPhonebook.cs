using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.Vpn;

internal sealed class EphemeralRasPhonebook : IDisposable
{
    internal const string OwnershipMarkerFileName = ".managed-session-v1";
    internal const string OwnershipLockFileName = ".owner.lock";
    internal const string PhoneBookFileName = "session.pbk";
    private const long MaxOwnershipMarkerBytes = 1024;

    private readonly string _sessionDirectory;
    private readonly FileStream _ownershipLock;
    private int _disposed;

    private EphemeralRasPhonebook(
        string sessionDirectory,
        string phoneBookPath,
        string entryName,
        FileStream ownershipLock)
    {
        _sessionDirectory = sessionDirectory;
        PhoneBookPath = phoneBookPath;
        EntryName = entryName;
        _ownershipLock = ownershipLock;
    }

    public string PhoneBookPath { get; }
    public string EntryName { get; }

    internal static string SessionRootDirectory => Path.Combine(
        Path.GetTempPath(),
        "ProxyToAnyConnect",
        "ras");

    public static EphemeralRasPhonebook Create(L2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != L2tpConnectionMode.CustomEphemeral)
        {
            throw new ArgumentException("CustomEphemeral L2TP options are required.", nameof(options));
        }

        CleanupOrphanedSessionDirectories();

        var productTempRoot = Path.Combine(Path.GetTempPath(), "ProxyToAnyConnect");
        Directory.CreateDirectory(productTempRoot);
        EnsureRegularDirectory(productTempRoot, "ProxyToAnyConnect temporary root");

        var rasRoot = SessionRootDirectory;
        Directory.CreateDirectory(rasRoot);
        EnsureRegularDirectory(rasRoot, "ProxyToAnyConnect RAS session root");
        var sanitizedId = SanitizeId(options.Id);
        var sessionRoot = Path.Combine(
            rasRoot,
            $"{sanitizedId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionRoot);

        var entryName = $"ProxyToAnyConnect-{sanitizedId}";
        if (entryName.Length > RasNative.RasMaxEntryName)
        {
            entryName = entryName[..RasNative.RasMaxEntryName];
        }

        FileStream? ownershipLock = null;
        EphemeralRasPhonebook? resource = null;
        try
        {
            // Establish exclusive ownership before publishing the persistent marker.
            // Another process scans only marked directories, so it cannot classify a
            // newly-created session as stale in the directory-create/lock-create gap.
            // DeleteOnClose removes the lock automatically after abnormal process exit.
            ownershipLock = new FileStream(
                Path.Combine(sessionRoot, OwnershipLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            // The marker opts this directory into orphan recovery and stores the exact
            // private entry name so a later process can best-effort remove RAS
            // entry-associated credentials before deleting an orphaned phonebook.
            File.WriteAllText(
                Path.Combine(sessionRoot, OwnershipMarkerFileName),
                entryName);

            var phoneBookPath = Path.Combine(sessionRoot, PhoneBookFileName);
            resource = new EphemeralRasPhonebook(
                sessionRoot,
                phoneBookPath,
                entryName,
                ownershipLock);
            ownershipLock = null;

            using (File.Create(phoneBookPath))
            {
            }

            var l2tpDevice = FindL2tpDevice();
            var entry = BuildEntry(options, l2tpDevice);
            var entrySize = checked((uint)Marshal.SizeOf<RasNative.RasEntry>());

            var setEntryResult = RasNative.RasSetEntryPropertiesW(
                phoneBookPath,
                entryName,
                entry,
                entrySize,
                0,
                0);
            if (setEntryResult != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to create private L2TP RAS entry '{entryName}': {RasNative.DescribeError(setEntryResult)}");
            }

            if (options.Custom.IpsecAuthentication == L2tpIpsecAuthentication.PreSharedKey)
            {
                SetPreSharedKey(phoneBookPath, entryName, options.Custom.ProtectedPreSharedKey);
            }

            AppLog.Info(
                "vpn.ephemeral.created",
                "Private temporary RAS phonebook entry was created for custom L2TP.",
                new
                {
                    VpnId = options.Id,
                    VpnName = options.Name,
                    EntryName = entryName,
                    PhoneBookPath = phoneBookPath,
                    DeviceName = l2tpDevice.SzDeviceName
                });

            return resource;
        }
        catch
        {
            if (resource is not null)
            {
                resource.Dispose();
            }
            else
            {
                ownershipLock?.Dispose();
                _ = TryDeleteOwnedSessionDirectory(sessionRoot);
            }

            throw;
        }
    }

    internal static void CleanupOrphanedSessionDirectories()
    {
        var root = SessionRootDirectory;
        if (!Directory.Exists(root) || !IsRegularDirectory(root))
        {
            return;
        }

        var productTempRoot = Path.GetDirectoryName(root);
        if (string.IsNullOrWhiteSpace(productTempRoot) || !IsRegularDirectory(productTempRoot))
        {
            return;
        }

        string[] sessionDirectories;
        try
        {
            sessionDirectories = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var sessionDirectory in sessionDirectories)
        {
            if (!TryGetExpectedEntryName(sessionDirectory, out var expectedEntryName) ||
                !TryValidateManagedLeafSet(sessionDirectory))
            {
                // Non-canonical names, reparse points and unknown children are not
                // exact filesystem owners. Preserve ambiguous content fail-safe.
                continue;
            }

            var markerPath = Path.Combine(sessionDirectory, OwnershipMarkerFileName);
            if (!TryReadValidOwnershipMarker(markerPath, expectedEntryName))
            {
                continue;
            }

            var lockPath = Path.Combine(sessionDirectory, OwnershipLockFileName);
            if (HasLiveOwner(lockPath))
            {
                continue;
            }

            // Re-check after probing the owner lock. Deletion remains non-recursive,
            // so a child raced in later can only make final directory removal fail.
            if (!TryValidateManagedLeafSet(sessionDirectory))
            {
                continue;
            }

            TryDeleteRecoveredRasEntry(sessionDirectory, expectedEntryName);
            _ = TryDeleteOwnedSessionDirectory(sessionDirectory);
        }
    }

    private static bool TryGetExpectedEntryName(string sessionDirectory, out string entryName)
    {
        entryName = string.Empty;
        try
        {
            var fullSessionPath = Path.GetFullPath(sessionDirectory);
            var fullRoot = Path.GetFullPath(SessionRootDirectory);
            var parent = Path.GetDirectoryName(fullSessionPath);
            if (!string.Equals(parent, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullSessionPath));
            var separator = directoryName.LastIndexOf('-');
            if (separator <= 0 || separator == directoryName.Length - 1)
            {
                return false;
            }

            var sanitizedId = directoryName[..separator];
            var generation = directoryName[(separator + 1)..];
            if (!Guid.TryParseExact(generation, "N", out _) ||
                sanitizedId.Any(character =>
                    !char.IsLetterOrDigit(character) && character != '-' && character != '_'))
            {
                return false;
            }

            entryName = $"ProxyToAnyConnect-{sanitizedId}";
            if (entryName.Length > RasNative.RasMaxEntryName)
            {
                entryName = entryName[..RasNative.RasMaxEntryName];
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidateManagedLeafSet(string sessionDirectory)
    {
        try
        {
            if (!IsRegularDirectory(sessionDirectory))
            {
                return false;
            }

            foreach (var child in Directory.EnumerateFileSystemEntries(
                         sessionDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(child);
                if (name is not (OwnershipMarkerFileName or OwnershipLockFileName or PhoneBookFileName))
                {
                    return false;
                }

                var attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryReadValidOwnershipMarker(string markerPath, string expectedEntryName)
    {
        try
        {
            if (!IsRegularFile(markerPath))
            {
                return false;
            }

            using var stream = new FileStream(
                markerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaxOwnershipMarkerBytes)
            {
                return false;
            }

            using var reader = new StreamReader(stream);
            var markerEntryName = reader.ReadToEnd().Trim();
            return markerEntryName.Equals(expectedEntryName, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteRecoveredRasEntry(string sessionDirectory, string entryName)
    {
        try
        {
            var phoneBookPath = Path.Combine(sessionDirectory, PhoneBookFileName);
            if (IsRegularFile(phoneBookPath))
            {
                _ = RasNative.RasDeleteEntryW(phoneBookPath, entryName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Native entry cleanup is best effort. Filesystem cleanup below remains
            // bounded to exact managed leaves even if RAS metadata removal fails.
        }
    }

    private static bool HasLiveOwner(string lockPath)
    {
        if (!File.Exists(lockPath))
        {
            return false;
        }

        try
        {
            using var probe = new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sharing violation means another live process owns the session. An
            // access-denied result is treated the same way: cleanup must fail safe.
            return true;
        }
    }

    private static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void EnsureRegularDirectory(string path, string description)
    {
        if (!IsRegularDirectory(path))
        {
            throw new IOException($"{description} is not a regular directory: {path}");
        }
    }

    private static bool TryDeleteOwnedSessionDirectory(string path)
    {
        if (!TryGetExpectedEntryName(path, out _) || !TryValidateManagedLeafSet(path))
        {
            return false;
        }

        // Delete the durable marker last. If an earlier exact-leaf delete fails, a
        // later process can still recognize and retry this managed residue.
        foreach (var leafName in new[] { PhoneBookFileName, OwnershipLockFileName, OwnershipMarkerFileName })
        {
            var leafPath = Path.Combine(path, leafName);
            if (!File.Exists(leafPath))
            {
                continue;
            }

            try
            {
                if (!IsRegularFile(leafPath))
                {
                    return false;
                }

                File.Delete(leafPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A raced-in child leaves an ambiguous residue instead of widening the
            // ownership boundary to recursive deletion.
            return false;
        }
    }

    public RasNative.RasDialParams CreateDialParams(CustomL2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialParams = new RasNative.RasDialParams
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasDialParams>()),
            SzEntryName = EntryName
        };

        if (!options.UseCurrentWindowsCredentials)
        {
            dialParams.SzUserName = options.UserName;
            dialParams.SzDomain = options.Domain;
            dialParams.SzPassword = WindowsSecretProtector.Unprotect(options.ProtectedPassword);
        }

        return dialParams;
    }

    private static RasNative.RasEntry BuildEntry(L2tpOptions options, RasNative.RasDevInfo device)
    {
        var custom = options.Custom;
        uint flags = 0;
        if (custom.UseCurrentWindowsCredentials)
        {
            flags |= RasNative.RasEoUseLogonCredentials;
        }
        if (custom.AllowPap)
        {
            flags |= RasNative.RasEoRequirePap;
        }
        if (custom.AllowChap)
        {
            flags |= RasNative.RasEoRequireChap;
        }
        if (custom.AllowMsChapV2)
        {
            flags |= RasNative.RasEoRequireMsChap2;
        }

        var entry = new RasNative.RasEntry
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasEntry>()),
            DwfOptions = flags,
            SzLocalPhoneNumber = custom.ServerAddress,
            DwfNetProtocols = RasNative.RasNpIp,
            DwFramingProtocol = RasNative.RasFpPpp,
            SzDeviceType = device.SzDeviceType,
            SzDeviceName = device.SzDeviceName,
            DwIdleDisconnectSeconds = RasNative.RasIdleDisabled,
            DwType = RasNative.RasEntryTypeVpn,
            DwEncryptionType = MapEncryption(custom.Encryption),
            DwVpnStrategy = RasNative.VpnStrategyL2tpOnly,
            DwfOptions2 = custom.IpsecAuthentication == L2tpIpsecAuthentication.PreSharedKey
                ? RasNative.RasEo2UsePreSharedKey
                : 0,
            DwRedialCount = 0,
            DwRedialPause = 0
        };

        return entry;
    }

    private static uint MapEncryption(L2tpEncryptionMode mode) => mode switch
    {
        L2tpEncryptionMode.None => 0,
        L2tpEncryptionMode.Required => 1,
        L2tpEncryptionMode.Maximum => 2,
        L2tpEncryptionMode.Optional => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static RasNative.RasDevInfo FindL2tpDevice()
    {
        var structureSize = checked((uint)Marshal.SizeOf<RasNative.RasDevInfo>());
        var initial = new[]
        {
            new RasNative.RasDevInfo { DwSize = structureSize }
        };
        var bufferSize = structureSize;
        var result = RasNative.RasEnumDevicesW(initial, ref bufferSize, out var count);

        RasNative.RasDevInfo[] devices;
        if (result == RasNative.ErrorSuccess)
        {
            devices = initial.Take(checked((int)Math.Min(count, 1u))).ToArray();
        }
        else
        {
            if (bufferSize <= structureSize)
            {
                throw new InvalidOperationException(
                    $"Unable to enumerate RAS devices: {RasNative.DescribeError(result)}");
            }

            var capacity = checked((int)((bufferSize + structureSize - 1) / structureSize));
            devices = new RasNative.RasDevInfo[capacity];
            for (var index = 0; index < devices.Length; index++)
            {
                devices[index].DwSize = structureSize;
            }

            result = RasNative.RasEnumDevicesW(devices, ref bufferSize, out count);
            if (result != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to enumerate RAS devices: {RasNative.DescribeError(result)}");
            }

            if (count < devices.Length)
            {
                Array.Resize(ref devices, checked((int)count));
            }
        }

        foreach (var device in devices)
        {
            if (string.Equals(device.SzDeviceType, "vpn", StringComparison.OrdinalIgnoreCase) &&
                device.SzDeviceName is { Length: > 0 } name &&
                name.Contains("L2TP", StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        throw new InvalidOperationException(
            "Windows RAS did not expose an L2TP VPN device (normally 'WAN Miniport (L2TP)').");
    }

    private static void SetPreSharedKey(string phoneBookPath, string entryName, string protectedPsk)
    {
        var psk = WindowsSecretProtector.Unprotect(protectedPsk);
        var credentials = new RasNative.RasCredentials
        {
            DwSize = checked((uint)Marshal.SizeOf<RasNative.RasCredentials>()),
            DwMask = RasNative.RasCmPreSharedKey,
            SzPassword = psk
        };

        try
        {
            var result = RasNative.RasSetCredentialsW(
                phoneBookPath,
                entryName,
                credentials,
                clearCredentials: false);
            if (result != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to set the L2TP/IPsec pre-shared key in the private RAS phonebook: {RasNative.DescribeError(result)}");
            }
        }
        finally
        {
            // P/Invoke has completed synchronously; the native API no longer needs
            // this managed carrier. Drop both managed references immediately rather
            // than retaining plaintext PSK on a RASCREDENTIALS instance until GC.
            credentials.SzPassword = string.Empty;
            psk = string.Empty;
        }
    }

    private static string SanitizeId(string id)
    {
        var sanitized = new string(id.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "session" : sanitized;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (TryValidateManagedLeafSet(_sessionDirectory) && IsRegularFile(PhoneBookPath))
            {
                _ = RasNative.RasDeleteEntryW(PhoneBookPath, EntryName);
            }
        }
        catch
        {
        }

        try
        {
            _ownershipLock.Dispose();
        }
        catch
        {
        }

        if (Directory.Exists(_sessionDirectory) && !TryDeleteOwnedSessionDirectory(_sessionDirectory))
        {
            AppLog.Warning(
                "vpn.ephemeral.cleanup_preserved_ambiguous",
                "Temporary private RAS session directory was preserved because exact filesystem ownership could not be proven for every leaf.",
                new { SessionDirectory = _sessionDirectory });
        }
    }
}
