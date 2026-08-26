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

        var options = new L2tpOptions
        {
            Id = $"ci-{Guid.NewGuid():N}",
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
                ProtectedPreSharedKey = WindowsSecretProtector.Protect("ci-test-psk"),
                Encryption = L2tpEncryptionMode.Required,
                AllowMsChapV2 = true
            }
        };

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

            if (phoneBookPath is not null && File.Exists(phoneBookPath))
            {
                throw new InvalidOperationException("Private RAS phonebook file remained after Dispose.");
            }

            if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
            {
                throw new InvalidOperationException("Private RAS session directory remained after Dispose.");
            }

            Console.WriteLine("PASS: private ephemeral L2TP RAS phonebook create/PSK/cleanup smoke test");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: private ephemeral L2TP RAS phonebook smoke test: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                if (sessionDirectory is not null && Directory.Exists(sessionDirectory))
                {
                    Directory.Delete(sessionDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
