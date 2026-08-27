using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class ConfigurationDialogOwnerSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await StopCancelsActiveDialogExactlyOnceAsync();
            NaturalCompletionReleasesDialogOwnership();
            CancellationFailureStillMakesOwnerTerminal();

            Console.WriteLine(
                "PASS: GUI configuration dialog ownership cancels active modals exactly once and becomes terminal on exit");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: GUI configuration dialog ownership regression: {ex}");
            return 1;
        }
    }

    private static async Task StopCancelsActiveDialogExactlyOnceAsync()
    {
        var owner = new ConfigurationDialogOwner();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var cancelCount = 0;

        var run = Task.Run(() => owner.Run(
            showDialog: () =>
            {
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("Synthetic modal did not receive the exit cancellation callback.");
                }

                return 42;
            },
            cancelDialog: () =>
            {
                Interlocked.Increment(ref cancelCount);
                release.Set();
            }));

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        owner.Stop();
        owner.Stop();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        if (result != 42 || Volatile.Read(ref cancelCount) != 1)
        {
            throw new InvalidOperationException(
                $"Expected one active-modal cancellation and result 42, observed cancelCount={cancelCount}, result={result}.");
        }

        try
        {
            _ = owner.Run(() => 0, () => { });
            throw new InvalidOperationException("Stopped dialog owner accepted a new modal generation.");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void NaturalCompletionReleasesDialogOwnership()
    {
        var owner = new ConfigurationDialogOwner();
        var cancelCount = 0;
        var result = owner.Run(
            showDialog: () => 7,
            cancelDialog: () => Interlocked.Increment(ref cancelCount));

        owner.Stop();
        if (result != 7 || cancelCount != 0)
        {
            throw new InvalidOperationException(
                "A naturally completed modal remained owned and was cancelled during later shutdown.");
        }
    }

    private static void CancellationFailureStillMakesOwnerTerminal()
    {
        var owner = new ConfigurationDialogOwner();
        using var release = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCount = 0;

        var run = Task.Run(() => owner.Run(
            showDialog: () =>
            {
                entered.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(2));
                return 0;
            },
            cancelDialog: () =>
            {
                Interlocked.Increment(ref cancelCount);
                release.Set();
                throw new IOException("expected modal-close failure");
            }));

        entered.Task.Wait(TimeSpan.FromSeconds(2));
        try
        {
            owner.Stop();
            throw new InvalidOperationException("Synthetic modal-close failure was not preserved.");
        }
        catch (IOException ex) when (ex.Message.Contains("expected modal-close failure", StringComparison.Ordinal))
        {
        }

        owner.Stop();
        if (cancelCount != 1)
        {
            throw new InvalidOperationException("Repeated Stop re-invoked a failed modal-close callback.");
        }

        _ = run.GetAwaiter().GetResult();
        try
        {
            _ = owner.Run(() => 0, () => { });
            throw new InvalidOperationException("Dialog owner lost terminal shutdown state after a close failure.");
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
