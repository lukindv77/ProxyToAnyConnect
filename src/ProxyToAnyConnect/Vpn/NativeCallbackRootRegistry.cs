namespace ProxyToAnyConnect.Vpn;

internal static class NativeCallbackRootHealth
{
    private static int _currentCount;
    private static int _highWatermark;

    public static int CurrentCount => Volatile.Read(ref _currentCount);
    public static int HighWatermark => Volatile.Read(ref _highWatermark);

    internal static void RootAdded()
    {
        var current = Interlocked.Increment(ref _currentCount);
        while (true)
        {
            var observed = Volatile.Read(ref _highWatermark);
            if (current <= observed ||
                Interlocked.CompareExchange(ref _highWatermark, current, observed) == observed)
            {
                return;
            }
        }
    }

    internal static void RootRemoved()
    {
        var remaining = Interlocked.Decrement(ref _currentCount);
        if (remaining < 0)
        {
            // A registry removal is idempotent and calls this method only after a
            // successful dictionary removal, so a negative process-wide count means
            // an ownership accounting bug rather than a condition to normalize away.
            Interlocked.Increment(ref _currentCount);
            throw new InvalidOperationException(
                "Native callback-root health accounting became negative.");
        }
    }
}

internal sealed class NativeCallbackRootRegistry<TCallback>
    where TCallback : class
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, TCallback> _roots = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _roots.Count;
            }
        }
    }

    public void AddOrReplace(nint handle, TCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (handle == 0)
        {
            return;
        }

        lock (_gate)
        {
            // HRASCONN is the ownership key. Re-observing the same exact native
            // handle replaces its root rather than growing a duplicate collection.
            var isNewOwner = !_roots.ContainsKey(handle);
            _roots[handle] = callback;
            if (isNewOwner)
            {
                NativeCallbackRootHealth.RootAdded();
            }
        }
    }

    public bool Remove(nint handle)
    {
        if (handle == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_roots.Remove(handle))
            {
                return false;
            }

            NativeCallbackRootHealth.RootRemoved();
            return true;
        }
    }
}
