using System.Runtime.ExceptionServices;
using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.Gui;

internal static class PersistedConfigurationConsumers
{
    public static async Task ApplyAsync(
        AppOptions desired,
        Action<LoggingOptions> configureLogging,
        Func<AppOptions, CancellationToken, Task> applyRuntimeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(configureLogging);
        ArgumentNullException.ThrowIfNull(applyRuntimeAsync);
        cancellationToken.ThrowIfCancellationRequested();

        Exception? loggingFailure = null;
        try
        {
            configureLogging(desired.Logging);
        }
        catch (Exception ex)
        {
            loggingFailure = ex;
        }

        Exception? runtimeFailure = null;
        try
        {
            await applyRuntimeAsync(desired, cancellationToken);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Application Exit / queue cancellation is lifecycle control flow and must
            // remain primary even if the independent logging consumer had a defect.
            // Keep that earlier consumer defect as diagnostic secondary information.
            if (loggingFailure is not null)
            {
                ex.Data["PersistedConfigurationConsumer:logging"] = loggingFailure;
            }

            ExceptionDispatchInfo.Capture(ex).Throw();
        }
        catch (Exception ex)
        {
            runtimeFailure = ex;
        }

        if (loggingFailure is not null)
        {
            if (runtimeFailure is not null)
            {
                loggingFailure.Data["PersistedConfigurationConsumer:runtime"] = runtimeFailure;
            }

            ExceptionDispatchInfo.Capture(loggingFailure).Throw();
        }

        if (runtimeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(runtimeFailure).Throw();
        }
    }
}
