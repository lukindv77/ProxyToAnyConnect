using System.Runtime.InteropServices;
using System.Text;

namespace ProxyToAnyConnect.Vpn;

internal static class RasNative
{
    internal const uint ErrorSuccess = 0;
    internal const int RasMaxEntryName = 256;
    internal const int RasMaxPhoneNumber = 128;
    internal const int RasMaxCallbackNumber = 128;
    internal const int Unlen = 256;
    internal const int Pwlen = 256;
    internal const int Dnlen = 15;
    internal const int RasMaxIpAddress = 15;

    // RASP_PppIp from RASPROJECTION.
    internal const int RaspPppIp = 0x8021;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal sealed class RasDialParams
    {
        public uint DwSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxEntryName + 1)]
        public string SzEntryName = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxPhoneNumber + 1)]
        public string SzPhoneNumber = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxCallbackNumber + 1)]
        public string SzCallbackNumber = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Unlen + 1)]
        public string SzUserName = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Pwlen + 1)]
        public string SzPassword = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Dnlen + 1)]
        public string SzDomain = string.Empty;

        public uint DwSubEntry;
        public nuint DwCallbackId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal sealed class RasPppIp
    {
        public uint DwSize;
        public uint DwError;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxIpAddress + 1)]
        public string SzIpAddress = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxIpAddress + 1)]
        public string SzServerIpAddress = string.Empty;

        public uint DwOptions;
        public uint DwServerOptions;
    }

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasGetEntryDialParamsW(
        string? phoneBook,
        [In, Out] RasDialParams dialParams,
        [MarshalAs(UnmanagedType.Bool)] out bool hasPassword);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasDialW(
        nint rasDialExtensions,
        string? phoneBook,
        [In] RasDialParams dialParams,
        uint notifierType,
        nint notifier,
        out nint rasConnection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasGetProjectionInfoW(
        nint rasConnection,
        int projection,
        [In, Out] RasPppIp projectionInfo,
        ref uint projectionInfoSize);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasHangUpW(nint rasConnection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasGetErrorStringW(
        uint errorValue,
        StringBuilder errorString,
        uint bufferSize);

    internal static string DescribeError(uint errorCode)
    {
        var buffer = new StringBuilder(512);
        var result = RasGetErrorStringW(errorCode, buffer, (uint)buffer.Capacity);
        return result == ErrorSuccess && buffer.Length > 0
            ? $"{errorCode}: {buffer}"
            : errorCode.ToString();
    }
}
