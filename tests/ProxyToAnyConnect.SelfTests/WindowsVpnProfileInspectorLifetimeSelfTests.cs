using System.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class WindowsVpnProfileInspectorLifetimeSelfTests
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: Windows VPN profile helper lifetime test requires Windows.");
            return 0;
        }

        try
        {
            await CallerCancellationTerminatesPowerShellTreeAsync();
            Console.WriteLine(
                "PASS: cancelled Windows VPN profile inspection terminates and drains its PowerShell process tree");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Windows VPN profile helper lifetime regression: {ex}");
            return 1;
        }
    }

    private static async Task CallerCancellationTerminatesPowerShellTreeAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ProxyToAnyConnect-SelfTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidPath = Path.Combine(directory, "powershell-pids.txt");
        var escapedPidPath = pidPath.Replace("'", "''", StringComparison.Ordinal);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $child = Start-Process `
                -FilePath (Join-Path $PSHOME 'powershell.exe') `
                -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') `
                -PassThru
            [System.IO.File]::WriteAllText('{{escapedPidPath}}', "$PID`n$($child.Id)")
            Start-Sleep -Seconds 30
            """;

        using var cancellation = new CancellationTokenSource();
        int[] processIds = [];
        try
        {
            var inspection = WindowsVpnProfileInspector.ExecutePowerShellAsync(
                script,
                "exercise cancellation ownership",
                cancellation.Token);

            processIds = await WaitForProcessIdsAsync(pidPath, TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            try
            {
                _ = await inspection;
                throw new InvalidOperationException(
                    "Cancelled PowerShell inspection unexpectedly completed successfully.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }

            foreach (var processId in processIds)
            {
                if (IsProcessAlive(processId))
                {
                    throw new InvalidOperationException(
                        $"PowerShell process {processId} was still alive after caller cancellation returned.");
                }
            }
        }
        finally
        {
            cancellation.Cancel();
            foreach (var processId in processIds)
            {
                TryKillProcessTree(processId);
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<int[]> WaitForProcessIdsAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path))
                {
                    var lines = await File.ReadAllLinesAsync(path, cancellation.Token);
                    var processIds = lines
                        .Select(line => int.TryParse(line.Trim(), out var processId) ? processId : 0)
                        .Where(processId => processId > 0)
                        .ToArray();
                    if (processIds.Length == 2)
                    {
                        return processIds;
                    }
                }
            }
            catch (IOException)
            {
                // Parent may still be atomically publishing the PID file.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKillProcessTree(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
