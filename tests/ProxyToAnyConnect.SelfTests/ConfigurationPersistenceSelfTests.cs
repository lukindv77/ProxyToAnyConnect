using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.SelfTests;

internal static class ConfigurationPersistenceSelfTests
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ProxyToAnyConnect",
            "config-persistence-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await SuccessfulSavePublishesCompleteJsonAsync(root);
            await CustomEphemeralRoundTripPreservesSchemaAsync(root);
            await PreCancelledSavePreservesPublishedFileAsync(root);
            await PublicationFailureCleansUniqueTemporaryFileAsync(root);

            Console.WriteLine(
                "PASS: configuration persistence publishes complete Existing/CustomEphemeral files and cleans cancelled/failed save ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: configuration persistence regression: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task SuccessfulSavePublishesCompleteJsonAsync(string root)
    {
        var path = Path.Combine(root, "successful.json");
        var options = CreateExistingProfileOptions("Successful persistence");

        await options.SaveAsync(path, CancellationToken.None);
        var loaded = await AppOptions.LoadAsync(path, CancellationToken.None);
        if (loaded.Proxies.Count != 1 ||
            !string.Equals(loaded.Proxies[0].Name, "Successful persistence", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Published configuration did not round-trip as one complete JSON document.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(path), "successful save");
    }

    private static async Task CustomEphemeralRoundTripPreservesSchemaAsync(string root)
    {
        var path = Path.Combine(root, "custom-ephemeral.json");
        var options = new AppOptions
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-custom",
                    Name = "Custom proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18081,
                    VpnConnectionId = "vpn-custom"
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = "vpn-custom",
                    Name = "Custom VPN",
                    Mode = L2tpConnectionMode.CustomEphemeral,
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com"
                    },
                    Custom = new CustomL2tpOptions
                    {
                        ServerAddress = "l2tp.example.com",
                        UserName = "self-test-user",
                        Domain = "SELFTEST",
                        UseCurrentWindowsCredentials = false,
                        ProtectedPassword = "protected-password-carrier",
                        IpsecAuthentication = L2tpIpsecAuthentication.PreSharedKey,
                        ProtectedPreSharedKey = "protected-psk-carrier",
                        Encryption = L2tpEncryptionMode.Maximum,
                        AllowPap = true,
                        AllowChap = true,
                        AllowMsChapV2 = false
                    }
                }
            ]
        };

        await options.SaveAsync(path, CancellationToken.None);
        var loaded = await AppOptions.LoadAsync(path, CancellationToken.None);
        var vpn = loaded.VpnConnections.Single();
        var custom = vpn.Custom;

        if (vpn.Mode != L2tpConnectionMode.CustomEphemeral ||
            custom.ServerAddress != "l2tp.example.com" ||
            custom.UserName != "self-test-user" ||
            custom.Domain != "SELFTEST" ||
            custom.UseCurrentWindowsCredentials ||
            custom.ProtectedPassword != "protected-password-carrier" ||
            custom.IpsecAuthentication != L2tpIpsecAuthentication.PreSharedKey ||
            custom.ProtectedPreSharedKey != "protected-psk-carrier" ||
            custom.Encryption != L2tpEncryptionMode.Maximum ||
            !custom.AllowPap ||
            !custom.AllowChap ||
            custom.AllowMsChapV2)
        {
            throw new InvalidOperationException(
                "CustomEphemeral configuration schema changed or lost fields during durable round-trip.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(path), "CustomEphemeral save");
    }

    private static async Task PreCancelledSavePreservesPublishedFileAsync(string root)
    {
        var path = Path.Combine(root, "cancelled.json");
        const string original = "previous-complete-generation";
        await File.WriteAllTextAsync(path, original);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await CreateExistingProfileOptions("Cancelled persistence").SaveAsync(path, cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled configuration save unexpectedly completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        var actual = await File.ReadAllTextAsync(path);
        if (!string.Equals(actual, original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Cancelled configuration save modified the previously published generation.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(path), "cancelled save");
    }

    private static async Task PublicationFailureCleansUniqueTemporaryFileAsync(string root)
    {
        var targetDirectory = Path.Combine(root, "blocked-target.json");
        Directory.CreateDirectory(targetDirectory);

        try
        {
            await CreateExistingProfileOptions("Failed publication").SaveAsync(targetDirectory, CancellationToken.None);
            throw new InvalidOperationException(
                "Configuration save unexpectedly replaced a directory with the JSON file.");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            // Windows may report the directory publication collision through either
            // IOException or UnauthorizedAccessException depending on filesystem.
        }

        if (!Directory.Exists(targetDirectory))
        {
            throw new InvalidOperationException("Failed publication damaged the existing destination directory.");
        }

        AssertNoTemporarySiblings(root, Path.GetFileName(targetDirectory), "failed publication");
    }

    private static AppOptions CreateExistingProfileOptions(string proxyName) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-existing",
                    Name = proxyName,
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18080,
                    VpnConnectionId = "vpn-existing"
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = "vpn-existing",
                    Name = "Existing VPN",
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-L2TP",
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com"
                    }
                }
            ]
        };

    private static void AssertNoTemporarySiblings(string root, string fileName, string phase)
    {
        var pattern = $".{fileName}.*.tmp";
        var leftovers = Directory.GetFiles(root, pattern, SearchOption.TopDirectoryOnly);
        if (leftovers.Length != 0)
        {
            throw new InvalidOperationException(
                $"{phase}: configuration save left {leftovers.Length} owned temporary file(s).");
        }
    }
}
