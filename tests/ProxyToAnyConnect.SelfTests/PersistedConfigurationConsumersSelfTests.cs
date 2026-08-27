using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class PersistedConfigurationConsumersSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await AppliesLoggingBeforeRuntimeForSameGenerationAsync();
            await LoggingFailureStillAttemptsRuntimeAsync();
            await RuntimeFailureRemainsPrimaryWhenLoggingSucceedsAsync();
            await BothFailuresPreserveLoggingPrimaryAsync();
            await PreCancellationTouchesNoConsumerAsync();

            Console.WriteLine(
                "PASS: one durable configuration generation reaches logging/runtime consumers with independent cleanup-style failure handling");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: persisted configuration consumer regression: {ex}");
            return 1;
        }
    }

    private static async Task AppliesLoggingBeforeRuntimeForSameGenerationAsync()
    {
        var desired = CreateOptions();
        var phases = new List<string>();
        AppOptions? runtimeObserved = null;

        await PersistedConfigurationConsumers.ApplyAsync(
            desired,
            logging =>
            {
                if (!ReferenceEquals(logging, desired.Logging))
                {
                    throw new InvalidOperationException("Logging consumer received a different generation.");
                }

                phases.Add("logging");
            },
            (options, _) =>
            {
                runtimeObserved = options;
                phases.Add("runtime");
                return Task.CompletedTask;
            },
            CancellationToken.None);

        if (!ReferenceEquals(runtimeObserved, desired) || !phases.SequenceEqual(["logging", "runtime"]))
        {
            throw new InvalidOperationException("Persisted consumers did not observe one generation in dependency order.");
        }
    }

    private static async Task LoggingFailureStillAttemptsRuntimeAsync()
    {
        var desired = CreateOptions();
        var runtimeCalls = 0;
        var loggingFailure = new SyntheticLoggingException();

        try
        {
            await PersistedConfigurationConsumers.ApplyAsync(
                desired,
                _ => throw loggingFailure,
                (_, _) =>
                {
                    runtimeCalls++;
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic logging failure was swallowed.");
        }
        catch (SyntheticLoggingException ex) when (ReferenceEquals(ex, loggingFailure))
        {
        }

        if (runtimeCalls != 1)
        {
            throw new InvalidOperationException("Logging consumer failure skipped runtime reconciliation.");
        }
    }

    private static async Task RuntimeFailureRemainsPrimaryWhenLoggingSucceedsAsync()
    {
        var desired = CreateOptions();
        var loggingCalls = 0;
        var runtimeFailure = new SyntheticRuntimeException();

        try
        {
            await PersistedConfigurationConsumers.ApplyAsync(
                desired,
                _ => loggingCalls++,
                (_, _) => throw runtimeFailure,
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic runtime failure was swallowed.");
        }
        catch (SyntheticRuntimeException ex) when (ReferenceEquals(ex, runtimeFailure))
        {
        }

        if (loggingCalls != 1)
        {
            throw new InvalidOperationException("Runtime failure occurred before the logging consumer was applied.");
        }
    }

    private static async Task BothFailuresPreserveLoggingPrimaryAsync()
    {
        var desired = CreateOptions();
        var loggingFailure = new SyntheticLoggingException();
        var runtimeFailure = new SyntheticRuntimeException();

        try
        {
            await PersistedConfigurationConsumers.ApplyAsync(
                desired,
                _ => throw loggingFailure,
                (_, _) => throw runtimeFailure,
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic dual-consumer failure was swallowed.");
        }
        catch (SyntheticLoggingException ex) when (ReferenceEquals(ex, loggingFailure))
        {
            if (!ReferenceEquals(ex.Data["PersistedConfigurationConsumer:runtime"], runtimeFailure))
            {
                throw new InvalidOperationException("Secondary runtime failure was not retained diagnostically.");
            }
        }
    }

    private static async Task PreCancellationTouchesNoConsumerAsync()
    {
        var desired = CreateOptions();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;

        try
        {
            await PersistedConfigurationConsumers.ApplyAsync(
                desired,
                _ => calls++,
                (_, _) =>
                {
                    calls++;
                    return Task.CompletedTask;
                },
                cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled live-consumer apply unexpectedly completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (calls != 0)
        {
            throw new InvalidOperationException("Pre-cancelled persisted generation touched a live consumer.");
        }
    }

    private static AppOptions CreateOptions() => new()
    {
        Logging = new LoggingOptions
        {
            RetentionDays = 30
        }
    };

    private sealed class SyntheticLoggingException : Exception
    {
    }

    private sealed class SyntheticRuntimeException : Exception
    {
    }
}
