using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsNameMaterializationSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;

    public static int Run()
    {
        try
        {
            SemanticsRemainEquivalent();

            var packet = BuildCompressedPacket();
            var offset = FindAliasOffset(packet);
            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(RunOptimized(packet, offset));
                GC.KeepAlive(RunPredecessor(packet, offset));
            }

            var optimizedBytes = MeasureAllocatedBytes(() =>
                GC.KeepAlive(RunOptimized(packet, offset)));
            var predecessorBytes = MeasureAllocatedBytes(() =>
                GC.KeepAlive(RunPredecessor(packet, offset)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"DNS CNAME materialization allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for List/string.Join predecessor.");
            }

            Action optimized = () => GC.KeepAlive(RunOptimized(packet, offset));
            Action predecessor = () => GC.KeepAlive(RunPredecessor(packet, offset));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"DNS CNAME materialization median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for List/string.Join predecessor " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS CNAME name materialization avoids per-label strings " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/name; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS CNAME materialization regression: {ex}");
            return 1;
        }
    }

    private static void SemanticsRemainEquivalent()
    {
        AssertEquivalent(EncodeName("api.service.example.internal"), 0);

        var compressed = BuildCompressedPacket();
        AssertEquivalent(compressed, FindAliasOffset(compressed));

        var nonAscii = new byte[]
        {
            3, (byte)'c', 0xE9, (byte)'f',
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            0
        };
        AssertEquivalent(nonAscii, 0);

        var longAccepted = BuildLongAcceptedName();
        var longResult = RunOptimized(longAccepted, 0);
        var longExpected = RunPredecessor(longAccepted, 0);
        if (longResult.Name != longExpected.Name || longResult.Offset != longExpected.Offset || longResult.Name.Length <= 256)
        {
            throw new InvalidOperationException("DNS pooled fallback changed a >256-character accepted name.");
        }

        byte[][] rejected =
        [
            [0xC0],
            [0xC0, 0x00],
            [0x40, 0x00],
            [0x03, (byte)'a']
        ];

        foreach (var packet in rejected)
        {
            var optimizedRejected = ThrowsIOException(() => RunOptimized(packet, 0));
            var predecessorRejected = ThrowsIOException(() => RunPredecessor(packet, 0));
            if (optimizedRejected != predecessorRejected || !optimizedRejected)
            {
                throw new InvalidOperationException("DNS CNAME materialization changed malformed-name rejection semantics.");
            }
        }
    }

    private static void AssertEquivalent(byte[] packet, int initialOffset)
    {
        var actual = RunOptimized(packet, initialOffset);
        var expected = RunPredecessor(packet, initialOffset);
        if (actual.Offset != expected.Offset || actual.Name != expected.Name)
        {
            throw new InvalidOperationException(
                $"DNS name changed: actual '{actual.Name}'/{actual.Offset}, expected '{expected.Name}'/{expected.Offset}.");
        }
    }

    private static ReadResult RunOptimized(byte[] packet, int initialOffset)
    {
        var offset = initialOffset;
        var name = L2tpDnsResolver.ReadName(packet, ref offset);
        return new ReadResult(offset, name);
    }

    private static ReadResult RunPredecessor(byte[] packet, int initialOffset)
    {
        var offset = initialOffset;
        var name = LegacyReadName(packet, ref offset);
        return new ReadResult(offset, name);
    }

    private static string LegacyReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var labels = new List<string>();
        var current = offset;
        var jumped = false;
        var jumps = 0;

        while (true)
        {
            EnsureRemaining(packet, current, 1);
            var length = packet[current++];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = current;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                EnsureRemaining(packet, current, 1);
                var pointer = ((length & 0x3F) << 8) | packet[current++];
                if (pointer >= packet.Length || ++jumps > 32)
                {
                    throw new IOException("Invalid DNS name compression pointer.");
                }

                if (!jumped)
                {
                    offset = current;
                    jumped = true;
                }

                current = pointer;
                continue;
            }

            if ((length & 0xC0) != 0 || length > 63)
            {
                throw new IOException("Invalid DNS label length.");
            }

            EnsureRemaining(packet, current, length);
            labels.Add(Encoding.ASCII.GetString(packet.Slice(current, length)));
            current += length;
            if (!jumped)
            {
                offset = current;
            }
        }
    }

    private static byte[] BuildCompressedPacket()
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeName("service.example.internal"));
        var aliasOffset = bytes.Count;
        bytes.Add(3);
        bytes.AddRange("api"u8.ToArray());
        bytes.Add(0xC0);
        bytes.Add(0x00);
        if (aliasOffset <= 0)
        {
            throw new InvalidOperationException("Invalid compressed test packet.");
        }

        return bytes.ToArray();
    }

    private static int FindAliasOffset(byte[] packet)
    {
        var offset = 0;
        while (packet[offset] != 0)
        {
            offset += packet[offset] + 1;
        }

        return offset + 1;
    }

    private static byte[] BuildLongAcceptedName()
    {
        var bytes = new List<byte>();
        for (var labelIndex = 0; labelIndex < 5; labelIndex++)
        {
            bytes.Add(63);
            for (var i = 0; i < 63; i++)
            {
                bytes.Add((byte)('a' + labelIndex));
            }
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            bytes.Add(checked((byte)encoded.Length));
            bytes.AddRange(encoded);
        }

        bytes.Add(0);
        return bytes.ToArray();
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> packet, int offset, int required)
    {
        if (offset < 0 || required < 0 || offset > packet.Length - required)
        {
            throw new IOException("DNS response is truncated or malformed.");
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

    private readonly record struct ReadResult(int Offset, string Name);
    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
