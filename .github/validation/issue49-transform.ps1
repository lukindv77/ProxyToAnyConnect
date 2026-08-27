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

    $updated = $text.Replace($oldNormalized, $newNormalized)
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$appOptions = 'src/ProxyToAnyConnect/Configuration/AppOptions.cs'
$verifier = 'src/ProxyToAnyConnect/Vpn/VpnConnectivityVerifier.cs'
$settingsTests = 'tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs'
$requestTests = 'tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs'

Replace-Exact $appOptions @'
using System.Net;
'@ @'
using System.Globalization;
using System.Net;
'@

Replace-Exact $appOptions @'
        if (string.IsNullOrWhiteSpace(verification.ProbeHost) ||
            Uri.CheckHostName(verification.ProbeHost) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.probeHost must be a DNS host name.");
        }
'@ @'
        if (!VerificationOptions.TryGetCanonicalProbeHost(verification.ProbeHost, out _))
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' verification.probeHost must be a valid DNS host name that can be canonicalized with IDNA.");
        }
'@

Replace-Exact $appOptions @'
        if (string.IsNullOrWhiteSpace(verification.ProbePath) ||
            !verification.ProbePath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"L2TP '{name}' verification.probePath must start with '/'.");
        }
'@ @'
        if (!VerificationOptions.IsValidProbePath(verification.ProbePath))
        {
            throw new InvalidOperationException(
                $"L2TP '{name}' verification.probePath must be an ASCII HTTP origin-form request-target without a fragment.");
        }
'@

Replace-Exact $appOptions @'
    [JsonPropertyName("maxResponseBytes")]
    public int MaxResponseBytes { get; init; } = DefaultResponseLimitBytes;
}
'@ @'
    [JsonPropertyName("maxResponseBytes")]
    public int MaxResponseBytes { get; init; } = DefaultResponseLimitBytes;

    internal static bool TryGetCanonicalProbeHost(string? value, out string canonicalHost)
    {
        canonicalHost = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var isAscii = true;
        foreach (var current in value)
        {
            if (current > 0x7f)
            {
                isAscii = false;
                break;
            }
        }

        if (isAscii)
        {
            if (Uri.CheckHostName(value) != UriHostNameType.Dns)
            {
                return false;
            }

            canonicalHost = value;
            return true;
        }

        try
        {
            canonicalHost = new IdnMapping
            {
                UseStd3AsciiRules = true
            }.GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            canonicalHost = string.Empty;
            return false;
        }

        if (Uri.CheckHostName(canonicalHost) != UriHostNameType.Dns ||
            canonicalHost.Any(character => character > 0x7f))
        {
            canonicalHost = string.Empty;
            return false;
        }

        return true;
    }

    internal static bool IsValidProbePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '/')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is '/' or '?')
            {
                continue;
            }

            if (current == '%')
            {
                if (index > value.Length - 3 ||
                    !IsAsciiHexDigit(value[index + 1]) ||
                    !IsAsciiHexDigit(value[index + 2]))
                {
                    return false;
                }

                index += 2;
                continue;
            }

            if (!IsAsciiPchar(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiPchar(char value) =>
        (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z') ||
        (value >= '0' && value <= '9') ||
        value is '-' or '.' or '_' or '~' or
            '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '=' or ':' or '@';

    private static bool IsAsciiHexDigit(char value) =>
        (value >= '0' && value <= '9') ||
        (value >= 'A' && value <= 'F') ||
        (value >= 'a' && value <= 'f');
}
'@

Replace-Exact $verifier @'
    private readonly VerificationOptions _options;
    private readonly L2tpDnsResolver _dnsResolver;

    public VpnConnectivityVerifier(VerificationOptions options)
    {
        _options = options;
        _dnsResolver = new L2tpDnsResolver();
    }
'@ @'
    private readonly VerificationOptions _options;
    private readonly L2tpDnsResolver _dnsResolver;
    private readonly string _probeHost;

    public VpnConnectivityVerifier(VerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!VerificationOptions.TryGetCanonicalProbeHost(options.ProbeHost, out _probeHost))
        {
            throw new ArgumentException(
                "Verification probe host is not a valid IDNA-canonicalizable DNS host name.",
                nameof(options));
        }

        _options = options;
        _dnsResolver = new L2tpDnsResolver();
    }
'@

Replace-Exact $verifier '_options.ProbeHost' '_probeHost' 4

Replace-Exact $verifier @'
        host ??= string.Empty;
        path ??= string.Empty;

        ReadOnlySpan<byte> requestPrefix = "GET "u8;
'@ @'
        host ??= string.Empty;
        path ??= string.Empty;

        if (!VerificationOptions.TryGetCanonicalProbeHost(host, out var canonicalHost))
        {
            throw new ArgumentException(
                "Verification probe host must be a valid IDNA-canonicalizable DNS host name.",
                nameof(host));
        }

        if (!VerificationOptions.IsValidProbePath(path))
        {
            throw new ArgumentException(
                "Verification probe path must be an ASCII HTTP origin-form request-target without a fragment.",
                nameof(path));
        }

        ReadOnlySpan<byte> requestPrefix = "GET "u8;
'@

Replace-Exact $verifier 'var hostByteCount = Encoding.ASCII.GetByteCount(host);' 'var hostByteCount = Encoding.ASCII.GetByteCount(canonicalHost);'
Replace-Exact $verifier 'offset += Encoding.ASCII.GetBytes(host.AsSpan(), destination[offset..]);' 'offset += Encoding.ASCII.GetBytes(canonicalHost.AsSpan(), destination[offset..]);'

Replace-Exact $settingsTests @'
            VerificationResponseLimitIsBounded();
            InvalidNumericValuesAreRepairable();
            UnusedProtectedSecretsAreDropped();

            Console.WriteLine(
                "PASS: settings enforce bounded verification responses, repair numeric values and drop unused secrets");
'@ @'
            VerificationResponseLimitIsBounded();
            VerificationProbePathIsWireExactOriginForm();
            VerificationProbeHostUsesCanonicalIdnAuthority();
            InvalidNumericValuesAreRepairable();
            UnusedProtectedSecretsAreDropped();

            Console.WriteLine(
                "PASS: settings enforce bounded verification responses, byte-exact probe targets, canonical IDN hosts, repair numeric values and drop unused secrets");
'@

Replace-Exact $settingsTests @'
    private static void InvalidNumericValuesAreRepairable()
'@ @'
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
        CreateOptions(VerificationOptions.DefaultResponseLimitBytes, probeHost: "münich.example").Validate();

        if (!VerificationOptions.TryGetCanonicalProbeHost("münich.example", out var canonical) ||
            !canonical.Equals("xn--mnich-kva.example", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Unicode verification host did not canonicalize to the expected IDNA A-label: '{canonical}'.");
        }

        foreach (var invalid in new[]
                 {
                     string.Empty,
                     "bad host.example",
                     "line\r\nhost.example",
                     "bad_.example",
                     "[2001:db8::1]"
                 })
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
                $"verification.probeHost '{EscapeForDiagnostic(invalid)}' escaped canonical DNS-host validation.");
        }
    }

    private static string EscapeForDiagnostic(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void InvalidNumericValuesAreRepairable()
'@

Replace-Exact $settingsTests @'
    private static AppOptions CreateOptions(int maxResponseBytes) =>
'@ @'
    private static AppOptions CreateOptions(
        int maxResponseBytes,
        string probePath = "/",
        string probeHost = "api.ipify.org") =>
'@

Replace-Exact $settingsTests 'ProbeHost = "api.ipify.org",' 'ProbeHost = probeHost,'
Replace-Exact $settingsTests 'ProbePath = "/",' 'ProbePath = probePath,'

Replace-Exact $requestTests @'
            RequestWireBytesRemainEquivalent();

            for (var i = 0; i < WarmupIterations; i++)
'@ @'
            RequestWireBytesRemainEquivalent();
            IdnHostIsCanonicalizedOnWire();
            InvalidRequestTargetsAreRejectedByBuilder();
            InvalidHostsAreRejectedByBuilder();

            for (var i = 0; i < WarmupIterations; i++)
'@

Replace-Exact $requestTests @'
            (RepresentativeHost, RepresentativePath),
            ("example.com", "/"),
            (string.Empty, string.Empty),
            (null, null),
            ("münich.example", "/café/😀")
'@ @'
            (RepresentativeHost, RepresentativePath),
            ("example.com", "/"),
            ("example.com", "/ip/check?x=1%202"),
            ("example.com", "/caf%C3%A9?next=%2Fok&flag=true")
'@

Replace-Exact $requestTests @'
    private static byte[] LegacyBuildProbeRequest(string? host, string? path)
'@ @'
    private static void IdnHostIsCanonicalizedOnWire()
    {
        var request = Encoding.ASCII.GetString(
            VpnConnectivityVerifier.BuildProbeRequest("münich.example", "/?format=text"));
        if (!request.Contains("\r\nHost: xn--mnich-kva.example\r\n", StringComparison.Ordinal) ||
            request.Contains("m?nich", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Verification request did not emit the canonical IDNA Host authority: {request}");
        }
    }

    private static void InvalidRequestTargetsAreRejectedByBuilder()
    {
        foreach (var invalid in new string?[]
                 {
                     null,
                     string.Empty,
                     "relative",
                     "/contains space",
                     "/line\r\nHost: injected.example",
                     "/café",
                     "/fragment#value",
                     "/bad%2",
                     "/bad%GG"
                 })
        {
            try
            {
                _ = VpnConnectivityVerifier.BuildProbeRequest("example.com", invalid);
            }
            catch (ArgumentException ex) when (ex.ParamName == "path")
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Verification request builder accepted unsafe/lossy target '{EscapeForDiagnostic(invalid)}'.");
        }
    }

    private static void InvalidHostsAreRejectedByBuilder()
    {
        foreach (var invalid in new string?[]
                 {
                     null,
                     string.Empty,
                     "bad host.example",
                     "line\r\nhost.example",
                     "bad_.example"
                 })
        {
            try
            {
                _ = VpnConnectivityVerifier.BuildProbeRequest(invalid, "/");
            }
            catch (ArgumentException ex) when (ex.ParamName == "host")
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Verification request builder accepted invalid host '{EscapeForDiagnostic(invalid)}'.");
        }
    }

    private static string EscapeForDiagnostic(string? value) =>
        value is null
            ? "<null>"
            : value.Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);

    private static byte[] LegacyBuildProbeRequest(string? host, string? path)
'@

Replace-Exact $requestTests @'
    {
        return Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
'@ @'
    {
        host ??= string.Empty;
        path ??= string.Empty;
        if (!VerificationOptions.TryGetCanonicalProbeHost(host, out var canonicalHost))
        {
            throw new ArgumentException("Invalid verification host.", nameof(host));
        }
        if (!VerificationOptions.IsValidProbePath(path))
        {
            throw new ArgumentException("Invalid verification target.", nameof(path));
        }

        return Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {canonicalHost}\r\n" +
'@
