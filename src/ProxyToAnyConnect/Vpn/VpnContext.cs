using System.Net;

namespace ProxyToAnyConnect.Vpn;

internal sealed class VpnContext : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    internal VpnContext(
        string entryName,
        IPAddress localIPv4,
        VpnInterfaceInfo interfaceInfo,
        IPAddress? serverIPv4 = null)
    {
        EntryName = entryName;
        LocalIPv4 = localIPv4;
        ServerIPv4 = serverIPv4;
        InterfaceName = interfaceInfo.Name;
        InterfaceDescription = interfaceInfo.Description;
        InterfaceIndex = interfaceInfo.InterfaceIndex;
        DnsServers = interfaceInfo.DnsServers;
    }

    public string EntryName { get; }
    public IPAddress LocalIPv4 { get; }
    public IPAddress? ServerIPv4 { get; }
    public string InterfaceName { get; }
    public string InterfaceDescription { get; }
    public int InterfaceIndex { get; }
    public IReadOnlyList<IPAddress> DnsServers { get; }
    public CancellationToken LifetimeToken => _lifetime.Token;
    public bool IsAlive => !_lifetime.IsCancellationRequested;

    internal void MarkDisconnected()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
