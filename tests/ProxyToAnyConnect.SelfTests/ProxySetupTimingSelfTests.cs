using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxySetupTimingSelfTests
{
    private const int WarmupIterations = 2048;
    private const int TimingRounds = 9;
    private const int IterationsPerRound = 32768;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const string OriginPath = "/path?q=1";

    private static readonly HashSet<string> FixedHopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
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
            var optimizedRequest = ProxyServer.ParsedProxyRequest.Parse(raw);
            var predecessorRequest = CurrentTextSpanParse(raw);
            var legacyParserRequest = LegacySplitParse(raw);

            if (!string.Equals(optimizedRequest.Method, legacyParserRequest.Method, StringComparison.Ordinal) ||
                !string.Equals(optimizedRequest.Target, legacyParserRequest.Target, StringComparison.Ordinal) ||
                !string.Equals(optimizedRequest.Version, legacyParserRequest.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Timing parser baseline does not match current request-line semantics.");
            }

            var optimizedOrigin = optimizedRequest.BuildOriginHeader(OriginPath);
            var predecessorOrigin = CurrentDirectBuildOriginHeader(predecessorRequest, OriginPath);
            if (!optimizedOrigin.AsSpan().SequenceEqual(predecessorOrigin))
            {
                throw new InvalidOperationException(
                    "Timing baseline does not match current origin-header bytes.");
            }

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
                GC.KeepAlive(LegacySplitParse(raw));
                GC.KeepAlive(optimizedRequest.BuildOriginHeader(OriginPath));
                GC.KeepAlive(CurrentDirectBuildOriginHeader(predecessorRequest, OriginPath));
            }

            Action optimizedParser = () =>
                GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
            Action predecessorParser = () =>
                GC.KeepAlive(LegacySplitParse(raw));
            Action optimizedOriginBuilder = () =>
                GC.KeepAlive(optimizedRequest.BuildOriginHeader(OriginPath));
            Action predecessorOriginBuilder = () =>
                GC.KeepAlive(CurrentDirectBuildOriginHeader(predecessorRequest, OriginPath));

            var parserTiming = MeasurePaired(optimizedParser, predecessorParser);
            var originTiming = MeasurePaired(optimizedOriginBuilder, predecessorOriginBuilder);

            AssertNoMaterialSlowdown("text-span parser", parserTiming);
            AssertNoMaterialSlowdown("stack-token origin builder", originTiming);

            Console.WriteLine(
                $"PASS: proxy setup paired timing guard " +
                $"(parser {parserTiming.OptimizedMedianNs:F0} vs " +
                $"{parserTiming.PredecessorMedianNs:F0} ns/op, " +
                $"{parserTiming.Ratio:F2}x; origin {originTiming.OptimizedMedianNs:F0} vs " +
                $"{originTiming.PredecessorMedianNs:F0} ns/op, {originTiming.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy setup timing regression: {ex}");
            return 1;
        }
    }

    private static TimingResult MeasurePaired(Action optimized, Action predecessor)
    {
        var optimizedRounds = new double[TimingRounds];
        var predecessorRounds = new double[TimingRounds];

        for (var round = 0; round < TimingRounds; round++)
        {
            if ((round & 1) == 0)
            {
                optimizedRounds[round] = MeasureNanosecondsPerOperation(optimized);
                predecessorRounds[round] = MeasureNanosecondsPerOperation(predecessor);
            }
            else
            {
                predecessorRounds[round] = MeasureNanosecondsPerOperation(predecessor);
                optimizedRounds[round] = MeasureNanosecondsPerOperation(optimized);
            }
        }

        var optimizedMedian = Median(optimizedRounds);
        var predecessorMedian = Median(predecessorRounds);
        return new TimingResult(
            optimizedMedian,
            predecessorMedian,
            optimizedMedian / predecessorMedian);
    }

    private static double MeasureNanosecondsPerOperation(Action action)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < IterationsPerRound; i++)
        {
            action();
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - started;
        return elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency) / IterationsPerRound;
    }

    private static double Median(double[] values)
    {
        var ordered = (double[])values.Clone();
        Array.Sort(ordered);
        return ordered[ordered.Length / 2];
    }

    private static void AssertNoMaterialSlowdown(string path, TimingResult result)
    {
        if (result.Ratio <= MaxMedianSlowdownRatio)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{path} median was {result.OptimizedMedianNs:F0} ns/op versus " +
            $"{result.PredecessorMedianNs:F0} ns/op for the immediate predecessor " +
            $"({result.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
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

    // Test-only mirror of the current text-span parser, used to construct the
    // request representation consumed by the immediate origin-builder predecessor.
    private static TimingRequest CurrentTextSpanParse(ReadOnlySpan<byte> headerBytes)
    {
        var text = Encoding.Latin1.GetString(headerBytes);
        var requestLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (requestLineEnd < 0)
        {
            throw new InvalidDataException("Invalid HTTP proxy request.");
        }

        var requestLine = text.AsSpan(0, requestLineEnd);
        Span<Range> requestParts = stackalloc Range[3];
        var requestPartCount = requestLine.Split(
            requestParts,
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        if (requestPartCount != 3)
        {
            throw new InvalidDataException("Invalid HTTP proxy request line.");
        }

        var method = requestLine[requestParts[0]].ToString();
        var target = requestLine[requestParts[1]].ToString();
        var version = requestLine[requestParts[2]].ToString();

        var headers = new List<TimingHeaderLine>();
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

            headers.Add(new TimingHeaderLine(
                line[..separator].Trim().ToString(),
                line[(separator + 1)..].Trim().ToString()));
            offset += lineEnd + 2;
        }

        return new TimingRequest(method, target, version, headers);
    }

    // Test-only older Split-based parser used as the timing predecessor after the
    // byte-span experiment was rejected for excessive CPU cost.
    private static TimingRequest LegacySplitParse(ReadOnlySpan<byte> headerBytes)
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

        var headers = new List<TimingHeaderLine>();
        for (var i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            var separator = lines[i].IndexOf(':');
            if (separator <= 0)
            {
                throw new InvalidDataException("Invalid HTTP header line.");
            }

            headers.Add(new TimingHeaderLine(
                lines[i][..separator].Trim(),
                lines[i][(separator + 1)..].Trim()));
        }

        return new TimingRequest(requestLine[0], requestLine[1], requestLine[2], headers);
    }

    // Test-only immediate predecessor of the bounded stack Connection-token path:
    // exact-size direct serialization backed by materialized token strings in a
    // temporary HashSet<string>.
    private static byte[] CurrentDirectBuildOriginHeader(
        TimingRequest request,
        string pathAndQuery)
    {
        var connectionTokens = CollectConnectionTokens(request);
        var byteCount = checked(
            Encoding.Latin1.GetByteCount(request.Method) + 1 +
            Encoding.Latin1.GetByteCount(pathAndQuery) + 1 +
            Encoding.Latin1.GetByteCount(request.Version) + 2);

        foreach (var header in request.Headers)
        {
            if (ShouldSkipOriginHeader(header, connectionTokens))
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
            if (ShouldSkipOriginHeader(header, connectionTokens))
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

    private static bool ShouldSkipOriginHeader(
        TimingHeaderLine header,
        HashSet<string>? connectionTokens) =>
        header.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        FixedHopByHopHeaders.Contains(header.Name) ||
        connectionTokens?.Contains(header.Name) == true;

    private static HashSet<string>? CollectConnectionTokens(TimingRequest request)
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

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);

    private sealed record TimingRequest(
        string Method,
        string Target,
        string Version,
        List<TimingHeaderLine> Headers);

    private readonly record struct TimingHeaderLine(string Name, string Value);
}
