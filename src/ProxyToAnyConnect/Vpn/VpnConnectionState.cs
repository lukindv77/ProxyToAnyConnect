namespace ProxyToAnyConnect.Vpn;

internal enum VpnConnectionState
{
    Disconnected = 0,
    Dialing = 1,
    Verifying = 2,
    Ready = 3
}
