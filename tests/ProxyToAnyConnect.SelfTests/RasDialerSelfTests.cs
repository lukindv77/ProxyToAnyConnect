using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RasDialerSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await ConnectedNotificationReturnsExactHandleAsync();
            await CallerCancellationHangsUpAndDrainsAsync();
            await InitialFailureWithHandleStillDrainsAsync();
            await TerminalDialFailureHangsUpAndDrainsAsync();
            await DisconnectedBeforeConnectedHangsUpAndDrainsAsync();
            await RepeatedCancellationDoesNotDuplicateOwnershipAsync();

            Console.WriteLine(
                "PASS: asynchronous RAS dial ownership cancels and drains exact connection handles");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: asynchronous RAS dial lifecycle regression: {ex}");
            return 1;
        }
    }

    private static async Task ConnectedNotificationReturnsExactHandleAsync()
    {
        var native = new FakeRasDialNative();
        var dialer = new RasDialer(native);
        var dialTask = dialer.DialAsync(null, CreateDialParams(), CancellationToken.None);

        await native.WaitUntilDialStartedAsync();
        native.Notify(RasDialer.RasCsConnected, RasNative.ErrorSuccess);

        var handle = await dialTask;
        if (handle != native.Handle)
        {
            throw new InvalidOperationException(
                $"Dial returned handle {handle}, expected {native.Handle}.");
        }

        if (native.HangUpCount != 0 || native.StatusCount != 0)
        {
            throw new InvalidOperationException(
                "Successful asynchronous dial unexpectedly entered teardown.");
        }
    }

    private static async Task CallerCancellationHangsUpAndDrainsAsync()
    {
        var native = new FakeRasDialNative(statusSuccessesBeforeInvalid: 2);
        var dialer = new RasDialer(native);
        using var cancellation = new CancellationTokenSource();
        var dialTask = dialer.DialAsync(null, CreateDialParams(), cancellation.Token);

        await native.WaitUntilDialStartedAsync();
        cancellation.Cancel();

        try
        {
            _ = await dialTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            AssertSingleDrainedHandle(native, "caller cancellation");
            return;
        }

        throw new InvalidOperationException("Caller cancellation was not propagated by RasDialer.");
    }

    private static async Task InitialFailureWithHandleStillDrainsAsync()
    {
        const uint initialError = 623;
        var native = new FakeRasDialNative(
            initialResult: initialError,
            statusSuccessesBeforeInvalid: 1);
        var dialer = new RasDialer(native);

        try
        {
            _ = await dialer.DialAsync(null, CreateDialParams(), CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            AssertSingleDrainedHandle(native, "initial RasDial failure");
            return;
        }

        throw new InvalidOperationException(
            "Initial RasDial failure unexpectedly produced a connected handle.");
    }

    private static async Task TerminalDialFailureHangsUpAndDrainsAsync()
    {
        const uint terminalError = 691;
        var native = new FakeRasDialNative(statusSuccessesBeforeInvalid: 1);
        var dialer = new RasDialer(native);
        var dialTask = dialer.DialAsync(null, CreateDialParams(), CancellationToken.None);

        await native.WaitUntilDialStartedAsync();
        native.Notify(connectionState: 6, error: terminalError, extendedError: 42);

        try
        {
            _ = await dialTask;
        }
        catch (InvalidOperationException)
        {
            AssertSingleDrainedHandle(native, "terminal RAS failure");
            return;
        }

        throw new InvalidOperationException(
            "Terminal RAS failure unexpectedly produced a connected handle.");
    }

    private static async Task DisconnectedBeforeConnectedHangsUpAndDrainsAsync()
    {
        var native = new FakeRasDialNative(statusSuccessesBeforeInvalid: 1);
        var dialer = new RasDialer(native);
        var dialTask = dialer.DialAsync(null, CreateDialParams(), CancellationToken.None);

        await native.WaitUntilDialStartedAsync();
        native.Notify(RasDialer.RasCsDisconnected, RasNative.ErrorSuccess);

        try
        {
            _ = await dialTask;
        }
        catch (InvalidOperationException)
        {
            AssertSingleDrainedHandle(native, "disconnected before Connected");
            return;
        }

        throw new InvalidOperationException(
            "Disconnected RAS state unexpectedly produced a connected handle.");
    }

    private static async Task RepeatedCancellationDoesNotDuplicateOwnershipAsync()
    {
        const int cycles = 64;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var native = new FakeRasDialNative(
                handle: (nint)(0x20000 + cycle),
                statusSuccessesBeforeInvalid: cycle % 3);
            var dialer = new RasDialer(native);
            using var cancellation = new CancellationTokenSource();
            var dialTask = dialer.DialAsync(null, CreateDialParams(), cancellation.Token);

            await native.WaitUntilDialStartedAsync();
            cancellation.Cancel();

            try
            {
                _ = await dialTask;
                throw new InvalidOperationException(
                    $"Cycle {cycle}: cancellation unexpectedly returned a connected handle.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                AssertSingleDrainedHandle(native, $"cancellation cycle {cycle}");
            }
        }
    }

    private static RasNative.RasDialParams CreateDialParams() =>
        new()
        {
            DwSize = 1,
            SzEntryName = "SelfTest"
        };

    private static void AssertSingleDrainedHandle(FakeRasDialNative native, string phase)
    {
        if (native.HangUpCount != 1 || native.LastHungUpHandle != native.Handle)
        {
            throw new InvalidOperationException(
                $"{phase}: expected one hangup of exact handle {native.Handle}; " +
                $"count={native.HangUpCount}, handle={native.LastHungUpHandle}.");
        }

        if (native.StatusCount < 1 || !native.InvalidHandleObserved)
        {
            throw new InvalidOperationException(
                $"{phase}: teardown returned before RasGetConnectStatus observed ERROR_INVALID_HANDLE.");
        }
    }

    private sealed class FakeRasDialNative : IRasDialNative
    {
        private readonly TaskCompletionSource _dialStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly uint _initialResult;
        private readonly int _statusSuccessesBeforeInvalid;
        private RasDialCallback? _notifier;
        private int _statusCount;

        public FakeRasDialNative(
            uint initialResult = RasNative.ErrorSuccess,
            int statusSuccessesBeforeInvalid = 0,
            nint? handle = null)
        {
            _initialResult = initialResult;
            _statusSuccessesBeforeInvalid = statusSuccessesBeforeInvalid;
            Handle = handle ?? (nint)0x12345;
        }

        public nint Handle { get; }
        public int HangUpCount { get; private set; }
        public nint LastHungUpHandle { get; private set; }
        public int StatusCount => Volatile.Read(ref _statusCount);
        public bool InvalidHandleObserved { get; private set; }

        public uint Dial(
            string? phoneBook,
            RasNative.RasDialParams dialParams,
            RasDialCallback notifier,
            out nint rasConnection)
        {
            _notifier = notifier;
            rasConnection = Handle;
            _dialStarted.TrySetResult();
            return _initialResult;
        }

        public uint HangUp(nint rasConnection)
        {
            HangUpCount++;
            LastHungUpHandle = rasConnection;
            return RasNative.ErrorSuccess;
        }

        public uint GetConnectStatus(nint rasConnection)
        {
            if (rasConnection != Handle)
            {
                throw new InvalidOperationException(
                    $"Status queried unexpected handle {rasConnection}; expected {Handle}.");
            }

            var call = Interlocked.Increment(ref _statusCount);
            if (call <= _statusSuccessesBeforeInvalid)
            {
                return RasNative.ErrorSuccess;
            }

            InvalidHandleObserved = true;
            return RasDialer.ErrorInvalidHandle;
        }

        public Task WaitUntilDialStartedAsync() => _dialStarted.Task;

        public void Notify(int connectionState, uint error, uint extendedError = 0)
        {
            var notifier = _notifier
                ?? throw new InvalidOperationException("RasDial notifier has not been registered.");
            notifier(Handle, 0, connectionState, error, extendedError);
        }
    }
}
