using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProxyToAnyConnect.Security;

namespace ProxyToAnyConnect.SelfTests;

internal static class DpapiAcquisitionCleanupSelfTests
{
    public static int Run()
    {
        try
        {
            SecondAcquisitionFailureReleasesFirstAndClearsInput();
            UnmanagedCopyFailureZeroesBeforeFree();
            ManagedCopyFailureZeroesDestination();

            Console.WriteLine(
                "PASS: DPAPI acquisition/copy rollback clears managed secrets and zeroes/frees partial unmanaged ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: DPAPI acquisition cleanup regression: {ex}");
            return 1;
        }
    }

    private static void SecondAcquisitionFailureReleasesFirstAndClearsInput()
    {
        var secret = Encoding.UTF8.GetBytes("self-test-sensitive-input");
        var entropy = new byte[] { 1, 2, 3, 4 };
        var acquisitions = 0;
        var ownedReleases = 0;

        try
        {
            _ = DpapiBlobMemory.WithInputBlobs<object?>(
                secret,
                entropy,
                bytes =>
                {
                    acquisitions++;
                    if (acquisitions == 2)
                    {
                        throw new SyntheticAcquisitionException();
                    }

                    return DpapiBlobMemory.Allocate(bytes);
                },
                blob =>
                {
                    if (blob.Data == 0)
                    {
                        return;
                    }

                    ownedReleases++;
                    DpapiBlobMemory.Free(blob, localAlloc: false);
                },
                static (_, _) => throw new InvalidOperationException(
                    "DPAPI operation must not run after the second acquisition fails."),
                static bytes => CryptographicOperations.ZeroMemory(bytes));

            throw new InvalidOperationException("Synthetic second DPAPI acquisition unexpectedly succeeded.");
        }
        catch (SyntheticAcquisitionException)
        {
        }

        if (acquisitions != 2 || ownedReleases != 1)
        {
            throw new InvalidOperationException(
                $"Second-acquisition rollback ownership mismatch: acquisitions={acquisitions}, releases={ownedReleases}.");
        }

        if (secret.Any(value => value != 0))
        {
            throw new InvalidOperationException(
                "Managed secret input was not cleared after the second DPAPI blob acquisition failed.");
        }
    }

    private static void UnmanagedCopyFailureZeroesBeforeFree()
    {
        var source = Encoding.UTF8.GetBytes("copy-failure-sensitive-input");
        nint allocated = 0;
        var freeCount = 0;
        var zeroObservedBeforeFree = false;

        try
        {
            _ = DpapiBlobMemory.Allocate(
                source,
                size =>
                {
                    allocated = Marshal.AllocHGlobal(size);
                    return allocated;
                },
                (bytes, destination) =>
                {
                    Marshal.Copy(bytes, 0, destination, bytes.Length);
                    throw new SyntheticCopyException();
                },
                pointer =>
                {
                    freeCount++;
                    zeroObservedBeforeFree = true;
                    for (var index = 0; index < source.Length; index++)
                    {
                        if (Marshal.ReadByte(pointer, index) != 0)
                        {
                            zeroObservedBeforeFree = false;
                            break;
                        }
                    }

                    Marshal.FreeHGlobal(pointer);
                    allocated = 0;
                });

            throw new InvalidOperationException("Synthetic unmanaged DPAPI copy unexpectedly succeeded.");
        }
        catch (SyntheticCopyException)
        {
        }
        finally
        {
            if (allocated != 0)
            {
                Marshal.FreeHGlobal(allocated);
            }
        }

        if (freeCount != 1 || !zeroObservedBeforeFree)
        {
            throw new InvalidOperationException(
                $"Failed unmanaged DPAPI copy did not zero/free exact partial ownership: frees={freeCount}, zero={zeroObservedBeforeFree}.");
        }
    }

    private static void ManagedCopyFailureZeroesDestination()
    {
        var source = Encoding.UTF8.GetBytes("managed-copy-sensitive-output");
        var blob = DpapiBlobMemory.Allocate(source);
        byte[]? observedDestination = null;

        try
        {
            try
            {
                _ = DpapiBlobMemory.CopyToManaged(
                    blob,
                    (sourcePointer, destination) =>
                    {
                        Marshal.Copy(sourcePointer, destination, 0, destination.Length);
                        throw new SyntheticCopyException();
                    },
                    destination => observedDestination = destination);

                throw new InvalidOperationException("Synthetic managed DPAPI copy unexpectedly succeeded.");
            }
            catch (SyntheticCopyException)
            {
            }

            if (observedDestination is null || observedDestination.Any(value => value != 0))
            {
                throw new InvalidOperationException(
                    "Managed DPAPI plaintext destination was not zeroed after copy failure before publication.");
            }
        }
        finally
        {
            DpapiBlobMemory.Free(blob, localAlloc: false);
            CryptographicOperations.ZeroMemory(source);
        }
    }

    private sealed class SyntheticAcquisitionException : Exception
    {
    }

    private sealed class SyntheticCopyException : Exception
    {
    }
}
