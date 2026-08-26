namespace ProxyToAnyConnect.Vpn;

// Narrow orchestration contract shared by lease/socket ownership and deterministic
// lifecycle tests. The proxy transfer hot path still consumes only the already-
// established VpnContext and does not dispatch through this interface per buffer.
internal interface IVpnConnectionController : IAsyncDisposable
{
    VpnContext? Current { get; }

    VpnConnectionState State { get; }

    Task<VpnContext> ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();
}
