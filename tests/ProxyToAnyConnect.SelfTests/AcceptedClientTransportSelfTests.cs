using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class AcceptedClientTransportSelfTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync()
    {
        try
        {
            await QueuedUnreadClientBytesDoNotDestroyWrittenResponseAsync();
            await CloseDoesNotWaitForFutureClientBytesAsync();

            Console.WriteLine(
                "PASS: accepted-client close preserves written response with bounded unread-tail cleanup");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: accepted-client close regression: {ex}");
            return 1;
        }
    }

    private static async Task QueuedUnreadClientBytesDoNotDestroyWrittenResponseAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
        var connectTask = client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var acceptedSocketTask = listener.AcceptSocketAsync(timeout.Token);
        await connectTask;
        var acceptedSocket = await acceptedSocketTask;
        acceptedSocket.NoDelay = true;

        using var accepted = new ProxyToAnyConnect.Proxy.TcpClient(acceptedSocket);
        await using var clientStream = client.GetStream();
        await using var acceptedStream = accepted.GetStream();

        var maliciousTail = "SMUGGLED-POST-CONTENT-LENGTH"u8.ToArray();
        await clientStream.WriteAsync(maliciousTail, timeout.Token);
        await WaitUntilQueuedAsync(acceptedSocket, maliciousTail.Length, timeout.Token);

        var response =
            "HTTP/1.1 200 OK\r\n"u8.ToArray()
            .Concat("Content-Length: 2\r\n"u8.ToArray())
            .Concat("Connection: close\r\n\r\nOK"u8.ToArray())
            .ToArray();
        await acceptedStream.WriteAsync(response, timeout.Token);
        await acceptedStream.FlushAsync(timeout.Token);

        accepted.Dispose();

        var received = new byte[response.Length];
        await ReadExactlyBeforeCloseAsync(clientStream, received, timeout.Token);
        if (!received.AsSpan().SequenceEqual(response))
        {
            throw new InvalidOperationException(
                "Accepted-client close corrupted bytes that were written before disposal.");
        }
    }

    private static async Task CloseDoesNotWaitForFutureClientBytesAsync()
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
        var connectTask = client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var acceptedSocketTask = listener.AcceptSocketAsync(timeout.Token);
        await connectTask;
        var acceptedSocket = await acceptedSocketTask;
        acceptedSocket.NoDelay = true;

        using var accepted = new ProxyToAnyConnect.Proxy.TcpClient(acceptedSocket);
        var stopwatch = Stopwatch.StartNew();
        accepted.Dispose();
        stopwatch.Stop();

        // This is intentionally a very loose guard, not a performance benchmark.
        // PrepareForClose only inspects Socket.Available; it must never wait for the
        // peer to send additional bytes as part of close hygiene.
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(1))
        {
            throw new TimeoutException(
                $"Accepted-client close waited {stopwatch.Elapsed.TotalMilliseconds:F0} ms for future input.");
        }
    }

    private static async Task WaitUntilQueuedAsync(
        Socket socket,
        int minimumBytes,
        CancellationToken cancellationToken)
    {
        while (socket.Available < minimumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1, cancellationToken);
        }
    }

    private static async Task ReadExactlyBeforeCloseAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            try
            {
                var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
                if (read == 0)
                {
                    throw new IOException(
                        $"Connection closed after {offset}/{buffer.Length} response bytes.");
                }

                offset += read;
            }
            catch (IOException ex) when (ex.InnerException is SocketException socketException)
            {
                throw new IOException(
                    $"Connection reset after {offset}/{buffer.Length} response bytes " +
                    $"(socket error {socketException.SocketErrorCode}).",
                    ex);
            }
        }
    }
}
