using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationBodyViewSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 16384;
    private const double MaxMedianSlowdownRatio = 1.25;
    private static int _lengthSink;

    public static int Run()
    {
        try
        {
            BodyViewSemanticsRemainEquivalent();

            var response = BuildPlainResponse(1024);
            Action optimized = () =>
            {
                var body = VpnConnectivityVerifier.ParseHttpSuccessBodyView(response);
                unchecked
                {
                    _lengthSink += body.Length;
                }
            };
            Action predecessor = () =>
            {
                var body = VpnConnectivityVerifier.ParseHttpSuccessBody(response);
                unchecked
                {
                    _lengthSink += body.Length;
                }
            };

            for (var i = 0; i < WarmupIterations; i++)
            {
                optimized();
                predecessor();
            }

            var optimizedBytes = MeasureAllocatedBytes(optimized);
            var predecessorBytes = MeasureAllocatedBytes(predecessor);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Plain verification body view allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the copy-returning predecessor.");
            }

            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Plain verification body view median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for the copy-returning predecessor " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: verification plain-body view removes the response-body copy " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: verification plain-body view regression: {ex}");
            return 1;
        }
    }

    private static void BodyViewSemanticsRemainEquivalent()
    {
        var plain = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Length: 11\r\n\r\n" +
            "203.0.113.7");
        var plainView = VpnConnectivityVerifier.ParseHttpSuccessBodyView(plain);
        var plainCopy = VpnConnectivityVerifier.ParseHttpSuccessBody(plain);
        if (!plainView.Span.SequenceEqual(plainCopy))
        {
            throw new InvalidOperationException("Plain verification body view changed body bytes.");
        }

        if (!MemoryMarshal.TryGetArray(plainView, out ArraySegment<byte> plainSegment) ||
            !ReferenceEquals(plainSegment.Array, plain))
        {
            throw new InvalidOperationException(
                "Plain verification body view no longer references the bounded response owner.");
        }

        var expectedOffset = FindHeaderEnd(plain) + 4;
        if (plainSegment.Offset != expectedOffset || plainSegment.Count != plain.Length - expectedOffset)
        {
            throw new InvalidOperationException("Plain verification body view changed slice boundaries.");
        }

        var chunked = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\n");
        var chunkedView = VpnConnectivityVerifier.ParseHttpSuccessBodyView(chunked);
        var chunkedCopy = VpnConnectivityVerifier.ParseHttpSuccessBody(chunked);
        if (!chunkedView.Span.SequenceEqual(chunkedCopy))
        {
            throw new InvalidOperationException("Chunked verification body view changed decoded bytes.");
        }

        if (!MemoryMarshal.TryGetArray(chunkedView, out ArraySegment<byte> chunkedSegment) ||
            ReferenceEquals(chunkedSegment.Array, chunked))
        {
            throw new InvalidOperationException(
                "Chunked verification body must retain its separate decoded owner.");
        }

        string[] rejected =
        [
            "HTTP/1.1 302 Found\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 nope OK\r\nContent-Length: 0\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n"
        ];

        foreach (var raw in rejected)
        {
            var response = Encoding.ASCII.GetBytes(raw);
            var viewRejected = ThrowsIOException(() =>
                VpnConnectivityVerifier.ParseHttpSuccessBodyView(response));
            var copyRejected = ThrowsIOException(() =>
                VpnConnectivityVerifier.ParseHttpSuccessBody(response));
            if (viewRejected != copyRejected || !viewRejected)
            {
                throw new InvalidOperationException(
                    "Verification body view changed malformed/unsuccessful response rejection.");
            }
        }

        try
        {
            VpnConnectivityVerifier.ParseHttpSuccessBodyView(null!);
            throw new InvalidOperationException("Verification body view accepted a null response owner.");
        }
        catch (ArgumentNullException)
        {
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

    private static byte[] BuildPlainResponse(int bodyBytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Verification: ProxyToAnyConnect\r\n\r\n");
        var response = GC.AllocateUninitializedArray<byte>(header.Length + bodyBytes);
        header.CopyTo(response, 0);
        for (var i = 0; i < bodyBytes; i++)
        {
            response[header.Length + i] = (byte)('0' + (i % 10));
        }

        return response;
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
