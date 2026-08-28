using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class RasDialerSelfTests
{
    private const string SyntheticPassword = "self-test-sensitive-password";

    public static async Task<int> RunAsync()
    {
        try
        {
            await NativeHandoffDoesNotRetainManagedPasswordAsync();
            await NativeThrowStillClearsManagedPasswordAsync();
            await PreCanceledDialClearsManagedPasswordAsync();
            await PreDialScopeFailureClearsManagedPasswordAsync();
            await ConnectedNotificationReturnsExactHandleAsync();
            await CallerCancellationHangsUpAndDrainsAsync();
            await CallerCancellationSurvivesHangupFailureAsync();
            await CallerCancellationSurvivesDrainTimeoutAsync();
            await HangUpDrainAttemptIsBoundedAsync();
            await InitialFailureWithHandleStillDrainsAsync();
            await TerminalDialFailureHangsUpAndDrainsAsync();
            await DisconnectedBeforeConnectedHangsUpAndDrainsAsync();
            await RepeatedCancellationDoesNotDuplicateOwnershipAsync();

            Console.WriteLine(
                "PASS: asynchronous RAS dial ownership clears managed passwords, preserves cancellation and bounds exact-handle teardown");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: asynchronous RAS dial lifecycle regression: {ex}");
            return 1;
        }
    }

    private static async Task NativeHandoffDoesNotRetainManagedPasswordAsync()
    {
        var native = new FakeRasDialNative();
        var dialer = new RasDialer(native);
        var dialParams = CreateDialParams(SyntheticPassword);

        // DialAsync runs synchronously through the native handoff before reaching
        // its first incomplete await. Therefore the returned Task can still be
        // pending while the managed RASDIALPARAMS password has already been cleared.
        var dialTask = dialer.DialAsync(null, dialParams, CancellationToken.None);

        if (!string.Equals(native.PasswordObservedDuringDial, SyntheticPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Native RasDial handoff did not receive the supplied plaintext password.");
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "Managed RasDialParams retained plaintext password after the native handoff returned.");
        }

        if (dialTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Synthetic asynchronous dial unexpectedly completed before its terminal notification.");
        }

        // Non-secret dial identity remains available for diagnostics/ownership.
        if (dialParams.SzEntryName != "SelfTest" ||
            dialParams.SzUserName != "self-test-user" ||
            dialParams.SzDomain != "SELFTEST")
        {
            throw new InvalidOperationException(
                "Password cleanup unexpectedly cleared non-secret RAS dial identity fields.");
        }

        native.Notify(RasDialer.RasCsConnected, RasNative.ErrorSuccess);
        _ = await dialTask;
    }

    private static async Task NativeThrowStillClearsManagedPasswordAsync()
    {
        var native = new ThrowingRasDialNative();
        var dialer = new RasDialer(native);
        var dialParams = CreateDialParams(SyntheticPassword);

        try
        {
            _ = await dialer.DialAsync(null, dialParams, CancellationToken.None);
            throw new InvalidOperationException("Throwing native adapter unexpectedly completed RasDial.");
        }
        catch (SyntheticNativeDialException)
        {
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "Managed RasDialParams retained plaintext password after native Dial threw.");
        }
    }

    private static async Task PreCanceledDialClearsManagedPasswordAsync()
    {
        var native = new FakeRasDialNative();
        var dialer = new RasDialer(native);
        var dialParams = CreateDialParams(SyntheticPassword);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            _ = await dialer.DialAsync(null, dialParams, cancellation.Token);
            throw new InvalidOperationException(
                "Already-cancelled RasDial unexpectedly reached native handoff.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "Already-cancelled RasDial retained the managed plaintext password carrier.");
        }

        if (native.PasswordObservedDuringDial is not null)
        {
            throw new InvalidOperationException(
                "Already-cancelled RasDial invoked the native adapter before cancellation propagation.");
        }
    }

    private static async Task PreDialScopeFailureClearsManagedPasswordAsync()
    {
        var dialParams = CreateDialParams(SyntheticPassword);
        try
        {
            _ = await RasConnectionManager.ExecuteDialPasswordScopeAsync(
                dialParams,
                () => throw new SyntheticNativeDialException());
            throw new InvalidOperationException(
                "Synthetic pre-dial failure unexpectedly completed the password scope.");
        }
        catch (SyntheticNativeDialException)
        {
        }

        if (dialParams.SzPassword.Length != 0)
        {
            throw new InvalidOperationException(
                "RasConnectionManager pre-dial failure retained the managed plaintext password carrier.");
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

    private static async Task CallerCancellationSurvivesHangupFailureAsync()
    {
        const uint hangUpFailure = 632;
        var native = new FakeRasDialNative(hangUpResult: hangUpFailure);
        var dialer = new RasDialer(native);
        using var cancellation = new CancellationTokenSource();
        var dialTask = dialer.DialAsync(null, CreateDialParams(), cancellation.Token);

        await native.WaitUntilDialStartedAsync();
        cancellation.Cancel();

        try
        {
            _ = await dialTask;
        }
        catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested)
        {
            if (native.HangUpCount != 1 || native.StatusCount != 0)
            {
                throw new InvalidOperationException(
                    "Cancellation teardown did not stop at the synthetic RasHangUp failure.");
            }

            if (ex.Data["RasTeardownError"] is not string teardownError ||
                !teardownError.Contains("RasHangUp failed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cancellation preserved its type but lost the secondary RAS teardown diagnostic.");
            }

            return;
        }

        throw new InvalidOperationException(
            "A RasHangUp cleanup failure replaced or swallowed caller cancellation.");
    }

    private static async Task CallerCancellationSurvivesDrainTimeoutAsync()
    {
        var native = new FakeRasDialNative(statusSuccessesBeforeInvalid: int.MaxValue);
        var dialer = new RasDialer(native, TimeSpan.FromMilliseconds(40));
        using var cancellation = new CancellationTokenSource();
        var dialTask = dialer.DialAsync(null, CreateDialParams(), cancellation.Token);

        await native.WaitUntilDialStartedAsync();
        cancellation.Cancel();

        try
        {
            _ = await dialTask;
        }
        catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested)
        {
            if (native.HangUpCount != 1 || native.StatusCount < 1 || native.InvalidHandleObserved)
            {
                throw new InvalidOperationException(
                    "Timed-out cancellation teardown did not preserve exact non-terminal RAS ownership.");
            }

            if (ex.Data["RasTeardownError"] is not string teardownError ||
                !teardownError.Contains("Timed out", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Caller cancellation survived drain timeout but lost its secondary timeout diagnostic.");
            }

            return;
        }

        throw new InvalidOperationException(
            "A bounded RAS drain timeout replaced or swallowed caller cancellation.");
    }

    private static async Task HangUpDrainAttemptIsBoundedAsync()
    {
        var native = new FakeRasDialNative(statusSuccessesBeforeInvalid: int.MaxValue);
        var dialer = new RasDialer(native, TimeSpan.FromMilliseconds(40));
        var startedAt = Environment.TickCount64;

        try
        {
            await dialer.HangUpAndDrainAsync(native.Handle);
            throw new InvalidOperationException(
                "Non-terminal synthetic RAS state unexpectedly drained without invalid-handle proof.");
        }
        catch (TimeoutException ex) when (
            ex.Message.Contains("ERROR_INVALID_HANDLE", StringComparison.Ordinal))
        {
        }

        var elapsed = Environment.TickCount64 - startedAt;
        if (elapsed > 1500 ||
            native.HangUpCount != 1 ||
            native.StatusCount < 1 ||
            native.InvalidHandleObserved)
        {
            throw new InvalidOperationException(
                $"Bounded RAS drain had unexpected ownership/timing: elapsed={elapsed}ms, " +
                $"hangups={native.HangUpCount}, status={native.StatusCount}, invalid={native.InvalidHandleObserved}.");
        }
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

    private static RasNative.RasDialParams CreateDialParams(string password = "") =>
        new()
        {
            DwSize = 1,
            SzEntryName = "SelfTest",
            SzUserName = "self-test-user",
            SzPassword = password,
            SzDomain = "SELFTEST"
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
        private readonly uint _hangUpResult;
        private RasDialCallback? _notifier;
        private int _statusCount;

        public FakeRasDialNative(
            uint initialResult = RasNative.ErrorSuccess,
            int statusSuccessesBeforeInvalid = 0,
            nint? handle = null,
            uint hangUpResult = RasNative.ErrorSuccess)
        {
            _initialResult = initialResult;
            _statusSuccessesBeforeInvalid = statusSuccessesBeforeInvalid;
            _hangUpResult = hangUpResult;
            Handle = handle ?? (nint)0x12345;
        }

        public nint Handle { get; }
        public int HangUpCount { get; private set; }
        public nint LastHungUpHandle { get; private set; }
        public int StatusCount => Volatile.Read(ref _statusCount);
        public bool InvalidHandleObserved { get; private set; }
        public string? PasswordObservedDuringDial { get; private set; }

        public uint Dial(
            string? phoneBook,
            RasNative.RasDialParams dialParams,
            RasDialCallback notifier,
            out nint rasConnection)
        {
            PasswordObservedDuringDial = dialParams.SzPassword;
            _notifier = notifier;
            rasConnection = Handle;
            _dialStarted.TrySetResult();
            return _initialResult;
        }

        public uint HangUp(nint rasConnection)
        {
            HangUpCount++;
            LastHungUpHandle = rasConnection;
            return _hangUpResult;
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

    private sealed class ThrowingRasDialNative : IRasDialNative
    {
        public uint Dial(
            string? phoneBook,
            RasNative.RasDialParams dialParams,
            RasDialCallback notifier,
            out nint rasConnection)
        {
            rasConnection = 0;
            throw new SyntheticNativeDialException();
        }

        public uint HangUp(nint rasConnection) =>
            throw new InvalidOperationException("Throwing native adapter must not enter hangup.");

        public uint GetConnectStatus(nint rasConnection) =>
            throw new InvalidOperationException("Throwing native adapter must not enter status polling.");
    }

    private sealed class SyntheticNativeDialException : Exception
    {
    }
}
