using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class NativeCallbackRootRegistrySelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            ZeroAndDuplicateHandlesRemainBounded();
            UniqueHandleChurnReturnsToZero();
            await ConcurrentHandleChurnReturnsToZeroAsync();

            Console.WriteLine(
                "PASS: native callback roots are exact-handle keyed, duplicate-bounded and return to zero after concurrent churn");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: native callback root registry regression: {ex}");
            return 1;
        }
    }

    private static void ZeroAndDuplicateHandlesRemainBounded()
    {
        var registry = new NativeCallbackRootRegistry<object>();
        registry.AddOrReplace(0, new object());
        if (registry.Count != 0 || registry.Remove(0))
        {
            throw new InvalidOperationException("Zero native handle unexpectedly acquired callback ownership.");
        }

        var handle = (nint)123;
        registry.AddOrReplace(handle, new object());
        registry.AddOrReplace(handle, new object());
        if (registry.Count != 1)
        {
            throw new InvalidOperationException(
                $"Duplicate exact handle grew callback roots to {registry.Count}; expected one.");
        }

        if (!registry.Remove(handle) || registry.Remove(handle) || registry.Count != 0)
        {
            throw new InvalidOperationException("Exact callback root removal was not idempotent/bounded.");
        }
    }

    private static void UniqueHandleChurnReturnsToZero()
    {
        const int count = 8192;
        var registry = new NativeCallbackRootRegistry<object>();
        for (var index = 1; index <= count; index++)
        {
            registry.AddOrReplace((nint)index, new object());
        }

        if (registry.Count != count)
        {
            throw new InvalidOperationException(
                $"Expected {count} unique callback roots, observed {registry.Count}.");
        }

        for (var index = 1; index <= count; index++)
        {
            if (!registry.Remove((nint)index))
            {
                throw new InvalidOperationException($"Callback root {index} disappeared before its owner released it.");
            }
        }

        if (registry.Count != 0)
        {
            throw new InvalidOperationException($"Sequential callback-root churn retained {registry.Count} roots.");
        }
    }

    private static async Task ConcurrentHandleChurnReturnsToZeroAsync()
    {
        const int workers = 8;
        const int handlesPerWorker = 1024;
        var registry = new NativeCallbackRootRegistry<object>();

        var tasks = Enumerable.Range(0, workers)
            .Select(worker => Task.Run(() =>
            {
                var first = worker * handlesPerWorker + 1;
                var last = first + handlesPerWorker;
                for (var handle = first; handle < last; handle++)
                {
                    registry.AddOrReplace((nint)handle, new object());
                    if ((handle & 3) == 0)
                    {
                        // Replacing the same exact owner concurrently with unrelated
                        // handles must not add another dictionary entry.
                        registry.AddOrReplace((nint)handle, new object());
                    }
                }

                for (var handle = first; handle < last; handle++)
                {
                    if (!registry.Remove((nint)handle))
                    {
                        throw new InvalidOperationException(
                            $"Concurrent callback-root owner {handle} was lost before release.");
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        if (registry.Count != 0)
        {
            throw new InvalidOperationException(
                $"Concurrent callback-root churn retained {registry.Count} root(s).");
        }
    }
}
