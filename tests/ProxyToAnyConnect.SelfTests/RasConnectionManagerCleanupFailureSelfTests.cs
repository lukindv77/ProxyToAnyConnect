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
            await DisposeReleasesShutdownOwnerWhenHangupFailsAsync();

            Console.WriteLine(
                "PASS: RAS manager teardown preserves primary failures while releasing independent lifetime ownership");
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
        var native = new CleanupRasNative(hangUpResult: 623);
        var manager = CreateManager(native);
        var monitorCancellation = new CancellationTokenSource();
        var primary = new SyntheticCleanupException("monitor teardown failed");

        SetPrivateField(manager, "_monitorCancellation", monitorCancellation);
        SetPrivateField(manager, "_monitorTask", Task.FromException(primary));
        SetPrivateField(manager, "_rasConnection", (nint)0x7001);

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

        if (native.HangUpCount != 1 || native.LastHungUpHandle != (nint)0x7001)
        {
            throw new InvalidOperationException(
                "RAS hangup was not attempted after monitor teardown failed.");
        }

        if (GetPrivateField<nint>(manager, "_rasConnection") != 0 ||
            GetPrivateField<CancellationTokenSource?>(manager, "_monitorCancellation") is not null ||
            GetPrivateField<Task?>(manager, "_monitorTask") is not null)
        {
            throw new InvalidOperationException(
                "RAS manager retained exact-handle or monitor ownership after failed disconnect cleanup.");
        }

        if (!CancellationSourceWasDisposed(monitorCancellation))
        {
            throw new InvalidOperationException(
                "Failed monitor teardown retained its CancellationTokenSource.");
        }

        await manager.DisposeAsync();
    }

    private static async Task DisposeReleasesShutdownOwnerWhenHangupFailsAsync()
    {
        var native = new CleanupRasNative(hangUpResult: 623);
        var manager = CreateManager(native);
        var shutdown = GetPrivateField<CancellationTokenSource>(manager, "_shutdown");
        SetPrivateField(manager, "_rasConnection", (nint)0x7002);

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

        if (native.HangUpCount != 1 || native.LastHungUpHandle != (nint)0x7002)
        {
            throw new InvalidOperationException(
                "RAS manager disposal did not attempt hangup of the exact owned handle.");
        }

        if (GetPrivateField<nint>(manager, "_rasConnection") != 0)
        {
            throw new InvalidOperationException(
                "RAS manager retained its connection handle after failed disposal cleanup.");
        }

        if (!CancellationSourceWasDisposed(shutdown))
        {
            throw new InvalidOperationException(
                "RAS manager retained its shutdown CancellationTokenSource after cleanup failed.");
        }

        // Disposal is an ownership transition even when cleanup reported a defect;
        // subsequent DisposeAsync calls must remain harmless and must not repeat hangup.
        await manager.DisposeAsync();
        if (native.HangUpCount != 1)
        {
            throw new InvalidOperationException(
                "Repeated RAS manager disposal duplicated native hangup ownership.");
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
        private readonly uint _hangUpResult;

        public CleanupRasNative(uint hangUpResult)
        {
            _hangUpResult = hangUpResult;
        }

        public int HangUpCount { get; private set; }
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
            return _hangUpResult;
        }

        public uint GetConnectStatus(nint rasConnection) =>
            throw new InvalidOperationException(
                "Hangup-error path must not poll connection status.");
    }

    private sealed class SyntheticCleanupException : Exception
    {
        public SyntheticCleanupException(string message)
            : base(message)
        {
        }
    }
}
