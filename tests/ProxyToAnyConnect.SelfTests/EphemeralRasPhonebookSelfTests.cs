using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Security;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class EphemeralRasPhonebookSelfTests
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: ephemeral RAS phonebook smoke test requires Windows.");
            return 0;
        }

        try
        {
            HappyPathCreateAndCleanup();
            PartialCreationFailureCleansPrivateResources();
            Console.WriteLine(
                "PASS: private ephemeral L2TP RAS phonebook create/PSK/cleanup and partial-failure cleanup smoke tests");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: private ephemeral L2TP RAS phonebook smoke test: {ex}");
            return 1;
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

    private static void PartialCreationFailureCleansPrivateResources()
    {
        var id = $"ci-failure-{Guid.NewGuid():N}";
        var sanitizedId = new string(id.Where(
            character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        var rasRoot = Path.Combine(Path.GetTempPath(), "ProxyToAnyConnect", "ras");
        var prefix = $"{sanitizedId}-";
        var before = ExistingMatchingDirectories(rasRoot, prefix);

        var options = CreateOptions(
            id,
            protectedPsk: "intentionally-not-a-dpapi-payload");

        try
        {
            _ = EphemeralRasPhonebook.Create(options);
            throw new InvalidOperationException(
                "Ephemeral RAS creation unexpectedly accepted an invalid protected PSK.");
        }
        catch (Exception ex) when (
            ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            // Expected after the private PBK entry has been prepared and before Create returns.
        }

        var after = ExistingMatchingDirectories(rasRoot, prefix);
        var leaked = after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray();
        if (leaked.Length != 0)
        {
            foreach (var path in leaked)
            {
                BestEffortDelete(path);
            }

            throw new InvalidOperationException(
                $"Partial CustomEphemeral creation leaked temporary RAS directorie(s): {string.Join(", ", leaked)}");
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
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
