using System.Runtime.InteropServices;
using System.Text;
using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.Configuration;

internal static class DpapiSecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("ProxyToAnyConnect/custom-l2tp/v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        return DpapiBlobMemory.WithInputBlobs(
            plaintextBytes,
            OptionalEntropy,
            DpapiBlobMemory.Allocate,
            static blob => DpapiBlobMemory.Free(blob, localAlloc: false),
            static (inputValue, entropyValue) =>
            {
                var input = inputValue;
                var entropy = entropyValue;
                if (!CryptProtectData(
                        ref input,
                        "ProxyToAnyConnect custom L2TP secret",
                        ref entropy,
                        0,
                        0,
                        CryptProtectUiForbidden,
                        out var output))
                {
                    throw new InvalidOperationException(
                        $"Windows DPAPI CryptProtectData failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                try
                {
                    var protectedBytes = DpapiBlobMemory.CopyToManaged(output);
                    return Convert.ToBase64String(protectedBytes);
                }
                finally
                {
                    DpapiBlobMemory.Free(output, localAlloc: true);
                }
            },
            static bytes => Array.Clear(bytes));
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            return string.Empty;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Stored custom L2TP secret is not a valid DPAPI blob.", ex);
        }

        return DpapiBlobMemory.WithInputBlobs(
            protectedBytes,
            OptionalEntropy,
            DpapiBlobMemory.Allocate,
            static blob => DpapiBlobMemory.Free(blob, localAlloc: false),
            static (inputValue, entropyValue) =>
            {
                var input = inputValue;
                var entropy = entropyValue;
                if (!CryptUnprotectData(
                        ref input,
                        0,
                        ref entropy,
                        0,
                        0,
                        CryptProtectUiForbidden,
                        out var output))
                {
                    throw new InvalidOperationException(
                        $"Windows DPAPI CryptUnprotectData failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }

                try
                {
                    var plaintextBytes = DpapiBlobMemory.CopyToManaged(output);
                    try
                    {
                        return Encoding.UTF8.GetString(plaintextBytes);
                    }
                    finally
                    {
                        Array.Clear(plaintextBytes);
                    }
                }
                finally
                {
                    DpapiBlobMemory.Free(output, localAlloc: true);
                }
            },
            static bytes => Array.Clear(bytes));
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DpapiDataBlob dataIn,
        string? dataDescription,
        ref DpapiDataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DpapiDataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DpapiDataBlob dataIn,
        nint dataDescription,
        ref DpapiDataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DpapiDataBlob dataOut);
}
