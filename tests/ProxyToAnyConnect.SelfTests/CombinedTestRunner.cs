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

        var additionalFailures = VerificationHttpParserTests.Run();
        if (additionalFailures != 0)
        {
            Console.Error.WriteLine($"Additional verification parser tests failed: {additionalFailures}.");
            return 1;
        }

        Console.WriteLine("Additional verification parser tests passed.");
        return 0;
    }
}
