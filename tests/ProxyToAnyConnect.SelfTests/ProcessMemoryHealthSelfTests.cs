using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProcessMemoryHealthSelfTests
{
    private const int Cycles = 64;

    public static async Task<int> RunAsync()
    {
        try
        {
            var snapshot = ProcessMemoryHealthMonitor.Capture();
            if (snapshot.ManagedHeapBytes <= 0 || snapshot.WorkingSetBytes <= 0 || snapshot.PrivateBytes <= 0)
            {
                throw new InvalidOperationException(
                    $"Invalid process memory snapshot: heap={snapshot.ManagedHeapBytes}, working={snapshot.WorkingSetBytes}, private={snapshot.PrivateBytes}.");
            }

            var weakMonitors = await CreateRunAndDisposeMonitorsAsync();
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
                $"PASS: process memory monitor captures metrics and has bounded lifecycle retention ({retained} final async/JIT root)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: process memory health monitor regression: {ex}");
            return 1;
        }
    }

    private static async Task<WeakReference[]> CreateRunAndDisposeMonitorsAsync()
    {
        var weakMonitors = new WeakReference[Cycles];

        for (var i = 0; i < Cycles; i++)
        {
            var monitor = new ProcessMemoryHealthMonitor(TimeSpan.FromHours(1));
            weakMonitors[i] = new WeakReference(monitor);

            var current = monitor.Current;
            if (current.TimestampUtc == default)
            {
                throw new InvalidOperationException($"Memory monitor {i} did not publish an initial snapshot.");
            }

            await monitor.DisposeAsync();
        }

        return weakMonitors;
    }
}
