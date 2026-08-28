$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Read-Lf([string]$Path) {
    $text = [System.IO.File]::ReadAllText($Path)
    return $text.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Write-Lf([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

function Replace-RegexOnce(
    [string]$Text,
    [string]$Pattern,
    [string]$Replacement,
    [string]$Description) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description anchor, got $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, 1)
}

$path = 'tests/ProxyToAnyConnect.SelfTests/VerificationHttpParserTests.cs'
$text = Read-Lf $path

$rejectPattern = '(?m)^(        AssertRejected\(\n            "HTTP/1\.1 0200 OK\\r\\nContent-Length: 11\\r\\n\\r\\n203\.0\.113\.7",\n            "Non-three-digit status code was accepted\."\);\n)'
$rejectReplacement = @'
$1        AssertRejected(
            "HTTP/1.1 200\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Status line without the required SP after status-code was accepted.");
'@
$text = Replace-RegexOnce $text $rejectPattern $rejectReplacement 'missing status separator regression'

$successPattern = '(?m)^(        if \(Encoding\.ASCII\.GetString\(body\) != "203\.0\.113\.7"\)\n        \{\n            throw new InvalidOperationException\("Successful verification response body was parsed incorrectly\."\);\n        \}\n)'
$successReplacement = @'
$1
        var emptyReason = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 \r\n" +
            "Content-Length: 11\r\n\r\n" +
            "203.0.113.7");
        var emptyReasonBody = VpnConnectivityVerifier.ParseHttpSuccessBody(emptyReason);
        if (Encoding.ASCII.GetString(emptyReasonBody) != "203.0.113.7")
        {
            throw new InvalidOperationException(
                "Status line with the required separator and an empty reason phrase was rejected.");
        }
'@
$text = Replace-RegexOnce $text $successPattern $successReplacement 'empty reason phrase positive regression'
Write-Lf $path $text
