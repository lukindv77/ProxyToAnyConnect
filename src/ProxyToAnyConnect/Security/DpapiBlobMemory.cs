using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ProxyToAnyConnect.Security;

[StructLayout(LayoutKind.Sequential)]
internal struct DpapiDataBlob
{
    public int Size;
    public nint Data;
}

internal static class DpapiBlobMemory
{
    internal static DpapiDataBlob Allocate(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Allocate(
            bytes,
            static size => Marshal.AllocHGlobal(size),
            static (source, destination) => Marshal.Copy(source, 0, destination, source.Length),
            static pointer => Marshal.FreeHGlobal(pointer));
    }

    internal static DpapiDataBlob Allocate(
        byte[] bytes,
        Func<int, nint> allocate,
        Action<byte[], nint> copy,
        Action<nint> free)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(allocate);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(free);
        if (bytes.Length == 0)
        {
            return default;
        }

        var pointer = allocate(bytes.Length);
        if (pointer == 0)
        {
            throw new OutOfMemoryException("DPAPI unmanaged allocation returned a null pointer.");
        }

        try
        {
            copy(bytes, pointer);
            return new DpapiDataBlob
            {
                Size = bytes.Length,
                Data = pointer
            };
        }
        catch
        {
            try
            {
                UnmanagedSecretMemory.Zero(pointer, bytes.Length);
            }
            finally
            {
                free(pointer);
            }

            throw;
        }
    }

    internal static byte[] CopyToManaged(DpapiDataBlob blob) =>
        CopyToManaged(
            blob,
            static (source, destination) => Marshal.Copy(source, destination, 0, destination.Length),
            observer: null);

    internal static byte[] CopyToManaged(
        DpapiDataBlob blob,
        Action<nint, byte[]> copy,
        Action<byte[]>? observer)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (blob.Size <= 0 || blob.Data == 0)
        {
            return [];
        }

        var bytes = new byte[blob.Size];
        observer?.Invoke(bytes);
        try
        {
            copy(blob.Data, bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    internal static TResult WithInputBlobs<TResult>(
        byte[] inputBytes,
        byte[] entropyBytes,
        Func<byte[], DpapiDataBlob> acquire,
        Action<DpapiDataBlob> release,
        Func<DpapiDataBlob, DpapiDataBlob, TResult> operation,
        Action<byte[]> clearInput)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);
        ArgumentNullException.ThrowIfNull(entropyBytes);
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(clearInput);

        DpapiDataBlob input = default;
        DpapiDataBlob entropy = default;
        try
        {
            input = acquire(inputBytes);
            entropy = acquire(entropyBytes);
            return operation(input, entropy);
        }
        finally
        {
            try
            {
                release(input);
            }
            finally
            {
                try
                {
                    release(entropy);
                }
                finally
                {
                    clearInput(inputBytes);
                }
            }
        }
    }

    internal static void Free(DpapiDataBlob blob, bool localAlloc)
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint memory);
}
