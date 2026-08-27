using System.Reflection;
using System.Runtime.CompilerServices;
using ProxyToAnyConnect.Gui;

namespace ProxyToAnyConnect.SelfTests;

internal static class L2tpSettingsDialogBackgroundOwnerSelfTests
{
    public static async Task<int> RunAsync()
    {
        try
        {
            await StopCancelsAndDrainsExactOwnedTaskAsync();
            Console.WriteLine(
                "PASS: L2TP settings dialog cancellation does not release its configuration generation before the exact profile-helper task drains");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: L2TP settings dialog background-owner regression: {ex}");
            return 1;
        }
    }

    private static async Task StopCancelsAndDrainsExactOwnedTaskAsync()
    {
        // StopBackgroundOperationsAsync touches only its explicit ownership fields.
        // Avoid constructing/showing WinForms on the aggregate test thread so this
        // focused lifetime test has no message-loop, apartment or real PowerShell
        // dependency; WindowsVpnProfileInspectorLifetimeSelfTests covers the actual
        // helper process-tree termination separately.
        var dialog = (L2tpSettingsDialog)RuntimeHelpers.GetUninitializedObject(
            typeof(L2tpSettingsDialog));
        using var cancellation = new CancellationTokenSource();
        var terminal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var helperTask = terminal.Task;

        SetPrivateField(dialog, "_profileLoadCancellation", cancellation);
        SetPrivateField(dialog, "_profileLoadTask", helperTask);

        var stopTask = dialog.StopBackgroundOperationsAsync();
        if (!cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Dialog stop did not cancel the exact active profile-load token.");
        }

        if (GetPrivateField<int>(dialog, "_profileLoadStopping") != 1)
        {
            throw new InvalidOperationException(
                "Dialog stop did not permanently close admission for newer profile-load generations.");
        }

        await Task.Delay(50);
        if (stopTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "Dialog stop returned before its exact profile-helper task reached terminal state.");
        }

        terminal.SetResult();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static void SetPrivateField<T>(object target, string name, T value)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Private ownership field '{name}' was not found.");
        field.SetValue(target, value);
    }

    private static T GetPrivateField<T>(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Private ownership field '{name}' was not found.");
        return field.GetValue(target) is T value
            ? value
            : throw new InvalidOperationException($"Private ownership field '{name}' has an unexpected value/type.");
    }
}
