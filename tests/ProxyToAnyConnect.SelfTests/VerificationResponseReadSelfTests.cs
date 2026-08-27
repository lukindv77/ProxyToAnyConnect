using System.Diagnostics;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationResponseReadSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 16384;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const int RepresentativeMaxBytes = 64 * 1024;

    public static int Run()
    {
        try
        {
            ResponseAccumulationPreservesBoundaries();

            var representative = BuildPayload(384);
            var stream = new ResettableFragmentStream(representative, int.MaxValue);
            Func<byte[]> optimized = () =>
            {
                stream.Reset();
                return VpnConnectivityVerifier.ReadResponseAsync(
                    stream,
                    RepresentativeMaxBytes,
                    CancellationToken.None).GetAwaiter().GetResult();
            };
            Func<byte[]> predecessor = () =>
            {
                stream.Reset();
                return MemoryStreamPredecessorAsync(
                    stream,
                    RepresentativeMaxBytes,
                    CancellationToken.None).GetAwaiter().GetResult();
            };

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(optimized());
                GC.KeepAlive(predecessor());
            }

            var optimizedBytes = MeasureAllocatedBytes(optimized);
            var predecessorBytes = MeasureAllocatedBytes(predecessor);
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Pooled verification response reader allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the MemoryStream predecessor.");
            }

            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Pooled verification response reader median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for MemoryStream " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: pooled verification response accumulation reduces setup cost " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: verification response reader regression: {ex}");
            return 1;
        }
    }

    private static void ResponseAccumulationPreservesBoundaries()
    {
        var fragmentedPayload = BuildPayload(9000);
        var fragmented = new ResettableFragmentStream(fragmentedPayload, 1, 7, 257, 13, 2048, 31);
        var fragmentedActual = VpnConnectivityVerifier.ReadResponseAsync(
            fragmented,
            fragmentedPayload.Length + 100,
            CancellationToken.None).GetAwaiter().GetResult();
        if (!fragmentedActual.AsSpan().SequenceEqual(fragmentedPayload))
        {
            throw new InvalidOperationException("Fragmented verification response bytes changed during accumulation.");
        }

        var exactPayload = BuildPayload(1024);
        var exact = new ResettableFragmentStream(exactPayload, 511, 513);
        var exactActual = VpnConnectivityVerifier.ReadResponseAsync(
            exact,
            exactPayload.Length,
            CancellationToken.None).GetAwaiter().GetResult();
        if (!exactActual.AsSpan().SequenceEqual(exactPayload))
        {
            throw new InvalidOperationException("Verification response exactly at the configured limit was not preserved.");
        }

        var overflowPayload = BuildPayload(1025);
        var overflow = new ResettableFragmentStream(overflowPayload, 1024, 1);
        if (!ThrowsSizeLimit(() => VpnConnectivityVerifier.ReadResponseAsync(
                overflow,
                1024,
                CancellationToken.None).GetAwaiter().GetResult()))
        {
            throw new InvalidOperationException("Verification response one byte over the limit was not rejected.");
        }

        var empty = new ResettableFragmentStream([], 1);
        var emptyActual = VpnConnectivityVerifier.ReadResponseAsync(
            empty,
            0,
            CancellationToken.None).GetAwaiter().GetResult();
        if (emptyActual.Length != 0)
        {
            throw new InvalidOperationException("Empty verification response changed at a zero-byte limit.");
        }

        var zeroLimitData = new ResettableFragmentStream([0x42], 1);
        if (!ThrowsSizeLimit(() => VpnConnectivityVerifier.ReadResponseAsync(
                zeroLimitData,
                0,
                CancellationToken.None).GetAwaiter().GetResult()))
        {
            throw new InvalidOperationException("Non-empty verification response at a zero-byte limit was not rejected.");
        }
    }

    private static bool ThrowsSizeLimit(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (IOException ex) when (ex.Message.Contains("size limit", StringComparison.Ordinal))
        {
            return true;
        }
    }

    private static byte[] BuildPayload(int length)
    {
        var payload = GC.AllocateUninitializedArray<byte>(length);
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 17);
        }

        return payload;
    }

    private static long MeasureAllocatedBytes(Func<byte[]> action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < AllocationIterations; i++)
        {
            GC.KeepAlive(action());
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static TimingResult MeasurePaired(Func<byte[]> optimized, Func<byte[]> predecessor)
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

    private static double MeasureNanosecondsPerOperation(Func<byte[]> action)
    {
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < IterationsPerRound; i++)
        {
            GC.KeepAlive(action());
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

    private static async Task<byte[]> MemoryStreamPredecessorAsync(
        Stream stream,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var response = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (response.Length + read > maxResponseBytes)
            {
                throw new IOException("L2TP verification response exceeded the configured size limit.");
            }

            response.Write(buffer, 0, read);
        }

        return response.ToArray();
    }

    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double PairedRatioMedian);

    private sealed class ResettableFragmentStream : Stream
    {
        private readonly byte[] _payload;
        private readonly int[] _fragments;
        private int _offset;
        private int _fragmentIndex;

        public ResettableFragmentStream(byte[] payload, params int[] fragments)
        {
            _payload = payload;
            _fragments = fragments.Length == 0 ? [int.MaxValue] : fragments;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _payload.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public void Reset()
        {
            _offset = 0;
            _fragmentIndex = 0;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= _payload.Length)
            {
                return ValueTask.FromResult(0);
            }

            var fragment = _fragments[_fragmentIndex % _fragments.Length];
            _fragmentIndex++;
            var count = Math.Min(buffer.Length, Math.Min(fragment, _payload.Length - _offset));
            _payload.AsSpan(_offset, count).CopyTo(buffer.Span);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
