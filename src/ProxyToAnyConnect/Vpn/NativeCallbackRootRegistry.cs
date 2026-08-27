namespace ProxyToAnyConnect.Vpn;

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
            _roots[handle] = callback;
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
            return _roots.Remove(handle);
        }
    }
}
