using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class NativeCallbackRootRegistrySelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var baseline = NativeCallbackRootHealth.CurrentCount;
            ZeroAndDuplicateHandlesRemainBounded(baseline);
            UniqueHandleChurnReturnsToBaseline(baseline);
            await ConcurrentHandleChurnReturnsToBaselineAsync(baseline);

            if (NativeCallbackRootHealth.CurrentCount != baseline)
            {
                throw new InvalidOperationException(
                    $"Native callback-root health retained {NativeCallbackRootHealth.CurrentCount - baseline} owner(s) after the full churn suite.");
            }

            Console.WriteLine(
                $"PASS: native callback roots are exact-handle keyed, duplicate-bounded, expose a monotonic high-watermark and return to baseline after concurrent churn (baseline={baseline}, high={NativeCallbackRootHealth.HighWatermark})");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: native callback root registry regression: {ex}");
            return 1;
        }
    }

    private static void ZeroAndDuplicateHandlesRemainBounded(int baseline)
    {
        var registry = new NativeCallbackRootRegistry<object>();
        registry.AddOrReplace(0, new object());
        if (registry.Count != 0 ||
            registry.Remove(0) ||
            NativeCallbackRootHealth.CurrentCount != baseline)
        {
            throw new InvalidOperationException("Zero native handle unexpectedly acquired callback ownership.");
        }

        var handle = (nint)123;
        registry.AddOrReplace(handle, new object());
        registry.AddOrReplace(handle, new object());
        if (registry.Count != 1 || NativeCallbackRootHealth.CurrentCount != baseline + 1)
        {
            throw new InvalidOperationException(
                $"Duplicate exact handle grew callback ownership unexpectedly: local={registry.Count}, global={NativeCallbackRootHealth.CurrentCount}.");
        }

        if (!registry.Remove(handle) ||
            registry.Remove(handle) ||
            registry.Count != 0 ||
            NativeCallbackRootHealth.CurrentCount != baseline)
        {
            throw new InvalidOperationException("Exact callback root removal was not idempotent/bounded.");
        }
    }

    private static void UniqueHandleChurnReturnsToBaseline(int baseline)
    {
        const int count = 8192;
        var registry = new NativeCallbackRootRegistry<object>();
        for (var index = 1; index <= count; index++)
        {
            registry.AddOrReplace((nint)index, new object());
        }

        if (registry.Count != count || NativeCallbackRootHealth.CurrentCount != baseline + count)
        {
            throw new InvalidOperationException(
                $"Expected {count} unique callback roots above baseline, local={registry.Count}, global={NativeCallbackRootHealth.CurrentCount}.");
        }

        if (NativeCallbackRootHealth.HighWatermark < baseline + count)
        {
            throw new InvalidOperationException(
                $"Native callback-root high-watermark {NativeCallbackRootHealth.HighWatermark} did not observe {baseline + count} live owners.");
        }

        for (var index = 1; index <= count; index++)
        {
            if (!registry.Remove((nint)index))
            {
                throw new InvalidOperationException($"Callback root {index} disappeared before its owner released it.");
            }
        }

        if (registry.Count != 0 || NativeCallbackRootHealth.CurrentCount != baseline)
        {
            throw new InvalidOperationException(
                $"Sequential callback-root churn did not return to baseline {baseline}: local={registry.Count}, global={NativeCallbackRootHealth.CurrentCount}.");
        }
    }

    private static async Task ConcurrentHandleChurnReturnsToBaselineAsync(int baseline)
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
                        // handles must not add another dictionary/global health owner.
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
        if (registry.Count != 0 || NativeCallbackRootHealth.CurrentCount != baseline)
        {
            throw new InvalidOperationException(
                $"Concurrent callback-root churn did not return to baseline {baseline}: local={registry.Count}, global={NativeCallbackRootHealth.CurrentCount}.");
        }
    }
}
