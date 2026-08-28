namespace ProxyToAnyConnect.Gui;

internal readonly record struct ApplicationShutdownFailure(
    string Phase,
    Exception Exception);

internal static class ApplicationShutdownSequence
{
    public static async Task<IReadOnlyList<ApplicationShutdownFailure>> DrainAsync(
        Func<Task> stopConfigurationCommandsAsync,
        Func<ValueTask> disposeRuntimeAsync,
        Func<ValueTask> disposeMemoryMonitorAsync)
    {
        ArgumentNullException.ThrowIfNull(stopConfigurationCommandsAsync);
        ArgumentNullException.ThrowIfNull(disposeRuntimeAsync);
        ArgumentNullException.ThrowIfNull(disposeMemoryMonitorAsync);

        var failures = new List<ApplicationShutdownFailure>(capacity: 3);

        await TryPhaseAsync(
            "configuration-command-queue",
            stopConfigurationCommandsAsync,
            failures);
        var failureCountBeforeRuntime = failures.Count;
        await TryPhaseAsync(
            "runtime-host",
            async () => await disposeRuntimeAsync(),
            failures);
        var runtimeFailed = failures.Count != failureCountBeforeRuntime;

        // Every independent first-pass owner is still attempted before retrying the
        // runtime. This keeps shutdown latency bounded by one extra exact-host cleanup
        // attempt without letting a transient RAS teardown defect skip memory cleanup.
        await TryPhaseAsync(
            "memory-monitor",
            async () => await disposeMemoryMonitorAsync(),
            failures);

        if (runtimeFailed)
        {
            await TryPhaseAsync(
                "runtime-host-retry",
                async () => await disposeRuntimeAsync(),
                failures);
        }

        return failures;
    }

    private static async Task TryPhaseAsync(
        string phase,
        Func<Task> action,
        List<ApplicationShutdownFailure> failures)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // Shutdown is ownership drainage, not a fail-fast operation. Retain each
            // phase failure for diagnostics but continue to the next independent owner.
            failures.Add(new ApplicationShutdownFailure(phase, ex));
        }
    }
}
