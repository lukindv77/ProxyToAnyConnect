using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.SelfTests;

internal static class PersistedDesiredConfigurationSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await RuntimeFailureDoesNotRollBackPersistedDesiredStateAsync();
            await SaveFailureDoesNotPublishOrApplyAsync();
            Console.WriteLine(
                "PASS: persisted desired configuration remains authoritative across runtime reconciliation failures");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: persisted desired-state regression: {ex}");
            return 1;
        }
    }

    private static async Task RuntimeFailureDoesNotRollBackPersistedDesiredStateAsync()
    {
        var desired = new AppOptions();
        AppOptions? adopted = null;
        var saveCalls = 0;
        var applyCalls = 0;

        try
        {
            await PersistedDesiredConfiguration.SaveThenApplyAsync(
                desired,
                (options, _) =>
                {
                    if (!ReferenceEquals(options, desired))
                    {
                        throw new InvalidOperationException("Save received a different desired generation.");
                    }
                    Interlocked.Increment(ref saveCalls);
                    return Task.CompletedTask;
                },
                options => adopted = options,
                (_, _) =>
                {
                    Interlocked.Increment(ref applyCalls);
                    throw new SyntheticRuntimeApplyException();
                },
                CancellationToken.None);

            throw new InvalidOperationException("Synthetic runtime apply failure was swallowed.");
        }
        catch (SyntheticRuntimeApplyException)
        {
        }

        if (saveCalls != 1 || applyCalls != 1 || !ReferenceEquals(adopted, desired))
        {
            throw new InvalidOperationException(
                "Runtime failure rolled back or skipped publication of the already-persisted desired generation.");
        }
    }

    private static async Task SaveFailureDoesNotPublishOrApplyAsync()
    {
        var desired = new AppOptions();
        var adopted = 0;
        var applied = 0;

        try
        {
            await PersistedDesiredConfiguration.SaveThenApplyAsync(
                desired,
                (_, _) => throw new SyntheticSaveException(),
                _ => Interlocked.Increment(ref adopted),
                (_, _) =>
                {
                    Interlocked.Increment(ref applied);
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            throw new InvalidOperationException("Synthetic save failure was swallowed.");
        }
        catch (SyntheticSaveException)
        {
        }

        if (adopted != 0 || applied != 0)
        {
            throw new InvalidOperationException(
                "Desired state or runtime was published after durable configuration save failed.");
        }
    }

    private sealed class SyntheticRuntimeApplyException : Exception
    {
    }

    private sealed class SyntheticSaveException : Exception
    {
    }
}
