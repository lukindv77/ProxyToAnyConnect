using System.Net;
using System.Net.Sockets;

namespace ProxyToAnyConnect.Proxy;

// ProxyServer intentionally uses these namespace-local transport wrappers instead of
// System.Net.Sockets.TcpListener/TcpClient. The wrapper changes only accepted-client
// teardown; request parsing, L2TP routing and transfer pumps remain in ProxyServer.
internal sealed class TcpListener
{
    private readonly System.Net.Sockets.TcpListener _inner;

    public TcpListener(IPAddress localAddress, int port)
    {
        _inner = new System.Net.Sockets.TcpListener(localAddress, port);
    }

    public void Start() => _inner.Start();

    public void Stop() => _inner.Stop();

    public async ValueTask<TcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken)
    {
        var socket = await _inner.AcceptSocketAsync(cancellationToken);
        try
        {
            return new TcpClient(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class TcpClient : IDisposable
{
    private const int MaxCloseDrainBytes = 64 * 1024;

    private readonly Socket _socket;
    private int _disposed;

    public TcpClient(Socket acceptedSocket)
    {
        _socket = acceptedSocket ?? throw new ArgumentNullException(nameof(acceptedSocket));
    }

    public Socket Client => _socket;

    public bool Connected => Volatile.Read(ref _disposed) == 0 && _socket.Connected;

    public bool NoDelay
    {
        get => _socket.NoDelay;
        set => _socket.NoDelay = value;
    }

    public NetworkStream GetStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // The accepted-client wrapper is the single owner of the socket. Individual
        // request/error streams are scoped views only; disposing them must not bypass
        // PrepareForClose by closing the socket before outer TcpClient.Dispose().
        return new NetworkStream(_socket, ownsSocket: false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            PrepareForClose(_socket);
        }
        finally
        {
            _socket.Dispose();
        }
    }

    private static void PrepareForClose(Socket socket)
    {
        try
        {
            var available = socket.Available;
            if (available <= 0)
            {
                return;
            }

            // Winsock may turn closesocket into an abortive close when unread receive
            // data remains. Explicit send shutdown lets already-written response bytes
            // and FIN progress before the socket is closed.
            socket.Shutdown(SocketShutdown.Send);

            // Never forward or wait for post-request bytes. Discard only bytes already
            // queued in the local receive buffer and cap the work so a hostile client
            // cannot turn close hygiene into an unbounded drain/session-slot hold.
            Span<byte> scratch = stackalloc byte[512];
            var drained = 0;
            while (drained < MaxCloseDrainBytes)
            {
                available = socket.Available;
                if (available <= 0)
                {
                    return;
                }

                var requested = Math.Min(
                    Math.Min(available, scratch.Length),
                    MaxCloseDrainBytes - drained);
                var received = socket.Receive(scratch[..requested], SocketFlags.None);
                if (received <= 0)
                {
                    return;
                }

                drained += received;
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
}
