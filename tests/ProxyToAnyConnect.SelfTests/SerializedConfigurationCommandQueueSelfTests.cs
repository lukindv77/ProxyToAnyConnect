using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class SerializedConfigurationCommandQueueSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await PreservesStrictGenerationOrderAndSingleOwnershipAsync();
            await FailureAndCancellationDoNotWedgeSuccessorsAsync();
            await StopCancelsActiveAndQueuedGenerationsAsync();
            await StopDrainsTailAfterCancellationCallbackFaultAsync();

            Console.WriteLine(
                "PASS: GUI configuration commands serialize strict generations, recover after failure/cancellation and drain shutdown ownership through callback faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: GUI configuration command serialization regression: {ex}");
            return 1;
        }
    }

    private static async Task PreservesStrictGenerationOrderAndSingleOwnershipAsync()
    {
        var queue = new SerializedConfigurationCommandQueue();
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<long>();
        var active = 0;
        var maxActive = 0;
        var sync = new object();

        async Task Command(long generation, CancellationToken cancellationToken)
        {
            var currentActive = Interlocked.Increment(ref active);
            lock (sync)
            {
                maxActive = Math.Max(maxActive, currentActive);
                order.Add(generation);
            }

            try
            {
                if (generation == 1)
                {
                    firstEntered.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                await Task.Yield();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var first = queue.RunAsync(Command);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = queue.RunAsync(Command);
        var third = queue.RunAsync(Command);

        await Task.Delay(50);
        lock (sync)
        {
            if (order.Count != 1 || order[0] != 1)
            {
                throw new InvalidOperationException("A later GUI configuration generation overtook the blocked first generation.");
            }
        }

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));

        lock (sync)
        {
            if (!order.SequenceEqual(new long[] { 1, 2, 3 }))
            {
                throw new InvalidOperationException(
                    $"GUI configuration generations executed out of order: {string.Join(",", order)}.");
            }

            if (maxActive != 1)
            {
                throw new InvalidOperationException($"Expected one active configuration command, observed {maxActive}.");
            }
        }
    }

    private static async Task FailureAndCancellationDoNotWedgeSuccessorsAsync()
    {
        var queue = new SerializedConfigurationCommandQueue();
        var blockerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorRan = false;

        var blocker = queue.RunAsync(async (generation, cancellationToken) =>
        {
            if (generation != 1)
            {
                throw new InvalidOperationException("Unexpected blocker generation.");
            }

            blockerEntered.TrySetResult(true);
            await releaseBlocker.Task.WaitAsync(cancellationToken);
        });
        await blockerEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledTask = queue.RunAsync(
            (_, _) => throw new InvalidOperationException("Cancelled queued command unexpectedly executed."),
            cancelled.Token);

        var failedTask = queue.RunAsync((generation, _) =>
        {
            if (generation != 3)
            {
                throw new InvalidOperationException("Unexpected failing generation.");
            }

            return Task.FromException(new IOException("expected generation failure"));
        });

        var successor = queue.RunAsync((generation, _) =>
        {
            if (generation != 4)
            {
                throw new InvalidOperationException("Unexpected successor generation.");
            }

            successorRan = true;
            return Task.CompletedTask;
        });

        releaseBlocker.TrySetResult(true);
        await blocker.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await cancelledTask;
            throw new InvalidOperationException("Cancelled queued command did not preserve cancellation.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
        {
        }

        try
        {
            await failedTask;
            throw new InvalidOperationException("Failing queued command did not preserve its exception.");
        }
        catch (IOException ex) when (ex.Message.Contains("expected generation failure", StringComparison.Ordinal))
        {
        }

        await successor.WaitAsync(TimeSpan.FromSeconds(2));
        if (!successorRan)
        {
            throw new InvalidOperationException("Successor did not run after cancelled/failed GUI configuration generations.");
        }
    }

    private static async Task StopCancelsActiveAndQueuedGenerationsAsync()
    {
        var queue = new SerializedConfigurationCommandQueue();
        var activeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeSawCancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedRan = false;

        var active = queue.RunAsync(async (_, cancellationToken) =>
        {
            activeEntered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activeSawCancellation.TrySetResult(true);
                throw;
            }
        });
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var queued = queue.RunAsync((_, _) =>
        {
            queuedRan = true;
            return Task.CompletedTask;
        });

        var stop = queue.StopAsync();
        var repeatedStop = queue.StopAsync();
        if (!ReferenceEquals(stop, repeatedStop))
        {
            throw new InvalidOperationException("Repeated queue StopAsync calls did not share one shutdown task.");
        }

        await activeSawCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await active;
            throw new InvalidOperationException("Active configuration command ignored queue shutdown cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await queued;
            throw new InvalidOperationException("Queued configuration command did not observe queue shutdown cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        if (queuedRan)
        {
            throw new InvalidOperationException("Queued configuration command executed after queue shutdown began.");
        }

        try
        {
            _ = queue.RunAsync((_, _) => Task.CompletedTask);
            throw new InvalidOperationException("Stopped configuration queue accepted a new generation.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task StopDrainsTailAfterCancellationCallbackFaultAsync()
    {
        var queue = new SerializedConfigurationCommandQueue();
        var activeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var active = queue.RunAsync(async (_, cancellationToken) =>
        {
            using var throwingRegistration = cancellationToken.Register(
                () => throw new InvalidOperationException("expected GUI shutdown callback fault"));
            activeEntered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                activeFinished.TrySetResult(true);
            }
        });
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stop = queue.StopAsync();
        await activeFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Exception? stopFailure = null;
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            stopFailure = ex;
        }

        if (stopFailure is null ||
            !stopFailure.ToString().Contains("expected GUI shutdown callback fault", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Queue shutdown did not preserve the cancellation callback cleanup defect after draining its tail.");
        }

        try
        {
            await active;
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException ex) when (
            ex.ToString().Contains("expected GUI shutdown callback fault", StringComparison.Ordinal))
        {
            // Runtime cancellation callback faults may surface to the active command
            // as an aggregate while StopAsync independently retains the same cleanup defect.
        }

        try
        {
            _ = queue.RunAsync((_, _) => Task.CompletedTask);
            throw new InvalidOperationException("Queue accepted a new generation after faulted shutdown completed.");
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
