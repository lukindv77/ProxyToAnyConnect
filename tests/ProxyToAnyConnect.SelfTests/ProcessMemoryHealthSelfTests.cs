using System.Diagnostics;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProcessMemoryHealthSelfTests
{
    private const int Cycles = 64;

    public static async Task<int> RunAsync()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var expectedStartUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

            var snapshot = ProcessMemoryHealthMonitor.Capture();
            if (snapshot.ManagedHeapBytes <= 0 || snapshot.WorkingSetBytes <= 0 || snapshot.PrivateBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid process memory snapshot: heap={snapshot.ManagedHeapBytes}, working={snapshot.WorkingSetBytes}, private={snapshot.PrivateBytes}.");
            }

            if (snapshot.ProcessId != Environment.ProcessId || snapshot.ProcessStartTimeUtc != expectedStartUtc)
            {
                throw new InvalidOperationException(
                    $"Memory snapshot process identity mismatch: pid={snapshot.ProcessId}/{Environment.ProcessId}, " +
                    $"start={snapshot.ProcessStartTimeUtc:O}/{expectedStartUtc:O}.");
            }

            var weakMonitors = await CreateRunAndDisposeMonitorsAsync(expectedStartUtc);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var retained = weakMonitors.Count(reference => reference.IsAlive);
            if (retained > 1)
            {
                throw new InvalidOperationException(
                    $"{retained} of {weakMonitors.Length} disposed ProcessMemoryHealthMonitor instances remained strongly reachable; expected at most one fixed async/JIT root.");
            }

            Console.WriteLine(
                $"PASS: process memory monitor captures exact process identity/metrics and has bounded lifecycle retention ({retained} final async/JIT root)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: process memory health monitor regression: {ex}");
            return 1;
        }
    }

    private static async Task<WeakReference[]> CreateRunAndDisposeMonitorsAsync(
        DateTimeOffset expectedStartUtc)
    {
        var weakMonitors = new WeakReference[Cycles];

        for (var i = 0; i < Cycles; i++)
        {
            var monitor = new ProcessMemoryHealthMonitor(TimeSpan.FromHours(1));
            weakMonitors[i] = new WeakReference(monitor);

            var current = monitor.Current;
            if (current.TimestampUtc == default ||
                current.ProcessId != Environment.ProcessId ||
                current.ProcessStartTimeUtc != expectedStartUtc)
            {
                throw new InvalidOperationException(
                    $"Memory monitor {i} did not publish a correctly process-bound initial snapshot.");
            }

            await monitor.DisposeAsync();
        }

        return weakMonitors;
    }
}
