using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationPooledResponseOwnerSelfTests
{
    private const int InitialResponseBufferBytes = 4 * 1024;
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 15;
    private const int IterationsPerRound = 16384;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const int RepresentativeMaxBytes = 64 * 1024;
    private static int _lengthSink;

    public static int Run()
    {
        try
        {
            OwnerPreservesBoundariesAndLifetime();

            var representative = BuildPayload(384);
            var stream = new ResettableFragmentStream(representative, int.MaxValue);
            Action optimized = () =>
            {
                stream.Reset();
                using var owner = VpnConnectivityVerifier.ReadPooledResponseAsync(
                    stream,
                    RepresentativeMaxBytes,
                    CancellationToken.None).GetAwaiter().GetResult();
                unchecked
                {
                    _lengthSink += owner.Memory.Length;
                }
            };
            Action predecessor = () =>
            {
                stream.Reset();
                var response = ExactCopyPredecessorAsync(
                    stream,
                    RepresentativeMaxBytes,
                    CancellationToken.None).GetAwaiter().GetResult();
                unchecked
                {
                    _lengthSink += response.Length;
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
                    $"Scoped pooled verification response owner allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the exact-copy predecessor.");
            }

            var timing = MeasurePaired(optimized, predecessor);
            if (timing.PairedRatioMedian > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Scoped pooled verification response owner median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for the exact-copy predecessor " +
                    $"({timing.PairedRatioMedian:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: scoped pooled verification response owner removes the final response copy " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/response; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.PairedRatioMedian:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: scoped pooled verification response owner regression: {ex}");
            return 1;
        }
    }

    private static void OwnerPreservesBoundariesAndLifetime()
    {
        var fragmentedPayload = BuildPayload(9000);
        var fragmented = new ResettableFragmentStream(fragmentedPayload, 1, 7, 257, 13, 2048, 31);
        var fragmentedOwner = VpnConnectivityVerifier.ReadPooledResponseAsync(
            fragmented,
            fragmentedPayload.Length + 100,
            CancellationToken.None).GetAwaiter().GetResult();
        if (fragmentedOwner.Length != fragmentedPayload.Length ||
            fragmentedOwner.Memory.Length != fragmentedPayload.Length ||
            !fragmentedOwner.Memory.Span.SequenceEqual(fragmentedPayload))
        {
            fragmentedOwner.Dispose();
            throw new InvalidOperationException(
                "Scoped pooled verification response owner exposed bytes outside the valid response slice.");
        }

        fragmentedOwner.Dispose();
        fragmentedOwner.Dispose();
        if (!ThrowsDisposed(() => _ = fragmentedOwner.Memory))
        {
            throw new InvalidOperationException(
                "Scoped pooled verification response owner remained readable after disposal.");
        }

        var plainResponse = BuildPlainResponse(128);
        var plainStream = new ResettableFragmentStream(plainResponse, 17, 29, 5, 257);
        using (var plainOwner = VpnConnectivityVerifier.ReadPooledResponseAsync(
                   plainStream,
                   plainResponse.Length + 10,
                   CancellationToken.None).GetAwaiter().GetResult())
        {
            var body = VpnConnectivityVerifier.ParseHttpSuccessBodyView(plainOwner.Memory);
            if (!MemoryMarshal.TryGetArray(plainOwner.Memory, out ArraySegment<byte> ownerSegment) ||
                !MemoryMarshal.TryGetArray(body, out ArraySegment<byte> bodySegment) ||
                !ReferenceEquals(ownerSegment.Array, bodySegment.Array))
            {
                throw new InvalidOperationException(
                    "Plain verification body view escaped the scoped pooled response owner.");
            }

            if (body.Length != 128 || !body.Span.SequenceEqual(plainResponse.AsSpan(plainResponse.Length - 128)))
            {
                throw new InvalidOperationException(
                    "Scoped pooled response owner changed plain verification body bytes.");
            }
        }

        var empty = new ResettableFragmentStream([], 1);
        using (var emptyOwner = VpnConnectivityVerifier.ReadPooledResponseAsync(
                   empty,
                   0,
                   CancellationToken.None).GetAwaiter().GetResult())
        {
            if (emptyOwner.Length != 0 || emptyOwner.Memory.Length != 0)
            {
                throw new InvalidOperationException(
                    "Scoped pooled verification response owner changed zero-length response semantics.");
            }
        }

        var overflow = new ResettableFragmentStream(BuildPayload(1025), 1024, 1);
        if (!ThrowsSizeLimit(() => VpnConnectivityVerifier.ReadPooledResponseAsync(
                overflow,
                1024,
                CancellationToken.None).GetAwaiter().GetResult()))
        {
            throw new InvalidOperationException(
                "Scoped pooled verification response owner changed one-byte-over-limit rejection.");
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledStream = new ResettableFragmentStream(BuildPayload(1), 1);
        if (!ThrowsCancellation(() => VpnConnectivityVerifier.ReadPooledResponseAsync(
                cancelledStream,
                1,
                cancelled.Token).GetAwaiter().GetResult()))
        {
            throw new InvalidOperationException(
                "Scoped pooled verification response owner changed cancellation propagation.");
        }
    }

    private static bool ThrowsDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
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

    private static bool ThrowsCancellation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (OperationCanceledException)
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

    private static byte[] BuildPlainResponse(int bodyBytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            $"Content-Length: {bodyBytes}\r\n" +
            "Connection: close\r\n\r\n");
        var response = GC.AllocateUninitializedArray<byte>(header.Length + bodyBytes);
        header.CopyTo(response, 0);
        for (var i = 0; i < bodyBytes; i++)
        {
            response[header.Length + i] = (byte)('0' + (i % 10));
        }

        return response;
    }

    private static async Task<byte[]> ExactCopyPredecessorAsync(
        Stream stream,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;
        var maximumBufferedBytes = maxResponseBytes switch
        {
            < 0 => 1,
            int.MaxValue => int.MaxValue,
            _ => maxResponseBytes + 1
        };
        var buffer = pool.Rent(Math.Max(1, Math.Min(InitialResponseBufferBytes, maximumBufferedBytes)));
        var length = 0;

        try
        {
            while (true)
            {
                var writable = Math.Min(
                    buffer.Length - length,
                    maximumBufferedBytes - length);
                if (writable == 0)
                {
                    if (length == maximumBufferedBytes)
                    {
                        var overflowProbe = pool.Rent(1);
                        try
                        {
                            var overflowRead = await stream.ReadAsync(
                                overflowProbe.AsMemory(0, 1),
                                cancellationToken);
                            if (overflowRead == 0)
                            {
                                break;
                            }

                            throw new IOException(
                                "L2TP verification response exceeded the configured size limit.");
                        }
                        finally
                        {
                            pool.Return(overflowProbe, clearArray: false);
                        }
                    }

                    var doubledCapacity = buffer.Length <= int.MaxValue / 2
                        ? buffer.Length * 2
                        : int.MaxValue;
                    var nextCapacity = Math.Min(
                        maximumBufferedBytes,
                        Math.Max(length + 1, doubledCapacity));
                    var replacement = pool.Rent(nextCapacity);
                    buffer.AsSpan(0, length).CopyTo(replacement);
                    pool.Return(buffer, clearArray: false);
                    buffer = replacement;
                    continue;
                }

                var read = await stream.ReadAsync(
                    buffer.AsMemory(length, writable),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (length > maxResponseBytes)
                {
                    throw new IOException(
                        "L2TP verification response exceeded the configured size limit.");
                }
            }

            if (length == 0)
            {
                return [];
            }

            var result = GC.AllocateUninitializedArray<byte>(length);
            buffer.AsSpan(0, length).CopyTo(result);
            return result;
        }
        finally
        {
            pool.Return(buffer, clearArray: false);
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
