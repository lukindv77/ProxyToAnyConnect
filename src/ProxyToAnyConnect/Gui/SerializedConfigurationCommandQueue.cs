namespace ProxyToAnyConnect.Gui;

internal sealed class SerializedConfigurationCommandQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _issuedGeneration;

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
            predecessor = _tail;
            _tail = completion.Task;
            generation = checked(++_issuedGeneration);
        }

        return RunCoreAsync(
            predecessor,
            completion,
            generation,
            command,
            cancellationToken);
    }

    private static async Task RunCoreAsync(
        Task predecessor,
        TaskCompletionSource<bool> completion,
        long generation,
        Func<long, CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        try
        {
            // Await the exact predecessor without cancellation so a cancelled queued
            // command can never open a hole that lets a later generation overtake an
            // earlier durable save/runtime-apply transaction.
            await predecessor;
            cancellationToken.ThrowIfCancellationRequested();
            await command(generation, cancellationToken);
        }
        finally
        {
            // The serialization tail is independent from command success. A failed or
            // cancelled generation must unblock its successor while its own returned
            // task still preserves the original exception/cancellation for the caller.
            completion.TrySetResult(true);
        }
    }
}
