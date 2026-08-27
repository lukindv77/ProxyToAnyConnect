using System.Collections.Concurrent;
using System.Reflection;
using ProxyToAnyConnect.Diagnostics;

namespace ProxyToAnyConnect.SelfTests;

internal static class RetentionCleanupSchedulerSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await SchedulingIsBoundedAndCoalescedAsync();
            await WorkerCancelsOnDisposeAsync();
            Console.WriteLine("PASS: JSONL retention scheduling is single-worker, coalesced, dispose-cancellable and releases token ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: JSONL retention scheduler regression: {ex}");
            return 1;
        }
    }

    private static async Task SchedulingIsBoundedAndCoalescedAsync()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var observedDates = new ConcurrentQueue<DateOnly>();
        var invocation = 0;
        var active = 0;
        var maxActive = 0;

        using var scheduler = new RetentionCleanupScheduler((date, cancellationToken) =>
        {
            observedDates.Enqueue(date);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, currentActive);
            try
            {
                if (Interlocked.Increment(ref invocation) == 1)
                {
                    entered.Set();
                    release.Wait(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var firstDate = new DateOnly(2026, 8, 26);
        var middleDate = firstDate.AddDays(1);
        var latestDate = firstDate.AddDays(2);
        var firstTask = scheduler.Schedule(firstDate);
        if (!entered.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Retention cleanup worker did not start.");
        }

        var duplicateTask = scheduler.Schedule(firstDate);
        var middleTask = scheduler.Schedule(middleDate);
        var latestTask = scheduler.Schedule(latestDate);
        var staleTask = scheduler.Schedule(middleDate);
        if (!ReferenceEquals(firstTask, duplicateTask) ||
            !ReferenceEquals(firstTask, middleTask) ||
            !ReferenceEquals(firstTask, latestTask) ||
            !ReferenceEquals(firstTask, staleTask) ||
            scheduler.WorkerStarts != 1 ||
            scheduler.ActiveWorkers != 1)
        {
            throw new InvalidOperationException("Concurrent retention schedules created more than one worker task.");
        }

        release.Set();
        await firstTask.WaitAsync(TimeSpan.FromSeconds(5));

        var dates = observedDates.ToArray();
        if (dates.Length != 2 || dates[0] != firstDate || dates[1] != latestDate)
        {
            throw new InvalidOperationException(
                $"Retention dates were not coalesced to first/latest: [{string.Join(", ", dates)}].");
        }

        if (maxActive != 1 || scheduler.MaxConcurrentWorkers != 1 || scheduler.ActiveWorkers != 0)
        {
            throw new InvalidOperationException(
                $"Retention cleanup ran concurrently (callbackMax={maxActive}, schedulerMax={scheduler.MaxConcurrentWorkers}).");
        }
    }

    private static async Task WorkerCancelsOnDisposeAsync()
    {
        using var entered = new ManualResetEventSlim(false);
        var cancellationObserved = 0;
        var callbackInvoked = 0;
        var scheduler = new RetentionCleanupScheduler((_, cancellationToken) =>
        {
            using var throwingRegistration = cancellationToken.Register(() =>
            {
                Interlocked.Exchange(ref callbackInvoked, 1);
                throw new SyntheticCleanupException("retention cancellation callback failed");
            });

            entered.Set();
            try
            {
                // Intentionally materialize the token's native wait handle. Scheduler
                // disposal must not dispose its CTS until this callback has unwound.
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref cancellationObserved, 1);
                throw;
            }
        });
        var lifetime = GetPrivateLifetime(scheduler);

        var task = scheduler.Schedule(new DateOnly(2026, 8, 26));
        if (!entered.Wait(TimeSpan.FromSeconds(5)))
        {
            scheduler.Dispose();
            throw new TimeoutException("Retention cleanup worker did not enter cancellable work.");
        }

        // The registered callback throws synchronously from CancellationTokenSource.Cancel.
        // Scheduler.Dispose is diagnostics cleanup and must absorb that secondary fault,
        // while still allowing the worker to observe cancellation and drain.
        scheduler.Dispose();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        if (Volatile.Read(ref callbackInvoked) != 1 ||
            Volatile.Read(ref cancellationObserved) != 1 ||
            scheduler.ActiveWorkers != 0)
        {
            throw new InvalidOperationException(
                "Disposing the log scheduler did not drain its active worker through a throwing cancellation callback.");
        }

        if (!CancellationSourceWasDisposed(lifetime))
        {
            throw new InvalidOperationException(
                "Retention scheduler did not dispose its CancellationTokenSource after the final worker exited.");
        }

        try
        {
            _ = scheduler.Schedule(new DateOnly(2026, 8, 27));
            throw new InvalidOperationException("Disposed retention scheduler accepted new work.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static CancellationTokenSource GetPrivateLifetime(RetentionCleanupScheduler scheduler)
    {
        var field = typeof(RetentionCleanupScheduler).GetField(
            "_lifetime",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(RetentionCleanupScheduler).FullName, "_lifetime");
        return field.GetValue(scheduler) as CancellationTokenSource
            ?? throw new InvalidOperationException("Retention scheduler lifetime source was unavailable.");
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

    private static void UpdateMaximum(ref int location, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref location, candidate, current) == current)
            {
                return;
            }
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
