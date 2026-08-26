namespace ProxyToAnyConnect.Vpn;

internal sealed partial class RasConnectionManager : IVpnConnectionController
{
    async Task<VpnContext> IVpnConnectionController.ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        CancellationToken shutdownToken;
        try
        {
            shutdownToken = _shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            throw new ObjectDisposedException(nameof(RasConnectionManager));
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            shutdownToken);

        return await ConnectAsync(operationCancellation.Token).ConfigureAwait(false);
    }
}
