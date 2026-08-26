using System.Runtime.InteropServices;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.Vpn;

internal sealed class EphemeralRasPhonebook : IDisposable
{
    private readonly string _sessionDirectory;
    private int _disposed;

    private EphemeralRasPhonebook(string sessionDirectory, string phoneBookPath, string entryName)
    {
        _sessionDirectory = sessionDirectory;
        PhoneBookPath = phoneBookPath;
        EntryName = entryName;
    }

    public string PhoneBookPath { get; }
    public string EntryName { get; }

    public static EphemeralRasPhonebook Create(L2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != L2tpConnectionMode.CustomEphemeral)
        {
            throw new ArgumentException("CustomEphemeral L2TP options are required.", nameof(options));
        }

        var sessionRoot = Path.Combine(
            Path.GetTempPath(),
            "ProxyToAnyConnect",
            "ras",
            $"{SanitizeId(options.Id)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionRoot);

        var phoneBookPath = Path.Combine(sessionRoot, "session.pbk");
        var entryName = $"ProxyToAnyConnect-{SanitizeId(options.Id)}";
        if (entryName.Length > RasNative.RasMaxEntryName)
        {
            entryName = entryName[..RasNative.RasMaxEntryName];
        }

        var resource = new EphemeralRasPhonebook(sessionRoot, phoneBookPath, entryName);
        try
        {
            // RAS expects a phone-book path. A zero-length private PBK is initialized by
            // RasSetEntryProperties when the first entry is written.
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
            resource.Dispose();
            throw;
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

        // Deliberately do NOT set RASEO_RemoteDefaultGateway. Custom sessions are split-tunnel;
        // proxy sockets select the L2TP interface explicitly and unrelated applications keep
        // their ordinary Windows default route.
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
        L2tpEncryptionMode.None => 0,       // ET_None
        L2tpEncryptionMode.Required => 1,   // ET_Require
        L2tpEncryptionMode.Maximum => 2,    // ET_RequireMax
        L2tpEncryptionMode.Optional => 3,   // ET_Optional
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
            devices = Enumerable.Range(0, capacity)
                .Select(_ => new RasNative.RasDevInfo { DwSize = structureSize })
                .ToArray();

            result = RasNative.RasEnumDevicesW(devices, ref bufferSize, out count);
            if (result != RasNative.ErrorSuccess)
            {
                throw new InvalidOperationException(
                    $"Unable to enumerate RAS devices: {RasNative.DescribeError(result)}");
            }

            if (count < devices.Length)
            {
                devices = devices.Take(checked((int)count)).ToArray();
            }
        }

        var l2tp = devices.FirstOrDefault(device =>
            device.SzDeviceType.Equals("vpn", StringComparison.OrdinalIgnoreCase) &&
            device.SzDeviceName.Contains("L2TP", StringComparison.OrdinalIgnoreCase));

        return l2tp ?? throw new InvalidOperationException(
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
            if (File.Exists(PhoneBookPath))
            {
                _ = RasNative.RasDeleteEntryW(PhoneBookPath, EntryName);
            }
        }
        catch
        {
            // Best-effort cleanup continues with filesystem removal below.
        }

        try
        {
            if (Directory.Exists(_sessionDirectory))
            {
                Directory.Delete(_sessionDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning(
                "vpn.ephemeral.cleanup_failed",
                "Unable to remove a temporary private RAS phonebook directory.",
                new { SessionDirectory = _sessionDirectory, Error = ex.Message });
        }
    }
}
