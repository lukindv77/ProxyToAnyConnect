using System.Diagnostics;
using System.Globalization;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationChunkedDecodeSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            DecoderSemanticsRemainEquivalent();

            var body = Encoding.ASCII.GetBytes(
                "8;first=yes\r\nabcdefgh\r\n" +
                "8\r\nijklmnop\r\n" +
                "8;second=1\r\nqrstuvwx\r\n" +
                "8\r\nyzABCDEF\r\n" +
                "8\r\nGHIJKLMN\r\n" +
                "8;third=value\r\nOPQRSTUV\r\n" +
                "8\r\nWXYZ0123\r\n" +
                "8\r\n456789ab\r\n" +
                "0\r\n\r\n");

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(VpnConnectivityVerifier.DecodeChunkedBody(body));
                GC.KeepAlive(LegacyDecodeChunkedBody(body));
            }

            var optimizedBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(VpnConnectivityVerifier.DecodeChunkedBody(body)));
            var predecessorBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(LegacyDecodeChunkedBody(body)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Exact-size chunk decoder allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the MemoryStream/string predecessor.");
            }

            Action optimized = () =>
                GC.KeepAlive(VpnConnectivityVerifier.DecodeChunkedBody(body));
            Action predecessor = () => GC.KeepAlive(LegacyDecodeChunkedBody(body));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Exact-size chunk decoder median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for MemoryStream/string " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: exact-size verification chunk decoding reduces setup cost " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: verification chunk decoder regression: {ex}");
            return 1;
        }
    }

    private static void DecoderSemanticsRemainEquivalent()
    {
        string[] accepted =
        [
            "4\r\nWiki\r\n5\r\npedia\r\n0\r\n\r\n",
            "4 \t; \tfoo \t= \tbar;quoted=\"a\\\"b\"\r\nWiki\r\nA\r\n0123456789\r\n0;ignored=yes\r\nX-Trailer: complete\r\n\r\n",
            "00000004\r\nWiki\r\n0\r\n\r\n",
            "4;ext=value\r\nWiki\r\n0\r\n\r\n",
            "0\r\n\r\n"
        ];

        foreach (var raw in accepted)
        {
            var body = Encoding.ASCII.GetBytes(raw);
            var actual = VpnConnectivityVerifier.DecodeChunkedBody(body);
            var expected = LegacyDecodeChunkedBody(body);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Chunk decoder changed accepted output for '{Escape(raw)}'.");
            }
        }

        string[] rejected =
        [
            "4Wiki",
            "G\r\n",
            "-1\r\n",
            "80000000\r\n",
            "4 0\r\n",
            " 4\r\nWiki\r\n0\r\n\r\n",
            "\t4\r\nWiki\r\n0\r\n\r\n",
            "\v4\r\nWiki\r\n0\r\n\r\n",
            "\f4\r\nWiki\r\n0\r\n\r\n",
            "4 \r\nWiki\r\n0\r\n\r\n",
            "4;\r\nWiki\r\n0\r\n\r\n",
            "4;=value\r\nWiki\r\n0\r\n\r\n",
            "4;bad name=value\r\nWiki\r\n0\r\n\r\n",
            "4;name=\"unterminated\r\nWiki\r\n0\r\n\r\n",
            "4;name=value garbage\r\nWiki\r\n0\r\n\r\n",
            "4;name=\"bad\rvalue\"\r\nWiki\r\n0\r\n\r\n",
            "4\r\nWik",
            "4\r\nWikiXX0\r\n",
            "4\r\nWiki\rX0\r\n",
            "0\r\n",
            "0\r\nanything-after-zero-is-ignored",
            "4\r\nWiki\r\n0\r\nignored",
            "4\r\nWiki\r\n0\r\n\r\nextra",
            "0\r\nBadTrailer\r\n\r\n"
        ];

        foreach (var raw in rejected)
        {
            var body = Encoding.ASCII.GetBytes(raw);
            if (!ThrowsIOException(() => VpnConnectivityVerifier.DecodeChunkedBody(body)))
            {
                throw new InvalidOperationException(
                    $"Chunk decoder accepted malformed/incomplete framing for '{Escape(raw)}'.");
            }
        }
    }

    private static string Escape(string value)
    {
        return value.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
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

    private static byte[] LegacyDecodeChunkedBody(ReadOnlySpan<byte> body)
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
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
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

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double PairedRatioMedian);
}
