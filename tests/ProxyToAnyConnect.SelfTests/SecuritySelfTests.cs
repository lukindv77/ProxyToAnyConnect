using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.SelfTests;

internal static class SecuritySelfTests
{
    public static int Run()
    {
        const string secret = "p@ssw0rd-ProxyToAnyConnect-✓";
        try
        {
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

            Console.WriteLine("PASS: Windows DPAPI secret protection roundtrip");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: Windows DPAPI secret protection roundtrip: {ex}");
            return 1;
        }
    }
}
