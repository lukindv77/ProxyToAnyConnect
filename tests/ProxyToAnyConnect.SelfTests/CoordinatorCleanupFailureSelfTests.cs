using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.SelfTests;

internal static class CoordinatorCleanupFailureSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await AllOwnersDisposeAfterEarlierFailureAsync();
            Console.WriteLine(
                "PASS: coordinator cleanup attempts every owner and preserves primary/secondary failure ordering");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: coordinator cleanup-failure regression: {ex}");
            return 1;
        }
    }

    private static async Task AllOwnersDisposeAfterEarlierFailureAsync()
    {
        var order = new List<string>();
        var primary = new SyntheticCleanupException("first owner failed");
        var secondary = new SyntheticCleanupException("third owner failed");
        var first = new RecordingDisposable("first", order, primary);
        var second = new RecordingDisposable("second", order, null);
        var third = new RecordingDisposable("third", order, secondary);

        var returned = await ProxyRuntimeCoordinator.DisposeOwnedResourcesAsync(
            new IAsyncDisposable[] { first, second, third },
            "self-test");

        if (!ReferenceEquals(returned, primary))
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not preserve the first teardown exception as primary.");
        }

        if (!order.SequenceEqual(["first", "second", "third"]))
        {
            throw new InvalidOperationException(
                $"Coordinator cleanup did not preserve deterministic owner order: {string.Join(",", order)}.");
        }

        if (first.DisposeCount != 1 || second.DisposeCount != 1 || third.DisposeCount != 1)
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not attempt every independent owner exactly once.");
        }

        var key = "CoordinatorCleanup:self-test:2";
        if (!primary.Data.Contains(key) ||
            primary.Data[key]?.ToString()?.Contains("third owner failed", StringComparison.Ordinal) != true)
        {
            throw new InvalidOperationException(
                "Coordinator cleanup did not attach the later teardown failure to the primary exception.");
        }
    }

    private sealed class RecordingDisposable : IAsyncDisposable
    {
        private readonly string _name;
        private readonly List<string> _order;
        private readonly Exception? _failure;

        public RecordingDisposable(string name, List<string> order, Exception? failure)
        {
            _name = name;
            _order = order;
            _failure = failure;
        }

        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _order.Add(_name);
            return _failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(_failure);
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
