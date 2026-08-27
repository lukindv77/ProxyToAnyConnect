using System.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class WindowsVpnProfileInspectorLifetimeSelfTests
{
    private static readonly TimeSpan HelperStartupObservationTimeout = TimeSpan.FromSeconds(15);

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
                -FilePath (Join-Path $env:SystemRoot 'System32\ping.exe') `
                -ArgumentList @('-n','31','127.0.0.1') `
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

            // Hosted Windows images can have highly variable cold PowerShell/process
            // startup latency. Give startup observation its own generous deadline, but
            // do not weaken the actual ownership assertion below: once cancellation
            // returns, every observed parent/child PID must already be gone.
            processIds = await WaitForProcessIdsAsync(
                pidPath,
                inspection,
                HelperStartupObservationTimeout);
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

    private static async Task<int[]> WaitForProcessIdsAsync(
        string path,
        Task inspection,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency);
        Exception? lastReadFailure = null;

        while (Stopwatch.GetTimestamp() < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    var lines = await File.ReadAllLinesAsync(path);
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
            catch (IOException ex)
            {
                // Parent may still be atomically publishing the PID file.
                lastReadFailure = ex;
            }

            if (inspection.IsCompleted)
            {
                try
                {
                    await inspection;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Windows VPN profile helper completed/faulted before publishing its parent/child PID evidence.",
                        ex);
                }

                throw new InvalidOperationException(
                    "Windows VPN profile helper completed before publishing its parent/child PID evidence.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        var diagnostic = lastReadFailure is null
            ? string.Empty
            : $" Last PID-file read error: {lastReadFailure.Message}";
        throw new TimeoutException(
            $"Windows VPN profile helper did not publish two process IDs within {timeout.TotalSeconds:F0} seconds. " +
            $"PID file exists={File.Exists(path)}.{diagnostic}");
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
