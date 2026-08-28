using System.Net;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Network;

internal sealed class L2tpDnsCache
{
    private const int DefaultMaxEntries = 512;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;
    private readonly TimeProvider _timeProvider;

    private VpnContext? _context;
    private long _accessSequence;

    public L2tpDnsCache(
        int maxEntries = DefaultMaxEntries,
        TimeProvider? timeProvider = null)
    {
        if (maxEntries is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        _maxEntries = maxEntries;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGet(
        string normalizedHost,
        VpnContext context,
        out IReadOnlyList<IPAddress> addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            EnsureContextLocked(context);
            var currentTimestamp = _timeProvider.GetTimestamp();
            if (!_entries.TryGetValue(normalizedHost, out var entry))
            {
                addresses = [];
                return false;
            }

            if (IsExpired(entry, currentTimestamp))
            {
                _entries.Remove(normalizedHost);
                addresses = [];
                return false;
            }

            entry.LastAccessSequence = ++_accessSequence;
            addresses = entry.Addresses;
            return true;
        }
    }

    public void Set(
        string normalizedHost,
        VpnContext context,
        IReadOnlyList<IPAddress> addresses,
        TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedHost);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(addresses);

        if (ttl <= TimeSpan.Zero || addresses.Count == 0)
        {
            return;
        }

        var copy = addresses as IPAddress[] ?? addresses.ToArray();
        var currentTimestamp = _timeProvider.GetTimestamp();

        lock (_gate)
        {
            EnsureContextLocked(context);
            RemoveExpiredLocked(currentTimestamp);

            if (!_entries.ContainsKey(normalizedHost) && _entries.Count >= _maxEntries)
            {
                EvictLeastRecentlyUsedLocked();
            }

            _entries[normalizedHost] = new CacheEntry(
                copy,
                currentTimestamp,
                ttl,
                ++_accessSequence);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _context = null;
            _accessSequence = 0;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    private void EnsureContextLocked(VpnContext context)
    {
        if (ReferenceEquals(_context, context))
        {
            return;
        }

        _entries.Clear();
        _context = context;
        _accessSequence = 0;
    }

    private bool IsExpired(CacheEntry entry, long currentTimestamp) =>
        _timeProvider.GetElapsedTime(entry.StoredTimestamp, currentTimestamp) >= entry.Ttl;

    private void RemoveExpiredLocked(long currentTimestamp)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var pair in _entries)
        {
            if (IsExpired(pair.Value, currentTimestamp))
            {
                (expired ??= []).Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _entries.Remove(key);
        }
    }

    private void EvictLeastRecentlyUsedLocked()
    {
        string? oldestKey = null;
        var oldestSequence = long.MaxValue;

        foreach (var pair in _entries)
        {
            if (pair.Value.LastAccessSequence < oldestSequence)
            {
                oldestSequence = pair.Value.LastAccessSequence;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey is not null)
        {
            _entries.Remove(oldestKey);
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(
            IPAddress[] addresses,
            long storedTimestamp,
            TimeSpan ttl,
            long lastAccessSequence)
        {
            Addresses = addresses;
            StoredTimestamp = storedTimestamp;
            Ttl = ttl;
            LastAccessSequence = lastAccessSequence;
        }

        public IPAddress[] Addresses { get; }
        public long StoredTimestamp { get; }
        public TimeSpan Ttl { get; }
        public long LastAccessSequence { get; set; }
    }
}
