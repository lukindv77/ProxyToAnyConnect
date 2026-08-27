using ProxyToAnyConnect.Gui;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.SelfTests;

internal static class L2tpSettingsDialogBackgroundSelfTests
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP: L2TP dialog background ownership test requires Windows WinForms.");
            return 0;
        }

        try
        {
            await StopWaitsForExactProfileLoadDrainAsync();
            Console.WriteLine(
                "PASS: L2TP settings dialog cancellation waits for exact profile-helper generation drain and becomes terminal");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: L2TP settings dialog background ownership regression: {ex}");
            return 1;
        }
    }

    private static async Task StopWaitsForExactProfileLoadDrainAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;

        async Task<IReadOnlyList<VpnProfileInfo>> LoadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loadCount);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.TrySetResult();
                await allowDrain.Task;
                throw;
            }

            return [];
        }

        using var dialog = new L2tpSettingsDialog(existing: null, profileLoader: LoadAsync);
        dialog.StartWindowsProfileLoad();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopTask = dialog.StopBackgroundOperationsAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Dialog shutdown completed before its cancelled profile load finished helper drain.");
        }

        allowDrain.TrySetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(2));
        await dialog.StopBackgroundOperationsAsync().WaitAsync(TimeSpan.FromSeconds(2));

        dialog.StartWindowsProfileLoad();
        await Task.Delay(50);
        if (Volatile.Read(ref loadCount) != 1)
        {
            throw new InvalidOperationException(
                $"Stopped dialog admitted {loadCount} profile-load generations; expected exactly one.");
        }
    }
}
