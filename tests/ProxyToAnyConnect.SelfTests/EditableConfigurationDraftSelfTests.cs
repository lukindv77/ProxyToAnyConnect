using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.SelfTests;

internal static class EditableConfigurationDraftSelfTests
{
    public static int Run()
    {
        try
        {
            MultipleIndependentDefectsCanBeRepairedSequentially();
            ValidDraftPublicationMarkerRejectsInvalidState();
            Console.WriteLine(
                "PASS: editable configuration draft preserves sequential repairs while publication remains validation-gated");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: editable configuration draft regression: {ex}");
            return 1;
        }
    }

    private static void MultipleIndependentDefectsCanBeRepairedSequentially()
    {
        var initial = CreateOptions(proxyPort: 0, maxResponseBytes: 1);
        var draft = new EditableConfigurationDraft(initial);
        if (draft.IsValid || draft.ValidationError is null || draft.HasUnpersistedChanges)
        {
            throw new InvalidOperationException("Two-defect initial configuration unexpectedly validated.");
        }

        var proxyRepair = Clone(
            draft.Current,
            proxyPort: 18080,
            maxResponseBytes: draft.Current.VpnConnections[0].Verification.MaxResponseBytes);
        if (draft.Stage(proxyRepair))
        {
            throw new InvalidOperationException(
                "Repairing only the proxy defect unexpectedly published a fully valid draft.");
        }
        if (draft.Current.Proxies[0].ListenPort != 18080 ||
            !draft.HasUnpersistedChanges ||
            draft.ValidationError is null ||
            !draft.ValidationError.Contains("verification.maxResponseBytes", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "First repair was discarded instead of becoming the base for the next repair.");
        }

        var secondRepair = Clone(
            draft.Current,
            proxyPort: draft.Current.Proxies[0].ListenPort,
            maxResponseBytes: VerificationOptions.DefaultResponseLimitBytes);
        if (!draft.Stage(secondRepair) || !draft.IsValid || draft.ValidationError is not null ||
            !draft.HasUnpersistedChanges)
        {
            throw new InvalidOperationException(
                $"Second repair did not validate the accumulated draft: {draft.ValidationError}");
        }
        if (draft.Current.Proxies[0].ListenPort != 18080)
        {
            throw new InvalidOperationException("Second repair lost the first staged proxy repair.");
        }
    }

    private static void ValidDraftPublicationMarkerRejectsInvalidState()
    {
        var draft = new EditableConfigurationDraft(CreateOptions(18080, VerificationOptions.DefaultResponseLimitBytes));
        draft.MarkPersisted(draft.Current);
        if (draft.HasUnpersistedChanges)
        {
            throw new InvalidOperationException("Persisted draft remained marked dirty.");
        }

        var invalid = Clone(draft.Current, proxyPort: 0, maxResponseBytes: VerificationOptions.DefaultResponseLimitBytes);
        if (draft.Stage(invalid))
        {
            throw new InvalidOperationException("Invalid candidate unexpectedly validated.");
        }

        var currentBeforeRejectedPublication = draft.Current;
        try
        {
            draft.MarkPersisted(invalid);
            throw new InvalidOperationException("Invalid draft was marked as persisted.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("Persisted configuration unexpectedly failed validation", StringComparison.Ordinal))
        {
        }

        if (!ReferenceEquals(draft.Current, currentBeforeRejectedPublication) ||
            !draft.HasUnpersistedChanges)
        {
            throw new InvalidOperationException(
                "Rejected persisted marker mutated or falsely cleaned the active repair draft.");
        }
    }

    private static AppOptions CreateOptions(int proxyPort, int maxResponseBytes) =>
        Clone(new AppOptions(), proxyPort, maxResponseBytes);

    private static AppOptions Clone(AppOptions source, int proxyPort, int maxResponseBytes)
    {
        var sourceProxy = source.Proxies[0];
        var sourceVpn = source.VpnConnections[0];
        return new AppOptions
        {
            Proxies =
            [
                new ProxyOptions
                {
                    Id = sourceProxy.Id,
                    Name = "Repair proxy",
                    Enabled = false,
                    ListenAddress = "127.0.0.1",
                    ListenPort = proxyPort,
                    VpnConnectionId = sourceVpn.Id,
                    MaxConcurrentConnections = 8,
                    MaxHeaderBytes = 8192,
                    ClientHeaderTimeoutSeconds = 5,
                    OutboundConnectTimeoutSeconds = 5,
                    DnsTimeoutMilliseconds = 1000
                }
            ],
            VpnConnections =
            [
                new L2tpOptions
                {
                    Id = sourceVpn.Id,
                    Name = "Repair VPN",
                    Shared = false,
                    Mode = L2tpConnectionMode.ExistingWindowsProfile,
                    EntryName = "SelfTest-repair",
                    Verification = new VerificationOptions
                    {
                        PublicAddress = "vpn.example.com",
                        ProbeHost = "api.ipify.org",
                        ProbePort = 443,
                        ProbePath = "/",
                        TimeoutSeconds = 5,
                        MaxResponseBytes = maxResponseBytes
                    },
                    Keepalive = new KeepaliveOptions
                    {
                        Mode = L2tpKeepaliveMode.Off,
                        IntervalSeconds = 10,
                        TimeoutMilliseconds = 1000,
                        FailureThreshold = 3
                    }
                }
            ],
            Logging = source.Logging
        };
    }
}
