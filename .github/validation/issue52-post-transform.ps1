Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = 'tests/ProxyToAnyConnect.SelfTests/DnsQuerySetupSelfTests.cs'
$text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$helper = 'private static bool ThrowsInvalidHost(Action action)'
$helperIndex = $text.IndexOf($helper, [StringComparison]::Ordinal)
if ($helperIndex -lt 0) {
    throw 'Resolver rejection helper was not produced by the issue52 transform.'
}

$oldCatch = 'catch (InvalidOperationException)'
$catchIndex = $text.IndexOf($oldCatch, $helperIndex, [StringComparison]::Ordinal)
if ($catchIndex -lt 0) {
    throw 'Expected resolver rejection catch was not produced by the issue52 transform.'
}

$nextMethodIndex = $text.IndexOf('private static ', $helperIndex + $helper.Length, [StringComparison]::Ordinal)
if ($nextMethodIndex -ge 0 -and $catchIndex -ge $nextMethodIndex) {
    throw 'Resolver rejection catch escaped the ThrowsInvalidHost helper boundary.'
}

$newCatch = 'catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)'
$updated = $text.Remove($catchIndex, $oldCatch.Length).Insert($catchIndex, $newCatch)
[IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
