using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationParserSetupSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    // This parser is sub-microsecond to low-microsecond on hosted runners. Long
    // warmup/rounds keep tiered JIT and scheduler noise from dominating the
    // unchanged 1.25x relative policy.
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            ParserSemanticsRemainEquivalent();

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                "Cache-Control: no-store\r\n" +
                "X-Verification: ProxyToAnyConnect\r\n" +
                "Content-Length: 11\r\n\r\n" +
                "203.0.113.7");

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(VpnConnectivityVerifier.ParseHttpSuccessBody(response));
                GC.KeepAlive(SplitLinqPredecessor(response));
            }

            var optimizedBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(VpnConnectivityVerifier.ParseHttpSuccessBody(response)));
            var predecessorBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(SplitLinqPredecessor(response)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Verification text-span parser allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the Split/LINQ predecessor.");
            }

            Action optimized = () =>
                GC.KeepAlive(VpnConnectivityVerifier.ParseHttpSuccessBody(response));
            Action predecessor = () => GC.KeepAlive(SplitLinqPredecessor(response));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Verification text-span parser median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for Split/LINQ " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: verification text-span parsing reduces setup cost " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: verification parser setup regression: {ex}");
            return 1;
        }
    }

    private static void ParserSemanticsRemainEquivalent()
    {
        string[] accepted =
        [
            "HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "HTTP/1.1   204   No Content\r\nX-Test: yes\r\n\r\n",
            "HTTP/1.1 200 OK\r\ntRaNsFeR-EnCoDiNg: x-chunked-value\r\n\r\nB\r\n203.0.113.7\r\n0\r\n\r\n"
        ];

        foreach (var raw in accepted)
        {
            var response = Encoding.ASCII.GetBytes(raw);
            var actual = VpnConnectivityVerifier.ParseHttpSuccessBody(response);
            var expected = SplitLinqPredecessor(response);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    "Verification parser changed accepted response body bytes.");
            }
        }

        string[] rejected =
        [
            "HTTP/1.1 302 Found\r\nLocation: https://other.example/\r\n\r\n",
            "HTTP/1.1 nope OK\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 500 Error\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"
        ];

        foreach (var raw in rejected)
        {
            var response = Encoding.ASCII.GetBytes(raw);
            var optimizedRejected = ThrowsIOException(
                () => VpnConnectivityVerifier.ParseHttpSuccessBody(response));
            var predecessorRejected = ThrowsIOException(() => SplitLinqPredecessor(response));
            if (optimizedRejected != predecessorRejected || !optimizedRejected)
            {
                throw new InvalidOperationException(
                    "Verification parser changed rejection behavior for malformed/unsuccessful response.");
            }
        }
    }

    private static bool ThrowsIOException(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static byte[] SplitLinqPredecessor(ReadOnlySpan<byte> response)
    {
        var headerEnd = FindHeaderEnd(response);
        if (headerEnd < 0)
        {
            throw new IOException("Verification endpoint returned an incomplete HTTP response.");
        }

        var headerBytes = response[..headerEnd];
        var headerText = Encoding.Latin1.GetString(headerBytes);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.None);
        if (headerLines.Length == 0 ||
            !TryParseStatusCode(headerLines[0], out var statusCode) ||
            statusCode is < 200 or >= 300)
        {
            throw new IOException(
                $"Verification endpoint returned an unsuccessful HTTP status: '{headerLines.FirstOrDefault()}'.");
        }

        var body = response[(headerEnd + 4)..];
        var isChunked = headerLines.Skip(1).Any(line =>
            line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("chunked", StringComparison.OrdinalIgnoreCase));

        return isChunked ? DecodeChunkedBody(body) : body.ToArray();
    }

    private static bool TryParseStatusCode(string statusLine, out int statusCode)
    {
        statusCode = 0;
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], out statusCode);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'\r' &&
                data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' &&
                data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] DecodeChunkedBody(ReadOnlySpan<byte> body)
    {
        using var decoded = new MemoryStream();
        var offset = 0;

        while (true)
        {
            var lineEnd = FindCrlf(body, offset);
            if (lineEnd < 0)
            {
                throw new IOException("Malformed chunked verification response.");
            }

            var sizeText = Encoding.ASCII.GetString(body[offset..lineEnd]);
            var extensionSeparator = sizeText.IndexOf(';');
            if (extensionSeparator >= 0)
            {
                sizeText = sizeText[..extensionSeparator];
            }

            if (!int.TryParse(
                    sizeText.Trim(),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var chunkSize) ||
                chunkSize < 0)
            {
                throw new IOException("Malformed HTTP chunk size in verification response.");
            }

            offset = lineEnd + 2;
            if (chunkSize == 0)
            {
                return decoded.ToArray();
            }

            if (offset > body.Length - chunkSize - 2)
            {
                throw new IOException("Truncated chunked verification response.");
            }

            decoded.Write(body.Slice(offset, chunkSize));
            offset += chunkSize;

            if (body[offset] != (byte)'\r' || body[offset + 1] != (byte)'\n')
            {
                throw new IOException("Malformed chunk terminator in verification response.");
            }

            offset += 2;
        }
    }

    private static int FindCrlf(ReadOnlySpan<byte> data, int start)
    {
        for (var i = start; i < data.Length - 1; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
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

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
