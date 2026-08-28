using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProxyToAnyConnect.Security;

internal static class WindowsSecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProxyToAnyConnect:DPAPI:v1");

    public static string Protect(string plainText)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        if (plainText.Length == 0)
        {
            return string.Empty;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        return DpapiBlobMemory.WithInputBlobs(
            plainBytes,
            Entropy,
            DpapiBlobMemory.Allocate,
            static blob => DpapiBlobMemory.Free(blob, localAlloc: false),
            static (inputValue, entropyValue) =>
            {
                var input = inputValue;
                var entropy = entropyValue;
                if (!CryptProtectData(
                        ref input,
                        "ProxyToAnyConnect secret",
                        ref entropy,
                        0,
                        0,
                        CryptProtectUiForbidden,
                        out var output))
                {
                    throw new InvalidOperationException(
                        $"Windows DPAPI CryptProtectData failed with error {Marshal.GetLastWin32Error()}.");
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
            static bytes => CryptographicOperations.ZeroMemory(bytes));
    }

    public static string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        if (protectedValue.Length == 0)
        {
            return string.Empty;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Protected secret is not valid base64 data.", ex);
        }

        return DpapiBlobMemory.WithInputBlobs(
            protectedBytes,
            Entropy,
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
                        $"Windows DPAPI CryptUnprotectData failed with error {Marshal.GetLastWin32Error()}.");
                }

                try
                {
                    var plainBytes = DpapiBlobMemory.CopyToManaged(output);
                    try
                    {
                        return Encoding.UTF8.GetString(plainBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plainBytes);
                    }
                }
                finally
                {
                    DpapiBlobMemory.Free(output, localAlloc: true);
                }
            },
            static bytes => CryptographicOperations.ZeroMemory(bytes));
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DpapiDataBlob dataIn,
        string description,
        ref DpapiDataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DpapiDataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DpapiDataBlob dataIn,
        nint description,
        ref DpapiDataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DpapiDataBlob dataOut);
}
