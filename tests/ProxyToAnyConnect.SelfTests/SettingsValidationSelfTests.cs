using System.Net;
using System.Net.Sockets;
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
            VerificationProbePathIsWireExactOriginForm();
            VerificationProbeHostUsesCanonicalIdnAuthority();
            VerificationEditorPreservesWireIdentity();
            CustomL2tpNativeFieldLimitsFailClosed();
            InvalidNumericValuesAreRepairable();
            UnusedProtectedSecretsAreDropped();

            Console.WriteLine(
                "PASS: settings enforce bounded verification responses, byte-exact probe targets, canonical IDN hosts, repair numeric values and drop unused secrets");
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

    private static void VerificationProbePathIsWireExactOriginForm()
    {
        foreach (var valid in new[]
                 {
                     "/",
                     "/?format=text",
                     "/ip/check?x=1%202",
                     "/caf%C3%A9?next=%2Fok&flag=true"
                 })
        {
            CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probePath: valid).Validate();
        }

        foreach (var invalid in new[]
                 {
                     string.Empty,
                     "relative",
                     "/contains space",
                     "/tab\tvalue",
                     "/line\r\nHost: injected.example",
                     "/café",
                     "/fragment#value",
                     "/bad%2",
                     "/bad%ZZ",
                     "/back\\slash",
                     "/raw[bracket]"
                 })
        {
            try
            {
                CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probePath: invalid).Validate();
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("verification.probePath", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"verification.probePath '{EscapeForDiagnostic(invalid)}' escaped the byte-exact origin-form contract.");
        }
    }

    private static void VerificationProbeHostUsesCanonicalIdnAuthority()
    {
        CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: "api.ipify.org").Validate();
        CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: "API.IPIFY.ORG").Validate();
        CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: "münich.example").Validate();
        CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: "127.0.0.1").Validate();

        if (!VerificationOptions.TryGetCanonicalProbeHost("münich.example", out var canonical) ||
            !canonical.Equals("xn--mnich-kva.example", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unicode verification host did not canonicalize to the expected IDNA A-label: '{canonical}'.");
        }

        if (!VerificationOptions.TryGetCanonicalProbeHost("API.IPIFY.ORG", out var asciiCanonical) ||
            !asciiCanonical.Equals("api.ipify.org", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASCII verification host did not canonicalize case consistently: '{asciiCanonical}'.");
        }

        if (!VerificationOptions.TryGetCanonicalProbeHost("127.0.0.1", out var ipv4Canonical) ||
            !ipv4Canonical.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Canonical IPv4 verification host was not preserved exactly: '{ipv4Canonical}'.");
        }

        var invalidHosts = new List<string>
        {
            string.Empty,
            "bad host.example",
            "line\r\nhost.example",
            "bad_.example",
            "[2001:db8::1]"
        };
        invalidHosts.AddRange(DetectRuntimeLegacyIpv4Forms());

        foreach (var invalid in invalidHosts)
        {
            try
            {
                CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: invalid).Validate();
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("verification.probeHost", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"verification.probeHost '{EscapeForDiagnostic(invalid)}' escaped canonical host validation.");
        }
    }

    private static string[] DetectRuntimeLegacyIpv4Forms()
    {
        string[] candidates =
        [
            "127.1",
            "127.0.1",
            "2130706433",
            "0x7f000001",
            "017700000001",
            "0177.0.0.1",
            "127.000.000.001"
        ];

        var detected = candidates
            .Where(candidate =>
                IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                !candidate.Equals(address.ToString(), StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (detected.Length == 0)
        {
            throw new InvalidOperationException(
                "The current Windows/.NET runtime did not recognize any legacy IPv4 form from the verification regression matrix.");
        }

        return detected;
    }

    private static string EscapeForDiagnostic(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void VerificationEditorPreservesWireIdentity()
    {
        const string rawHost = " example.com ";
        const string rawPath = "/path ";
        var materialized = L2tpSettingsDialog.CreateVerificationOptions(
            "vpn.example.com",
            rawHost,
            443,
            rawPath,
            5,
            VerificationOptions.DefaultResponseLimitBytes);

        if (!materialized.ProbeHost.Equals(rawHost, StringComparison.Ordinal) ||
            !materialized.ProbePath.Equals(rawPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "L2TP editor silently rewrote verification host/path identity before validation.");
        }

        if (VerificationOptions.TryGetCanonicalProbeHost(materialized.ProbeHost, out _) ||
            VerificationOptions.IsValidProbePath(materialized.ProbePath))
        {
            throw new InvalidOperationException(
                "Whitespace-bearing editor verification identity escaped the shared fail-closed validator.");
        }

        var valid = L2tpSettingsDialog.CreateVerificationOptions(
            "vpn.example.com",
            "API.IPIFY.ORG",
            443,
            "/ip/check?x=1%202",
            5,
            VerificationOptions.DefaultResponseLimitBytes);
        if (!valid.ProbeHost.Equals("API.IPIFY.ORG", StringComparison.Ordinal) ||
            !valid.ProbePath.Equals("/ip/check?x=1%202", StringComparison.Ordinal) ||
            !VerificationOptions.TryGetCanonicalProbeHost(valid.ProbeHost, out var canonical) ||
            !canonical.Equals("api.ipify.org", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "L2TP editor did not preserve valid verification input for the common canonicalization boundary.");
        }
    }

    private static void CustomL2tpNativeFieldLimitsFailClosed()
    {
        var serverAtLimit =
            new string('a', 62) + "." + new string('b', 63) + ".c";
        var serverOverLimit = serverAtLimit + "d";
        if (serverAtLimit.Length != CustomL2tpOptions.MaximumServerAddressChars ||
            serverOverLimit.Length != CustomL2tpOptions.MaximumServerAddressChars + 1)
        {
            throw new InvalidOperationException("Custom L2TP server-address boundary fixture is invalid.");
        }

        CreateCustomOptions(
            serverAtLimit,
            new string('u', CustomL2tpOptions.MaximumUserNameChars),
            new string('d', CustomL2tpOptions.MaximumDomainChars)).Validate();

        AssertInvalidCustomField(
            CreateCustomOptions(serverOverLimit, "user", "domain"),
            "serverAddress");
        AssertInvalidCustomField(
            CreateCustomOptions(
                "vpn.example.com",
                new string('u', CustomL2tpOptions.MaximumUserNameChars + 1),
                "domain"),
            "userName");
        AssertInvalidCustomField(
            CreateCustomOptions(
                "vpn.example.com",
                "user",
                new string('d', CustomL2tpOptions.MaximumDomainChars + 1)),
            "domain");

        L2tpSettingsDialog.ValidateCustomNativeFieldLengths(
            L2tpConnectionMode.CustomEphemeral,
            useWindowsCredentials: false,
            L2tpIpsecAuthentication.PreSharedKey,
            "vpn.example.com",
            new string('u', CustomL2tpOptions.MaximumUserNameChars),
            new string('d', CustomL2tpOptions.MaximumDomainChars),
            new string('p', CustomL2tpOptions.MaximumPasswordChars),
            new string('k', CustomL2tpOptions.MaximumPreSharedKeyChars));

        AssertEditorNativeFieldRejected(
            password: new string('p', CustomL2tpOptions.MaximumPasswordChars + 1),
            preSharedKey: "psk");
        AssertEditorNativeFieldRejected(
            password: "password",
            preSharedKey: new string('k', CustomL2tpOptions.MaximumPreSharedKeyChars + 1));
    }

    private static void AssertInvalidCustomField(AppOptions options, string expectedField)
    {
        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains(expectedField, StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Oversized custom L2TP {expectedField} escaped loaded-configuration validation.");
    }

    private static void AssertEditorNativeFieldRejected(string password, string preSharedKey)
    {
        try
        {
            L2tpSettingsDialog.ValidateCustomNativeFieldLengths(
                L2tpConnectionMode.CustomEphemeral,
                useWindowsCredentials: false,
                L2tpIpsecAuthentication.PreSharedKey,
                "vpn.example.com",
                "user",
                "domain",
                password,
                preSharedKey);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Oversized custom L2TP plaintext secret escaped the pre-DPAPI editor boundary.");
    }

    private static AppOptions CreateCustomOptions(string serverAddress, string userName, string domain) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-custom-limits",
                    Name = "Custom limits proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18302,
                    VpnConnectionId = "vpn-custom-limits",
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
                    Id = "vpn-custom-limits",
                    Name = "Custom limits VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.CustomEphemeral,
                    MonitorIntervalMilliseconds = 1000,
                    RouteMonitorIntervalMilliseconds = 5000,
                    ReconnectCooldownMilliseconds = 1000,
                    Custom = new CustomL2tpOptions
                    {
                        ServerAddress = serverAddress,
                        UserName = userName,
                        Domain = domain,
                        UseCurrentWindowsCredentials = false,
                        ProtectedPassword = "protected-password",
                        IpsecAuthentication = L2tpIpsecAuthentication.PreSharedKey,
                        ProtectedPreSharedKey = "protected-psk",
                        Encryption = L2tpEncryptionMode.Required,
                        AllowMsChapV2 = true
                    },
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com",
                        ProbeHost = "api.ipify.org",
                        ProbePort = 443,
                        ProbePath = "/",
                        TimeoutSeconds = 5,
                        MaxResponseBytes = VerificationOptions.DefaultResponseLimitBytes
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

    private static AppOptions CreateOptions(
        int maxResponseBytes,
        string probePath = "/",
        string probeHost = "api.ipify.org") =>
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
                        ProbeHost = probeHost,
                        ProbePort = 443,
                        ProbePath = probePath,
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
