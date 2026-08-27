namespace ProxyToAnyConnect.Vpn;

// Narrow orchestration contract shared by lease/socket ownership and deterministic
// lifecycle tests. The proxy transfer hot path still consumes only the already-
// established VpnContext and does not dispatch through this interface per buffer.
internal interface IVpnConnectionController : IAsyncDisposable
{
    VpnContext? Current { get; }

    VpnConnectionState State { get; }

    // Alternate/test controllers that do not implement reconnect throttling remain
    // immediately eligible. RasConnectionManager supplies its live cooldown value.
    long ReconnectCooldownRemainingMilliseconds => 0;

    Task<VpnContext> ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync();
}
