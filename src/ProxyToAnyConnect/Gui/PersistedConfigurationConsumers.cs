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

        Exception? primaryFailure = null;
        try
        {
            configureLogging(desired.Logging);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }

        try
        {
            await applyRuntimeAsync(desired, cancellationToken);
        }
        catch (Exception ex)
        {
            if (primaryFailure is null)
            {
                primaryFailure = ex;
            }
            else
            {
                primaryFailure.Data["PersistedConfigurationConsumer:runtime"] = ex;
            }
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }
}
