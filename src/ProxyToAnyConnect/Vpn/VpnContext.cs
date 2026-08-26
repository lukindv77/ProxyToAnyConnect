using System.Net;

namespace ProxyToAnyConnect.Vpn;

internal sealed class VpnContext : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;

    // One reference belongs to RasConnectionManager while this context is the
    // current RAS session. Every live outbound proxy connection adds one more.
    // The CTS is disposed only after the manager owner and every connection
    // have released their references.
    private int _references = 1;
    private int _ownerReleased;
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
        _lifetimeToken = _lifetime.Token;
    }

    public string EntryName { get; }
    public IPAddress LocalIPv4 { get; }
    public IPAddress? ServerIPv4 { get; }
    public string InterfaceName { get; }
    public string InterfaceDescription { get; }
    public int InterfaceIndex { get; }
    public IReadOnlyList<IPAddress> DnsServers { get; }
    public CancellationToken LifetimeToken => _lifetimeToken;
    public bool IsAlive => !_lifetimeToken.IsCancellationRequested;

    // Internal diagnostics are intentionally scalar only: no history or
    // connection registry is retained merely for observability.
    internal int ReferenceCount => Math.Max(0, Volatile.Read(ref _references));
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal void MarkDisconnected()
    {
        if (!_lifetimeToken.IsCancellationRequested)
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A racing final reference release already disposed the CTS.
            }
        }

        // MarkDisconnected is the single terminal transition for a RAS context.
        // Release the manager-owned reference exactly once on every path:
        // explicit disconnect, monitor fail-closed, failed verification, or disposal.
        if (Interlocked.Exchange(ref _ownerReleased, 1) == 0)
        {
            ReleaseReference();
        }
    }

    internal bool TryAcquireConnectionReference()
    {
        while (true)
        {
            if (!IsAlive || Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var current = Volatile.Read(ref _references);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _references, current + 1, current) != current)
            {
                continue;
            }

            // Disconnect may race the increment. Do not let a newly-created
            // connection keep a dead context alive unnecessarily.
            if (IsAlive && Volatile.Read(ref _disposed) == 0)
            {
                return true;
            }

            ReleaseConnectionReference();
            return false;
        }
    }

    internal void ReleaseConnectionReference() => ReleaseReference();

    public void Dispose() => MarkDisconnected();

    private void ReleaseReference()
    {
        var remaining = Interlocked.Decrement(ref _references);
        if (remaining < 0)
        {
            throw new InvalidOperationException("VpnContext reference count became negative.");
        }

        if (remaining != 0 || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Dispose();
    }
}
