using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.SelfTests;

internal static class EditableConfigurationWorkflowSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await SequentialRepairsStageUntilWholeDraftIsValidAsync();
            await SaveFailurePreservesLatestDraftWithoutRuntimeApplyAsync();
            await RuntimeFailureKeepsDurablyAdoptedGenerationAsync();
            await PreCancellationDoesNotStageDraftAsync();

            Console.WriteLine(
                "PASS: invalid loaded configuration can be repaired sequentially without premature persistence/runtime apply or lost draft edits");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: editable configuration draft workflow regression: {ex}");
            return 1;
        }
    }

    private static async Task SequentialRepairsStageUntilWholeDraftIsValidAsync()
    {
        var original = CreateOptions(
            proxyDnsTimeoutMilliseconds: 100,
            vpnMonitorIntervalMilliseconds: 100,
            proxyName: "Original invalid");
        var staged = original;
        AppOptions? persisted = null;
        var saveCount = 0;
        var applyCount = 0;

        var firstRepair = CreateOptions(
            proxyDnsTimeoutMilliseconds: 1000,
            vpnMonitorIntervalMilliseconds: 100,
            proxyName: "First repair retained");
        var first = await EditableConfigurationWorkflow.StageValidateSaveApplyAsync(
            firstRepair,
            draft => staged = draft,
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            },
            desired => persisted = desired,
            (_, _) =>
            {
                applyCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        if (first.IsGloballyValid ||
            string.IsNullOrWhiteSpace(first.ValidationError) ||
            !first.ValidationError.Contains("monitorIntervalMilliseconds", StringComparison.Ordinal) ||
            !ReferenceEquals(staged, firstRepair) ||
            persisted is not null ||
            saveCount != 0 ||
            applyCount != 0)
        {
            throw new InvalidOperationException(
                "First partial repair was not retained exclusively as an in-memory draft while another defect remained.");
        }

        // Build the second editor result from the current staged generation. The
        // proxy correction/name from the first operation must survive while the VPN
        // defect is repaired independently.
        var secondRepair = new AppOptions
        {
            Proxies = staged.Proxies,
            VpnConnections =
            [
                CloneVpn(staged.VpnConnections.Single(), monitorIntervalMilliseconds: 1000)
            ],
            Logging = staged.Logging
        };
        var second = await EditableConfigurationWorkflow.StageValidateSaveApplyAsync(
            secondRepair,
            draft => staged = draft,
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            },
            desired => persisted = desired,
            (_, _) =>
            {
                applyCount++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        if (!second.IsGloballyValid ||
            second.ValidationError is not null ||
            !ReferenceEquals(staged, secondRepair) ||
            !ReferenceEquals(persisted, secondRepair) ||
            saveCount != 1 ||
            applyCount != 1 ||
            staged.Proxies.Single().Name != "First repair retained" ||
            staged.Proxies.Single().DnsTimeoutMilliseconds != 1000 ||
            staged.VpnConnections.Single().MonitorIntervalMilliseconds != 1000)
        {
            throw new InvalidOperationException(
                "Second repair did not publish the complete accumulated draft exactly once after global validity was restored.");
        }
    }

    private static async Task SaveFailurePreservesLatestDraftWithoutRuntimeApplyAsync()
    {
        var baseline = CreateOptions(1000, 1000, "Baseline");
        var desired = CreateOptions(1500, 1250, "Unsaved repair");
        var staged = baseline;
        AppOptions? persisted = baseline;
        var applyCount = 0;

        try
        {
            await EditableConfigurationWorkflow.StageValidateSaveApplyAsync(
                desired,
                draft => staged = draft,
                (_, _) => throw new IOException("synthetic durable save failure"),
                committed => persisted = committed,
                (_, _) =>
                {
                    applyCount++;
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic save failure unexpectedly completed.");
        }
        catch (IOException ex) when (ex.Message.Contains("synthetic durable save failure", StringComparison.Ordinal))
        {
        }

        if (!ReferenceEquals(staged, desired) ||
            !ReferenceEquals(persisted, baseline) ||
            applyCount != 0)
        {
            throw new InvalidOperationException(
                "Durable save failure discarded the latest GUI draft, adopted it as persisted state, or touched runtime.");
        }
    }

    private static async Task RuntimeFailureKeepsDurablyAdoptedGenerationAsync()
    {
        var baseline = CreateOptions(1000, 1000, "Baseline");
        var desired = CreateOptions(2000, 1500, "Persisted before runtime failure");
        var staged = baseline;
        AppOptions persisted = baseline;
        var saveCount = 0;

        try
        {
            await EditableConfigurationWorkflow.StageValidateSaveApplyAsync(
                desired,
                draft => staged = draft,
                (_, _) =>
                {
                    saveCount++;
                    return Task.CompletedTask;
                },
                committed => persisted = committed,
                (_, _) => throw new SyntheticRuntimeFailureException(),
                CancellationToken.None);
            throw new InvalidOperationException("Synthetic runtime failure unexpectedly completed.");
        }
        catch (SyntheticRuntimeFailureException)
        {
        }

        if (saveCount != 1 ||
            !ReferenceEquals(staged, desired) ||
            !ReferenceEquals(persisted, desired))
        {
            throw new InvalidOperationException(
                "Runtime convergence failure rolled back or failed to adopt the already durable desired generation.");
        }
    }

    private static async Task PreCancellationDoesNotStageDraftAsync()
    {
        var baseline = CreateOptions(1000, 1000, "Baseline");
        var desired = CreateOptions(2000, 2000, "Should not stage");
        var staged = baseline;
        var saveCount = 0;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            await EditableConfigurationWorkflow.StageValidateSaveApplyAsync(
                desired,
                draft => staged = draft,
                (_, _) =>
                {
                    saveCount++;
                    return Task.CompletedTask;
                },
                _ => { },
                (_, _) => Task.CompletedTask,
                cancellation.Token);
            throw new InvalidOperationException("Pre-cancelled draft operation unexpectedly completed.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (!ReferenceEquals(staged, baseline) || saveCount != 0)
        {
            throw new InvalidOperationException(
                "A command cancelled before its serialized turn still mutated GUI draft ownership.");
        }
    }

    private static AppOptions CreateOptions(
        int proxyDnsTimeoutMilliseconds,
        int vpnMonitorIntervalMilliseconds,
        string proxyName) =>
        new()
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = "proxy-draft",
                    Name = proxyName,
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = 18080,
                    VpnConnectionId = "vpn-draft",
                    DnsTimeoutMilliseconds = proxyDnsTimeoutMilliseconds
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = "vpn-draft",
                    Name = "Draft VPN",
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-Draft-L2TP",
                    MonitorIntervalMilliseconds = vpnMonitorIntervalMilliseconds,
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com"
                    }
                }
            ]
        };

    private static L2tpOptions CloneVpn(L2tpOptions source, int monitorIntervalMilliseconds) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Shared = source.Shared,
            Mode = source.Mode,
            EntryName = source.EntryName,
            MonitorIntervalMilliseconds = monitorIntervalMilliseconds,
            RouteMonitorIntervalMilliseconds = source.RouteMonitorIntervalMilliseconds,
            ReconnectCooldownMilliseconds = source.ReconnectCooldownMilliseconds,
            Verification = source.Verification,
            Keepalive = source.Keepalive,
            Custom = source.Custom
        };

    private sealed class SyntheticRuntimeFailureException : Exception
    {
    }
}
