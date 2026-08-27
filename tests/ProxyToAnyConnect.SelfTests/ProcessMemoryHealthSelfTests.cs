using System.Diagnostics;
using System.Reflection;
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

            if (snapshot.NativeCallbackRootCount < 0 ||
                snapshot.NativeCallbackRootHighWatermark < snapshot.NativeCallbackRootCount)
            {
                throw new InvalidOperationException(
                    $"Memory snapshot published invalid native callback-root health: " +
                    $"current={snapshot.NativeCallbackRootCount}, high={snapshot.NativeCallbackRootHighWatermark}.");
            }

            await ThrowingCancellationCallbackStillDrainsAndDisposesAsync();

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
                $"PASS: process memory monitor captures process/native-root health and drains/disposes ownership through cancellation callback faults ({retained} final async/JIT root)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: process memory health monitor regression: {ex}");
            return 1;
        }
    }

    private static async Task ThrowingCancellationCallbackStillDrainsAndDisposesAsync()
    {
        var monitor = new ProcessMemoryHealthMonitor(TimeSpan.FromHours(1));
        var shutdown = GetPrivateField<CancellationTokenSource>(monitor, "_shutdown");
        var runTask = GetPrivateField<Task>(monitor, "_runTask");
        _ = shutdown.Token.Register(
            static () => throw new SyntheticCleanupException(
                "process memory monitor cancellation callback failed"));

        try
        {
            await monitor.DisposeAsync();
            throw new InvalidOperationException(
                "Throwing process-memory cancellation callback was not surfaced from DisposeAsync.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "process memory monitor cancellation callback failed"))
        {
        }

        if (!runTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Process-memory monitor returned from faulted cancellation before its exact worker task drained.");
        }

        if (!CancellationSourceWasDisposed(shutdown))
        {
            throw new InvalidOperationException(
                "Process-memory monitor cancellation callback fault left the shutdown CTS undisposed.");
        }

        await monitor.DisposeAsync();
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
                current.ProcessStartTimeUtc != expectedStartUtc ||
                current.NativeCallbackRootCount < 0 ||
                current.NativeCallbackRootHighWatermark < current.NativeCallbackRootCount)
            {
                throw new InvalidOperationException(
                    $"Memory monitor {i} did not publish a correctly process-bound initial snapshot.");
            }

            await monitor.DisposeAsync();
        }

        return weakMonitors;
    }

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        return (T)(field.GetValue(owner)
            ?? throw new InvalidOperationException($"Field '{fieldName}' was unexpectedly null."));
    }

    private static bool CancellationSourceWasDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed class SyntheticCleanupException : Exception
    {
        public SyntheticCleanupException(string message)
            : base(message)
        {
        }
    }
}
