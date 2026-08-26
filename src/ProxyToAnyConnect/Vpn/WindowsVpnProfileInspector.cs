using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProxyToAnyConnect.Vpn;

internal sealed class WindowsVpnProfileInspector
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);

    public async Task<VpnProfileInfo> InspectAsync(string entryName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var escapedName = entryName.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $name = '{{escapedName}}'
            $profile = Get-VpnConnection -Name $name -ErrorAction SilentlyContinue
            if ($null -eq $profile) {
                $profile = Get-VpnConnection -Name $name -AllUserConnection -ErrorAction SilentlyContinue
            }
            if ($null -eq $profile) {
                throw "VPN profile '$name' was not found."
            }
            [pscustomobject]@{
                Name = [string]$profile.Name
                TunnelType = [string]$profile.TunnelType
                SplitTunneling = [bool]$profile.SplitTunneling
                AllUserConnection = [bool]$profile.AllUserConnection
            } | ConvertTo-Json -Compress
            """;

        var stdout = await ExecutePowerShellAsync(script, $"inspect VPN profile '{entryName}'", cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<VpnProfileInfo>(stdout)
                ?? throw new InvalidOperationException("VPN profile inspection returned an empty result.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse VPN profile information for '{entryName}'. Output: {stdout}",
                ex);
        }
    }

    public async Task<IReadOnlyList<VpnProfileInfo>> ListL2tpProfilesAsync(CancellationToken cancellationToken)
    {
        var script = """
            $ErrorActionPreference = 'Stop'
            $profiles = @()
            $profiles += @(Get-VpnConnection -ErrorAction SilentlyContinue)
            $profiles += @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue)
            $result = @(
                $profiles |
                    Where-Object { [string]$_.TunnelType -eq 'L2tp' } |
                    ForEach-Object {
                        [pscustomobject]@{
                            Name = [string]$_.Name
                            TunnelType = [string]$_.TunnelType
                            SplitTunneling = [bool]$_.SplitTunneling
                            AllUserConnection = [bool]$_.AllUserConnection
                        }
                    } |
                    Sort-Object Name, AllUserConnection -Unique
            )
            ConvertTo-Json -InputObject $result -Compress
            """;

        var stdout = await ExecutePowerShellAsync(script, "enumerate Windows L2TP profiles", cancellationToken);
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<VpnProfileInfo>>(stdout) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse Windows L2TP profile list. Output: {stdout}",
                ex);
        }
    }

    public static void ValidateForProxy(VpnProfileInfo profile)
    {
        if (!profile.TunnelType.Equals("L2tp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"VPN profile '{profile.Name}' uses tunnel type '{profile.TunnelType}'. L2TP is required.");
        }

        if (!profile.SplitTunneling)
        {
            throw new InvalidOperationException(
                $"VPN profile '{profile.Name}' is configured as full-tunnel. Enable split tunneling before using ProxyToAnyConnect.");
        }
    }

    public static string? ResolveRasPhoneBook(VpnProfileInfo profile)
    {
        if (!profile.AllUserConnection)
        {
            // NULL lets RAS use the current user's default phone book.
            return null;
        }

        var commonApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(commonApplicationData))
        {
            throw new InvalidOperationException("Windows CommonApplicationData path is unavailable.");
        }

        var phoneBook = Path.Combine(
            commonApplicationData,
            "Microsoft",
            "Network",
            "Connections",
            "Pbk",
            "rasphone.pbk");

        if (!File.Exists(phoneBook))
        {
            throw new InvalidOperationException(
                $"Global RAS phone book for AllUserConnection was not found at '{phoneBook}'.");
        }

        return phoneBook;
    }

    private static async Task<string> ExecutePowerShellAsync(
        string script,
        string operation,
        CancellationToken cancellationToken)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        if (!File.Exists(powershellPath))
        {
            throw new InvalidOperationException($"Windows PowerShell was not found at '{powershellPath}'.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedCommand);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start Windows PowerShell to {operation}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InspectionTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Timed out while attempting to {operation}.");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to {operation}. PowerShell exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exited between HasExited and Kill.
        }
    }
}

internal sealed record VpnProfileInfo(
    string Name,
    string TunnelType,
    bool SplitTunneling,
    bool AllUserConnection)
{
    public string DisplayName =>
        $"{Name} [{(AllUserConnection ? "All users" : "Current user")}]" +
        (SplitTunneling ? string.Empty : " — full tunnel");
}
