namespace ProxyToAnyConnect.Configuration;

internal static class PersistedDesiredConfiguration
{
    public static async Task SaveThenApplyAsync(
        AppOptions desired,
        Func<AppOptions, CancellationToken, Task> saveAsync,
        Action<AppOptions> adoptPersisted,
        Func<AppOptions, CancellationToken, Task> applyRuntimeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(saveAsync);
        ArgumentNullException.ThrowIfNull(adoptPersisted);
        ArgumentNullException.ThrowIfNull(applyRuntimeAsync);

        // Durable publication is the desired-state linearization point. Once Save
        // succeeds, callers must expose the same desired configuration that will be
        // loaded after restart even if live runtime reconciliation fails afterwards.
        await saveAsync(desired, cancellationToken).ConfigureAwait(false);
        adoptPersisted(desired);

        // Runtime apply is deliberately after adoption. Its failure is a runtime
        // convergence problem, not a rollback of an already-published config file.
        await applyRuntimeAsync(desired, cancellationToken).ConfigureAwait(false);
    }
}
