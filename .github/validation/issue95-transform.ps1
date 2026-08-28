$ErrorActionPreference = 'Stop'

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
    $matches = [regex]::Matches($Text, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description anchor, found $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, [System.Text.RegularExpressions.RegexOptions]::Multiline)
}

$resolverPath = 'src/ProxyToAnyConnect/Vpn/VpnInterfaceResolver.cs'
$resolver = Read-Lf $resolverPath
$resolver = Replace-RegexOnce $resolver `
    '^    public static VpnInterfaceInfo ResolveByAddress\(IPAddress localIPv4\)\n    \{\n        foreach \(var networkInterface in NetworkInterface\.GetAllNetworkInterfaces\(\)\)$' `
    @'
    public static VpnInterfaceInfo ResolveByAddress(IPAddress localIPv4)
    {
        ArgumentNullException.ThrowIfNull(localIPv4);
        List<VpnInterfaceInfo>? matches = null;

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
'@ `
    'interface candidate collection start'
$resolver = Replace-RegexOnce $resolver `
    '^            return new VpnInterfaceInfo\(\n                networkInterface\.Name,\n                networkInterface\.Description,\n                ipv4Properties\.Index,\n                dnsServers\);\n        \}\n\n        throw new InvalidOperationException\(\n            \$"Unable to map RAS IPv4 address \{localIPv4\} to a Windows network interface\."\);$' `
    @'
            (matches ??= new List<VpnInterfaceInfo>()).Add(
                new VpnInterfaceInfo(
                    networkInterface.Name,
                    networkInterface.Description,
                    ipv4Properties.Index,
                    dnsServers));
        }

        return SelectUniqueMatch(localIPv4, matches ?? Array.Empty<VpnInterfaceInfo>());
    }

    internal static VpnInterfaceInfo SelectUniqueMatch(
        IPAddress localIPv4,
        IReadOnlyList<VpnInterfaceInfo> matches)
    {
        ArgumentNullException.ThrowIfNull(localIPv4);
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Unable to map RAS IPv4 address {localIPv4} to a Windows network interface.");
        }

        throw new InvalidOperationException(
            $"RAS IPv4 address {localIPv4} is present on multiple Windows network interfaces: " +
            string.Join(", ", matches.Select(match => $"{match.Name} (if={match.InterfaceIndex})")) +
            ". Refusing enumeration-order-dependent interface ownership.");
'@ `
    'unique interface selection'
Write-Lf $resolverPath $resolver

$testsPath = 'tests/ProxyToAnyConnect.SelfTests/NativeRouteSelfTests.cs'
$tests = Read-Lf $testsPath
$tests = Replace-RegexOnce $tests `
    '^            NetworkOrderAddressConversionWorks\(\);\n            await CapturesWindowsDefaultRoutesAsync\(\);$' `
    @'
            NetworkOrderAddressConversionWorks();
            InterfaceMappingMustBeUnique();
            await CapturesWindowsDefaultRoutesAsync();
'@ `
    'unique interface test registration'
$tests = Replace-RegexOnce $tests `
    '^    private static async Task CapturesWindowsDefaultRoutesAsync\(\)$' `
    @'
    private static void InterfaceMappingMustBeUnique()
    {
        var local = IPAddress.Parse("10.23.45.67");
        var first = new VpnInterfaceInfo(
            "ras-a",
            "RAS A",
            42,
            [IPAddress.Parse("10.0.0.53")]);
        var second = new VpnInterfaceInfo(
            "other",
            "Other Adapter",
            77,
            [IPAddress.Parse("10.0.0.54")]);

        var selected = VpnInterfaceResolver.SelectUniqueMatch(local, [first]);
        if (!ReferenceEquals(selected, first))
        {
            throw new InvalidOperationException("Unique PPP IPv4 interface candidate was not preserved exactly.");
        }

        AssertInvalidOperation(() => VpnInterfaceResolver.SelectUniqueMatch(local, []));
        AssertInvalidOperation(() => VpnInterfaceResolver.SelectUniqueMatch(local, [first, second]));
    }

    private static void AssertInvalidOperation(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("Expected ambiguous or missing PPP interface ownership to fail closed.");
    }

    private static async Task CapturesWindowsDefaultRoutesAsync()
'@ `
    'unique interface regression helper'
Write-Lf $testsPath $tests
