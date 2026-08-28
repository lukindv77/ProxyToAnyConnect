using System.Text;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class VerificationHttpParserTests
{
    public static int Run()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Verification HTTP 2xx body is accepted", AcceptsSuccessBody),
            ("Verification HTTP redirect is rejected", RejectsRedirect),
            ("Verification HTTP malformed status line is rejected", RejectsMalformedStatusLine),
            ("Verification HTTP Content-Length framing is exact", RejectsContentLengthFramingViolations),
            ("Verification HTTP transfer framing ambiguity is rejected", RejectsTransferFramingAmbiguity),
            ("Verification HTTP chunked body is decoded", DecodesChunkedBody),
            ("Verification HTTP chunked trailers are validated", ValidatesChunkedTrailers)
        };

        var failed = 0;
        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"FAIL: {name}: {ex}");
            }
        }

        return failed;
    }

    private static void AcceptsSuccessBody()
    {
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Content-Length: 11\r\n\r\n" +
            "203.0.113.7");

        var body = VpnConnectivityVerifier.ParseHttpSuccessBody(response);
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Successful verification response body was parsed incorrectly.");
        }
    }

    private static void RejectsRedirect()
    {
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\n" +
            "Location: https://other.example/\r\n" +
            "Content-Length: 0\r\n\r\n");

        try
        {
            _ = VpnConnectivityVerifier.ParseHttpSuccessBody(response);
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException("HTTP redirect must not be accepted as successful L2TP verification.");
    }

    private static void RejectsMalformedStatusLine()
    {
        AssertRejected(
            "NOTHTTP 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Malformed non-HTTP status line was accepted.");
        AssertRejected(
            "HTTP/2 200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Unsupported textual HTTP version was accepted.");
        AssertRejected(
            "HTTP/1.1 0200 OK\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Non-three-digit status code was accepted.");
    }

    private static void RejectsContentLengthFramingViolations()
    {
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n203.0.113.7",
            "Bytes after Content-Length: 0 were accepted as verification evidence.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 12\r\n\r\n203.0.113.7",
            "Truncated Content-Length body was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 999999999999999999999\r\n\r\n",
            "Overflowing Content-Length was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nContent-Length: 11\r\nContent-Length: 11\r\n\r\n203.0.113.7",
            "Duplicate Content-Length fields were accepted by the fail-closed verifier.");
    }

    private static void RejectsTransferFramingAmbiguity()
    {
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 11\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\n",
            "Transfer-Encoding plus Content-Length ambiguity was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\n",
            "Unsupported transfer coding was accepted because it contained the word chunked.");
    }

    private static void DecodesChunkedBody()
    {
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/plain\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n" +
            "0\r\n\r\n");

        var body = VpnConnectivityVerifier.ParseHttpSuccessBody(response);
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Chunked verification response was decoded incorrectly.");
        }
    }

    private static void ValidatesChunkedTrailers()
    {
        var valid = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Transfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n" +
            "0\r\nX-Verification: complete\r\n\r\n");
        var body = VpnConnectivityVerifier.ParseHttpSuccessBody(valid);
        if (Encoding.ASCII.GetString(body) != "203.0.113.7")
        {
            throw new InvalidOperationException("Valid chunked trailer framing changed decoded verification body.");
        }

        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n",
            "Incomplete zero-chunk terminator was accepted.");
        AssertRejected(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "B\r\n203.0.113.7\r\n0\r\n\r\nextra",
            "Bytes after a complete chunked message were accepted.");
    }

    private static void AssertRejected(string rawResponse, string message)
    {
        try
        {
            _ = VpnConnectivityVerifier.ParseHttpSuccessBody(Encoding.ASCII.GetBytes(rawResponse));
        }
        catch (IOException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
