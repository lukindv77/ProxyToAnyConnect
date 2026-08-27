using System.Net;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class L2tpSocketFactoryCancellationSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await PreCancelledCallerDoesNotTouchVpnAsync();
            CallerCancellationWinsConcurrentVpnLoss();
            VpnLossStillFailsClosed();
            OrdinaryConnectFailureRemainsRetryable();

            Console.WriteLine(
                "PASS: L2TP outbound setup preserves caller cancellation and VPN fail-closed ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: L2TP outbound cancellation regression: {ex}");
            return 1;
        }
    }

    private static async Task PreCancelledCallerDoesNotTouchVpnAsync()
    {
        var controller = new TrackingController();
        var factory = new L2tpSocketFactory(controller, new L2tpDnsResolver());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await factory.ConnectAsync("203.0.113.10", 443, cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled outbound connect unexpectedly completed.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
        }

        if (controller.CurrentReadCount != 0 || controller.ConnectCount != 0)
        {
            throw new InvalidOperationException(
                $"Pre-cancelled outbound setup touched VPN state (Current reads={controller.CurrentReadCount}, Connect calls={controller.ConnectCount}).");
        }

        await controller.DisposeAsync();
    }

    private static void CallerCancellationWinsConcurrentVpnLoss()
    {
        using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.MarkDisconnected();
        var failure = new OperationCanceledException("linked connect cancelled");

        try
        {
            L2tpSocketFactory.ThrowIfConnectCancellationRequiresAbort(
                failure,
                cancellation.Token,
                context);
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
            return;
        }

        throw new InvalidOperationException(
            "Caller cancellation did not win the concurrent proxy-shutdown/VPN-loss race.");
    }

    private static void VpnLossStillFailsClosed()
    {
        using var context = CreateContext();
        context.MarkDisconnected();
        var failure = new OperationCanceledException("VPN lifetime cancelled");

        try
        {
            L2tpSocketFactory.ThrowIfConnectCancellationRequiresAbort(
                failure,
                CancellationToken.None,
                context);
        }
        catch (VpnUnavailableException ex) when (ReferenceEquals(ex.InnerException, failure))
        {
            return;
        }

        throw new InvalidOperationException(
            "VPN lifetime cancellation was not translated to fail-closed VpnUnavailableException.");
    }

    private static void OrdinaryConnectFailureRemainsRetryable()
    {
        using var context = CreateContext();
        L2tpSocketFactory.ThrowIfConnectCancellationRequiresAbort(
            new IOException("synthetic ordinary connect failure"),
            CancellationToken.None,
            context);
    }

    private static VpnContext CreateContext() =>
        new(
            "test",
            IPAddress.Loopback,
            new VpnInterfaceInfo(
                "loopback",
                "loopback",
                1,
                Array.Empty<IPAddress>()));

    private sealed class TrackingController : IVpnConnectionController
    {
        public int CurrentReadCount { get; private set; }
        public int ConnectCount { get; private set; }

        public VpnContext? Current
        {
            get
            {
                CurrentReadCount++;
                return null;
            }
        }

        public VpnConnectionState State => VpnConnectionState.Disconnected;

        public Task<VpnContext> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            throw new InvalidOperationException("ConnectAsync must not run for a pre-cancelled caller.");
        }

        public Task DisconnectAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
