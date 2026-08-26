using System.Diagnostics;
using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationProbeRequestSelfTests
{
    private const int WarmupIterations = 256;
    private const int AllocationIterations = 1000;
    private const int TimingRounds = 9;
    private const int IterationsPerRound = 65536;
    private const double MaxMedianSlowdownRatio = 1.25;
    private const string RepresentativeHost = "api64.ipify.org";
    private const string RepresentativePath = "/?format=text";

    public static int Run()
    {
        try
        {
            RequestWireBytesRemainEquivalent();

            for (var i = 0; i < WarmupIterations; i++)
            {
                GC.KeepAlive(VpnConnectivityVerifier.BuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath));
                GC.KeepAlive(LegacyBuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath));
            }

            var optimizedBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(VpnConnectivityVerifier.BuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath)));
            var predecessorBytes = MeasureAllocatedBytes(
                () => GC.KeepAlive(LegacyBuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath)));
            if (optimizedBytes >= predecessorBytes)
            {
                throw new InvalidOperationException(
                    $"Exact-size verification request builder allocated {optimizedBytes} bytes versus " +
                    $"{predecessorBytes} bytes for the interpolated-string predecessor.");
            }

            Action optimized = () =>
                GC.KeepAlive(VpnConnectivityVerifier.BuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath));
            Action predecessor = () =>
                GC.KeepAlive(LegacyBuildProbeRequest(
                    RepresentativeHost,
                    RepresentativePath));
            var timing = MeasurePaired(optimized, predecessor);
            if (timing.Ratio > MaxMedianSlowdownRatio)
            {
                throw new InvalidOperationException(
                    $"Exact-size verification request builder median was {timing.OptimizedMedianNs:F0} ns/op versus " +
                    $"{timing.PredecessorMedianNs:F0} ns/op for interpolated-string + ASCII encoding " +
                    $"({timing.Ratio:F2}x, limit {MaxMedianSlowdownRatio:F2}x).");
            }

            Console.WriteLine(
                $"PASS: exact-size verification request construction reduces setup cost " +
                $"(alloc {optimizedBytes / (double)AllocationIterations:F0} vs " +
                $"{predecessorBytes / (double)AllocationIterations:F0} bytes/request; " +
                $"timing {timing.OptimizedMedianNs:F0} vs {timing.PredecessorMedianNs:F0} ns/op, " +
                $"{timing.Ratio:F2}x)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: verification request construction regression: {ex}");
            return 1;
        }
    }

    private static void RequestWireBytesRemainEquivalent()
    {
        (string? Host, string? Path)[] cases =
        [
            (RepresentativeHost, RepresentativePath),
            ("example.com", "/"),
            (string.Empty, string.Empty),
            (null, null),
            ("münich.example", "/café/😀")
        ];

        foreach (var (host, path) in cases)
        {
            var actual = VpnConnectivityVerifier.BuildProbeRequest(host, path);
            var expected = LegacyBuildProbeRequest(host, path);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Verification request wire bytes changed for host '{host ?? "<null>"}' and path '{path ?? "<null>"}'.");
            }
        }
    }

    private static byte[] LegacyBuildProbeRequest(string? host, string? path)
    {
        return Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\n" +
            $"Host: {host}\r\n" +
            "User-Agent: ProxyToAnyConnect/1.0\r\n" +
            "Accept: text/plain\r\n" +
            "Accept-Encoding: identity\r\n" +
            "Connection: close\r\n\r\n");
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
