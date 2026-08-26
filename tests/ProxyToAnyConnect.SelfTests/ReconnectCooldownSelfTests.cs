using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class ReconnectCooldownSelfTests
{
    public static int Run()
    {
        try
        {
            AssertRemaining(now: 1_000, retryNotBefore: 0, expected: 0);
            AssertRemaining(now: 1_000, retryNotBefore: 1_500, expected: 500);
            AssertRemaining(now: 1_500, retryNotBefore: 1_500, expected: 0);
            AssertRemaining(now: 2_000, retryNotBefore: 1_500, expected: 0);
            Console.WriteLine("PASS: L2TP reconnect cooldown calculation");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: L2TP reconnect cooldown calculation: {ex}");
            return 1;
        }
    }

    private static void AssertRemaining(long now, long retryNotBefore, long expected)
    {
        var actual = RasConnectionManager.GetReconnectCooldownRemainingMilliseconds(now, retryNotBefore);
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Cooldown calculation returned {actual}; expected {expected} for now={now}, retryNotBefore={retryNotBefore}.");
        }
    }
}
