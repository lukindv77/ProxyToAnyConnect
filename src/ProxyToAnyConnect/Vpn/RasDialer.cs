using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Vpn;

internal delegate void RasDialCallback(
    nint rasConnection,
    uint message,
    int connectionState,
    uint error,
    uint extendedError);

internal interface IRasDialNative
{
    uint Dial(
        string? phoneBook,
        RasNative.RasDialParams dialParams,
        RasDialCallback notifier,
        out nint rasConnection);

    uint HangUp(nint rasConnection);

    uint GetConnectStatus(nint rasConnection);
}

internal sealed class WindowsRasDialNative : IRasDialNative
{
    private const uint RasDialFunc1Notifier = 1;
    private const uint ErrorInvalidHandle = 6;
    private const int RasMaxDeviceType = 16;
    private const int RasMaxDeviceName = 128;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeRasDialFunc1(
        nint rasConnection,
        uint message,
        int connectionState,
        uint error,
        uint extendedError);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private sealed class RasConnStatus
    {
        public uint DwSize;
        public int RasConnState;
        public uint DwError;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceType + 1)]
        public string SzDeviceType = string.Empty;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceName + 1)]
        public string SzDeviceName = string.Empty;
    }

    public uint Dial(
        string? phoneBook,
        RasNative.RasDialParams dialParams,
        RasDialCallback notifier,
        out nint rasConnection)
    {
        NativeRasDialFunc1 nativeNotifier =
            (handle, message, state, error, extendedError) =>
                notifier(handle, message, state, error, extendedError);

        var result = RasDialWithCallbackW(
            0,
            phoneBook,
            dialParams,
            RasDialFunc1Notifier,
            nativeNotifier,
            out rasConnection);

        // Rasapi32 retains the unmanaged callback pointer until the async dial reaches
        // a terminal state or is hung up. Root the exact thunk by HRASCONN; the dialer
        // removes it only after Connected or after teardown reaches invalid-handle.
        RasDialCallbackRoots.Add(rasConnection, nativeNotifier);
        return result;
    }

    public uint HangUp(nint rasConnection) => RasNative.RasHangUpW(rasConnection);

    public uint GetConnectStatus(nint rasConnection)
    {
        var status = new RasConnStatus
        {
            DwSize = checked((uint)Marshal.SizeOf<RasConnStatus>())
        };
        var result = RasGetConnectStatusW(rasConnection, status);
        if (result == ErrorInvalidHandle)
        {
            RasDialCallbackRoots.Remove(rasConnection);
        }
        return result;
    }

    internal static void ReleaseCallbackRoot(nint rasConnection) =>
        RasDialCallbackRoots.Remove(rasConnection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "RasDialW")]
    private static extern uint RasDialWithCallbackW(
        nint rasDialExtensions,
        string? phoneBook,
        [In] RasNative.RasDialParams dialParams,
        uint notifierType,
        NativeRasDialFunc1 notifier,
        out nint rasConnection);

    [DllImport("rasapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RasGetConnectStatusW(nint rasConnection, [In, Out] RasConnStatus status);

    private static class RasDialCallbackRoots
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<nint, NativeRasDialFunc1> Roots = new();

        public static void Add(nint handle, NativeRasDialFunc1 notifier)
        {
            if (handle == 0)
            {
                return;
            }

            lock (Gate)
            {
                Roots[handle] = notifier;
            }
        }

        public static void Remove(nint handle)
        {
            if (handle == 0)
            {
                return;
            }

            lock (Gate)
            {
                Roots.Remove(handle);
            }
        }
    }
}

internal sealed class RasDialer
{
    internal const uint ErrorInvalidHandle = 6;
    internal const int RasCsConnected = 0x2000;
    internal const int RasCsDisconnected = 0x2001;

    private static readonly TimeSpan HangUpPollInterval = TimeSpan.FromMilliseconds(10);
    private readonly IRasDialNative _native;

    public RasDialer()
        : this(new WindowsRasDialNative())
    {
    }

    internal RasDialer(IRasDialNative native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public async Task<nint> DialAsync(
        string? phoneBook,
        RasNative.RasDialParams dialParams,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dialParams);
        cancellationToken.ThrowIfCancellationRequested();

        var terminal = new TaskCompletionSource<RasDialTerminalState>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RasDialCallback notifier = (_, _, state, error, extendedError) =>
        {
            if (error != RasNative.ErrorSuccess ||
                state is RasCsConnected or RasCsDisconnected)
            {
                terminal.TrySetResult(new RasDialTerminalState(state, error, extendedError));
            }
        };

        nint handle = 0;
        try
        {
            uint initialResult;
            try
            {
                initialResult = _native.Dial(phoneBook, dialParams, notifier, out handle);
            }
            finally
            {
                // RASDIALPARAMS is input to the native RasDial call. Once that
                // handoff returns, asynchronous progress is owned by HRASCONN plus
                // the rooted callback. Do not retain the DPAPI-unprotected password
                // in our managed dial-parameter object throughout that callback wait.
                dialParams.SzPassword = string.Empty;
            }

            if (initialResult != RasNative.ErrorSuccess)
            {
                if (handle != 0)
                {
                    await HangUpAndDrainAsync(handle).ConfigureAwait(false);
                }

                throw new InvalidOperationException(
                    $"RasDial failed before asynchronous connection setup: " +
                    RasNative.DescribeError(initialResult));
            }

            if (handle == 0)
            {
                throw new InvalidOperationException(
                    "Asynchronous RasDial returned success without a connection handle.");
            }

            RasDialTerminalState completed;
            try
            {
                completed = await terminal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await HangUpAndDrainAsync(handle).ConfigureAwait(false);
                throw;
            }

            if (completed.Error != RasNative.ErrorSuccess)
            {
                await HangUpAndDrainAsync(handle).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"RAS connection failed while dialing: {RasNative.DescribeError(completed.Error)}" +
                    (completed.ExtendedError == 0
                        ? string.Empty
                        : $" (extended error {completed.ExtendedError})"));
            }

            if (completed.ConnectionState != RasCsConnected)
            {
                await HangUpAndDrainAsync(handle).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"RAS connection ended before reaching Connected (state {completed.ConnectionState}).");
            }

            WindowsRasDialNative.ReleaseCallbackRoot(handle);
            return handle;
        }
        finally
        {
            // Also covers implementations of IRasDialNative that throw before the
            // inner handoff-finally executes in future refactors.
            dialParams.SzPassword = string.Empty;
            GC.KeepAlive(notifier);
        }
    }

    public async Task HangUpAndDrainAsync(nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        var hangUpResult = _native.HangUp(handle);
        if (hangUpResult is not (RasNative.ErrorSuccess or ErrorInvalidHandle))
        {
            throw new InvalidOperationException(
                $"RasHangUp failed: {RasNative.DescribeError(hangUpResult)}");
        }

        if (hangUpResult == ErrorInvalidHandle)
        {
            WindowsRasDialNative.ReleaseCallbackRoot(handle);
            return;
        }

        while (true)
        {
            var result = _native.GetConnectStatus(handle);
            if (result == ErrorInvalidHandle)
            {
                WindowsRasDialNative.ReleaseCallbackRoot(handle);
                return;
            }

            if (result != RasNative.ErrorSuccess)
            {
                WindowsRasDialNative.ReleaseCallbackRoot(handle);
                throw new InvalidOperationException(
                    $"Unable to drain RAS connection state after hangup: " +
                    RasNative.DescribeError(result));
            }

            await Task.Delay(HangUpPollInterval).ConfigureAwait(false);
        }
    }

    private readonly record struct RasDialTerminalState(
        int ConnectionState,
        uint Error,
        uint ExtendedError);
}
