Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [string] $Old,
        [Parameter(Mandatory = $true)] [string] $New,
        [int] $ExpectedCount = 1
    )

    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $oldNormalized = $Old.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $newNormalized = $New.Replace("`r`n", "`n").TrimEnd("`r", "`n")
    $actualCount = [regex]::Matches($text, [regex]::Escape($oldNormalized)).Count
    if ($actualCount -ne $ExpectedCount) {
        throw "Expected $ExpectedCount exact replacement target(s) in '$Path', found $actualCount."
    }

    $updated = $text.Replace($oldNormalized, $newNormalized)
    [IO.File]::WriteAllText($Path, $updated, [Text.UTF8Encoding]::new($false))
}

$activeProtector = 'src/ProxyToAnyConnect/Security/WindowsSecretProtector.cs'
$legacyProtector = 'src/ProxyToAnyConnect/Configuration/DpapiSecretProtector.cs'
$tests = 'tests/ProxyToAnyConnect.SelfTests/SecuritySelfTests.cs'

Replace-Exact $activeProtector @'
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
'@ @'
    private static void FreeBlob(DataBlob blob, bool localAlloc = true)
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
'@

Replace-Exact $legacyProtector @'
using System.Runtime.InteropServices;
using System.Text;
'@ @'
using System.Runtime.InteropServices;
using System.Text;
using ProxyToAnyConnect.Security;
'@

Replace-Exact $legacyProtector @'
                if (output.Data != 0)
                {
                    _ = LocalFree(output.Data);
                }
'@ @'
                FreeBlob(output, localAlloc: true);
'@ 2

Replace-Exact $legacyProtector @'
    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != 0)
        {
            Marshal.FreeHGlobal(blob.Data);
        }
    }
'@ @'
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
'@

Replace-Exact $tests @'
using ProxyToAnyConnect.Security;
'@ @'
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ProxyToAnyConnect.Security;
'@

Replace-Exact $tests @'
        try
        {
            var protectedValue = WindowsSecretProtector.Protect(secret);
'@ @'
        try
        {
            UnmanagedSecretBufferIsZeroed();

            var protectedValue = WindowsSecretProtector.Protect(secret);
'@

Replace-Exact $tests @'
            Console.WriteLine("PASS: Windows DPAPI secret protection roundtrip");
'@ @'
            Console.WriteLine("PASS: unmanaged secret buffers are zeroed before release and Windows DPAPI roundtrip succeeds");
'@

Replace-Exact $tests @'
    }
}
'@ @'
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
'@
