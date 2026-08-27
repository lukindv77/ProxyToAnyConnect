namespace ProxyToAnyConnect.SelfTests;

internal static class CombinedTestRunner
{
    public static async Task<int> Main()
    {
        var failedSuites = new List<string>();

        async Task RunAsync(string name, Func<Task<int>> suite)
        {
            try
            {
                if (await suite() == 0)
                {
                    return;
                }

                failedSuites.Add(name);
                Console.Error.WriteLine($"SUITE FAIL: {name} returned a non-zero result.");
            }
            catch (Exception ex)
            {
                failedSuites.Add(name);
                Console.Error.WriteLine($"SUITE FAIL: {name} threw outside its normal test boundary: {ex}");
            }
        }

        void Run(string name, Func<int> suite)
        {
            try
            {
                if (suite() == 0)
                {
                    return;
                }

                failedSuites.Add(name);
                Console.Error.WriteLine($"SUITE FAIL: {name} returned a non-zero result.");
            }
            catch (Exception ex)
            {
                failedSuites.Add(name);
                Console.Error.WriteLine($"SUITE FAIL: {name} threw outside its normal test boundary: {ex}");
            }
        }

        // Preserve the established order and run every suite sequentially. This
        // intentionally adds no test parallelism or shared-state concurrency; it
        // only prevents one independent failure from hiding later regressions in
        // the same Windows CI run.
        await RunAsync(nameof(Program), Program.Main);
        Run(nameof(VerificationHttpParserTests), VerificationHttpParserTests.Run);
        Run(nameof(VerificationParserSetupSelfTests), VerificationParserSetupSelfTests.Run);
        Run(nameof(VerificationResponseReadSelfTests), VerificationResponseReadSelfTests.Run);
        Run(nameof(VerificationChunkedDecodeSelfTests), VerificationChunkedDecodeSelfTests.Run);
        Run(nameof(VerificationProbeRequestSelfTests), VerificationProbeRequestSelfTests.Run);
        Run(nameof(VerificationBodyViewSelfTests), VerificationBodyViewSelfTests.Run);
        Run(nameof(VerificationPooledResponseOwnerSelfTests), VerificationPooledResponseOwnerSelfTests.Run);
        await RunAsync(nameof(ProxyLifetimeSelfTests), ProxyLifetimeSelfTests.RunAsync);
        await RunAsync(nameof(ProxyShutdownDrainSelfTests), ProxyShutdownDrainSelfTests.RunAsync);
        await RunAsync(nameof(AcceptedClientTransportSelfTests), AcceptedClientTransportSelfTests.RunAsync);
        await RunAsync(nameof(NativeRouteSelfTests), NativeRouteSelfTests.RunAsync);
        await RunAsync(nameof(WindowsVpnProfileInspectorLifetimeSelfTests), WindowsVpnProfileInspectorLifetimeSelfTests.RunAsync);
        await RunAsync(nameof(IcmpBoundPingSelfTests), IcmpBoundPingSelfTests.RunAsync);
        await RunAsync(nameof(RasDialerSelfTests), RasDialerSelfTests.RunAsync);
        await RunAsync(nameof(RasConnectionManagerCleanupFailureSelfTests), RasConnectionManagerCleanupFailureSelfTests.RunAsync);
        Run(nameof(RasReadyPublicationSelfTests), RasReadyPublicationSelfTests.Run);
        await RunAsync(nameof(L2tpSocketFactoryCancellationSelfTests), L2tpSocketFactoryCancellationSelfTests.RunAsync);
        await RunAsync(nameof(VpnLeaseManagerLifetimeSelfTests), VpnLeaseManagerLifetimeSelfTests.RunAsync);
        await RunAsync(nameof(VpnLeaseCleanupFailureSelfTests), VpnLeaseCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(VpnSharedFailClosedSelfTests), VpnSharedFailClosedSelfTests.RunAsync);
        Run(nameof(ReconnectCooldownSelfTests), ReconnectCooldownSelfTests.Run);
        await RunAsync(nameof(VpnReconnectCooldownMaintenanceSelfTests), VpnReconnectCooldownMaintenanceSelfTests.RunAsync);
        await RunAsync(nameof(LoggingAndMetricsSelfTests), LoggingAndMetricsSelfTests.RunAsync);
        Run(nameof(DailyLogPathCacheSelfTests), DailyLogPathCacheSelfTests.Run);
        Run(nameof(DailyLogEncodingSelfTests), DailyLogEncodingSelfTests.Run);
        await RunAsync(nameof(RetentionCleanupSchedulerSelfTests), RetentionCleanupSchedulerSelfTests.RunAsync);
        Run(nameof(SecuritySelfTests), SecuritySelfTests.Run);
        Run(nameof(EphemeralRasPhonebookSelfTests), EphemeralRasPhonebookSelfTests.Run);
        Run(nameof(DnsCacheSelfTests), DnsCacheSelfTests.Run);
        Run(nameof(DnsQuerySetupSelfTests), DnsQuerySetupSelfTests.Run);
        Run(nameof(DnsNameSkipSelfTests), DnsNameSkipSelfTests.Run);
        Run(nameof(DnsResponseAddressListSelfTests), DnsResponseAddressListSelfTests.Run);
        Run(nameof(DnsNameMaterializationSelfTests), DnsNameMaterializationSelfTests.Run);
        Run(nameof(DnsCnameLoopTrackingSelfTests), DnsCnameLoopTrackingSelfTests.Run);
        Run(nameof(DnsParsedResponseValueSelfTests), DnsParsedResponseValueSelfTests.Run);
        Run(nameof(DnsAResultStorageSelfTests), DnsAResultStorageSelfTests.Run);
        Run(nameof(VpnContextLifetimeSelfTests), VpnContextLifetimeSelfTests.Run);
        Run(nameof(VpnLatestStatusSelfTests), VpnLatestStatusSelfTests.Run);
        await RunAsync(nameof(ProcessMemoryHealthSelfTests), ProcessMemoryHealthSelfTests.RunAsync);
        Run(nameof(SettingsValidationSelfTests), SettingsValidationSelfTests.Run);
        await RunAsync(nameof(ConfigurationAndReconfigureSelfTests), ConfigurationAndReconfigureSelfTests.RunAsync);
        await RunAsync(nameof(RuntimeReconfigureCancellationSelfTests), RuntimeReconfigureCancellationSelfTests.RunAsync);
        await RunAsync(nameof(CoordinatorOperationLifetimeSelfTests), CoordinatorOperationLifetimeSelfTests.RunAsync);
        await RunAsync(nameof(CoordinatorCleanupFailureSelfTests), CoordinatorCleanupFailureSelfTests.RunAsync);
        await RunAsync(nameof(RuntimeHostOperationLifetimeSelfTests), RuntimeHostOperationLifetimeSelfTests.RunAsync);
        await RunAsync(nameof(ProxyTransactionalStartupSelfTests), ProxyTransactionalStartupSelfTests.RunAsync);
        await RunAsync(nameof(ProxyTransactionalShutdownSelfTests), ProxyTransactionalShutdownSelfTests.RunAsync);
        await RunAsync(nameof(SelectiveReconfigureStressSelfTests), SelectiveReconfigureStressSelfTests.RunAsync);
        Run(nameof(ProxyHeaderScanSelfTests), ProxyHeaderScanSelfTests.Run);
        Run(nameof(ProxyParserAllocationSelfTests), ProxyParserAllocationSelfTests.Run);
        Run(nameof(ProxySetupTimingSelfTests), ProxySetupTimingSelfTests.Run);
        Run(nameof(ProxyConnectSetupSelfTests), ProxyConnectSetupSelfTests.Run);
        await RunAsync(nameof(ProxyHttpFramingSelfTests), ProxyHttpFramingSelfTests.RunAsync);
        await RunAsync(nameof(ProxyLifecycleStressSelfTests), ProxyLifecycleStressSelfTests.RunAsync);
        await RunAsync(nameof(ProxyDataPathSelfTests), ProxyDataPathSelfTests.RunAsync);

        if (failedSuites.Count != 0)
        {
            Console.Error.WriteLine(
                $"Extended self-test run completed with {failedSuites.Count} failed suite(s): " +
                string.Join(", ", failedSuites));
            return 1;
        }

        Console.WriteLine("All extended fail-closed, lifetime, shutdown-drain, response-safe accepted-close, bounded-status, memory-health, bounded-settings/secret-pruning, daily-log-path/io-lifecycle, configuration/reconfigure/cancellation-reconciliation/generation-serialization/coordinator-cleanup/host-shutdown, Windows-profile-helper ownership, native-ICMP keepalive, cancellable-RAS-dial/manager-cleanup/Ready-publication, L2TP-outbound-cancellation, VPN-lease-owner/shared/dedicated lifecycle/cleanup-failure, shared-VPN fail-closed/reconnect ownership, reconnect-cooldown maintenance, transactional proxy startup/shutdown, incremental-header, parser-allocation/timing, verification-parser/read/chunk/request/body-view/owner, CONNECT-setup/HTTP-framing, DNS-query/name-skip/address-list/name-materialization/cname-loop/value-result/a-storage, stress, DNS-cache and data-path self-tests passed.");
        return 0;
    }
}
