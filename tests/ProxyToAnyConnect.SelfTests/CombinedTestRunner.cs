namespace ProxyToAnyConnect.SelfTests;

internal static class CombinedTestRunner
{
    public static async Task<int> Main()
    {
        var existingResult = await Program.Main();
        if (existingResult != 0)
        {
            return existingResult;
        }

        var parserFailures = VerificationHttpParserTests.Run();
        if (parserFailures != 0)
        {
            Console.Error.WriteLine($"Additional verification parser tests failed: {parserFailures}.");
            return 1;
        }

        Console.WriteLine("Additional verification parser tests passed.");

        var lifetimeFailures = await ProxyLifetimeSelfTests.RunAsync();
        if (lifetimeFailures != 0)
        {
            return 1;
        }

        Console.WriteLine("All extended fail-closed self-tests passed.");
        return 0;
    }
}
