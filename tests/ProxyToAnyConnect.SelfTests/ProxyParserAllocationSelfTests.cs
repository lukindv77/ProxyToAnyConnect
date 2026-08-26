using System.Text;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyParserAllocationSelfTests
{
    private const int WarmupIterations = 32;
    private const int MeasurementIterations = 1000;

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

            var optimizedBytes = MeasureOptimized(raw);
            var legacyBytes = MeasureLegacy(raw);
            if (optimizedBytes >= legacyBytes)
            {
                throw new InvalidOperationException(
                    $"Span header traversal allocated {optimizedBytes} bytes versus " +
                    $"{legacyBytes} bytes for the legacy Split path.");
            }

            Console.WriteLine(
                $"PASS: proxy parser span traversal reduces allocations " +
                $"({optimizedBytes / (double)MeasurementIterations:F0} vs " +
                $"{legacyBytes / (double)MeasurementIterations:F0} bytes/request)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy parser allocation regression: {ex}");
            return 1;
        }
    }

    private static long MeasureOptimized(byte[] raw)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long MeasureLegacy(byte[] raw)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasurementIterations; i++)
        {
            GC.KeepAlive(LegacySplitParse(raw));
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

    private sealed record LegacyRequest(
        string Method,
        string Target,
        string Version,
        List<LegacyHeaderLine> Headers);

    private sealed record LegacyHeaderLine(string Name, string Value);
}
