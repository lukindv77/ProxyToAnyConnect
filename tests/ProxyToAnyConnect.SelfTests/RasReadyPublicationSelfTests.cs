using System.Net;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RasReadyPublicationSelfTests
{
    public static int Run()
    {
        try
        {
            LiveContextWithActiveTokenCanPublish();
            CancelledOperationCannotPublishReadyContext();
            DeadContextCannotPublishReadyState();
            CancellationWinsWhenContextIsAlsoDead();

            Console.WriteLine(
                "PASS: RAS Ready publication rejects late cancellation and dead contexts before ownership exposure");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: RAS Ready publication regression: {ex}");
            return 1;
        }
    }

    private static void LiveContextWithActiveTokenCanPublish()
    {
        using var context = CreateContext();
        RasConnectionManager.EnsureReadyPublicationAllowed(context, CancellationToken.None);
    }

    private static void CancelledOperationCannotPublishReadyContext()
    {
        using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            RasConnectionManager.EnsureReadyPublicationAllowed(context, cancellation.Token);
            throw new InvalidOperationException(
                "Cancelled RAS operation passed the Ready publication gate.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private static void DeadContextCannotPublishReadyState()
    {
        using var context = CreateContext();
        context.MarkDisconnected();

        try
        {
            RasConnectionManager.EnsureReadyPublicationAllowed(context, CancellationToken.None);
            throw new InvalidOperationException(
                "Disconnected RAS context passed the Ready publication gate.");
        }
        catch (IOException ex) when (
            ex.Message.Contains("disappeared", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void CancellationWinsWhenContextIsAlsoDead()
    {
        using var context = CreateContext();
        context.MarkDisconnected();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            RasConnectionManager.EnsureReadyPublicationAllowed(context, cancellation.Token);
            throw new InvalidOperationException(
                "Cancelled/dead RAS operation passed the Ready publication gate.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Caller/owner cancellation remains control flow instead of being
            // rewritten as an ordinary fail-closed connection error.
        }
    }

    private static VpnContext CreateContext() =>
        new(
            "ready-publication-selftest",
            IPAddress.Loopback,
            new VpnInterfaceInfo(
                "ready-publication-selftest",
                "ready-publication-selftest",
                1,
                Array.Empty<IPAddress>()));
}
