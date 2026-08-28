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
        var input = AllocateBlob(plaintextBytes);
        var entropy = AllocateBlob(OptionalEntropy);

        try
        {
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
                var protectedBytes = new byte[output.Size];
                if (output.Size > 0)
                {
                    Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                }

                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                FreeBlob(output, localAlloc: true);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
            Array.Clear(plaintextBytes);
        }
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

        var input = AllocateBlob(protectedBytes);
        var entropy = AllocateBlob(OptionalEntropy);

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
                    $"Windows DPAPI CryptUnprotectData failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }

            try
            {
                var plaintextBytes = new byte[output.Size];
                if (output.Size > 0)
                {
                    Marshal.Copy(output.Data, plaintextBytes, 0, output.Size);
                }

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
                FreeBlob(output, localAlloc: true);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
            Array.Clear(protectedBytes);
        }
    }

    private static DataBlob AllocateBlob(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return default;
        }

        var pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static void FreeBlob(DataBlob blob, bool localAlloc = false)
    {
        if (blob.Data == 0)
        {
            return;
        }

        try
        {
            UnmanagedSecretMemory.Zero(blob.Data, blob.Size);
        }
        finally
        {
            if (localAlloc)
            {
                _ = LocalFree(blob.Data);
            }
            else
            {
                Marshal.FreeHGlobal(blob.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        nint dataDescription,
        ref DataBlob optionalEntropy,
        nint reserved,
        nint promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
