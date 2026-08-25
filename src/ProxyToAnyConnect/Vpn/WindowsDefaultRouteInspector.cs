using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ProxyToAnyConnect.Vpn;

internal sealed class WindowsDefaultRouteInspector
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromSeconds(10);

    public async Task<DefaultRouteSnapshot> CaptureIPv4Async(CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $routes = @(
                Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
                    ForEach-Object {
                        [pscustomobject]@{
                            InterfaceIndex = [uint32]$_.ifIndex
                            NextHop = [string]$_.NextHop
                            RouteMetric = [uint32]$_.RouteMetric
                            PolicyStore = [string]$_.PolicyStore
                        }
                    } |
                    Sort-Object InterfaceIndex, NextHop, RouteMetric, PolicyStore
            )
            ConvertTo-Json -InputObject $routes -Compress
            """;

        var json = await RunPowerShellAsync(script, cancellationToken);

        try
        {
            var routes = JsonSerializer.Deserialize<List<DefaultRouteEntry>>(json)
                ?? [];
            return new DefaultRouteSnapshot(routes);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse the Windows IPv4 default-route snapshot. Output: {json}",
                ex);
        }
    }

    public static void EnsureUnchanged(DefaultRouteSnapshot before, DefaultRouteSnapshot after)
    {
        if (before.Routes.SequenceEqual(after.Routes))
        {
            return;
        }

        throw new InvalidOperationException(
            "Windows IPv4 default routes changed while establishing L2TP. " +
            $"Before: {before}; After: {after}. The new VPN connection will be rejected.");
    }

    private static async Task<string> RunPowerShellAsync(
        string script,
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
            throw new InvalidOperationException("Unable to start Windows PowerShell to inspect default routes.");
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
            throw new TimeoutException("Timed out while reading Windows IPv4 default routes.");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to inspect Windows IPv4 default routes. PowerShell exit code {process.ExitCode}: {stderr}");
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

internal sealed record DefaultRouteEntry(
    uint InterfaceIndex,
    string NextHop,
    uint RouteMetric,
    string PolicyStore);

internal sealed record DefaultRouteSnapshot(IReadOnlyList<DefaultRouteEntry> Routes)
{
    public override string ToString() =>
        Routes.Count == 0
            ? "<none>"
            : string.Join(
                "; ",
                Routes.Select(route =>
                    $"if={route.InterfaceIndex},next={route.NextHop},metric={route.RouteMetric},store={route.PolicyStore}"));
}
