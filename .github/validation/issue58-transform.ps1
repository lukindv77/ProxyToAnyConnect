Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path', found $actualCount."
    }

    [IO.File]::WriteAllText(
        $Path,
        $text.Replace($oldNormalized, $newNormalized),
        [Text.UTF8Encoding]::new($false))
}

$app = 'src/ProxyToAnyConnect/Configuration/AppOptions.cs'
$dialog = 'src/ProxyToAnyConnect/Gui/L2tpSettingsDialog.cs'
$phonebook = 'src/ProxyToAnyConnect/Vpn/EphemeralRasPhonebook.cs'
$settingsTests = 'tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs'
$phonebookTests = 'tests/ProxyToAnyConnect.SelfTests/EphemeralRasPhonebookSelfTests.cs'

Replace-Exact $app @'
    private static void ValidateCustomL2tp(string name, CustomL2tpOptions custom)
    {
        if (string.IsNullOrWhiteSpace(custom.ServerAddress) ||
            (!IPAddress.TryParse(custom.ServerAddress, out _) &&
             Uri.CheckHostName(custom.ServerAddress) != UriHostNameType.Dns))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom serverAddress must be an IP address or DNS host name.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.UserName))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom userName is required.");
        }
'@ @'
    private static void ValidateCustomL2tp(string name, CustomL2tpOptions custom)
    {
        if (string.IsNullOrWhiteSpace(custom.ServerAddress) ||
            (!IPAddress.TryParse(custom.ServerAddress, out _) &&
             Uri.CheckHostName(custom.ServerAddress) != UriHostNameType.Dns))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom serverAddress must be an IP address or DNS host name.");
        }

        if (custom.ServerAddress.Length > CustomL2tpOptions.MaximumServerAddressChars)
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' custom serverAddress exceeds the Windows RAS limit of {CustomL2tpOptions.MaximumServerAddressChars} characters.");
        }

        if (!custom.UseCurrentWindowsCredentials && string.IsNullOrWhiteSpace(custom.UserName))
        {
            throw new InvalidOperationException($"L2TP '{name}' custom userName is required.");
        }

        if (!custom.UseCurrentWindowsCredentials &&
            custom.UserName.Length > CustomL2tpOptions.MaximumUserNameChars)
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' custom userName exceeds the Windows RAS limit of {CustomL2tpOptions.MaximumUserNameChars} characters.");
        }

        if (!custom.UseCurrentWindowsCredentials &&
            custom.Domain.Length > CustomL2tpOptions.MaximumDomainChars)
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' custom domain exceeds the Windows RAS limit of {CustomL2tpOptions.MaximumDomainChars} characters.");
        }
'@

Replace-Exact $app @'
internal sealed class CustomL2tpOptions
{
    [JsonPropertyName("serverAddress")]
'@ @'
internal sealed class CustomL2tpOptions
{
    internal const int MaximumServerAddressChars = 128;
    internal const int MaximumUserNameChars = 256;
    internal const int MaximumPasswordChars = 256;
    internal const int MaximumDomainChars = 15;
    internal const int MaximumPreSharedKeyChars = 256;

    [JsonPropertyName("serverAddress")]
'@

Replace-Exact $dialog @'
        var existingProtectedPassword = _existing?.Custom.ProtectedPassword ?? string.Empty;
        var existingProtectedPsk = _existing?.Custom.ProtectedPreSharedKey ?? string.Empty;
        var protectedPassword = ResolveProtectedSecret(
'@ @'
        var serverAddress = _serverAddress.Text.Trim();
        var userName = _userName.Text.Trim();
        var domain = _domain.Text.Trim();
        ValidateCustomNativeFieldLengths(
            mode,
            useWindowsCredentials,
            ipsecAuth,
            serverAddress,
            userName,
            domain,
            _password.Text,
            _preSharedKey.Text);

        var existingProtectedPassword = _existing?.Custom.ProtectedPassword ?? string.Empty;
        var existingProtectedPsk = _existing?.Custom.ProtectedPreSharedKey ?? string.Empty;
        var protectedPassword = ResolveProtectedSecret(
'@

Replace-Exact $dialog @'
                ServerAddress = _serverAddress.Text.Trim(),
                UserName = _userName.Text.Trim(),
                Domain = _domain.Text.Trim(),
'@ @'
                ServerAddress = serverAddress,
                UserName = userName,
                Domain = domain,
'@

Replace-Exact $dialog @'
    internal static string ResolveProtectedSecret(
'@ @'
    internal static void ValidateCustomNativeFieldLengths(
        L2tpConnectionMode mode,
        bool useWindowsCredentials,
        L2tpIpsecAuthentication ipsecAuthentication,
        string serverAddress,
        string userName,
        string domain,
        string enteredPassword,
        string enteredPreSharedKey)
    {
        if (mode != L2tpConnectionMode.CustomEphemeral)
        {
            return;
        }

        EnsureNativeFieldLength(serverAddress, CustomL2tpOptions.MaximumServerAddressChars, "server address");
        if (!useWindowsCredentials)
        {
            EnsureNativeFieldLength(userName, CustomL2tpOptions.MaximumUserNameChars, "user name");
            EnsureNativeFieldLength(domain, CustomL2tpOptions.MaximumDomainChars, "domain");
            if (!string.IsNullOrEmpty(enteredPassword))
            {
                EnsureNativeFieldLength(enteredPassword, CustomL2tpOptions.MaximumPasswordChars, "password");
            }
        }

        if (ipsecAuthentication == L2tpIpsecAuthentication.PreSharedKey &&
            !string.IsNullOrEmpty(enteredPreSharedKey))
        {
            EnsureNativeFieldLength(
                enteredPreSharedKey,
                CustomL2tpOptions.MaximumPreSharedKeyChars,
                "pre-shared key");
        }
    }

    private static void EnsureNativeFieldLength(string value, int maximumChars, string fieldName)
    {
        if (value.Length > maximumChars)
        {
            throw new InvalidOperationException(
                $"Custom L2TP {fieldName} exceeds the Windows RAS limit of {maximumChars} characters.");
        }
    }

    internal static string ResolveProtectedSecret(
'@

Replace-Exact $phonebook @'
        if (options.Mode != L2tpConnectionMode.CustomEphemeral)
        {
            throw new ArgumentException("CustomEphemeral L2TP options are required.", nameof(options));
        }

        CleanupOrphanedSessionDirectories();
'@ @'
        if (options.Mode != L2tpConnectionMode.CustomEphemeral)
        {
            throw new ArgumentException("CustomEphemeral L2TP options are required.", nameof(options));
        }

        ValidateNativeTextFields(options.Custom);
        CleanupOrphanedSessionDirectories();
'@

Replace-Exact $phonebook @'
    public RasNative.RasDialParams CreateDialParams(CustomL2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialParams = new RasNative.RasDialParams
'@ @'
    public RasNative.RasDialParams CreateDialParams(CustomL2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateNativeTextFields(options);

        var dialParams = new RasNative.RasDialParams
'@

Replace-Exact $phonebook @'
            dialParams.SzUserName = options.UserName;
            dialParams.SzDomain = options.Domain;
            dialParams.SzPassword = WindowsSecretProtector.Unprotect(options.ProtectedPassword);
'@ @'
            dialParams.SzUserName = options.UserName;
            dialParams.SzDomain = options.Domain;
            var password = WindowsSecretProtector.Unprotect(options.ProtectedPassword);
            EnsureNativeFieldCapacity(password, CustomL2tpOptions.MaximumPasswordChars, "password");
            dialParams.SzPassword = password;
'@

Replace-Exact $phonebook @'
    private static RasNative.RasEntry BuildEntry(L2tpOptions options)
'@ @'
    internal static void ValidateNativeTextFields(CustomL2tpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureNativeFieldCapacity(
            options.ServerAddress,
            CustomL2tpOptions.MaximumServerAddressChars,
            "server address");
        if (!options.UseCurrentWindowsCredentials)
        {
            EnsureNativeFieldCapacity(
                options.UserName,
                CustomL2tpOptions.MaximumUserNameChars,
                "user name");
            EnsureNativeFieldCapacity(
                options.Domain,
                CustomL2tpOptions.MaximumDomainChars,
                "domain");
        }
    }

    internal static void EnsureNativeFieldCapacity(string value, int maximumChars, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximumChars)
        {
            throw new InvalidOperationException(
                $"Custom L2TP {fieldName} exceeds the Windows RAS limit of {maximumChars} characters.");
        }
    }

    private static RasNative.RasEntry BuildEntry(L2tpOptions options)
'@

Replace-Exact $phonebook @'
        var psk = WindowsSecretProtector.Unprotect(protectedPsk);
        var credentials = new RasNative.RasCredentials
'@ @'
        var psk = WindowsSecretProtector.Unprotect(protectedPsk);
        EnsureNativeFieldCapacity(
            psk,
            CustomL2tpOptions.MaximumPreSharedKeyChars,
            "pre-shared key");
        var credentials = new RasNative.RasCredentials
'@

Replace-Exact $settingsTests @'
            VerificationEditorPreservesWireIdentity();
            InvalidNumericValuesAreRepairable();
'@ @'
            VerificationEditorPreservesWireIdentity();
            CustomL2tpNativeFieldLimitsFailClosed();
            InvalidNumericValuesAreRepairable();
'@

Replace-Exact $settingsTests @'
    private static void InvalidNumericValuesAreRepairable()
'@ @'
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
'@

Replace-Exact $phonebookTests @'
        try
        {
            OrphanRecoveryRespectsCrossProcessOwnership();
'@ @'
        try
        {
            NativeFieldLimitsStaySynchronizedAndExact();
            OrphanRecoveryRespectsCrossProcessOwnership();
'@

Replace-Exact $phonebookTests @'
    private static void OrphanRecoveryRespectsCrossProcessOwnership()
'@ @'
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
'@
