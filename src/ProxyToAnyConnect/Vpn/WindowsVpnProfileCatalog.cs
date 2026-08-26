using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProxyToAnyConnect.Vpn;

internal sealed class WindowsVpnProfileCatalog
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<VpnProfileInfo>> ListL2tpAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $profiles = @()
            $profiles += @(Get-VpnConnection -ErrorAction SilentlyContinue)
            $profiles += @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue)
            @($profiles |
                Where-Object { [string]$_.TunnelType -eq 'L2tp' } |
                ForEach-Object {
                    [pscustomobject]@{
                        Name = [string]$_.Name
                        TunnelType = [string]$_.TunnelType
                        SplitTunneling = [bool]$_.SplitTunneling
                        AllUserConnection = [bool]$_.AllUserConnection
                    }
                } |
                Sort-Object Name, AllUserConnection -Unique) | ConvertTo-Json -Compress
            """;

        var output = await RunPowerShellAsync(script, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var profiles = new List<VpnProfileInfo>();
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    var profile = element.Deserialize<VpnProfileInfo>();
                    if (profile is not null)
                    {
                        profiles.Add(profile);
                    }
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var profile = document.RootElement.Deserialize<VpnProfileInfo>();
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }

            return profiles;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse Windows VPN profile list. Output: {output}",
                ex);
        }
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

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
            throw new InvalidOperationException("Unable to start Windows PowerShell to enumerate VPN profiles.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InspectionTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new TimeoutException("Timed out while enumerating Windows VPN profiles.");
        }

        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to enumerate Windows VPN profiles. PowerShell exit code {process.ExitCode}: {stderr}");
        }

        return (await stdoutTask).Trim();
    }
}
