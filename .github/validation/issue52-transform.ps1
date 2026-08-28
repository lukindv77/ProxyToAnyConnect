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
$dnsResolver = 'src/ProxyToAnyConnect/Network/L2tpDnsResolver.cs'
$settingsTests = 'tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs'
$requestTests = 'tests/ProxyToAnyConnect.SelfTests/VerificationProbeRequestSelfTests.cs'
$dnsQueryTests = 'tests/ProxyToAnyConnect.SelfTests/DnsQuerySetupSelfTests.cs'

Replace-Exact $appOptions @'
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
            if (!IsValidAsciiDnsHost(value))
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

        if (!IsValidAsciiDnsHost(canonicalHost))
        {
            canonicalHost = string.Empty;
            return false;
        }

        return true;
    }
'@ @'
    internal static bool TryGetCanonicalProbeHost(string? value, out string canonicalHost)
    {
        canonicalHost = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var canonicalLiteral = literal.ToString();
            if (!value.Equals(canonicalLiteral, StringComparison.Ordinal))
            {
                return false;
            }

            canonicalHost = canonicalLiteral;
            return true;
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
            if (!IsValidAsciiDnsHost(value))
            {
                return false;
            }

            canonicalHost = value.ToLowerInvariant();
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

        if (!IsValidAsciiDnsHost(canonicalHost))
        {
            canonicalHost = string.Empty;
            return false;
        }

        return true;
    }
'@

Replace-Exact $appOptions @'
            throw new InvalidOperationException(
                $"L2TP '{name}' verification.probeHost must be a valid DNS host name that can be canonicalized with IDNA.");
'@ @'
            throw new InvalidOperationException(
                $"L2TP '{name}' verification.probeHost must be a canonical IPv4 literal or a valid DNS host name that can be canonicalized with IDNA.");
'@

Replace-Exact $dnsResolver @'
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("IPv6 targets are not supported yet.");
            }

            return [literal];
        }
'@ @'
        var literal = ParseCanonicalIPv4Literal(host);
        if (literal is not null)
        {
            return [literal];
        }
'@

Replace-Exact $dnsResolver @'
    private async Task<DnsResolutionResult> QueryAsync(
'@ @'
    internal static IPAddress? ParseCanonicalIPv4Literal(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!IPAddress.TryParse(host, out var literal))
        {
            return null;
        }

        if (literal.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("IPv6 targets are not supported yet.");
        }

        var canonicalLiteral = literal.ToString();
        if (!host.Equals(canonicalLiteral, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IPv4 literal '{host}' is not in canonical dotted-decimal form.");
        }

        return literal;
    }

    private async Task<DnsResolutionResult> QueryAsync(
'@

Replace-Exact $dnsResolver @'
    private static string NormalizeDnsName(string host) =>
        NormalizeDnsHostStrict(host.Trim());
'@ @'
    internal static string NormalizeDnsName(string host) =>
        NormalizeDnsHostStrict(host);
'@

Replace-Exact $settingsTests @'
using ProxyToAnyConnect.Configuration;
'@ @'
using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Configuration;
'@

Replace-Exact $settingsTests @'
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
'@ @'
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
'@

Replace-Exact $requestTests @'
using System.Diagnostics;
using System.Text;
'@ @'
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
'@

Replace-Exact $requestTests @'
            IdnHostIsCanonicalizedOnWire();
'@ @'
            HostAuthorityIsCanonicalizedOnWire();
'@

Replace-Exact $requestTests @'
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
'@ @'
    private static void HostAuthorityIsCanonicalizedOnWire()
    {
        var idnRequest = Encoding.ASCII.GetString(
            VpnConnectivityVerifier.BuildProbeRequest("münich.example", "/?format=text"));
        if (!idnRequest.Contains("\r\nHost: xn--mnich-kva.example\r\n", StringComparison.Ordinal) ||
            idnRequest.Contains("m?nich", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Verification request did not emit the canonical IDNA Host authority: {idnRequest}");
        }

        var asciiRequest = Encoding.ASCII.GetString(
            VpnConnectivityVerifier.BuildProbeRequest("API.IPIFY.ORG", "/"));
        if (!asciiRequest.Contains("\r\nHost: api.ipify.org\r\n", StringComparison.Ordinal) ||
            asciiRequest.Contains("Host: API.IPIFY.ORG", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Verification request did not emit the canonical lowercase ASCII authority: {asciiRequest}");
        }

        var ipv4Request = Encoding.ASCII.GetString(
            VpnConnectivityVerifier.BuildProbeRequest("127.0.0.1", "/"));
        if (!ipv4Request.Contains("\r\nHost: 127.0.0.1\r\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Verification request did not preserve the canonical IPv4 authority: {ipv4Request}");
        }
    }
'@

Replace-Exact $requestTests @'
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
'@ @'
    private static void InvalidHostsAreRejectedByBuilder()
    {
        var invalidHosts = new List<string?>
        {
            null,
            string.Empty,
            "bad host.example",
            "line\r\nhost.example",
            "bad_.example"
        };
        invalidHosts.AddRange(DetectRuntimeLegacyIpv4Forms());

        foreach (var invalid in invalidHosts)
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
                "The current Windows/.NET runtime did not recognize any legacy IPv4 form from the request-builder regression matrix.");
        }

        return detected;
    }
'@

Replace-Exact $dnsQueryTests @'
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
'@ @'
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
'@

Replace-Exact $dnsQueryTests @'
            QueryWireFormatAndValidationMatchCurrentSemantics();
'@ @'
            QueryWireFormatAndValidationMatchCurrentSemantics();
            ResolverAuthorityNormalizationIsFailClosed();
'@

Replace-Exact $dnsQueryTests @'
    private static void AssertBothReject(string host)
'@ @'
    private static void ResolverAuthorityNormalizationIsFailClosed()
    {
        if (!L2tpDnsResolver.NormalizeDnsName("EXAMPLE.COM").Equals("example.com", StringComparison.Ordinal) ||
            !L2tpDnsResolver.NormalizeDnsName("münich.example").Equals("xn--mnich-kva.example", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolver DNS-name normalization did not preserve canonical IDNA/lowercase semantics.");
        }

        foreach (var invalid in new[]
                 {
                     " edge.example",
                     "edge.example ",
                     "\tedge.example",
                     "edge.example\t",
                     "edge.example\r"
                 })
        {
            if (!ThrowsInvalidHost(() => L2tpDnsResolver.NormalizeDnsName(invalid)))
            {
                throw new InvalidOperationException(
                    $"Resolver silently normalized whitespace/control-bearing DNS identity '{EscapeForDiagnostic(invalid)}'.");
            }
        }

        var canonicalLiteral = L2tpDnsResolver.ParseCanonicalIPv4Literal("127.0.0.1");
        if (canonicalLiteral is null || !canonicalLiteral.Equals(IPAddress.Loopback))
        {
            throw new InvalidOperationException("Resolver did not preserve the canonical IPv4 literal boundary.");
        }

        if (L2tpDnsResolver.ParseCanonicalIPv4Literal("example.com") is not null)
        {
            throw new InvalidOperationException("Resolver misclassified a DNS name as an IPv4 literal.");
        }

        foreach (var legacy in DetectRuntimeLegacyIpv4Forms())
        {
            if (!ThrowsInvalidHost(() => L2tpDnsResolver.ParseCanonicalIPv4Literal(legacy)))
            {
                throw new InvalidOperationException(
                    $"Resolver accepted runtime-recognized non-canonical IPv4 identity '{legacy}'.");
            }
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
                "The current Windows/.NET runtime did not recognize any legacy IPv4 form from the DNS resolver regression matrix.");
        }

        return detected;
    }

    private static bool ThrowsInvalidHost(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string EscapeForDiagnostic(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void AssertBothReject(string host)
'@
