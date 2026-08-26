using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Network;

internal sealed class L2tpDnsResolver
{
    private static readonly IdnMapping IdnMapping = new();
    private const int MaxCnameDepth = 8;
    private const int MaxDnsPacketBytes = ushort.MaxValue;
    private readonly int _timeoutMilliseconds;

    public L2tpDnsResolver(int timeoutMilliseconds = 6000)
    {
        if (timeoutMilliseconds is < 250 or > 60000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(
        string host,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new NotSupportedException("IPv6 targets are not supported yet.");
            }

            return [literal];
        }

        if (context.DnsServers.Count == 0)
        {
            throw new InvalidOperationException(
                $"L2TP interface '{context.InterfaceName}' did not provide an IPv4 DNS server.");
        }

        var asciiHost = IdnMapping.GetAscii(host.TrimEnd('.'));
        Exception? lastError = null;

        foreach (var dnsServer in context.DnsServers)
        {
            try
            {
                var result = await QueryAsync(asciiHost, dnsServer, context, cancellationToken);
                if (result.Count > 0)
                {
                    return result;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            $"Unable to resolve '{host}' through the L2TP DNS servers.", lastError);
    }

    private async Task<IReadOnlyList<IPAddress>> QueryAsync(
        string host,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(_timeoutMilliseconds));

        try
        {
            return await ResolveCoreAsync(
                NormalizeDnsName(host),
                dnsServer,
                context,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                depth: 0,
                timeout.Token);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && !context.LifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"DNS resolution through L2TP timed out after {_timeoutMilliseconds} ms.",
                ex);
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveCoreAsync(
        string host,
        IPAddress dnsServer,
        VpnContext context,
        HashSet<string> visitedNames,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxCnameDepth)
        {
            throw new IOException("DNS CNAME chain exceeded the maximum supported depth.");
        }

        if (!visitedNames.Add(host))
        {
            throw new IOException($"DNS CNAME loop detected at '{host}'.");
        }

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
        var query = BuildQuery(host, transactionId);

        var udpResponse = await QueryUdpAsync(query, dnsServer, context, cancellationToken);
        var parsed = ParseResponse(udpResponse, transactionId);

        if (parsed.Truncated)
        {
            var tcpResponse = await QueryTcpAsync(query, dnsServer, context, cancellationToken);
            parsed = ParseResponse(tcpResponse, transactionId);
        }

        if (parsed.Addresses.Count > 0)
        {
            return parsed.Addresses;
        }

        if (parsed.CanonicalName is not null)
        {
            return await ResolveCoreAsync(
                NormalizeDnsName(parsed.CanonicalName),
                dnsServer,
                context,
                visitedNames,
                depth + 1,
                cancellationToken);
        }

        return [];
    }

    private static async Task<byte[]> QueryUdpAsync(
        byte[] query,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
        socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
        await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(dnsServer, 53), cancellationToken);

        var buffer = new byte[MaxDnsPacketBytes];
        var result = await socket.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            new IPEndPoint(IPAddress.Any, 0),
            cancellationToken);

        return buffer.AsSpan(0, result.ReceivedBytes).ToArray();
    }

    private static async Task<byte[]> QueryTcpAsync(
        byte[] query,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
        socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
        await socket.ConnectAsync(new IPEndPoint(dnsServer, 53), cancellationToken);

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, checked((ushort)query.Length));
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(query, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (responseLength < 12)
        {
            throw new IOException("DNS-over-TCP response is too short.");
        }

        var response = new byte[responseLength];
        await ReadExactlyAsync(stream, response, cancellationToken);
        return response;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("DNS-over-TCP stream ended before the complete response was received.");
            }

            offset += read;
        }
    }

    private static byte[] BuildQuery(string host, ushort transactionId)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);

        foreach (var label in host.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
            }

            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> questionTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(questionTail, 1);
        BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..], 1);
        stream.Write(questionTail);
        return stream.ToArray();
    }

    internal static ParsedDnsResponse ParseResponse(ReadOnlySpan<byte> response, ushort transactionId)
    {
        if (response.Length < 12)
        {
            throw new IOException("DNS response is too short.");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response) != transactionId)
        {
            throw new IOException("DNS response transaction ID does not match the query.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0)
        {
            throw new IOException("DNS packet is not a response.");
        }

        var truncated = (flags & 0x0200) != 0;
        var responseCode = flags & 0x000F;
        if (responseCode != 0)
        {
            throw new IOException($"DNS server returned response code {responseCode}.");
        }

        if (truncated)
        {
            return new ParsedDnsResponse([], null, Truncated: true);
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            _ = ReadName(response, ref offset);
            EnsureRemaining(response, offset, 4);
            offset += 4;
        }

        var addresses = new List<IPAddress>();
        string? canonicalName = null;

        for (var i = 0; i < answerCount; i++)
        {
            _ = ReadName(response, ref offset);
            EnsureRemaining(response, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureRemaining(response, offset, dataLength);

            if (type == 1 && dataLength == 4)
            {
                addresses.Add(new IPAddress(response.Slice(offset, 4)));
            }
            else if (type == 5)
            {
                var cnameOffset = offset;
                canonicalName = ReadName(response, ref cnameOffset);
            }

            offset += dataLength;
        }

        return new ParsedDnsResponse(addresses, canonicalName, Truncated: false);
    }

    private static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var current = offset;
        var jumped = false;
        var jumps = 0;

        while (true)
        {
            EnsureRemaining(packet, current, 1);
            var length = packet[current++];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = current;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureRemaining(packet, current, 1);
                var pointer = ((length & 0x3F) << 8) | packet[current++];
                if (pointer >= packet.Length || ++jumps > 32)
                {
                    throw new IOException("Invalid DNS name compression pointer.");
                }

                if (!jumped)
                {
                    offset = current;
                    jumped = true;
                }

                current = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("Invalid DNS label length.");
            }

            EnsureRemaining(packet, current, length);
            labels.Add(Encoding.ASCII.GetString(packet.Slice(current, length)));
            current += length;
            if (!jumped)
            {
                offset = current;
            }
        }
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> packet, int offset, int required)
    {
        if (offset < 0 || required < 0 || offset > packet.Length - required)
        {
            throw new IOException("DNS response is truncated or malformed.");
        }
    }

    private static string NormalizeDnsName(string host) =>
        IdnMapping.GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant();
}

internal sealed record ParsedDnsResponse(
    IReadOnlyList<IPAddress> Addresses,
    string? CanonicalName,
    bool Truncated);
