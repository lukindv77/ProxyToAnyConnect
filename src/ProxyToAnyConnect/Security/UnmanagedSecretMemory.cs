using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Security;

internal static class UnmanagedSecretMemory
{
    internal static void Zero(nint data, int size)
    {
        if (size < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        if (data == 0 || size == 0)
        {
            return;
        }

        // Marshal.WriteByte performs an observable unmanaged write for every owned
        // byte, so the wipe cannot be removed as a dead managed-memory store. Secret
        // blobs are small and this path is configuration/dial setup, never proxy I/O.
        for (var index = 0; index < size; index++)
        {
            Marshal.WriteByte(data, index, 0);
        }
    }
}
