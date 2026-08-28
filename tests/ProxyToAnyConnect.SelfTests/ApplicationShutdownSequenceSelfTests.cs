using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class ApplicationShutdownSequenceSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await ConfigurationDrainPrecedesRuntimeTeardownAsync();
            await CleanupFailuresDoNotSkipIndependentOwnersAsync();

            Console.WriteLine(
                "PASS: application shutdown drains GUI configuration generations before runtime and continues independent cleanup after faults");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: application shutdown ownership ordering regression: {ex}");
            return 1;
        }
    }

    private static async Task ConfigurationDrainPrecedesRuntimeTeardownAsync()
    {
        var configurationEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfiguration = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var phases = new List<string>();
        var sync = new object();

        void Add(string phase)
        {
            lock (sync)
            {
                phases.Add(phase);
            }
        }

        var drain = ApplicationShutdownSequence.DrainAsync(
            async () =>
            {
                Add("configuration-enter");
                configurationEntered.TrySetResult(true);
                await releaseConfiguration.Task;
                Add("configuration-exit");
            },
            () =>
            {
                Add("runtime");
                return ValueTask.CompletedTask;
            },
            () =>
            {
                Add("memory");
                return ValueTask.CompletedTask;
            });

        await configurationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        lock (sync)
        {
            if (!phases.SequenceEqual(new[] { "configuration-enter" }))
            {
                throw new InvalidOperationException(
                    "Runtime or memory cleanup started before configuration ownership was drained.");
            }
        }

        releaseConfiguration.TrySetResult(true);
        var failures = await drain.WaitAsync(TimeSpan.FromSeconds(2));
        if (failures.Count != 0)
        {
            throw new InvalidOperationException("Successful shutdown sequence reported cleanup failures.");
        }

        lock (sync)
        {
            var expected = new[] { "configuration-enter", "configuration-exit", "runtime", "memory" };
            if (!phases.SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Unexpected shutdown owner order: {string.Join(",", phases)}.");
            }
        }
    }

    private static async Task CleanupFailuresDoNotSkipIndependentOwnersAsync()
    {
        var phases = new List<string>();
        var failures = await ApplicationShutdownSequence.DrainAsync(
            () =>
            {
                phases.Add("configuration");
                return Task.FromException(new IOException("configuration cleanup fault"));
            },
            () =>
            {
                phases.Add("runtime");
                return ValueTask.FromException(new InvalidOperationException("runtime cleanup fault"));
            },
            () =>
            {
                phases.Add("memory");
                return ValueTask.FromException(new ApplicationException("memory cleanup fault"));
            });

        if (!phases.SequenceEqual(new[] { "configuration", "runtime", "memory", "runtime" }))
        {
            throw new InvalidOperationException(
                "An earlier shutdown fault skipped/reordered an independent owner or the bounded runtime retry.");
        }

        if (failures.Count != 4 ||
            failures[0].Phase != "configuration-command-queue" || failures[0].Exception is not IOException ||
            failures[1].Phase != "runtime-host" || failures[1].Exception is not InvalidOperationException ||
            failures[2].Phase != "memory-monitor" || failures[2].Exception is not ApplicationException ||
            failures[3].Phase != "runtime-host-retry" || failures[3].Exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Shutdown sequence did not retain first-pass and residual retry failures in deterministic owner order.");
        }
    }
}
