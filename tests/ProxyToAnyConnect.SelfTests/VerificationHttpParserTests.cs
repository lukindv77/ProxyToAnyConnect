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
            ("Verification HTTP chunked body is decoded", DecodesChunkedBody)
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
}
