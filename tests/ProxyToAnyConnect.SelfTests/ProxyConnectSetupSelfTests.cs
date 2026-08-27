using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyConnectSetupSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 16384;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            ConnectParsingPreservesSemanticsAndValidation();

            var raw = BuildRepresentativeConnectHeader();
            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
                GC.KeepAlive(CurrentFullMaterializationParse(raw));
            }

            var optimizedBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw)));
            var predecessorBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(CurrentFullMaterializationParse(raw)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"CONNECT syntax-only parse allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for security-equivalent full header materialization.");
            }

            Action optimized = () => GC.KeepAlive(ProxyServer.ParsedProxyRequest.Parse(raw));
            Action predecessor = () => GC.KeepAlive(CurrentFullMaterializationParse(raw));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"CONNECT syntax-only parse median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for security-equivalent full materialization " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: CONNECT header syntax-only parsing reduces setup cost against security-equivalent materialization " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/request; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: CONNECT setup regression: {ex}");
            return 1;
        }
    }

    private static void ConnectParsingPreservesSemanticsAndValidation()
    {
        var raw = Encoding.Latin1.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            "Host: example.test:443\r\n" +
            "Proxy-Authorization: Basic ignored-by-tunnel-setup\r\n" +
            "X-Test: caf\u00e9\r\n\r\n");
        var optimized = ProxyServer.ParsedProxyRequest.Parse(raw);
        var predecessor = CurrentFullMaterializationParse(raw);

        if (!string.Equals(optimized.Method, predecessor.Method, StringComparison.Ordinal) ||
            !string.Equals(optimized.Target, predecessor.Target, StringComparison.Ordinal) ||
            !string.Equals(optimized.Version, predecessor.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CONNECT syntax-only path changed request-line semantics.");
        }

        AssertBothRejectMalformed(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            "Host: example.test:443\r\n" +
            "BrokenHeader\r\n\r\n");
        AssertBothRejectMalformed(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            ": missing-name\r\n\r\n");
        AssertBothRejectMalformed(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            "Bad Name: rejected\r\n\r\n");
        AssertBothRejectMalformed(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            "X-Test: bad\0value\r\n\r\n");
    }

    private static void AssertBothRejectMalformed(string request)
    {
        var raw = Encoding.Latin1.GetBytes(request);
        var optimizedRejected = ThrowsInvalidData(() => ProxyServer.ParsedProxyRequest.Parse(raw));
        var predecessorRejected = ThrowsInvalidData(() => CurrentFullMaterializationParse(raw));
        if (!optimizedRejected || !predecessorRejected)
        {
            throw new InvalidOperationException(
                $"CONNECT malformed-header rejection changed for '{request}'.");
        }
    }

    private static bool ThrowsInvalidData(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static long MeasureAllocatedBytes(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
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

        var pairedRatios = new double[TimingRounds];
        for (var round = 0; round < TimingRounds; round++)
        {
            pairedRatios[round] = optimizedRounds[round] / predecessorRounds[round];
        }
        var optimizedMedian = Median(optimizedRounds);
        var predecessorMedian = Median(predecessorRounds);
        return new TimingResult(optimizedMedian, predecessorMedian, Median(pairedRatios));
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

    private static byte[] BuildRepresentativeConnectHeader() =>
        Encoding.Latin1.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\n" +
            "Host: example.test:443\r\n" +
            "User-Agent: ProxyToAnyConnect-SelfTest/1.0\r\n" +
            "Proxy-Authorization: Basic placeholder\r\n" +
            "Connection: keep-alive\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "X-Request-Id: 0123456789abcdef\r\n" +
            "X-Trace-One: alpha\r\n" +
            "X-Trace-Two: beta\r\n" +
            "X-Trace-Three: gamma\r\n" +
            "X-Trace-Four: delta\r\n" +
            "Cookie: a=1; b=2; c=3\r\n\r\n");

    // Test-only semantic-equivalent full-materialization comparator. It performs
    // the same header-name and field-value validation as production before
    // materializing every header name/value, so the timing gate compares equal work.
    private static BaselineRequest CurrentFullMaterializationParse(ReadOnlySpan<byte> headerBytes)
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

        var methodSpan = requestLine[..firstSpace];
        var targetSpan = requestLine[(firstSpace + 1)..secondSpace];
        if (!IsValidBaselineHeaderName(methodSpan))
        {
            throw new InvalidDataException("Invalid HTTP method token.");
        }

        if (!versionSpan.SequenceEqual("HTTP/1.0".AsSpan()) &&
            !versionSpan.SequenceEqual("HTTP/1.1".AsSpan()))
        {
            throw new InvalidDataException("Unsupported HTTP request version.");
        }

        var method = methodSpan.ToString();
        var target = targetSpan.ToString();
        var version = versionSpan.ToString();
        var headers = new List<BaselineHeaderLine>();
        var offset = requestLineEnd + 2;
        while (offset < text.Length)
        {
            var remaining = text.AsSpan(offset);
            var lineEnd = remaining.IndexOf("\r\n".AsSpan());
            if (lineEnd < 0)
            {
                throw new InvalidDataException("Invalid HTTP header line.");
            }

            if (lineEnd == 0)
            {
                break;
            }

            var line = remaining[..lineEnd];
            var separator = line.IndexOf(':');
            if (separator <= 0 || char.IsWhiteSpace(line[separator - 1]))
            {
                throw new InvalidDataException("Invalid HTTP header line.");
            }

            var name = line[..separator];
            if (!IsValidBaselineHeaderName(name))
            {
                throw new InvalidDataException("Invalid HTTP header name.");
            }

            var rawValue = line[(separator + 1)..];
            if (!IsValidBaselineHeaderValue(rawValue))
            {
                throw new InvalidDataException("Invalid HTTP header field value.");
            }

            headers.Add(new BaselineHeaderLine(
                name.ToString(),
                rawValue.Trim().ToString()));
            offset += lineEnd + 2;
        }

        return new BaselineRequest(method, target, version, headers);
    }

    private static bool IsValidBaselineHeaderName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!((uint)(character - '0') <= 9 ||
                  (uint)((character | 0x20) - 'a') <= 25 ||
                  character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidBaselineHeaderValue(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if ((character < 0x20 && character != '\t') || character == 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double PairedRatioMedian);

    private sealed record BaselineRequest(
        string Method,
        string Target,
        string Version,
        List<BaselineHeaderLine> Headers);

    private readonly record struct BaselineHeaderLine(string Name, string Value);
}
