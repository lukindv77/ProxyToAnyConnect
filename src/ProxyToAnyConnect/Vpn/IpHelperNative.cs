using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Vpn;

internal static class IpHelperNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibIpForwardRow
    {
        public uint DwForwardDest;
        public uint DwForwardMask;
        public uint DwForwardPolicy;
        public uint DwForwardNextHop;
        public uint DwForwardIfIndex;
        public uint DwForwardType;
        public uint DwForwardProto;
        public uint DwForwardAge;
        public uint DwForwardNextHopAS;
        public uint DwForwardMetric1;
        public uint DwForwardMetric2;
        public uint DwForwardMetric3;
        public uint DwForwardMetric4;
        public uint DwForwardMetric5;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern uint GetIpForwardTable(
        nint ipForwardTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order);
}
