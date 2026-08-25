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
        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(host, transactionId);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            context.LifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
            await socket.ConnectAsync(new IPEndPoint(dnsServer, 53), timeout.Token);
            await socket.SendAsync(query, SocketFlags.None, timeout.Token);

            var response = new byte[4096];
            var received = await socket.ReceiveAsync(response, SocketFlags.None, timeout.Token);
            return ParseAResponse(response.AsSpan(0, received), transactionId);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested &&
                  !context.LifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException($"DNS query to {dnsServer} timed out.", ex);
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

    private static IReadOnlyList<IPAddress> ParseAResponse(ReadOnlySpan<byte> response, ushort transactionId)
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
            offset = SkipName(response, offset);
            EnsureAvailable(response, offset, 4);
            offset += 4;
        }

        var addresses = new List<IPAddress>();
        for (var i = 0; i < answerCount; i++)
        {
            offset = SkipName(response, offset);
            EnsureAvailable(response, offset, 10);

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 8, 2));
            offset += 10;

            EnsureAvailable(response, offset, dataLength);
            if (type == 1 && recordClass == 1 && dataLength == 4)
            {
                addresses.Add(new IPAddress(response.Slice(offset, 4).ToArray()));
            }

            offset += dataLength;
        }

        return addresses;
    }

    private static int SkipName(ReadOnlySpan<byte> packet, int offset)
    {
        while (true)
        {
            EnsureAvailable(packet, offset, 1);
            var length = packet[offset];

            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureAvailable(packet, offset, 2);
                return offset + 2;
            }

            if ((length & 0xC0) != 0)
            {
                throw new IOException("Invalid DNS name encoding.");
            }

            offset++;
            EnsureAvailable(packet, offset, length);
            offset += length;
        }
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> packet, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > packet.Length - length)
        {
            throw new IOException("Truncated DNS response.");
        }
    }
}
