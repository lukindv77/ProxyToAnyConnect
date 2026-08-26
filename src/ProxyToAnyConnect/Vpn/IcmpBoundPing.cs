using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace ProxyToAnyConnect.Vpn;

internal static class IcmpBoundPing
{
    private const uint IpSuccess = 0;
    private const int ErrorIoPending = 997;
    private const int ErrorTimeout = 1460;
    private const uint ReplyBufferSize = 512;
    private static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(2);
    private static readonly byte[] Payload = CreatePinnedPayload();
    private static int _activeNativeOperations;

    internal static int ActiveNativeOperations => Volatile.Read(ref _activeNativeOperations);

    public static async Task<IcmpProbeResult> SendAsync(
        IPAddress source,
        IPAddress destination,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (source.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            destination.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("L2TP keepalive supports IPv4 only.");
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var handle = IcmpCreateFile();
        if (handle == new nint(-1))
        {
            return new IcmpProbeResult(false, null, Marshal.GetLastWin32Error());
        }

        nint replyBuffer = 0;
        EventWaitHandle? completionEvent = null;
        Interlocked.Increment(ref _activeNativeOperations);
        try
        {
            replyBuffer = Marshal.AllocHGlobal(checked((int)ReplyBufferSize));
            completionEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset);

            var sendResult = IcmpSendEcho2Ex(
                handle,
                completionEvent.SafeWaitHandle.DangerousGetHandle(),
                0,
                0,
                ToIpAddr(source),
                ToIpAddr(destination),
                Marshal.UnsafeAddrOfPinnedArrayElement(Payload, 0),
                checked((ushort)Payload.Length),
                0,
                replyBuffer,
                ReplyBufferSize,
                checked((uint)Math.Ceiling(timeout.TotalMilliseconds)));

            var pending = false;
            if (sendResult == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ErrorIoPending)
                {
                    return new IcmpProbeResult(false, null, error);
                }

                pending = true;
            }
            else if (sendResult == ErrorIoPending)
            {
                // The IcmpSendEcho2Ex documentation describes ERROR_IO_PENDING as
                // the asynchronous return value. Some Windows examples observe the
                // conventional zero + GetLastError(ERROR_IO_PENDING) form instead;
                // accept both representations of the same pending operation.
                pending = true;
            }

            IcmpProbeResult result;
            if (pending)
            {
                // Do not let caller cancellation release native buffers while Windows
                // may still write the asynchronous reply. Cancellation is surfaced only
                // after this exact native operation has signaled completion (or passed a
                // conservative guard beyond its own native timeout), so no probe worker,
                // ICMP handle or reply buffer survives SendAsync completion.
                var signaled = await WaitForSignalAsync(
                    completionEvent,
                    timeout + CompletionGrace).ConfigureAwait(false);
                result = signaled
                    ? ParseReply(replyBuffer)
                    : new IcmpProbeResult(false, null, ErrorTimeout);
            }
            else
            {
                // A completion can race the asynchronous API setup and be returned
                // synchronously. Parse the same native reply buffer in either case.
                result = ParseReply(replyBuffer);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            // Keep every object that Windows can still touch alive through the ICMP
            // handle close. On current Windows IcmpCloseHandle joins outstanding async
            // requests; ordering handle -> event -> buffer therefore also makes the
            // defensive completion-guard path memory-safe if an event was not signaled.
            _ = IcmpCloseHandle(handle);
            completionEvent?.Dispose();

            if (replyBuffer != 0)
            {
                Marshal.FreeHGlobal(replyBuffer);
            }

            Interlocked.Decrement(ref _activeNativeOperations);
        }
    }

    private static IcmpProbeResult ParseReply(nint replyBuffer)
    {
        var replies = IcmpParseReplies(replyBuffer, ReplyBufferSize);
        if (replies == 0)
        {
            var error = Marshal.GetLastWin32Error();
            return new IcmpProbeResult(false, null, error);
        }

        // IcmpParseReplies leaves ICMP_ECHO_REPLY at the start of the buffer:
        // IPAddr Address (DWORD), ULONG Status, ULONG RoundTripTime. These first
        // 12 bytes have identical offsets on x86/x64, so the pointer-containing
        // remainder of the native structure does not need to be marshalled.
        var status = unchecked((uint)Marshal.ReadInt32(replyBuffer, 4));
        var roundTripMilliseconds = unchecked((uint)Marshal.ReadInt32(replyBuffer, 8));
        return status == IpSuccess
            ? new IcmpProbeResult(true, TimeSpan.FromMilliseconds(roundTripMilliseconds), 0)
            : new IcmpProbeResult(false, null, unchecked((int)status));
    }

    private static Task<bool> WaitForSignalAsync(WaitHandle waitHandle, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, timedOut) =>
                ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
            completion,
            timeout,
            executeOnlyOnce: true);

        return AwaitAndUnregisterAsync(completion.Task, registration);
    }

    private static async Task<bool> AwaitAndUnregisterAsync(
        Task<bool> completion,
        RegisteredWaitHandle registration)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    private static byte[] CreatePinnedPayload()
    {
        ReadOnlySpan<byte> payloadBytes = "ProxyToAnyConnect"u8;
        var payload = GC.AllocateUninitializedArray<byte>(payloadBytes.Length, pinned: true);
        payloadBytes.CopyTo(payload);
        return payload;
    }

    private static uint ToIpAddr(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern nint IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IcmpCloseHandle(nint icmpHandle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        nint icmpHandle,
        nint eventHandle,
        nint apcRoutine,
        nint apcContext,
        uint sourceAddress,
        uint destinationAddress,
        nint requestData,
        ushort requestSize,
        nint requestOptions,
        nint replyBuffer,
        uint replySize,
        uint timeout);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpParseReplies(nint replyBuffer, uint replySize);
}

internal readonly record struct IcmpProbeResult(
    bool Success,
    TimeSpan? RoundTripTime,
    int ErrorCode);
