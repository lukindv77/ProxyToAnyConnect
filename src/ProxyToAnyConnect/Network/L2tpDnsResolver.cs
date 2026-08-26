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

    private static async Task<IReadOnlyList<IPAddress>> QueryAsync(
        string host,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));

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
            when (!cancellationToken.IsCancellationRequested &&
                  !context.LifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DNS query to {dnsServer} timed out.", ex);
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
            throw new IOException($"DNS CNAME chain for '{host}' exceeded {MaxCnameDepth} hops.");
        }

        if (!visitedNames.Add(host))
        {
            throw new IOException($"DNS CNAME loop detected at '{host}'.");
        }

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(host, transactionId);
        var udpResponse = await QueryUdpAsync(query, dnsServer, context, cancellationToken);
        var parsed = ParseResponse(udpResponse, transactionId);

        if (parsed.Truncated)
        {
            var tcpResponse = await QueryTcpAsync(query, dnsServer, context, cancellationToken);
            parsed = ParseResponse(tcpResponse, transactionId);
            if (parsed.Truncated)
            {
                throw new IOException("DNS response remained truncated after TCP fallback.");
            }
        }

        if (parsed.Addresses.Count > 0)
        {
            return parsed.Addresses.Distinct().ToArray();
        }

        if (parsed.CanonicalName is not null)
        {
            return await ResolveCoreAsync(
                parsed.CanonicalName,
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
        using var socket = CreateBoundSocket(
            SocketType.Dgram,
            ProtocolType.Udp,
            context);

        await socket.ConnectAsync(new IPEndPoint(dnsServer, 53), cancellationToken);
        await socket.SendAsync(query, SocketFlags.None, cancellationToken);

        var response = new byte[4096];
        var received = await socket.ReceiveAsync(response, SocketFlags.None, cancellationToken);
        return response.AsSpan(0, received).ToArray();
    }

    private static async Task<byte[]> QueryTcpAsync(
        byte[] query,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var socket = CreateBoundSocket(
            SocketType.Stream,
            ProtocolType.Tcp,
            context);

        await socket.ConnectAsync(new IPEndPoint(dnsServer, 53), cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        var framedQuery = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framedQuery.AsSpan(0, 2), checked((ushort)query.Length));
        query.CopyTo(framedQuery.AsSpan(2));

        await stream.WriteAsync(framedQuery, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var lengthPrefix = new byte[2];
        await ReadExactlyAsync(stream, lengthPrefix, cancellationToken);
        var responseLength = BinaryPrimitives.ReadUInt16BigEndian(lengthPrefix);
        if (responseLength < 12 || responseLength > MaxDnsPacketBytes)
        {
            throw new IOException($"Invalid DNS-over-TCP response length {responseLength}.");
        }

        var response = new byte[responseLength];
        await ReadExactlyAsync(stream, response, cancellationToken);
        return response;
    }

    private static Socket CreateBoundSocket(
        SocketType socketType,
        ProtocolType protocolType,
        VpnContext context)
    {
        var socket = new Socket(AddressFamily.InterNetwork, socketType, protocolType);

        try
        {
            socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
            WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new IOException("DNS-over-TCP connection closed before the complete response was received.");
            }

            offset += read;
        }
    }

    private static byte[] BuildQuery(string host, ushort transactionId)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header[0..2], transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x0100); // Recursion desired.
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1); // QDCOUNT.
        stream.Write(header);

        foreach (var label in host.Split('.'))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            if (labelBytes.Length is 0 or > 63)
            {
                throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
            }

            stream.WriteByte((byte)labelBytes.Length);
            stream.Write(labelBytes);
        }

        stream.WriteByte(0);
        Span<byte> question = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(question[0..2], 1); // A.
        BinaryPrimitives.WriteUInt16BigEndian(question[2..4], 1); // IN.
        stream.Write(question);
        return stream.ToArray();
    }

    internal static DnsResponseParseResult ParseResponse(
        ReadOnlySpan<byte> response,
        ushort transactionId)
    {
        if (response.Length < 12)
        {
            throw new IOException("DNS response is too short.");
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response[0..2]) != transactionId)
        {
            throw new IOException("DNS transaction ID mismatch.");
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..4]);
        if ((flags & 0x8000) == 0)
        {
            throw new IOException("DNS packet is not a response.");
        }

        if ((flags & 0x0200) != 0)
        {
            return new DnsResponseParseResult([], null, Truncated: true);
        }

        var responseCode = flags & 0x000F;
        if (responseCode != 0)
        {
            throw new IOException($"DNS server returned response code {responseCode}.");
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..8]);
        var offset = 12;

        for (var i = 0; i < questionCount; i++)
        {
            _ = ReadName(response, offset, out offset);
            EnsureAvailable(response, offset, 4);
            offset += 4;
        }

        var addresses = new List<IPAddress>();
        string? canonicalName = null;

        for (var i = 0; i < answerCount; i++)
        {
            _ = ReadName(response, offset, out offset);
            EnsureAvailable(response, offset, 10);

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 8, 2));
            offset += 10;

            EnsureAvailable(response, offset, dataLength);
            if (recordClass == 1)
            {
                if (type == 1 && dataLength == 4)
                {
                    addresses.Add(new IPAddress(response.Slice(offset, 4).ToArray()));
                }
                else if (type == 5)
                {
                    var cname = ReadName(response, offset, out _);
                    if (!string.IsNullOrWhiteSpace(cname))
                    {
                        canonicalName = NormalizeDnsName(cname);
                    }
                }
            }

            offset += dataLength;
        }

        return new DnsResponseParseResult(addresses, canonicalName, Truncated: false);
    }

    private static string ReadName(
        ReadOnlySpan<byte> packet,
        int offset,
        out int nextOffset)
    {
        var labels = new List<string>();
        var visitedPointers = new HashSet<int>();
        var current = offset;
        var jumped = false;
        nextOffset = offset;

        while (true)
        {
            EnsureAvailable(packet, current, 1);
            var length = packet[current];

            if (length == 0)
            {
                if (!jumped)
                {
                    nextOffset = current + 1;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(packet, current, 2);
                var pointer = ((length & 0x3F) << 8) | packet[current + 1];
                if (pointer >= packet.Length)
                {
                    throw new IOException("DNS compression pointer is outside the packet.");
                }

                if (!visitedPointers.Add(pointer))
                {
                    throw new IOException("DNS compression pointer loop detected.");
                }

                if (!jumped)
                {
                    nextOffset = current + 2;
                    jumped = true;
                }

                current = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw new IOException("Invalid DNS name encoding.");
            }

            current++;
            EnsureAvailable(packet, current, length);
            labels.Add(Encoding.ASCII.GetString(packet.Slice(current, length)));
            current += length;

            if (!jumped)
            {
                nextOffset = current;
            }
        }
    }

    private static string NormalizeDnsName(string name) =>
        name.Trim().TrimEnd('.');

    private static void EnsureAvailable(ReadOnlySpan<byte> packet, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > packet.Length - length)
        {
            throw new IOException("Truncated DNS response.");
        }
    }
}

internal sealed record DnsResponseParseResult(
    IReadOnlyList<IPAddress> Addresses,
    string? CanonicalName,
    bool Truncated);
