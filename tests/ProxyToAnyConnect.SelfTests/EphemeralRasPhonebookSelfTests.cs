using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Security;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class EphemeralRasPhonebookSelfTests
{
    private const int PartialFailureChurnCycles = 16;

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: ephemeral RAS phonebook smoke test requires Windows.");
            return 0;
        }

        try
        {
            NativeFieldLimitsStaySynchronizedAndExact();
            OrphanRecoveryRespectsCrossProcessOwnership();
            OrphanRecoveryPreservesAmbiguousFilesystemContent();
            HappyPathCreateAndCleanup();
            PartialCreationFailureChurnCleansPrivateResources();
            Console.WriteLine(
                $"PASS: private ephemeral L2TP RAS phonebook exact-leaf/reparse-safe orphan recovery/create/PSK/cleanup and {PartialFailureChurnCycles}-cycle failure churn tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: private ephemeral L2TP RAS phonebook smoke test: {ex}");
            return 1;
        }
    }

    private static void NativeFieldLimitsStaySynchronizedAndExact()
    {
        if (CustomL2tpOptions.MaximumServerAddressChars != RasNative.RasMaxPhoneNumber ||
            CustomL2tpOptions.MaximumUserNameChars != RasNative.Unlen ||
            CustomL2tpOptions.MaximumPasswordChars != RasNative.Pwlen ||
            CustomL2tpOptions.MaximumDomainChars != RasNative.Dnlen ||
            CustomL2tpOptions.MaximumPreSharedKeyChars != RasNative.Pwlen)
        {
            throw new InvalidOperationException(
                "Managed custom L2TP limits drifted from fixed-width Windows RAS fields.");
        }

        foreach (var maximum in new[]
                 {
                     CustomL2tpOptions.MaximumServerAddressChars,
                     CustomL2tpOptions.MaximumUserNameChars,
                     CustomL2tpOptions.MaximumPasswordChars,
                     CustomL2tpOptions.MaximumDomainChars,
                     CustomL2tpOptions.MaximumPreSharedKeyChars
                 })
        {
            EphemeralRasPhonebook.EnsureNativeFieldCapacity(
                new string('x', maximum),
                maximum,
                "self-test");
            try
            {
                EphemeralRasPhonebook.EnsureNativeFieldCapacity(
                    new string('x', maximum + 1),
                    maximum,
                    "self-test");
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Windows RAS native-field guard accepted {maximum + 1} characters for a {maximum}-character field.");
        }
    }

    private static void OrphanRecoveryRespectsCrossProcessOwnership()
    {
        var root = EphemeralRasPhonebook.SessionRootDirectory;
        var suffix = Guid.NewGuid().ToString("N");
        var staleWithoutLock = Path.Combine(root, $"selftest-stale-no-lock-{suffix}");
        var staleUnlockedLock = Path.Combine(root, $"selftest-stale-unlocked-{suffix}");
        var active = Path.Combine(root, $"selftest-active-{suffix}");
        var legacyUnmarked = Path.Combine(root, $"selftest-legacy-{suffix}");
        FileStream? activeLock = null;

        try
        {
            CreateManagedMarker(staleWithoutLock);

            CreateManagedMarker(staleUnlockedLock);
            File.WriteAllText(
                Path.Combine(staleUnlockedLock, EphemeralRasPhonebook.OwnershipLockFileName),
                "stale");

            CreateManagedMarker(active);
            activeLock = new FileStream(
                Path.Combine(active, EphemeralRasPhonebook.OwnershipLockFileName),
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            Directory.CreateDirectory(legacyUnmarked);
            File.WriteAllText(Path.Combine(legacyUnmarked, "session.pbk"), "legacy");

            EphemeralRasPhonebook.CleanupOrphanedSessionDirectories();

            if (Directory.Exists(staleWithoutLock))
            {
                throw new InvalidOperationException(
                    "Marked orphan without an owner lock was not recovered.");
            }

            if (Directory.Exists(staleUnlockedLock))
            {
                throw new InvalidOperationException(
                    "Marked orphan with an unowned lock file was not recovered.");
            }

            if (!Directory.Exists(active))
            {
                throw new InvalidOperationException(
                    "Orphan cleanup deleted a session whose owner lock is still held.");
            }

            if (!Directory.Exists(legacyUnmarked))
            {
                throw new InvalidOperationException(
                    "Orphan cleanup deleted an unmarked legacy directory with unknown ownership.");
            }

            activeLock.Dispose();
            activeLock = null;
            EphemeralRasPhonebook.CleanupOrphanedSessionDirectories();

            if (Directory.Exists(active))
            {
                throw new InvalidOperationException(
                    "Marked session was not recovered after its owner lock was released.");
            }

            if (!Directory.Exists(legacyUnmarked))
            {
                throw new InvalidOperationException(
                    "Second orphan cleanup deleted an unmarked legacy directory.");
            }
        }
        finally
        {
            activeLock?.Dispose();
            BestEffortDelete(staleWithoutLock);
            BestEffortDelete(staleUnlockedLock);
            BestEffortDelete(active);
            BestEffortDelete(legacyUnmarked);
        }
    }

    private static void CreateManagedMarker(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, EphemeralRasPhonebook.OwnershipMarkerFileName),
            ExpectedEntryNameForManagedDirectory(directory));
    }

    private static string ExpectedEntryNameForManagedDirectory(string directory)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var separator = name.LastIndexOf('-');
        if (separator <= 0)
        {
            throw new InvalidOperationException($"Self-test managed directory is not canonical: {directory}");
        }

        var sanitizedId = name[..separator];
        var entryName = $"ProxyToAnyConnect-{sanitizedId}";
        return entryName.Length <= RasNative.RasMaxEntryName
            ? entryName
            : entryName[..RasNative.RasMaxEntryName];
    }

    private static void OrphanRecoveryPreservesAmbiguousFilesystemContent()
    {
        var root = EphemeralRasPhonebook.SessionRootDirectory;
        Directory.CreateDirectory(root);
        var malformedMarker = Path.Combine(root, $"selftest-malformed-{Guid.NewGuid():N}");
        var oversizedMarker = Path.Combine(root, $"selftest-oversized-{Guid.NewGuid():N}");
        var unexpectedChild = Path.Combine(root, $"selftest-unexpected-{Guid.NewGuid():N}");
        var nonCanonical = Path.Combine(root, "selftest-noncanonical-owned-looking");
        var externalTarget = Path.Combine(
            Path.GetTempPath(),
            $"ProxyToAnyConnect-external-target-{Guid.NewGuid():N}");
        var reparseSession = Path.Combine(root, $"selftest-reparse-{Guid.NewGuid():N}");
        var reparseCreated = false;

        try
        {
            CreateManagedMarker(malformedMarker);
            File.WriteAllText(
                Path.Combine(malformedMarker, EphemeralRasPhonebook.OwnershipMarkerFileName),
                "ProxyToAnyConnect-wrong-entry");
            File.WriteAllText(
                Path.Combine(malformedMarker, EphemeralRasPhonebook.PhoneBookFileName),
                "keep");

            CreateManagedMarker(oversizedMarker);
            File.WriteAllText(
                Path.Combine(oversizedMarker, EphemeralRasPhonebook.OwnershipMarkerFileName),
                new string('x', 2048));

            CreateManagedMarker(unexpectedChild);
            File.WriteAllText(
                Path.Combine(unexpectedChild, "unexpected-child.txt"),
                "must survive managed cleanup");

            Directory.CreateDirectory(nonCanonical);
            File.WriteAllText(
                Path.Combine(nonCanonical, EphemeralRasPhonebook.OwnershipMarkerFileName),
                "ProxyToAnyConnect-selftest-noncanonical-owned-looking");

            try
            {
                Directory.CreateDirectory(externalTarget);
                File.WriteAllText(Path.Combine(externalTarget, "sentinel.txt"), "external");
                Directory.CreateSymbolicLink(reparseSession, externalTarget);
                reparseCreated = true;
                File.WriteAllText(
                    Path.Combine(externalTarget, EphemeralRasPhonebook.OwnershipMarkerFileName),
                    ExpectedEntryNameForManagedDirectory(reparseSession));
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                reparseCreated = false;
            }

            EphemeralRasPhonebook.CleanupOrphanedSessionDirectories();

            foreach (var preserved in new[] { malformedMarker, oversizedMarker, unexpectedChild, nonCanonical })
            {
                if (!Directory.Exists(preserved))
                {
                    throw new InvalidOperationException(
                        $"Orphan cleanup removed ambiguous managed-looking directory: {preserved}");
                }
            }

            if (!File.Exists(Path.Combine(unexpectedChild, "unexpected-child.txt")) ||
                !File.Exists(Path.Combine(unexpectedChild, EphemeralRasPhonebook.OwnershipMarkerFileName)))
            {
                throw new InvalidOperationException(
                    "Orphan cleanup partially consumed a directory containing an unexpected child.");
            }

            if (reparseCreated &&
                (!Directory.Exists(reparseSession) ||
                 !File.Exists(Path.Combine(externalTarget, "sentinel.txt"))))
            {
                throw new InvalidOperationException(
                    "Orphan cleanup traversed/deleted a reparse-point session or its external target.");
            }
        }
        finally
        {
            BestEffortDelete(malformedMarker);
            BestEffortDelete(oversizedMarker);
            BestEffortDelete(unexpectedChild);
            BestEffortDelete(nonCanonical);
            if (reparseCreated)
            {
                BestEffortDelete(reparseSession);
            }
            BestEffortDelete(externalTarget);
        }
    }

    private static void HappyPathCreateAndCleanup()
    {
        var options = CreateOptions(
            id: $"ci-{Guid.NewGuid():N}",
            protectedPsk: WindowsSecretProtector.Protect("ci-test-psk"));

        string? phoneBookPath = null;
        string? sessionDirectory = null;
        try
        {
            using (var phoneBook = EphemeralRasPhonebook.Create(options))
            {
                phoneBookPath = phoneBook.PhoneBookPath;
                sessionDirectory = Path.GetDirectoryName(phoneBookPath);

                if (!File.Exists(phoneBookPath))
                {
                    throw new InvalidOperationException("Private RAS phonebook file was not created.");
                }

                if (string.IsNullOrWhiteSpace(sessionDirectory) ||
                    !phoneBookPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Private RAS phonebook was created outside the temporary runtime area: {phoneBookPath}");
                }

                if (!File.Exists(Path.Combine(sessionDirectory, EphemeralRasPhonebook.OwnershipMarkerFileName)) ||
                    !File.Exists(Path.Combine(sessionDirectory, EphemeralRasPhonebook.OwnershipLockFileName)))
                {
                    throw new InvalidOperationException(
                        "Private RAS session did not publish its managed ownership marker/lock.");
                }

                var dialParams = phoneBook.CreateDialParams(options.Custom);
                if (!dialParams.SzEntryName.Equals(phoneBook.EntryName, StringComparison.Ordinal) ||
                    !dialParams.SzUserName.Equals("ci-user", StringComparison.Ordinal) ||
                    !dialParams.SzPassword.Equals("ci-password", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Ephemeral RAS dial parameters were not populated correctly.");
                }
            }

            AssertRemoved(phoneBookPath, sessionDirectory, "normal Dispose");
        }
        finally
        {
            BestEffortDelete(sessionDirectory);
        }
    }

    private static void PartialCreationFailureChurnCleansPrivateResources()
    {
        var rasRoot = Path.Combine(Path.GetTempPath(), "ProxyToAnyConnect", "ras");
        var runPrefix = $"ci-failure-churn-{Guid.NewGuid():N}";
        var before = ExistingMatchingDirectories(rasRoot, runPrefix);

        try
        {
            for (var cycle = 0; cycle < PartialFailureChurnCycles; cycle++)
            {
                var id = $"{runPrefix}-{cycle:D2}";
                var options = CreateOptions(
                    id,
                    protectedPsk: "intentionally-not-a-dpapi-payload");

                try
                {
                    _ = EphemeralRasPhonebook.Create(options);
                    throw new InvalidOperationException(
                        $"Ephemeral RAS creation unexpectedly accepted an invalid protected PSK on cycle {cycle}.");
                }
                catch (InvalidOperationException ex) when (
                    ex.Message.Contains("Protected secret", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("DPAPI", StringComparison.OrdinalIgnoreCase))
                {
                    // Expected after the private PBK entry has been prepared and before
                    // Create returns. The failed creation owns all partial resources.
                }

                var residual = ExistingMatchingDirectories(rasRoot, id);
                if (residual.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Partial CustomEphemeral creation retained a private RAS directory after cycle {cycle}: {string.Join(", ", residual)}");
                }
            }
        }
        finally
        {
            var after = ExistingMatchingDirectories(rasRoot, runPrefix);
            var leaked = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in leaked)
            {
                BestEffortDelete(path);
            }

            if (leaked.Length != 0)
            {
                throw new InvalidOperationException(
                    $"{PartialFailureChurnCycles}-cycle CustomEphemeral failure churn leaked temporary RAS directorie(s): {string.Join(", ", leaked)}");
            }
        }
    }

    private static L2tpOptions CreateOptions(string id, string protectedPsk) =>
        new()
        {
            Id = id,
            Name = "CI ephemeral L2TP",
            Mode = L2tpConnectionMode.CustomEphemeral,
            Custom = new CustomL2tpOptions
            {
                ServerAddress = "203.0.113.1",
                UserName = "ci-user",
                Domain = "",
                UseCurrentWindowsCredentials = false,
                ProtectedPassword = WindowsSecretProtector.Protect("ci-password"),
                IpsecAuthentication = L2tpIpsecAuthentication.PreSharedKey,
                ProtectedPreSharedKey = protectedPsk,
                Encryption = L2tpEncryptionMode.Required,
                AllowMsChapV2 = true
            }
        };

    private static HashSet<string> ExistingMatchingDirectories(string root, string prefix)
    {
        if (!Directory.Exists(root))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory
            .EnumerateDirectories(root, $"{prefix}*", SearchOption.TopDirectoryOnly)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertRemoved(string? phoneBookPath, string? sessionDirectory, string phase)
    {
        if (phoneBookPath is not null && File.Exists(phoneBookPath))
        {
            throw new InvalidOperationException(
                $"Private RAS phonebook file remained after {phase}: {phoneBookPath}");
        }

        if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
        {
            throw new InvalidOperationException(
                $"Private RAS session directory remained after {phase}: {sessionDirectory}");
        }
    }

    private static void BestEffortDelete(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            Directory.Delete(
                path,
                recursive: (attributes & FileAttributes.ReparsePoint) == 0);
        }
        catch
        {
        }
    }
}
