using System.Buffers;
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
    private const int UdpReceiveBufferBytes = 4 * 1024;
    private readonly int _timeoutMilliseconds;
    private readonly L2tpDnsCache? _cache;

    public L2tpDnsResolver(int timeoutMilliseconds = 6000, L2tpDnsCache? cache = null)
    {
        if (timeoutMilliseconds is < 250 or > 60000)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }

        _timeoutMilliseconds = timeoutMilliseconds;
        _cache = cache;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveIPv4Async(
        string host,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        var literal = ParseCanonicalIPv4Literal(host);
        if (literal is not null)
        {
            return [literal];
        }

        if (context.DnsServers.Count == 0)
        {
            throw new InvalidOperationException(
                $"L2TP interface '{context.InterfaceName}' did not provide an IPv4 DNS server.");
        }

        var asciiHost = NormalizeDnsName(host);
        if (_cache is not null && _cache.TryGet(asciiHost, context, out var cached))
        {
            return cached;
        }

        Exception? lastError = null;

        foreach (var dnsServer in context.DnsServers)
        {
            try
            {
                var result = await QueryAsync(asciiHost, dnsServer, context, cancellationToken);
                if (result.Addresses.Count > 0)
                {
                    if (_cache is not null && result.MinimumTtlSeconds > 0)
                    {
                        _cache.Set(
                            asciiHost,
                            context,
                            result.Addresses,
                            TimeSpan.FromSeconds(result.MinimumTtlSeconds));
                    }

                    return result.Addresses;
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

    internal static IPAddress? ParseCanonicalIPv4Literal(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!IPAddress.TryParse(host, out var literal))
        {
            return null;
        }

        if (literal.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("IPv6 targets are not supported yet.");
        }

        var canonicalLiteral = literal.ToString();
        if (!host.Equals(canonicalLiteral, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IPv4 literal '{host}' is not in canonical dotted-decimal form.");
        }

        return literal;
    }

    private async Task<DnsResolutionResult> QueryAsync(
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
                host,
                dnsServer,
                context,
                visitedNames: null,
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

    private static async Task<DnsResolutionResult> ResolveCoreAsync(
        string host,
        IPAddress dnsServer,
        VpnContext context,
        HashSet<string>? visitedNames,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxCnameDepth)
        {
            throw new IOException("DNS CNAME chain exceeded the maximum supported depth.");
        }

        if (!TryEnterDnsName(visitedNames, host))
        {
            throw new IOException($"DNS CNAME loop detected at '{host}'.");
        }

        var transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue + 1);
        var query = BuildQuery(host, transactionId);

        var parsed = await QueryUdpAsync(
            query,
            transactionId,
            host,
            dnsServer,
            context,
            cancellationToken);

        if (parsed.Truncated)
        {
            var tcpResponse = await QueryTcpAsync(query, dnsServer, context, cancellationToken);
            parsed = ParseResponse(tcpResponse, transactionId, host);
        }

        if (parsed.Addresses.Count > 0)
        {
            return new DnsResolutionResult(
                parsed.Addresses,
                parsed.MinimumTtlSeconds ?? 0);
        }

        if (parsed.CanonicalName is not null)
        {
            visitedNames = EnsureVisitedNamesForCname(visitedNames, host);
            var recursive = await ResolveCoreAsync(
                NormalizeDnsName(parsed.CanonicalName),
                dnsServer,
                context,
                visitedNames,
                depth + 1,
                cancellationToken);

            var cnameTtl = parsed.MinimumTtlSeconds ?? 0;
            var effectiveTtl = cnameTtl == 0 || recursive.MinimumTtlSeconds == 0
                ? 0
                : Math.Min(cnameTtl, recursive.MinimumTtlSeconds);
            return new DnsResolutionResult(recursive.Addresses, effectiveTtl);
        }

        return new DnsResolutionResult([], 0);
    }

    internal static bool TryEnterDnsName(HashSet<string>? visitedNames, string host) =>
        visitedNames is null || visitedNames.Add(host);

    internal static HashSet<string> EnsureVisitedNamesForCname(
        HashSet<string>? visitedNames,
        string currentHost)
    {
        if (visitedNames is not null)
        {
            return visitedNames;
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentHost
        };
    }

    private static async Task<ParsedDnsResponse> QueryUdpAsync(
        byte[] query,
        ushort transactionId,
        string expectedHost,
        IPAddress dnsServer,
        VpnContext context,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        WindowsSocketInterfaceBinder.BindToIPv4Interface(socket, context.InterfaceIndex);
        socket.Bind(new IPEndPoint(context.LocalIPv4, 0));
        socket.Connect(new IPEndPoint(dnsServer, 53));

        await socket.SendAsync(query, SocketFlags.None, cancellationToken);

        var buffer = ArrayPool<byte>.Shared.Rent(UdpReceiveBufferBytes);
        try
        {
            var received = await socket.ReceiveAsync(
                buffer.AsMemory(0, UdpReceiveBufferBytes),
                SocketFlags.None,
                cancellationToken);

            if (received >= UdpReceiveBufferBytes)
            {
                return new ParsedDnsResponse([], null, Truncated: true, MinimumTtlSeconds: null);
            }

            return ParseResponse(buffer.AsSpan(0, received), transactionId, expectedHost);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
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

        var response = GC.AllocateUninitializedArray<byte>(responseLength);
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

    internal static byte[] BuildQuery(string host, ushort transactionId)
    {
        var encodedHostByteCount = Encoding.ASCII.GetByteCount(host);
        var labelStart = 0;
        while (labelStart <= host.Length)
        {
            var remaining = host.AsSpan(labelStart);
            var dot = remaining.IndexOf('.');
            var label = dot < 0 ? remaining : remaining[..dot];
            var labelByteCount = Encoding.ASCII.GetByteCount(label);
            if (labelByteCount is 0 or > 63)
            {
                throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
            }

            if (dot < 0)
            {
                break;
            }

            labelStart += dot + 1;
        }

        // Replacing each encoded dot with a one-byte DNS label length leaves the
        // QNAME one byte longer than the encoded host, before the terminal zero.
        var qnameByteCount = checked(encodedHostByteCount + 1);
        var query = GC.AllocateUninitializedArray<byte>(
            checked(12 + qnameByteCount + 1 + 4));
        var destination = query.AsSpan();
        destination[..12].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(destination, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], 1);

        var written = 12;
        labelStart = 0;
        while (labelStart <= host.Length)
        {
            var remaining = host.AsSpan(labelStart);
            var dot = remaining.IndexOf('.');
            var label = dot < 0 ? remaining : remaining[..dot];
            var lengthOffset = written++;
            var labelByteCount = Encoding.ASCII.GetBytes(label, destination[written..]);
            destination[lengthOffset] = checked((byte)labelByteCount);
            written += labelByteCount;

            if (dot < 0)
            {
                break;
            }

            labelStart += dot + 1;
        }

        destination[written++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[written..], 1);
        written += 2;
        BinaryPrimitives.WriteUInt16BigEndian(destination[written..], 1);
        return query;
    }

    internal static ParsedDnsResponse ParseResponse(
        ReadOnlySpan<byte> response,
        ushort transactionId,
        string expectedHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHost);

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
            return new ParsedDnsResponse([], null, Truncated: true, MinimumTtlSeconds: null);
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        if (questionCount != 1)
        {
            throw new IOException("DNS response must contain exactly one question.");
        }

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);
        var offset = 12;
        var questionNameOffset = offset;
        SkipName(response, ref offset);
        if (!DnsNameEqualsAscii(response, questionNameOffset, expectedHost))
        {
            throw new IOException("DNS response question name does not match the query.");
        }

        EnsureRemaining(response, offset, 4);
        var questionType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
        var questionClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
        if (questionType != 1 || questionClass != 1)
        {
            throw new IOException("DNS response question is not the requested A/IN question.");
        }
        offset += 4;

        IPAddress? firstAddress = null;
        List<IPAddress>? additionalAddresses = null;
        string? canonicalName = null;
        uint? minimumTtlSeconds = null;

        for (var i = 0; i < answerCount; i++)
        {
            var ownerOffset = offset;
            SkipName(response, ref offset);
            EnsureRemaining(response, offset, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(response[offset..]);
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 2)..]);
            var ttlSeconds = BinaryPrimitives.ReadUInt32BigEndian(response[(offset + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..]);
            offset += 10;
            EnsureRemaining(response, offset, dataLength);
            var ownedByQuery = DnsNameEqualsAscii(response, ownerOffset, expectedHost);

            if (ownedByQuery && recordClass == 1 && type == 1 && dataLength == 4)
            {
                var address = new IPAddress(response.Slice(offset, 4));
                if (firstAddress is null)
                {
                    firstAddress = address;
                }
                else
                {
                    (additionalAddresses ??= new List<IPAddress>()).Add(address);
                }

                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }
            else if (ownedByQuery && recordClass == 1 && type == 5)
            {
                var cnameOffset = offset;
                var parsedCanonicalName = ReadName(response, ref cnameOffset);
                if (cnameOffset != checked(offset + dataLength))
                {
                    throw new IOException("DNS CNAME RDATA length does not match its encoded name.");
                }

                canonicalName = parsedCanonicalName;
                minimumTtlSeconds = MinTtl(minimumTtlSeconds, ttlSeconds);
            }

            offset += dataLength;
        }

        IReadOnlyList<IPAddress> parsedAddresses;
        if (firstAddress is null)
        {
            parsedAddresses = Array.Empty<IPAddress>();
        }
        else if (additionalAddresses is null)
        {
            parsedAddresses = new[] { firstAddress };
        }
        else
        {
            var result = GC.AllocateUninitializedArray<IPAddress>(additionalAddresses.Count + 1);
            result[0] = firstAddress;
            additionalAddresses.CopyTo(result, 1);
            parsedAddresses = result;
        }

        return new ParsedDnsResponse(
            parsedAddresses,
            canonicalName,
            Truncated: false,
            MinimumTtlSeconds: minimumTtlSeconds);
    }

    private static bool DnsNameEqualsAscii(
        ReadOnlySpan<byte> packet,
        int offset,
        ReadOnlySpan<char> expectedHost)
    {
        var current = offset;
        var expectedOffset = 0;
        var hasLabel = false;
        var jumps = 0;

        while (true)
        {
            EnsureRemaining(packet, current, 1);
            var length = packet[current++];
            if (length == 0)
            {
                return expectedOffset == expectedHost.Length;
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureRemaining(packet, current, 1);
                var pointer = ((length & 0x3F) << 8) | packet[current++];
                if (pointer >= packet.Length || ++jumps > 32)
                {
                    throw new IOException("Invalid DNS name compression pointer.");
                }

                current = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("Invalid DNS label length.");
            }

            EnsureRemaining(packet, current, length);
            if (hasLabel)
            {
                if (expectedOffset >= expectedHost.Length || expectedHost[expectedOffset] != '.')
                {
                    return false;
                }

                expectedOffset++;
            }

            if (expectedOffset > expectedHost.Length - length)
            {
                return false;
            }

            for (var i = 0; i < length; i++)
            {
                var expected = expectedHost[expectedOffset + i];
                if (expected > 0x7F || FoldAsciiCase(packet[current + i]) != FoldAsciiCase((byte)expected))
                {
                    return false;
                }
            }

            expectedOffset += length;
            current += length;
            hasLabel = true;
        }
    }

    private static byte FoldAsciiCase(byte value) =>
        value is >= 0x41 and <= 0x5A ? (byte)(value + 0x20) : value;

    private static uint MinTtl(uint? current, uint candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);

    internal static void SkipName(ReadOnlySpan<byte> packet, ref int offset)
    {
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

                return;
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
            current += length;
            if (!jumped)
            {
                offset = current;
            }
        }
    }

    internal static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        Span<char> initial = stackalloc char[256];
        Span<char> destination = initial;
        char[]? rented = null;
        var written = 0;
        var hasLabel = false;
        var current = offset;
        var jumped = false;
        var jumps = 0;

        try
        {
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

                    return written == 0
                        ? string.Empty
                        : new string(destination[..written]);
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
                var required = checked(written + (hasLabel ? 1 : 0) + length);
                if (required > destination.Length)
                {
                    var doubled = destination.Length <= int.MaxValue / 2
                        ? destination.Length * 2
                        : int.MaxValue;
                    var replacement = ArrayPool<char>.Shared.Rent(Math.Max(required, doubled));
                    destination[..written].CopyTo(replacement);
                    if (rented is not null)
                    {
                        ArrayPool<char>.Shared.Return(rented, clearArray: false);
                    }

                    rented = replacement;
                    destination = rented;
                }

                if (hasLabel)
                {
                    destination[written++] = '.';
                }

                written += Encoding.ASCII.GetChars(
                    packet.Slice(current, length),
                    destination[written..]);
                hasLabel = true;
                current += length;
                if (!jumped)
                {
                    offset = current;
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented, clearArray: false);
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

    internal static string NormalizeDnsName(string host) =>
        NormalizeDnsHostStrict(host);

    internal static string NormalizeDnsHostStrict(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            throw new InvalidOperationException("DNS host is empty.");
        }

        if (host.EndsWith(".", StringComparison.Ordinal))
        {
            host = host[..^1];
            if (host.Length == 0 || host.EndsWith(".", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DNS host has an invalid terminal root label.");
            }
        }

        var asciiHost = IdnMapping.GetAscii(host).ToLowerInvariant();
        if (asciiHost.Length > 253)
        {
            throw new InvalidOperationException("DNS host exceeds 253 ASCII characters.");
        }

        var labelStart = 0;
        for (var i = 0; i <= asciiHost.Length; i++)
        {
            if (i < asciiHost.Length && asciiHost[i] != '.')
            {
                continue;
            }

            var labelLength = i - labelStart;
            if (labelLength is 0 or > 63 ||
                asciiHost[labelStart] == '-' ||
                asciiHost[i - 1] == '-')
            {
                throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
            }

            for (var j = labelStart; j < i; j++)
            {
                var character = asciiHost[j];
                if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                {
                    throw new InvalidOperationException($"Invalid DNS label in '{host}'.");
                }
            }

            labelStart = i + 1;
        }

        return asciiHost;
    }

    private readonly record struct DnsResolutionResult(
        IReadOnlyList<IPAddress> Addresses,
        uint MinimumTtlSeconds);
}

internal readonly record struct ParsedDnsResponse(
    IReadOnlyList<IPAddress> Addresses,
    string? CanonicalName,
    bool Truncated,
    uint? MinimumTtlSeconds);
