using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class SettingsValidationSelfTests
{
    public static int Run()
    {
        try
        {
            VerificationResponseLimitIsBounded();
            InvalidNumericValuesAreRepairable();
            UnusedProtectedSecretsAreDropped();

            Console.WriteLine(
                "PASS: settings enforce bounded verification responses, repair numeric values and drop unused secrets");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: settings validation regression: {ex}");
            return 1;
        }
    }

    private static void VerificationResponseLimitIsBounded()
    {
        CreateOptions(VerificationOptions.MinimumResponseLimitBytes).Validate();
        CreateOptions(VerificationOptions.MaximumResponseLimitBytes).Validate();

        AssertInvalidResponseLimit(VerificationOptions.MinimumResponseLimitBytes - 1);
        AssertInvalidResponseLimit(VerificationOptions.MaximumResponseLimitBytes + 1);
    }

    private static void AssertInvalidResponseLimit(int maxResponseBytes)
    {
        try
        {
            CreateOptions(maxResponseBytes).Validate();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("verification.maxResponseBytes", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"verification.maxResponseBytes={maxResponseBytes} escaped the bounded configuration contract.");
    }

    private static void InvalidNumericValuesAreRepairable()
    {
        if (L2tpSettingsDialog.ClampNumericValue(
                int.MaxValue,
                VerificationOptions.MinimumResponseLimitBytes,
                VerificationOptions.MaximumResponseLimitBytes) != VerificationOptions.MaximumResponseLimitBytes)
        {
            throw new InvalidOperationException("L2TP editor did not clamp an oversized legacy value.");
        }

        if (L2tpSettingsDialog.ClampNumericValue(-1, 250, 60000) != 250)
        {
            throw new InvalidOperationException("L2TP editor did not clamp an undersized legacy value.");
        }

        if (ProxySettingsDialog.ClampNumericValue(0, 1, 65535) != 1 ||
            ProxySettingsDialog.ClampNumericValue(70000, 1, 65535) != 65535)
        {
            throw new InvalidOperationException("Proxy editor did not clamp invalid legacy numeric values.");
        }
    }

    private static void UnusedProtectedSecretsAreDropped()
    {
        const string existingProtected = "dpapi-existing-secret";

        var preserved = L2tpSettingsDialog.ResolveProtectedSecret(
            credentialRequired: true,
            enteredPlaintext: string.Empty,
            existingProtected: existingProtected);
        if (!string.Equals(preserved, existingProtected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Blank secret editor input did not preserve the existing protected value while the credential remains required.");
        }

        var dropped = L2tpSettingsDialog.ResolveProtectedSecret(
            credentialRequired: false,
            enteredPlaintext: string.Empty,
            existingProtected: existingProtected);
        if (dropped.Length != 0)
        {
            throw new InvalidOperationException(
                "A protected credential remained persisted after the selected authentication mode stopped requiring it.");
        }
    }

    private static AppOptions CreateOptions(int maxResponseBytes) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-settings",
                    Name = "Settings proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18301,
                    VpnConnectionId = "vpn-settings",
                    MaxConcurrentConnections = 8,
                    MaxHeaderBytes = 8192,
                    ClientHeaderTimeoutSeconds = 5,
                    OutboundConnectTimeoutSeconds = 5,
                    DnsTimeoutMilliseconds = 1000
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = "vpn-settings",
                    Name = "Settings VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-settings",
                    MonitorIntervalMilliseconds = 1000,
                    RouteMonitorIntervalMilliseconds = 5000,
                    ReconnectCooldownMilliseconds = 1000,
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com",
                        ProbeHost = "api.ipify.org",
                        ProbePort = 443,
                        ProbePath = "/",
                        TimeoutSeconds = 5,
                        MaxResponseBytes = maxResponseBytes
                    },
                    Keepalive = new KeepaliveOptions
                    {
                        Mode = L2tpKeepaliveMode.Off,
                        IntervalSeconds = 10,
                        TimeoutMilliseconds = 1000,
                        FailureThreshold = 3
                    }
                }
            ]
        };
}
