using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Network;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectCommitBoundarySelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    public static async Task<int> RunAsync()
    {
        try
        {
            await CommittedRemainderWriteFailureIsClassifiedAsync();
            await CommittedRemainderCancellationPreservesOwnerCancellationAsync();
            await PreEstablishmentVpnFailureStillReturns503Async();
            Console.WriteLine(
                "PASS: CONNECT response commit boundary classifies post-200 remainder failures as close-only and preserves pre-establishment errors");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CONNECT response commit-boundary regression: {ex}");
            return 1;
        }
    }

    private static async Task CommittedRemainderWriteFailureIsClassifiedAsync()
    {
        var failure = new IOException("Synthetic upstream write failure after CONNECT commitment.");
        await using var stream = new ThrowingWriteStream(failure);
        try
        {
            await ProxyServer.WriteConnectRemainderAfterCommitAsync(
                stream,
                "TLS-CLIENT-HELLO"u8.ToArray(),
                CancellationToken.None);
            throw new InvalidOperationException(
                "Committed CONNECT remainder failure did not surface as a committed-response failure.");
        }
        catch (ProxyServer.ProxyResponseCommittedException ex)
            when (ReferenceEquals(ex.InnerException, failure))
        {
        }

        if (stream.WriteAttempts != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one committed remainder write attempt, got {stream.WriteAttempts}.");
        }
    }

    private static async Task CommittedRemainderCancellationPreservesOwnerCancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new ThrowingWriteStream(
            new OperationCanceledException(cancellation.Token));

        try
        {
            await ProxyServer.WriteConnectRemainderAfterCommitAsync(
                stream,
                "cancelled"u8.ToArray(),
                cancellation.Token);
            throw new InvalidOperationException(
                "Committed CONNECT remainder cancellation did not remain cancellation control flow.");
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token)
        {
        }
    }

    private static async Task PreEstablishmentVpnFailureStillReturns503Async()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        var proxyPort = ReserveLoopbackPort();
        using var proxyCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proxy = new ProxyServer(CreateProxyOptions(proxyPort), new VpnUnavailableOutboundFactory());
        var proxyTask = proxy.RunAsync(proxyCancellation.Token);

        try
        {
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(
                "CONNECT unavailable.test:443 HTTP/1.1\r\nHost: unavailable.test:443\r\n\r\n"u8.ToArray(),
                timeout.Token);

            var responseHeader = Encoding.ASCII.GetString(await ReadHeaderAsync(stream, timeout.Token));
            if (!responseHeader.StartsWith("HTTP/1.1 503 L2TP VPN unavailable\r\n", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pre-establishment VPN failure no longer maps to HTTP 503: {responseHeader}");
            }
        }
        finally
        {
            proxyCancellation.Cancel();
            await proxyTask;
        }
    }

    private static ProxyOptions CreateProxyOptions(int port) =>
        new()
        {
            Id = "connect-commit-selftest",
            Name = "CONNECT commit self-test",
            ListenAddress = "127.0.0.1",
            ListenPort = port,
            MaxConcurrentConnections = 4,
            MaxHeaderBytes = 8192,
            ClientHeaderTimeoutSeconds = 5,
            OutboundConnectTimeoutSeconds = 5,
            DnsTimeoutMilliseconds = 1000
        };

    private static async Task<byte[]> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        var one = new byte[1];
        while (bytes.Count < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before proxy response header completed.");
            }

            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 &&
                bytes[count - 4] == (byte)'\r' &&
                bytes[count - 3] == (byte)'\n' &&
                bytes[count - 2] == (byte)'\r' &&
                bytes[count - 1] == (byte)'\n')
            {
                return bytes.ToArray();
            }
        }

        throw new InvalidDataException("Proxy response header exceeded the test limit.");
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class VpnUnavailableOutboundFactory : IProxyOutboundConnectionFactory
    {
        public Task<IProxyOutboundConnection> ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<IProxyOutboundConnection>(
                new VpnUnavailableException("Synthetic pre-establishment VPN failure."));
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        private readonly Exception _failure;
        private int _writeAttempts;

        public ThrowingWriteStream(Exception failure)
        {
            _failure = failure;
        }

        public int WriteAttempts => Volatile.Read(ref _writeAttempts);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeAttempts);
            return ValueTask.FromException(_failure);
        }
    }
}
