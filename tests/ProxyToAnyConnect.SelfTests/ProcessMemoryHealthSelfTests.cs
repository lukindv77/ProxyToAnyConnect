using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProcessMemoryHealthSelfTests
{
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

            var weak = await CreateRunAndDisposeMonitorAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (weak.IsAlive)
            {
                throw new InvalidOperationException("Disposed ProcessMemoryHealthMonitor remained strongly reachable.");
            }

            Console.WriteLine("PASS: process memory health monitor captures metrics and releases timer/task ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: process memory health monitor regression: {ex}");
            return 1;
        }
    }

    private static async Task<WeakReference> CreateRunAndDisposeMonitorAsync()
    {
        var monitor = new ProcessMemoryHealthMonitor(TimeSpan.FromMilliseconds(10));
        var weak = new WeakReference(monitor);
        await Task.Delay(35);

        var current = monitor.Current;
        if (current.TimestampUtc == default)
        {
            throw new InvalidOperationException("Memory monitor did not publish a current snapshot.");
        }

        await monitor.DisposeAsync();
        return weak;
    }
}
