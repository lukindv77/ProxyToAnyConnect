namespace ProxyToAnyConnect.Runtime;

internal sealed class TrafficCounter
{
    private long _receivedBytes;
    private long _sentBytes;

    public long ReceivedBytes => Math.Max(0, Interlocked.Read(ref _receivedBytes));
    public long SentBytes => Math.Max(0, Interlocked.Read(ref _sentBytes));

    public void AddReceived(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _receivedBytes, bytes);
        }
    }

    public void AddSent(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _sentBytes, bytes);
        }
    }

    public TrafficSnapshot Snapshot() => new(ReceivedBytes, SentBytes);
}

internal readonly record struct TrafficSnapshot(long ReceivedBytes, long SentBytes);

internal sealed class RollingPingWindow
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Queue<PingSample> _samples = new();

    public void AddSuccessfulSample(TimeSpan roundTripTime, DateTimeOffset? timestamp = null)
    {
        if (roundTripTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(roundTripTime));
        }

        var now = timestamp ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _samples.Enqueue(new PingSample(now, roundTripTime.TotalMilliseconds));
            RemoveExpiredLocked(now);
        }
    }

    public PingWindowSnapshot Snapshot(DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            RemoveExpiredLocked(now);
            if (_samples.Count == 0)
            {
                return new PingWindowSnapshot(null, 0);
            }

            var average = _samples.Average(sample => sample.RoundTripMilliseconds);
            return new PingWindowSnapshot(average, _samples.Count);
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (_samples.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _samples.Dequeue();
        }
    }

    private readonly record struct PingSample(DateTimeOffset Timestamp, double RoundTripMilliseconds);
}

internal readonly record struct PingWindowSnapshot(double? AverageMilliseconds, int SampleCount);

internal sealed class ProxyRuntimeMetrics
{
    public TrafficCounter Traffic { get; } = new();
}

internal sealed class L2tpRuntimeMetrics
{
    public TrafficCounter Traffic { get; } = new();
    public RollingPingWindow Ping { get; } = new();

    public L2tpMetricsSnapshot Snapshot()
    {
        var traffic = Traffic.Snapshot();
        var ping = Ping.Snapshot();
        return new L2tpMetricsSnapshot(
            traffic.ReceivedBytes,
            traffic.SentBytes,
            ping.AverageMilliseconds,
            ping.SampleCount);
    }
}

internal readonly record struct L2tpMetricsSnapshot(
    long ReceivedBytes,
    long SentBytes,
    double? AveragePingMilliseconds,
    int PingSampleCount);
