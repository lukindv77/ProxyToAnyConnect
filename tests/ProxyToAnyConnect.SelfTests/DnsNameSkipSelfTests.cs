using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Network;

namespace ProxyToAnyConnect.SelfTests;

internal static class DnsNameSkipSelfTests
{
    private const int WarmupIterations = 4096;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private static int _sink;

    public static int Run()
    {
        try
        {
            OffsetsAndValidationMatchMaterializingPredecessor();
            ParseResponseStillHandlesQuestionAndOwnerNames();

            var packet = BuildCompressedNamePacket();
            var offset = FindAliasOffset(packet);
            for (var i = 0; i < WarmupIterations; i++)
            {
                ConsumeOptimized(packet, offset);
                ConsumePredecessor(packet, offset);
            }

            var optimizedBytes = MeasureAllocatedBytes(() => ConsumeOptimized(packet, offset));
            var predecessorBytes = MeasureAllocatedBytes(() => ConsumePredecessor(packet, offset));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"DNS name skip allocated {optimizedBytes} bytes versus {predecessorBytes} bytes for materialization.");
            }

            Action optimized = () => ConsumeOptimized(packet, offset);
            Action predecessor = () => ConsumePredecessor(packet, offset);
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"DNS name skip median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for materialization " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: DNS discard-name parsing avoids materialization " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/name; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DNS discard-name parsing regression: {ex}");
            return 1;
        }
    }

    private static void ConsumeOptimized(byte[] packet, int initialOffset)
    {
        _sink ^= RunOptimized(packet, initialOffset);
    }

    private static void ConsumePredecessor(byte[] packet, int initialOffset)
    {
        var result = RunPredecessor(packet, initialOffset);
        _sink ^= result.Offset ^ result.Name.Length;
    }

    private static void OffsetsAndValidationMatchMaterializingPredecessor()
    {
        var uncompressed = EncodeName("www.example.com");
        AssertEquivalent(uncompressed, 0);

        var compressed = BuildCompressedNamePacket();
        AssertEquivalent(compressed, FindAliasOffset(compressed));

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
                throw new InvalidOperationException("DNS name skip changed malformed-name rejection semantics.");
            }
        }
    }

    private static void AssertEquivalent(byte[] packet, int initialOffset)
    {
        var optimizedOffset = RunOptimized(packet, initialOffset);
        var predecessor = RunPredecessor(packet, initialOffset);
        if (optimizedOffset != predecessor.Offset)
        {
            throw new InvalidOperationException(
                $"DNS name skip advanced to {optimizedOffset}, expected {predecessor.Offset}.");
        }
    }

    private static void ParseResponseStillHandlesQuestionAndOwnerNames()
    {
        const ushort transactionId = 0x1234;
        var packet = new List<byte>();
        AddUInt16(packet, transactionId);
        AddUInt16(packet, 0x8180);
        AddUInt16(packet, 1);
        AddUInt16(packet, 1);
        AddUInt16(packet, 0);
        AddUInt16(packet, 0);
        packet.AddRange(EncodeName("www.example.com"));
        AddUInt16(packet, 1);
        AddUInt16(packet, 1);
        packet.Add(0xC0);
        packet.Add(0x0C);
        AddUInt16(packet, 1);
        AddUInt16(packet, 1);
        AddUInt32(packet, 60);
        AddUInt16(packet, 4);
        packet.AddRange([203, 0, 113, 7]);

        var parsed = L2tpDnsResolver.ParseResponse(packet.ToArray(), transactionId);
        if (parsed.Addresses.Count != 1 || parsed.Addresses[0].ToString() != "203.0.113.7")
        {
            throw new InvalidOperationException("DNS A response changed after discard-name optimization.");
        }
    }

    private static int RunOptimized(byte[] packet, int initialOffset)
    {
        var offset = initialOffset;
        L2tpDnsResolver.SkipName(packet, ref offset);
        return offset;
    }

    private static LegacyReadResult RunPredecessor(byte[] packet, int initialOffset)
    {
        var offset = initialOffset;
        var name = LegacyReadName(packet, ref offset);
        return new LegacyReadResult(offset, name);
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

    private static void EnsureRemaining(ReadOnlySpan<byte> packet, int offset, int required)
    {
        if (offset < 0 || required < 0 || offset > packet.Length - required)
        {
            throw new IOException("DNS response is truncated or malformed.");
        }
    }

    private static byte[] BuildCompressedNamePacket()
    {
        var bytes = new List<byte>();
        bytes.AddRange(EncodeName("www.example.com"));
        bytes.Add(3);
        bytes.AddRange("api"u8.ToArray());
        bytes.Add(0xC0);
        bytes.Add(0x04);
        return bytes.ToArray();
    }

    private static int FindAliasOffset(byte[] packet) => EncodeName("www.example.com").Length;

    private static byte[] EncodeName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            bytes.Add(checked((byte)labelBytes.Length));
            bytes.AddRange(labelBytes);
        }

        bytes.Add(0);
        return bytes.ToArray();
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

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)(value >> 24));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
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

    private readonly record struct LegacyReadResult(int Offset, string Name);
    private readonly record struct TimingResult(
        double OptimizedMedianNs,
        double PredecessorMedianNs,
        double Ratio);
}
