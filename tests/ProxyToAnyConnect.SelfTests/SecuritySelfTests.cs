using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.SelfTests;

internal static class SecuritySelfTests
{
    public static int Run()
    {
        const string secret = "p@ssw0rd-ProxyToAnyConnect-✓";
        try
        {
            UnmanagedSecretBufferIsZeroed();

            var protectedValue = WindowsSecretProtector.Protect(secret);
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                throw new InvalidOperationException("DPAPI returned an empty protected value.");
            }

            if (protectedValue.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Protected value unexpectedly contains plaintext.");
            }

            var unprotected = WindowsSecretProtector.Unprotect(protectedValue);
            if (!string.Equals(unprotected, secret, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("DPAPI roundtrip returned a different secret.");
            }

            Console.WriteLine("PASS: unmanaged secret buffers are zeroed before release and Windows DPAPI roundtrip succeeds");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Windows DPAPI secret protection roundtrip: {ex}");
            return 1;
        }
    }

    private static void UnmanagedSecretBufferIsZeroed()
    {
        var source = Enumerable.Repeat((byte)0xA5, 64).ToArray();
        var observed = new byte[source.Length];
        var pointer = Marshal.AllocHGlobal(source.Length);
        try
        {
            Marshal.Copy(source, 0, pointer, source.Length);
            UnmanagedSecretMemory.Zero(pointer, source.Length);
            Marshal.Copy(pointer, observed, 0, observed.Length);
            if (observed.Any(value => value != 0))
            {
                throw new InvalidOperationException("Unmanaged secret memory retained non-zero bytes after the wipe primitive.");
            }

            UnmanagedSecretMemory.Zero(0, 0);
            try
            {
                UnmanagedSecretMemory.Zero(pointer, -1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            throw new InvalidOperationException("Unmanaged secret memory wipe accepted a negative owned length.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source);
            CryptographicOperations.ZeroMemory(observed);
            Marshal.FreeHGlobal(pointer);
        }
    }
}
