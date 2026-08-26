using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Vpn;

internal static class IcmpBoundPing
{
    private const uint IpSuccess = 0;
    private static readonly byte[] Payload = "ProxyToAnyConnect"u8.ToArray();

    public static async Task<IcmpProbeResult> SendAsync(
        IPAddress source,
        IPAddress destination,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("L2TP keepalive supports IPv4 only.");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var task = Task.Run(() => SendCore(source, destination, timeout), CancellationToken.None);
        return await task.WaitAsync(cancellationToken);
    }

    private static IcmpProbeResult SendCore(
        IPAddress source,
        IPAddress destination,
        TimeSpan timeout)
    {
        var handle = IcmpCreateFile();
        if (handle == new nint(-1))
        {
            return new IcmpProbeResult(false, null, Marshal.GetLastWin32Error());
        }

        const int replyBufferSize = 512;
        var replyBuffer = Marshal.AllocHGlobal(replyBufferSize);
        try
        {
            var replies = IcmpSendEcho2Ex(
                handle,
                0,
                0,
                0,
                ToIpAddr(source),
                ToIpAddr(destination),
                Payload,
                checked((ushort)Payload.Length),
                0,
                replyBuffer,
                replyBufferSize,
                checked((uint)Math.Ceiling(timeout.TotalMilliseconds)));

            if (replies == 0)
            {
                return new IcmpProbeResult(false, null, Marshal.GetLastWin32Error());
            }

            // ICMP_ECHO_REPLY starts with:
            // IPAddr Address (DWORD), ULONG Status, ULONG RoundTripTime.
            // These first 12 bytes have identical offsets on x86/x64, so we do not
            // need to marshal the pointer-containing remainder of the native struct.
            var status = unchecked((uint)Marshal.ReadInt32(replyBuffer, 4));
            var roundTripMilliseconds = unchecked((uint)Marshal.ReadInt32(replyBuffer, 8));
            return status == IpSuccess
                ? new IcmpProbeResult(true, TimeSpan.FromMilliseconds(roundTripMilliseconds), 0)
                : new IcmpProbeResult(false, null, unchecked((int)status));
        }
        finally
        {
            Marshal.FreeHGlobal(replyBuffer);
            _ = IcmpCloseHandle(handle);
        }
    }

    private static uint ToIpAddr(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern nint IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(nint icmpHandle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        nint icmpHandle,
        nint eventHandle,
        nint apcRoutine,
        nint apcContext,
        uint sourceAddress,
        uint destinationAddress,
        [In] byte[] requestData,
        ushort requestSize,
        nint requestOptions,
        nint replyBuffer,
        int replySize,
        uint timeout);
}

internal readonly record struct IcmpProbeResult(
    bool Success,
    TimeSpan? RoundTripTime,
    int ErrorCode);
