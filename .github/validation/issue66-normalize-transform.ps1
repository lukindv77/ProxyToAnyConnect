Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$path = '.github/validation/issue66-transform.ps1'
$text = [IO.File]::ReadAllText($path)
$old = '    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)'
$new = @'
    $Old = $Old.Replace("`r`n", "`n")
    $New = $New.Replace("`r`n", "`n")
    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
'@.TrimEnd("`r", "`n")

$first = $text.IndexOf($old, [StringComparison]::Ordinal)
if ($first -lt 0 -or $text.IndexOf($old, $first + $old.Length, [StringComparison]::Ordinal) -ge 0) {
    throw 'Unable to patch the unique Replace-Exact anchor normalizer.'
}

$text = $text.Substring(0, $first) + $new + $text.Substring($first + $old.Length)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
