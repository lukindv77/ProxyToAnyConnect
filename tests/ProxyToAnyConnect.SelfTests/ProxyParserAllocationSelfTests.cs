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
            var raw = BuildRepresentativeHeader();

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
                GC.KeepAlive(LegacySplitParse(raw));
            }

            var optimizedParserBytes = MeasureOptimizedParser(raw);
            var legacyParserBytes = MeasureLegacyParser(raw);
            if (optimizedParserBytes >= legacyParserBytes)
            {
                throw new InvalidOperationException(
                    $"Span header traversal allocated {optimizedParserBytes} bytes versus " +
                    $"{legacyParserBytes} bytes for the legacy Split path.");
            }

            var optimizedRequest = ProxyServer.ParsedProxyRequest.Parse(raw);
            var legacyRequest = LegacySplitParse(raw);
            var optimizedOrigin = optimizedRequest.BuildOriginHeader(OriginPath);
            var legacyOrigin = LegacyBuildOriginHeader(legacyRequest, OriginPath);
            if (!optimizedOrigin.AsSpan().SequenceEqual(legacyOrigin))
            {
                throw new InvalidOperationException(
                    "Optimized origin-header filtering changed the legacy output bytes.");
            }

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(optimizedRequest.BuildOriginHeader(OriginPath));
                GC.KeepAlive(LegacyBuildOriginHeader(legacyRequest, OriginPath));
            }

            var optimizedOriginBytes = MeasureOptimizedOrigin(optimizedRequest);
            var legacyOriginBytes = MeasureLegacyOrigin(legacyRequest);
            if (optimizedOriginBytes >= legacyOriginBytes)
            {
                throw new InvalidOperationException(
                    $"Span Connection tokenization allocated {optimizedOriginBytes} bytes versus " +
                    $"{legacyOriginBytes} bytes for the legacy Split/LINQ origin-header path.");
            }

            Console.WriteLine(
                $"PASS: proxy parser/origin span paths reduce allocations " +
                $"(parse {optimizedParserBytes / (double)MeasurementIterations:F0} vs " +
                $"{legacyParserBytes / (double)MeasurementIterations:F0}; origin " +
                $"{optimizedOriginBytes / (double)MeasurementIterations:F0} vs " +
                $"{legacyOriginBytes / (double)MeasurementIterations:F0} bytes/request)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy parser allocation regression: {ex}");
            return 1;
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

    private static long MeasureLegacyOrigin(LegacyRequest request)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(LegacyBuildOriginHeader(request, OriginPath));
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

    // Test-only copy of the old BuildOriginHeader allocation shape.
    private static byte[] LegacyBuildOriginHeader(LegacyRequest request, string pathAndQuery)
    {
        var connectionTokens = request.Headers
            .Where(header => header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Split(','))
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        builder.Append(request.Method).Append(' ').Append(pathAndQuery).Append(' ').Append(request.Version).Append("\r\n");

        foreach (var header in request.Headers)
        {
            if (header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                LegacyFixedHopByHopHeaders.Contains(header.Name) ||
                connectionTokens.Contains(header.Name))
            {
                continue;
            }

            builder.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private sealed record LegacyRequest(
        string Method,
        string Target,
        string Version,
        List<LegacyHeaderLine> Headers);

    private sealed record LegacyHeaderLine(string Name, string Value);
}
