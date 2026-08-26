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
        var input = CreateBlob(plainBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
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
                var protectedBytes = CopyBlob(output);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                FreeBlob(output);
            }
        }
        finally
        {
            FreeBlob(input, localAlloc: false);
            FreeBlob(entropy, localAlloc: false);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
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

        var input = CreateBlob(protectedBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
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
                var plainBytes = CopyBlob(output);
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
                FreeBlob(output);
            }
        }
        finally
        {
            FreeBlob(input, localAlloc: false);
            FreeBlob(entropy, localAlloc: false);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return default;
        }

        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob
        {
            Size = bytes.Length,
            Data = pointer
        };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == 0)
        {
            return [];
        }

        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void FreeBlob(DataBlob blob, bool localAlloc = true)
    {
        if (blob.Data == 0)
        {
            return;
        }

        if (localAlloc)
        {
            _ = LocalFree(blob.Data);
        }
        else
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        nint description,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);
}
