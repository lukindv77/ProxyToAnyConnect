using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Vpn;

internal sealed class WindowsDefaultRouteInspector
{
    private const int MaxCaptureAttempts = 3;

    public Task<DefaultRouteSnapshot> CaptureIPv4Async(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CaptureIPv4(cancellationToken));
    }

    private static DefaultRouteSnapshot CaptureIPv4(CancellationToken cancellationToken)
    {
        uint size = 0;
        var result = IpHelperNative.GetIpForwardTable(0, ref size, order: true);
        if (result is not (IpHelperNative.ErrorSuccess or IpHelperNative.ErrorInsufficientBuffer))
        {
            throw CreateRouteTableException(result);
        }

        if (size < sizeof(uint))
        {
            throw new InvalidOperationException(
                $"Windows returned an invalid IPv4 route-table buffer size: {size} bytes.");
        }

        for (var attempt = 1; attempt <= MaxCaptureAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allocatedSize = size;
            var buffer = Marshal.AllocHGlobal(checked((int)allocatedSize));
            try
            {
                result = IpHelperNative.GetIpForwardTable(buffer, ref size, order: true);
                if (result == IpHelperNative.ErrorInsufficientBuffer)
                {
                    continue;
                }

                if (result != IpHelperNative.ErrorSuccess)
                {
                    throw CreateRouteTableException(result);
                }

                return ParseDefaultRoutes(buffer, Math.Min(allocatedSize, size));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            "Windows IPv4 route table changed too quickly to capture a stable snapshot.");
    }

    internal static DefaultRouteSnapshot ParseDefaultRoutes(nint tableBuffer, uint bufferSize)
    {
        if (tableBuffer == 0)
        {
            throw new ArgumentNullException(nameof(tableBuffer));
        }

        if (bufferSize < sizeof(uint))
        {
            throw new InvalidOperationException("IPv4 route-table buffer is too small.");
        }

        var count = checked((uint)Marshal.ReadInt32(tableBuffer));
        var rowSize = Marshal.SizeOf<IpHelperNative.MibIpForwardRow>();
        var requiredSize = checked((ulong)sizeof(uint) + ((ulong)count * (uint)rowSize));
        if (requiredSize > bufferSize)
        {
            throw new InvalidOperationException(
                $"IPv4 route-table buffer is truncated. Entries={count}, required={requiredSize}, available={bufferSize}.");
        }

        var routes = new List<DefaultRouteEntry>();
        for (uint index = 0; index < count; index++)
        {
            var offset = checked(sizeof(uint) + ((int)index * rowSize));
            var rowPointer = IntPtr.Add(tableBuffer, offset);
            var row = Marshal.PtrToStructure<IpHelperNative.MibIpForwardRow>(rowPointer);

            // Microsoft documents a destination of 0.0.0.0 as the IPv4 default route.
            // Require the /0 mask too so malformed or policy-specific entries do not
            // accidentally become part of the invariant snapshot.
            if (row.DwForwardDest != 0 || row.DwForwardMask != 0)
            {
                continue;
            }

            routes.Add(new DefaultRouteEntry(
                row.DwForwardIfIndex,
                NetworkUInt32ToIPv4(row.DwForwardNextHop).ToString(),
                row.DwForwardMetric1,
                "GetIpForwardTable"));
        }

        return new DefaultRouteSnapshot(
            routes
                .OrderBy(route => route.InterfaceIndex)
                .ThenBy(route => route.NextHop, StringComparer.Ordinal)
                .ThenBy(route => route.RouteMetric)
                .ToArray());
    }

    internal static IPAddress NetworkUInt32ToIPv4(uint value)
    {
        // GetIpForwardTable exposes IPv4 address fields in network byte order.
        // Marshaling them into a UInt32 preserves their four memory bytes; using
        // BitConverter here reconstructs those bytes without an extra host/network swap.
        return new IPAddress(BitConverter.GetBytes(value));
    }

    public static void EnsureUnchanged(DefaultRouteSnapshot before, DefaultRouteSnapshot after)
    {
        if (before.Routes.SequenceEqual(after.Routes))
        {
            return;
        }

        throw new InvalidOperationException(
            "Windows IPv4 default routes changed while L2TP was active. " +
            $"Before: {before}; After: {after}. The VPN connection will be rejected fail-closed.");
    }

    private static InvalidOperationException CreateRouteTableException(uint errorCode) =>
        new(
            $"Unable to inspect the Windows IPv4 route table through GetIpForwardTable: " +
            $"{errorCode}: {new Win32Exception(checked((int)errorCode)).Message}");
}

internal sealed record DefaultRouteEntry(
    uint InterfaceIndex,
    string NextHop,
    uint RouteMetric,
    string Source);

internal sealed record DefaultRouteSnapshot(IReadOnlyList<DefaultRouteEntry> Routes)
{
    public override string ToString() =>
        Routes.Count == 0
            ? "<none>"
            : string.Join(
                "; ",
                Routes.Select(route =>
                    $"if={route.InterfaceIndex},next={route.NextHop},metric={route.RouteMetric},source={route.Source}"));
}
