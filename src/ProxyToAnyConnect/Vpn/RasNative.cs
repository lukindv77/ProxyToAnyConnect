using System.Runtime.InteropServices;
using System.Text;

namespace ProxyToAnyConnect.Vpn;

internal static class RasNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorBufferTooSmall = 603;
    internal const int MaxPath = 260;
    internal const int RasMaxEntryName = 256;
    internal const int RasMaxPhoneNumber = 128;
    internal const int RasMaxCallbackNumber = 128;
    internal const int RasMaxDeviceType = 16;
    internal const int RasMaxDeviceName = 128;
    internal const int RasMaxAreaCode = 10;
    internal const int RasMaxPadType = 32;
    internal const int RasMaxX25Address = 200;
    internal const int RasMaxFacilities = 200;
    internal const int RasMaxUserData = 200;
    internal const int RasMaxDnsSuffix = 256;
    internal const int Unlen = 256;
    internal const int Pwlen = 256;
    internal const int Dnlen = 15;
    internal const int RasMaxIpAddress = 15;

    internal const uint RasNpIp = 0x00000004;
    internal const uint RasFpPpp = 0x00000001;
    internal const uint RasEntryTypeVpn = 2;
    internal const uint VpnStrategyL2tpOnly = 3;
    internal const uint RasIdleDisabled = 0xffffffff;

    internal const uint RasEoUseLogonCredentials = 0x00004000;
    internal const uint RasEoRequirePap = 0x00040000;
    internal const uint RasEoRequireChap = 0x08000000;
    internal const uint RasEoRequireMsChap2 = 0x20000000;
    internal const uint RasEo2UsePreSharedKey = 0x00000010;

    internal const uint RasCmPreSharedKey = 0x00000010;

    // RASP_PppIp from RASPROJECTION.
    internal const int RaspPppIp = 0x8021;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct RasIpAddress
    {
        public byte A;
        public byte B;
        public byte C;
        public byte D;
    }

    // Windows XP-sized RASENTRYW. dwSize identifies the structure version to RAS;
    // all fields needed for IPv4 L2TP/IPsec are present in this version.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal sealed class RasEntry
    {
        public uint DwSize;
        public uint DwfOptions;
        public uint DwCountryId;
        public uint DwCountryCode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxAreaCode + 1)]
        public string SzAreaCode = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxPhoneNumber + 1)]
        public string SzLocalPhoneNumber = string.Empty;

        public uint DwAlternateOffset;
        public RasIpAddress IpAddr;
        public RasIpAddress IpAddrDns;
        public RasIpAddress IpAddrDnsAlt;
        public RasIpAddress IpAddrWins;
        public RasIpAddress IpAddrWinsAlt;
        public uint DwFrameSize;
        public uint DwfNetProtocols;
        public uint DwFramingProtocol;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string SzScript = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string SzAutodialDll = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string SzAutodialFunc = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceType + 1)]
        public string SzDeviceType = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceName + 1)]
        public string SzDeviceName = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxPadType + 1)]
        public string SzX25PadType = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxX25Address + 1)]
        public string SzX25Address = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxFacilities + 1)]
        public string SzX25Facilities = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxUserData + 1)]
        public string SzX25UserData = string.Empty;

        public uint DwChannels;
        public uint DwReserved1;
        public uint DwReserved2;
        public uint DwSubEntries;
        public uint DwDialMode;
        public uint DwDialExtraPercent;
        public uint DwDialExtraSampleSeconds;
        public uint DwHangUpExtraPercent;
        public uint DwHangUpExtraSampleSeconds;
        public uint DwIdleDisconnectSeconds;
        public uint DwType;
        public uint DwEncryptionType;
        public uint DwCustomAuthKey;
        public Guid GuidId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string SzCustomDialDll = string.Empty;

        public uint DwVpnStrategy;
        public uint DwfOptions2;
        public uint DwfOptions3;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDnsSuffix)]
        public string SzDnsSuffix = string.Empty;

        public uint DwTcpWindowSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)]
        public string SzPrerequisitePbk = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxEntryName + 1)]
        public string SzPrerequisiteEntry = string.Empty;

        public uint DwRedialCount;
        public uint DwRedialPause;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct RasDevInfo
    {
        public uint DwSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceType + 1)]
        public string SzDeviceType;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceName + 1)]
        public string SzDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal sealed class RasCredentials
    {
        public uint DwSize;
        public uint DwMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Unlen + 1)]
        public string SzUserName = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Pwlen + 1)]
        public string SzPassword = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = Dnlen + 1)]
        public string SzDomain = string.Empty;
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
    internal static extern uint RasEnumDevicesW(
        [In, Out] RasDevInfo[] devices,
        ref uint bufferSize,
        out uint deviceCount);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasSetEntryPropertiesW(
        string phoneBook,
        string entryName,
        [In] RasEntry entry,
        uint entryInfoSize,
        nint deviceInfo,
        uint deviceInfoSize);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasDeleteEntryW(string phoneBook, string entryName);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint RasSetCredentialsW(
        string phoneBook,
        string entryName,
        [In] RasCredentials credentials,
        [MarshalAs(UnmanagedType.Bool)] bool clearCredentials);

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
