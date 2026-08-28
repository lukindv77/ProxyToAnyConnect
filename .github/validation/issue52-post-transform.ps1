Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = 'tests/ProxyToAnyConnect.SelfTests/DnsQuerySetupSelfTests.cs'
$text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$old = @'
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
'@.TrimEnd("`r", "`n")
$new = @'
    private static bool ThrowsInvalidHost(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return true;
        }
    }
'@.TrimEnd("`r", "`n")
$count = [regex]::Matches($text, [regex]::Escape($old)).Count
if ($count -ne 1) {
    throw "Expected one resolver rejection helper target, found $count."
}
[IO.File]::WriteAllText($path, $text.Replace($old, $new), [Text.UTF8Encoding]::new($false))
