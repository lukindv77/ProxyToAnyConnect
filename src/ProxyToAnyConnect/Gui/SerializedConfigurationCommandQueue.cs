namespace ProxyToAnyConnect.Gui;

internal sealed class SerializedConfigurationCommandQueue
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _tail = Task.CompletedTask;
    private TaskCompletionSource<bool>? _stopCompletion;
    private long _issuedGeneration;
    private int _stopped;

    public Task RunAsync(
        Func<long, CancellationToken, Task> command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Task predecessor;
        long generation;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
            predecessor = _tail;
            _tail = completion.Task;
            generation = checked(++_issuedGeneration);
        }

        return RunCoreAsync(
            predecessor,
            completion,
            generation,
            command,
            cancellationToken,
            _shutdown.Token);
    }

    public Task StopAsync()
    {
        Task tail;
        TaskCompletionSource<bool> stopCompletion;

        lock (_sync)
        {
            if (_stopCompletion is not null)
            {
                return _stopCompletion.Task;
            }

            Volatile.Write(ref _stopped, 1);
            tail = _tail;
            stopCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopCompletion = stopCompletion;
        }

        // Begin cancellation only after releasing _sync. Cancellation callbacks are
        // arbitrary application code and must never be able to deadlock by trying to
        // enqueue another command while the queue's ownership lock is held.
        _ = StopCoreAsync(tail, stopCompletion);
        return stopCompletion.Task;
    }

    private static async Task RunCoreAsync(
        Task predecessor,
        TaskCompletionSource<bool> completion,
        long generation,
        Func<long, CancellationToken, Task> command,
        CancellationToken callerCancellation,
        CancellationToken shutdownCancellation)
    {
        try
        {
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                callerCancellation,
                shutdownCancellation);
            var operationToken = operationCancellation.Token;

            // Await the exact predecessor without cancellation so a cancelled queued
            // command can never open a hole that lets a later generation overtake an
            // earlier durable save/runtime-apply transaction.
            await predecessor;
            operationToken.ThrowIfCancellationRequested();
            await command(generation, operationToken);
        }
        finally
        {
            // The serialization tail is independent from command success. A failed or
            // cancelled generation must unblock its successor while its own returned
            // task still preserves the original exception/cancellation for the caller.
            completion.TrySetResult(true);
        }
    }

    private async Task StopCoreAsync(
        Task tail,
        TaskCompletionSource<bool> stopCompletion)
    {
        Exception? shutdownFailure = null;
        try
        {
            try
            {
                _shutdown.Cancel(throwOnFirstException: false);
            }
            catch (Exception ex)
            {
                // Cancellation callback failures are cleanup defects, but every
                // queued generation must still be allowed to observe cancellation
                // and release its serialization ownership before Stop completes.
                shutdownFailure = ex;
            }

            await tail;
        }
        catch (Exception ex)
        {
            shutdownFailure ??= ex;
        }
        finally
        {
            try
            {
                _shutdown.Dispose();
            }
            catch (Exception ex)
            {
                shutdownFailure ??= ex;
            }
        }

        if (shutdownFailure is null)
        {
            stopCompletion.TrySetResult(true);
        }
        else
        {
            stopCompletion.TrySetException(shutdownFailure);
        }
    }
}
