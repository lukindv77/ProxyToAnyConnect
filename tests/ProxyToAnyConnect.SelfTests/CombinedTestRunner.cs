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

        if (VerificationParserSetupSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VerificationResponseReadSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VerificationChunkedDecodeSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VerificationProbeRequestSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VerificationBodyViewSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VerificationPooledResponseOwnerSelfTests.Run() != 0)
        {
            return 1;
        }

        var lifetimeFailures = await ProxyLifetimeSelfTests.RunAsync();
        if (lifetimeFailures != 0)
        {
            return 1;
        }

        if (await ProxyShutdownDrainSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        var routeFailures = await NativeRouteSelfTests.RunAsync();
        if (routeFailures != 0)
        {
            return 1;
        }

        if (ReconnectCooldownSelfTests.Run() != 0)
        {
            return 1;
        }

        if (await LoggingAndMetricsSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        if (DailyLogPathCacheSelfTests.Run() != 0)
        {
            return 1;
        }

        if (SecuritySelfTests.Run() != 0)
        {
            return 1;
        }

        if (EphemeralRasPhonebookSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsCacheSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsQuerySetupSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsNameSkipSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsResponseAddressListSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsNameMaterializationSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsCnameLoopTrackingSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsParsedResponseValueSelfTests.Run() != 0)
        {
            return 1;
        }

        if (DnsAResultStorageSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VpnContextLifetimeSelfTests.Run() != 0)
        {
            return 1;
        }

        if (VpnLatestStatusSelfTests.Run() != 0)
        {
            return 1;
        }

        if (await ProcessMemoryHealthSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        if (await ConfigurationAndReconfigureSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        if (await SelectiveReconfigureStressSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        if (ProxyHeaderScanSelfTests.Run() != 0)
        {
            return 1;
        }

        if (ProxyParserAllocationSelfTests.Run() != 0)
        {
            return 1;
        }

        if (ProxySetupTimingSelfTests.Run() != 0)
        {
            return 1;
        }

        if (ProxyConnectSetupSelfTests.Run() != 0)
        {
            return 1;
        }

        if (await ProxyLifecycleStressSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        if (await ProxyDataPathSelfTests.RunAsync() != 0)
        {
            return 1;
        }

        Console.WriteLine("All extended fail-closed, lifetime, shutdown-drain, bounded-status, memory-health, daily-log-path, configuration/reconfigure, incremental-header, parser-allocation/timing, verification-parser/read/chunk/request/body-view/owner, CONNECT-setup, DNS-query/name-skip/address-list/name-materialization/cname-loop/value-result/a-storage, stress, DNS-cache and data-path self-tests passed.");
        return 0;
    }
}
