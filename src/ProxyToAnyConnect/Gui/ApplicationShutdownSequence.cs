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
        await TryPhaseAsync(
            "runtime-host",
            async () => await disposeRuntimeAsync(),
            failures);
        await TryPhaseAsync(
            "memory-monitor",
            async () => await disposeMemoryMonitorAsync(),
            failures);

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
