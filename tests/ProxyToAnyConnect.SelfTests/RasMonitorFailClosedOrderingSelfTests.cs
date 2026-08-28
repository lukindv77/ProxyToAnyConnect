using System.Net;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RasMonitorFailClosedOrderingSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await ContextIsInvalidatedBeforeBlockedSiblingDrainAsync();
            await ExplicitOwnerDrainDoesNotInventInvalidationAsync();

            Console.WriteLine(
                "PASS: failed VPN monitor invalidates the exact context before blocked sibling cleanup drains");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: VPN monitor fail-closed ordering regression: {ex}");
            return 1;
        }
    }

    private static async Task ContextIsInvalidatedBeforeBlockedSiblingDrainAsync()
    {
        var context = CreateContext("monitor-failure", 71);
        using var monitorCancellation = new CancellationTokenSource();
        var releaseSibling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var drain = RasConnectionManager.InvalidateThenDrainMonitorTasksAsync(
            context.MarkDisconnected,
            monitorCancellation,
            Task.CompletedTask,
            releaseSibling.Task,
            Task.CompletedTask);

        if (drain.IsCompleted)
        {
            throw new InvalidOperationException(
                "Monitor cleanup did not remain blocked on the intentionally delayed sibling task.");
        }

        if (context.IsAlive || !context.LifetimeToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "VPN context remained usable while sibling monitor cleanup was still blocked.");
        }

        if (!monitorCancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Sibling monitor cancellation was not requested after fail-closed invalidation.");
        }

        releaseSibling.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task ExplicitOwnerDrainDoesNotInventInvalidationAsync()
    {
        var context = CreateContext("owner-cancel", 72);
        using var monitorCancellation = new CancellationTokenSource();
        var releaseSibling = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var drain = RasConnectionManager.InvalidateThenDrainMonitorTasksAsync(
            invalidateBeforeDrain: null,
            monitorCancellation,
            Task.CompletedTask,
            releaseSibling.Task,
            Task.CompletedTask);

        if (!context.IsAlive || context.LifetimeToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Owner-driven sibling drain manufactured a fail-closed context transition.");
        }

        releaseSibling.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        context.MarkDisconnected();
    }

    private static VpnContext CreateContext(string entryName, int interfaceIndex) =>
        new(
            entryName,
            IPAddress.Parse("10.70.0.2"),
            new VpnInterfaceInfo(
                $"if-{interfaceIndex}",
                $"if-{interfaceIndex}",
                interfaceIndex,
                [IPAddress.Parse("10.70.0.1")]),
            IPAddress.Parse("10.70.0.254"));
}
