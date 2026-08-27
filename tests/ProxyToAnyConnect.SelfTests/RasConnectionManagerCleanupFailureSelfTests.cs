using System.Reflection;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RasConnectionManagerCleanupFailureSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await DisconnectContinuesAfterMonitorFailureAsync();
            await DisconnectDrainsMonitorWhenCancellationCallbackThrowsAsync();
            await DisposeReleasesShutdownOwnerAndRetriesResidualHandleAsync();

            Console.WriteLine(
                "PASS: RAS manager teardown preserves primary failures while retaining only retryable native ownership");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: RAS manager cleanup-failure regression: {ex}");
            return 1;
        }
    }

    private static async Task DisconnectContinuesAfterMonitorFailureAsync()
    {
        var native = new CleanupRasNative(623, RasNative.ErrorSuccess);
        var manager = CreateManager(native);
        var monitorCancellation = new CancellationTokenSource();
        var primary = new SyntheticCleanupException("monitor teardown failed");
        var expectedHandle = (nint)0x7001;

        SetPrivateField(manager, "_monitorCancellation", monitorCancellation);
        SetPrivateField(manager, "_monitorTask", Task.FromException(primary));
        SetPrivateField(manager, "_rasConnection", expectedHandle);

        try
        {
            await manager.DisconnectAsync();
            throw new InvalidOperationException(
                "Synthetic monitor teardown failure was not propagated.");
        }
        catch (SyntheticCleanupException ex) when (ReferenceEquals(ex, primary))
        {
            if (!ex.Data.Contains("RasCleanup:ras-hangup"))
            {
                throw new InvalidOperationException(
                    "Secondary RasHangUp failure was not attached to the primary monitor cleanup exception.");
            }
        }

        if (native.HangUpCount != 1 || native.LastHungUpHandle != expectedHandle)
        {
            throw new InvalidOperationException(
                "RAS hangup was not attempted after monitor teardown failed.");
        }

        if (GetPrivateField<nint>(manager, "_rasConnection") != expectedHandle)
        {
            throw new InvalidOperationException(
                "Failed RasHangUp did not retain the exact handle for a later safe cleanup retry.");
        }

        if (GetPrivateField<CancellationTokenSource?>(manager, "_monitorCancellation") is not null ||
            GetPrivateField<Task?>(manager, "_monitorTask") is not null)
        {
            throw new InvalidOperationException(
                "RAS manager retained monitor ownership after failed disconnect cleanup.");
        }

        if (!CancellationSourceWasDisposed(monitorCancellation))
        {
            throw new InvalidOperationException(
                "Failed monitor teardown retained its CancellationTokenSource.");
        }

        // A later explicit disconnect owns the retained generation and must be able to
        // finish cleanup once RasHangUp succeeds and invalid-handle is observed.
        await manager.DisconnectAsync();
        if (native.HangUpCount != 2 || native.LastHungUpHandle != expectedHandle)
        {
            throw new InvalidOperationException(
                "Second disconnect did not retry the retained exact RAS handle.");
        }

        if (native.GetConnectStatusCount == 0 ||
            GetPrivateField<nint>(manager, "_rasConnection") != 0)
        {
            throw new InvalidOperationException(
                "Successful retry did not drain the retained RAS handle to terminal invalid-handle state.");
        }

        await manager.DisposeAsync();
    }

    private static async Task DisconnectDrainsMonitorWhenCancellationCallbackThrowsAsync()
    {
        var native = new CleanupRasNative(RasNative.ErrorSuccess);
        var manager = CreateManager(native);
        var monitorCancellation = new CancellationTokenSource();
        using var registration = monitorCancellation.Token.Register(
            static () => throw new SyntheticCleanupException("monitor cancellation callback failed"));
        var monitorTask = Task.Delay(
            System.Threading.Timeout.InfiniteTimeSpan,
            monitorCancellation.Token);
        var expectedHandle = (nint)0x7003;

        SetPrivateField(manager, "_monitorCancellation", monitorCancellation);
        SetPrivateField(manager, "_monitorTask", monitorTask);
        SetPrivateField(manager, "_rasConnection", expectedHandle);

        try
        {
            await manager.DisconnectAsync();
            throw new InvalidOperationException(
                "Throwing monitor cancellation callback was not surfaced as a cleanup defect.");
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.Any(inner =>
                inner is SyntheticCleanupException synthetic &&
                synthetic.Message == "monitor cancellation callback failed"))
        {
        }

        if (!monitorTask.IsCompleted || !CancellationSourceWasDisposed(monitorCancellation))
        {
            throw new InvalidOperationException(
                "Throwing monitor cancellation callback prevented exact task drain or CTS disposal.");
        }

        if (GetPrivateField<CancellationTokenSource?>(manager, "_monitorCancellation") is not null ||
            GetPrivateField<Task?>(manager, "_monitorTask") is not null)
        {
            throw new InvalidOperationException(
                "Throwing monitor cancellation callback retained published monitor ownership.");
        }

        if (native.HangUpCount != 1 ||
            native.LastHungUpHandle != expectedHandle ||
            native.GetConnectStatusCount == 0 ||
            GetPrivateField<nint>(manager, "_rasConnection") != 0)
        {
            throw new InvalidOperationException(
                "Throwing monitor cancellation callback prevented exact RAS handle drain.");
        }

        await manager.DisposeAsync();
    }

    private static async Task DisposeReleasesShutdownOwnerAndRetriesResidualHandleAsync()
    {
        var native = new CleanupRasNative(
            623,
            623,
            RasNative.ErrorSuccess);
        var manager = CreateManager(native);
        var shutdown = GetPrivateField<CancellationTokenSource>(manager, "_shutdown");
        var expectedHandle = (nint)0x7002;
        SetPrivateField(manager, "_rasConnection", expectedHandle);

        try
        {
            await manager.DisposeAsync();
            throw new InvalidOperationException(
                "Synthetic RasHangUp failure was not propagated from manager disposal.");
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("RasHangUp failed", StringComparison.OrdinalIgnoreCase))
        {
        }

        if (native.HangUpCount != 2 || native.LastHungUpHandle != expectedHandle)
        {
            throw new InvalidOperationException(
                "Initial RAS manager disposal did not perform its bounded immediate retry of the exact handle.");
        }

        if (GetPrivateField<nint>(manager, "_rasConnection") != expectedHandle)
        {
            throw new InvalidOperationException(
                "Repeated failed RasHangUp calls did not retain the exact residual handle safely.");
        }

        if (!CancellationSourceWasDisposed(shutdown))
        {
            throw new InvalidOperationException(
                "RAS manager retained its shutdown CancellationTokenSource after cleanup failed.");
        }

        // Disposed state blocks new connection ownership, but residual native state is
        // still retryable. A later DisposeAsync must drain that exact handle instead of
        // turning the first cleanup failure into a process-lifetime native leak.
        await manager.DisposeAsync();
        if (native.HangUpCount != 3 || native.LastHungUpHandle != expectedHandle)
        {
            throw new InvalidOperationException(
                "Repeated DisposeAsync did not retry the retained native RAS handle.");
        }

        if (native.GetConnectStatusCount == 0 ||
            GetPrivateField<nint>(manager, "_rasConnection") != 0)
        {
            throw new InvalidOperationException(
                "Residual RAS cleanup retry did not reach terminal invalid-handle state.");
        }

        // Once terminal cleanup succeeds, further DisposeAsync calls are idempotent.
        await manager.DisposeAsync();
        if (native.HangUpCount != 3)
        {
            throw new InvalidOperationException(
                "Successful residual cleanup was repeated by an idempotent DisposeAsync call.");
        }
    }

    private static RasConnectionManager CreateManager(CleanupRasNative native) =>
        new(
            new L2tpOptions
            {
                Id = $"ras-cleanup-{Guid.NewGuid():N}",
                Name = "RAS cleanup self-test"
            },
            metrics: null,
            new RasDialer(native));

    private static T GetPrivateField<T>(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        var value = field.GetValue(owner);
        if (value is null)
        {
            return default!;
        }

        return (T)value;
    }

    private static void SetPrivateField<T>(object owner, string fieldName, T value)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(owner.GetType().FullName, fieldName);
        field.SetValue(owner, value);
    }

    private static bool CancellationSourceWasDisposed(CancellationTokenSource source)
    {
        try
        {
            _ = source.Token;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    private sealed class CleanupRasNative : IRasDialNative
    {
        private readonly Queue<uint> _hangUpResults;

        public CleanupRasNative(params uint[] hangUpResults)
        {
            _hangUpResults = new Queue<uint>(hangUpResults);
        }

        public int HangUpCount { get; private set; }
        public int GetConnectStatusCount { get; private set; }
        public nint LastHungUpHandle { get; private set; }

        public uint Dial(
            string? phoneBook,
            RasNative.RasDialParams dialParams,
            RasDialCallback notifier,
            out nint rasConnection)
        {
            rasConnection = 0;
            throw new InvalidOperationException(
                "Cleanup-failure self-test must not enter RasDial.");
        }

        public uint HangUp(nint rasConnection)
        {
            HangUpCount++;
            LastHungUpHandle = rasConnection;
            return _hangUpResults.Count == 0
                ? RasNative.ErrorSuccess
                : _hangUpResults.Dequeue();
        }

        public uint GetConnectStatus(nint rasConnection)
        {
            GetConnectStatusCount++;
            return RasDialer.ErrorInvalidHandle;
        }
    }

    private sealed class SyntheticCleanupException : Exception
    {
        public SyntheticCleanupException(string message)
            : base(message)
        {
        }
    }
}
