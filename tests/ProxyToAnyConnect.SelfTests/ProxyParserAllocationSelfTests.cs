using System.Text;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyParserAllocationSelfTests
{
    private const int WarmupIterations = 32;
    private const int MeasurementIterations = 1000;
    private const string OriginPath = "/path?q=1";

    private static readonly HashSet<string> LegacyFixedHopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Proxy-Authorization",
        "Proxy-Authenticate",
        "Proxy-Connection",
        "Keep-Alive",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    public static int Run()
    {
        try
        {
            RequestLineSpanSplitMatchesLegacySemantics();
            OriginHeaderDirectSerializationMatchesCurrentBuilder();
            ConnectionTokenStackPathMatchesHashSetFallback();

            var raw = BuildRepresentativeHeader();

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
                GC.KeepAlive(CurrentTextSpanParse(raw));
                GC.KeepAlive(LegacySplitParse(raw));
            }

            var optimizedParserBytes = MeasureOptimizedParser(raw);
            var legacyParserBytes = MeasureLegacyParser(raw);
            if (optimizedParserBytes >= legacyParserBytes)
            {
                throw new InvalidOperationException(
                    $"Text-span parser allocated {optimizedParserBytes} bytes versus " +
                    $"{legacyParserBytes} bytes for the older Split parser.");
            }

            var optimizedRequest = ProxyServer.ParsedProxyRequest.Parse(raw);
            var currentTextRequest = CurrentTextSpanParse(raw);
            var optimizedOrigin = optimizedRequest.BuildOriginHeader(OriginPath);
            var currentBuilderOrigin = CurrentBuilderBuildOriginHeader(currentTextRequest, OriginPath);
            if (!optimizedOrigin.AsSpan().SequenceEqual(currentBuilderOrigin))
            {
                throw new InvalidOperationException(
                    "Direct origin-header serialization changed the current builder output bytes.");
            }

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(optimizedRequest.BuildOriginHeader(OriginPath));
                GC.KeepAlive(CurrentBuilderBuildOriginHeader(currentTextRequest, OriginPath));
            }

            var optimizedOriginBytes = MeasureOptimizedOrigin(optimizedRequest);
            var currentHashSetOriginBytes = MeasureCurrentDirectOrigin(currentTextRequest);
            var currentBuilderOriginBytes = MeasureCurrentBuilderOrigin(currentTextRequest);
            if (optimizedOriginBytes >= currentHashSetOriginBytes)
            {
                throw new InvalidOperationException(
                    $"Stack Connection token path allocated {optimizedOriginBytes} bytes versus " +
                    $"{currentHashSetOriginBytes} bytes for the immediate HashSet/token-string predecessor.");
            }

            Console.WriteLine(
                $"PASS: proxy parser/origin paths reduce allocations " +
                $"(parse text {optimizedParserBytes / (double)MeasurementIterations:F0} vs split " +
                $"{legacyParserBytes / (double)MeasurementIterations:F0}; origin stack " +
                $"{optimizedOriginBytes / (double)MeasurementIterations:F0} vs hash " +
                $"{currentHashSetOriginBytes / (double)MeasurementIterations:F0} " +
                $"(builder {currentBuilderOriginBytes / (double)MeasurementIterations:F0}) bytes/request)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy parser allocation regression: {ex}");
            return 1;
        }
    }

    private static void RequestLineSpanSplitMatchesLegacySemantics()
    {
        string[] requestLines =
        [
            "GET http://example.test/a HTTP/1.1",
            "GET http://example.test/b HTTP/1.1",
            "CONNECT example.test:443 HTTP/1.1",
            "GET http://example.test/a HTTP/1.0"
        ];

        foreach (var requestLine in requestLines)
        {
            var raw = Encoding.Latin1.GetBytes(
                requestLine + "\r\nHost: example.test\r\n\r\n");
            var optimized = ProxyServer.ParsedProxyRequest.Parse(raw);
            var legacy = LegacySplitParse(raw);

            if (!string.Equals(optimized.Method, legacy.Method, StringComparison.Ordinal) ||
                !string.Equals(optimized.Target, legacy.Target, StringComparison.Ordinal) ||
                !string.Equals(optimized.Version, legacy.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Request-line span split changed legacy semantics for '{requestLine}'. " +
                    $"optimized=({optimized.Method}|{optimized.Target}|{optimized.Version}), " +
                    $"legacy=({legacy.Method}|{legacy.Target}|{legacy.Version}).");
            }
        }
    }

    private static void OriginHeaderDirectSerializationMatchesCurrentBuilder()
    {
        (string Raw, string Path)[] cases =
        [
            (
                "GET http://example.test/simple HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "X-Keep: yes\r\n\r\n",
                "/simple"
            ),
            (
                "GET http://example.test/filter HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "Connection: X-Hop, keep-alive\r\n" +
                "X-Hop: remove-me\r\n" +
                "Proxy-Authorization: Basic secret\r\n" +
                "TE: trailers\r\n" +
                "X-Keep: retained\r\n\r\n",
                "/filter"
            ),
            (
                "GET http://example.test/multi HTTP/1.1\r\n" +
                "Host: example.test\r\n" +
                "connection: X-One\r\n" +
                "Connection: x-two, Upgrade\r\n" +
                "X-One: remove-one\r\n" +
                "X-Two: remove-two\r\n" +
                "Upgrade: websocket\r\n" +
                "X-Keep: caf\u00e9\r\n\r\n",
                "/emoji-\ud83d\ude00?q=\u00f1"
            )
        ];

        foreach (var testCase in cases)
        {
            var raw = Encoding.Latin1.GetBytes(testCase.Raw);
            var optimized = ProxyServer.ParsedProxyRequest.Parse(raw);
            var current = CurrentTextSpanParse(raw);
            var actual = optimized.BuildOriginHeader(testCase.Path);
            var expected = CurrentBuilderBuildOriginHeader(current, testCase.Path);

            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Direct origin-header serialization changed current builder bytes for '{testCase.Path}'.");
            }
        }
    }

    private static void ConnectionTokenStackPathMatchesHashSetFallback()
    {
        var commonRaw = Encoding.Latin1.GetBytes(
            "GET http://example.test/common HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "Connection: X-One, x-two, X-One\r\n" +
            "Connection: keep-alive\r\n" +
            "X-One: remove-one\r\n" +
            "X-Two: remove-two\r\n" +
            "X-Keep: retained\r\n\r\n");
        AssertOriginMatchesCurrentHashSet(commonRaw, "/common");

        var tokenCount = ProxyServer.ParsedProxyRequest.StackConnectionTokenCapacity + 3;
        var builder = new StringBuilder();
        builder.Append("GET http://example.test/overflow HTTP/1.1\r\n")
            .Append("Host: example.test\r\n")
            .Append("Connection: ");
        for (var i = 0; i < tokenCount; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append('X').Append(i);
        }

        builder.Append("\r\n");
        for (var i = 0; i < tokenCount; i++)
        {
            builder.Append('X').Append(i).Append(": remove-").Append(i).Append("\r\n");
        }

        builder.Append("X-Keep: retained\r\n\r\n");
        var overflowRaw = Encoding.Latin1.GetBytes(builder.ToString());
        AssertOriginMatchesCurrentHashSet(overflowRaw, "/overflow");

        var output = Encoding.Latin1.GetString(
            ProxyServer.ParsedProxyRequest.Parse(overflowRaw).BuildOriginHeader("/overflow"));
        if (!output.Contains("X-Keep: retained", StringComparison.Ordinal) ||
            output.Contains("X0: remove-0", StringComparison.Ordinal) ||
            output.Contains($"X{tokenCount - 1}: remove-{tokenCount - 1}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Connection-token overflow fallback did not preserve hop-by-hop filtering.");
        }
    }

    private static void AssertOriginMatchesCurrentHashSet(byte[] raw, string path)
    {
        var optimized = ProxyServer.ParsedProxyRequest.Parse(raw);
        var current = CurrentTextSpanParse(raw);
        var actual = optimized.BuildOriginHeader(path);
        var expected = CurrentDirectBuildOriginHeader(current, path);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException(
                $"Stack/fallback Connection token filtering changed current bytes for '{path}'.");
        }
    }

    private static long MeasureOptimizedParser(byte[] raw)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureCurrentTextParser(byte[] raw)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(CurrentTextSpanParse(raw));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureLegacyParser(byte[] raw)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(LegacySplitParse(raw));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureOptimizedOrigin(ProxyServer.ParsedProxyRequest request)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(request.BuildOriginHeader(OriginPath));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureCurrentDirectOrigin(CurrentTextRequest request)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(CurrentDirectBuildOriginHeader(request, OriginPath));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureCurrentBuilderOrigin(CurrentTextRequest request)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(CurrentBuilderBuildOriginHeader(request, OriginPath));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static byte[] BuildRepresentativeHeader() =>
        Encoding.Latin1.GetBytes(
            "GET http://example.test/path?q=1 HTTP/1.1\r\n" +
            "Host: example.test\r\n" +
            "User-Agent: ProxyToAnyConnect-SelfTest/1.0\r\n" +
            "Accept: text/html,application/xhtml+xml\r\n" +
            "Accept-Language: en-US,en;q=0.9\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Pragma: no-cache\r\n" +
            "Connection: X-Hop, keep-alive\r\n" +
            "X-Hop: remove-me\r\n" +
            "X-Request-Id: 0123456789abcdef\r\n" +
            "X-Forward-Test: retained\r\n" +
            "Cookie: a=1; b=2; c=3\r\n" +
            "Proxy-Connection: keep-alive\r\n\r\n");

    // Test-only mirror of the current text-span production parser. It is retained
    // only to provide the request representation for the immediate origin-builder
    // predecessors used by allocation comparisons.
    private static CurrentTextRequest CurrentTextSpanParse(ReadOnlySpan<byte> headerBytes)
    {
        var text = Encoding.Latin1.GetString(headerBytes);
        var requestLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (requestLineEnd < 0)
        {
            throw new InvalidDataException("Invalid HTTP proxy request.");
        }

        var requestLine = text.AsSpan(0, requestLineEnd);
        var firstSpace = requestLine.IndexOf(' ');
        if (firstSpace <= 0 || firstSpace >= requestLine.Length - 1)
        {
            throw new InvalidDataException("Invalid HTTP proxy request line.");
        }

        var afterFirstSpace = requestLine[(firstSpace + 1)..];
        var secondSpaceOffset = afterFirstSpace.IndexOf(' ');
        if (secondSpaceOffset <= 0)
        {
            throw new InvalidDataException("Invalid HTTP proxy request line.");
        }

        var secondSpace = firstSpace + 1 + secondSpaceOffset;
        var versionSpan = requestLine[(secondSpace + 1)..];
        if (versionSpan.IsEmpty || versionSpan.IndexOf(' ') >= 0)
        {
            throw new InvalidDataException("Invalid HTTP proxy request line.");
        }

        var method = requestLine[..firstSpace].ToString();
        var target = requestLine[(firstSpace + 1)..secondSpace].ToString();
        var version = versionSpan.ToString();

        var headers = new List<CurrentTextHeaderLine>();
        var offset = requestLineEnd + 2;
        while (offset < text.Length)
        {
            var remaining = text.AsSpan(offset);
            var lineEnd = remaining.IndexOf("\r\n".AsSpan());
            if (lineEnd < 0)
            {
                throw new InvalidDataException("Invalid HTTP proxy request.");
            }

            if (lineEnd == 0)
            {
                break;
            }

            var line = remaining[..lineEnd];
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Invalid HTTP header line.");
            }

            headers.Add(new CurrentTextHeaderLine(
                line[..separator].Trim().ToString(),
                line[(separator + 1)..].Trim().ToString()));
            offset += lineEnd + 2;
        }

        return new CurrentTextRequest(method, target, version, headers);
    }

    // Test-only copy of the pre-refactor parsing shape. This intentionally uses
    // string.Split for every CRLF line so the allocation comparison stays local
    // to one runtime/runner and does not depend on machine-specific memory sizes.
    private static LegacyRequest LegacySplitParse(ReadOnlySpan<byte> headerBytes)
    {
        var text = Encoding.Latin1.GetString(headerBytes);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length < 2)
        {
            throw new InvalidDataException("Invalid HTTP proxy request.");
        }

        var requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3)
        {
            throw new InvalidDataException("Invalid HTTP proxy request line.");
        }

        var headers = new List<LegacyHeaderLine>();
        for (var i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Invalid HTTP header line.");
            }

            headers.Add(new LegacyHeaderLine(
                lines[i][..separator].Trim(),
                lines[i][(separator + 1)..].Trim()));
        }

        return new LegacyRequest(requestLine[0], requestLine[1], requestLine[2], headers);
    }

    // Test-only copy of the immediate pre-stack-token production path: direct
    // exact-size Latin-1 serialization plus a temporary HashSet<string> containing
    // materialized Connection tokens. This isolates the allocation saved by the
    // bounded stack token references.
    private static byte[] CurrentDirectBuildOriginHeader(CurrentTextRequest request, string pathAndQuery)
    {
        var connectionTokens = CollectCurrentConnectionTokens(request);
        var byteCount = checked(
            Encoding.Latin1.GetByteCount(request.Method) + 1 +
            Encoding.Latin1.GetByteCount(pathAndQuery) + 1 +
            Encoding.Latin1.GetByteCount(request.Version) + 2);

        foreach (var header in request.Headers)
        {
            if (ShouldSkipCurrentOriginHeader(header, connectionTokens))
            {
                continue;
            }

            byteCount = checked(
                byteCount + Encoding.Latin1.GetByteCount(header.Name) + 2 +
                Encoding.Latin1.GetByteCount(header.Value) + 2);
        }

        byteCount = checked(byteCount + "Connection: close\r\n\r\n"u8.Length);
        var result = GC.AllocateUninitializedArray<byte>(byteCount);
        var destination = result.AsSpan();
        var written = 0;
        written += Encoding.Latin1.GetBytes(request.Method.AsSpan(), destination[written..]);
        destination[written++] = (byte)' ';
        written += Encoding.Latin1.GetBytes(pathAndQuery.AsSpan(), destination[written..]);
        destination[written++] = (byte)' ';
        written += Encoding.Latin1.GetBytes(request.Version.AsSpan(), destination[written..]);
        "\r\n"u8.CopyTo(destination[written..]);
        written += 2;

        foreach (var header in request.Headers)
        {
            if (ShouldSkipCurrentOriginHeader(header, connectionTokens))
            {
                continue;
            }

            written += Encoding.Latin1.GetBytes(header.Name.AsSpan(), destination[written..]);
            ": "u8.CopyTo(destination[written..]);
            written += 2;
            written += Encoding.Latin1.GetBytes(header.Value.AsSpan(), destination[written..]);
            "\r\n"u8.CopyTo(destination[written..]);
            written += 2;
        }

        "Connection: close\r\n\r\n"u8.CopyTo(destination[written..]);
        return result;
    }

    private static bool ShouldSkipCurrentOriginHeader(
        CurrentTextHeaderLine header,
        HashSet<string>? connectionTokens) =>
        header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        LegacyFixedHopByHopHeaders.Contains(header.Name) ||
        connectionTokens?.Contains(header.Name) == true;

    // Test-only copy of the production BuildOriginHeader shape immediately before
    // direct byte serialization. Connection tokenization is intentionally the
    // already-optimized span/lazy-set form so the allocation comparison isolates
    // StringBuilder + ToString() + Encoding.GetBytes(byte[]) materialization.
    private static byte[] CurrentBuilderBuildOriginHeader(CurrentTextRequest request, string pathAndQuery)
    {
        var connectionTokens = CollectCurrentConnectionTokens(request);
        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(pathAndQuery).Append(' ').Append(request.Version).Append("\r\n");

        foreach (var header in request.Headers)
        {
            if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                LegacyFixedHopByHopHeaders.Contains(header.Name) ||
                connectionTokens?.Contains(header.Name) == true)
            {
                continue;
            }

            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static HashSet<string>? CollectCurrentConnectionTokens(CurrentTextRequest request)
    {
        HashSet<string>? tokens = null;
        foreach (var header in request.Headers)
        {
            if (!header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var remaining = header.Value.AsSpan();
            while (!remaining.IsEmpty)
            {
                var comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining[..comma]).Trim();
                if (!token.IsEmpty)
                {
                    (tokens ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                        .Add(token.ToString());
                }

                if (comma < 0)
                {
                    break;
                }

                remaining = remaining[(comma + 1)..];
            }
        }

        return tokens;
    }

    private sealed record CurrentTextRequest(
        string Method,
        string Target,
        string Version,
        List<CurrentTextHeaderLine> Headers);

    private readonly record struct CurrentTextHeaderLine(string Name, string Value);

    private sealed record LegacyRequest(
        string Method,
        string Target,
        string Version,
        List<LegacyHeaderLine> Headers);

    private sealed record LegacyHeaderLine(string Name, string Value);
}
