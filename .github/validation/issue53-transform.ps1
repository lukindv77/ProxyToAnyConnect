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

$dialog = 'src/ProxyToAnyConnect/Gui/L2tpSettingsDialog.cs'
$tests = 'tests/ProxyToAnyConnect.SelfTests/SettingsValidationSelfTests.cs'

Replace-Exact $dialog @'
            Verification = new VerificationOptions
            {
                PublicAddress = _publicAddress.Text.Trim(),
                ProbeHost = _probeHost.Text.Trim(),
                ProbePort = decimal.ToInt32(_probePort.Value),
                ProbePath = _probePath.Text.Trim(),
                TimeoutSeconds = decimal.ToInt32(_verificationTimeout.Value),
                MaxResponseBytes = decimal.ToInt32(_verificationMaxResponse.Value)
            },
'@ @'
            Verification = CreateVerificationOptions(
                _publicAddress.Text.Trim(),
                _probeHost.Text,
                decimal.ToInt32(_probePort.Value),
                _probePath.Text,
                decimal.ToInt32(_verificationTimeout.Value),
                decimal.ToInt32(_verificationMaxResponse.Value)),
'@

Replace-Exact $dialog @'
    internal static string ResolveProtectedSecret(
'@ @'
    internal static VerificationOptions CreateVerificationOptions(
        string publicAddress,
        string probeHost,
        int probePort,
        string probePath,
        int timeoutSeconds,
        int maxResponseBytes) =>
        new()
        {
            PublicAddress = publicAddress,
            ProbeHost = probeHost,
            ProbePort = probePort,
            ProbePath = probePath,
            TimeoutSeconds = timeoutSeconds,
            MaxResponseBytes = maxResponseBytes
        };

    internal static string ResolveProtectedSecret(
'@

Replace-Exact $tests @'
            VerificationProbeHostUsesCanonicalIdnAuthority();
            InvalidNumericValuesAreRepairable();
'@ @'
            VerificationProbeHostUsesCanonicalIdnAuthority();
            VerificationEditorPreservesWireIdentity();
            InvalidNumericValuesAreRepairable();
'@

Replace-Exact $tests @'
    private static void InvalidNumericValuesAreRepairable()
'@ @'
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

    private static void InvalidNumericValuesAreRepairable()
'@
