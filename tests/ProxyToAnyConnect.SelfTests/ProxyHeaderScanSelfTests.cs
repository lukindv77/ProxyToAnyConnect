using System.Text;
using ProxyToAnyConnect.Proxy;

namespace ProxyToAnyConnect.SelfTests;

internal static class ProxyHeaderScanSelfTests
{
    public static int Run()
    {
        try
        {
            FindsDelimiterAcrossIncrementalReadBoundary();
            HonorsSearchStartInsteadOfRescanningOldPrefix();
            RejectsInvalidSearchStart();

            Console.WriteLine("PASS: proxy header terminator scanning is incremental and boundary-safe");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: proxy incremental header scan regression: {ex}");
            return 1;
        }
    }

    private static void FindsDelimiterAcrossIncrementalReadBoundary()
    {
        var data = Encoding.ASCII.GetBytes(
            "CONNECT example.test:443 HTTP/1.1\r\nHost: example.test:443\r\n\r\n");
        var expected = data.Length - 4;

        // Simulate the previous read ending two bytes into CRLFCRLF. The reader
        // keeps only a three-byte overlap because no earlier start can become a
        // newly completed four-byte delimiter after the next read.
        var previousLength = data.Length - 2;
        var searchStart = Math.Max(0, previousLength - 3);
        var actual = ProxyServer.FindHeaderEnd(data, searchStart);

        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Boundary-spanning CRLFCRLF was found at {actual}; expected {expected}.");
        }
    }

    private static void HonorsSearchStartInsteadOfRescanningOldPrefix()
    {
        var data = Encoding.ASCII.GetBytes("old\r\n\r\nprefix-new\r\n\r\n");
        var first = ProxyServer.FindHeaderEnd(data);
        var searchStart = first + 4;
        var second = ProxyServer.FindHeaderEnd(data, searchStart);

        if (first != 3 || second != 15)
        {
            throw new InvalidOperationException(
                $"Incremental search returned first={first}, second={second}; expected 3 and 15.");
        }
    }

    private static void RejectsInvalidSearchStart()
    {
        try
        {
            _ = ProxyServer.FindHeaderEnd("abc"u8, 4);
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }

        throw new InvalidOperationException("Out-of-range incremental search start was accepted.");
    }
}
