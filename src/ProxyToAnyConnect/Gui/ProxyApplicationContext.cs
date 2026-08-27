using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal sealed class ProxyApplicationContext : ApplicationContext
{
    private readonly MainForm _mainForm;
    private readonly ProxyRuntimeHost _runtimeHost;
    private readonly NotifyIcon _notifyIcon;
    private readonly ProcessMemoryHealthMonitor _memoryHealthMonitor;
    private int _exitStarted;

    public ProxyApplicationContext(MainForm mainForm, ProxyRuntimeHost runtimeHost)
    {
        _mainForm = mainForm;
        _runtimeHost = runtimeHost;
        _memoryHealthMonitor = new ProcessMemoryHealthMonitor();

        var trayMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Открыть");
        var memoryItem = new ToolStripMenuItem("Состояние памяти...");
        var exitItem = new ToolStripMenuItem("Выйти");
        openItem.Click += (_, _) => _mainForm.ShowFromTray();
        memoryItem.Click += (_, _) => ShowMemoryHealth();
        exitItem.Click += async (_, _) => await ExitApplicationAsync();
        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(memoryItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "ProxyToAnyConnect",
            Icon = SystemIcons.Application,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _mainForm.ShowFromTray();

        _mainForm.ExitRequested += async (_, _) => await ExitApplicationAsync();
        _mainForm.FormClosed += (_, _) =>
        {
            if (Volatile.Read(ref _exitStarted) != 0)
            {
                ExitThread();
            }
        };

        MainForm = _mainForm;
        _mainForm.Show();

        _mainForm.BeginInvoke(async () =>
        {
            try
            {
                await _runtimeHost.StartEnabledAsync();
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _exitStarted) != 0)
            {
                // Exit owns cancellation of any foreground startup operation. This
                // is expected lifecycle control flow, not a runtime startup failure.
                AppLog.Info(
                    "runtime.start.cancelled_for_exit",
                    "Runtime startup was cancelled because application exit began.");
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _exitStarted) != 0)
            {
                // The UI callback may begin after ExitApplicationAsync has already
                // disposed the host. Treat the same startup-vs-exit race as normal.
                AppLog.Info(
                    "runtime.start.skipped_for_exit",
                    "Runtime startup was skipped because application exit had already disposed the host.");
            }
            catch (Exception ex)
            {
                AppLog.Error("runtime.start.failed", "Runtime startup failed.", ex);
            }
        });
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        var number = Math.Max(0, value);
        var display = (double)number;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{number} {units[unit]}" : $"{display:F2} {units[unit]}";
    }

    private void ShowMemoryHealth()
    {
        ProcessMemorySnapshot snapshot;
        try
        {
            snapshot = ProcessMemoryHealthMonitor.Capture();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось получить состояние памяти:\n{ex.Message}",
                "ProxyToAnyConnect — память",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(
            $"Managed heap: {FormatBytes(snapshot.ManagedHeapBytes)}\n" +
            $"Working set: {FormatBytes(snapshot.WorkingSetBytes)}\n" +
            $"Private bytes: {FormatBytes(snapshot.PrivateBytes)}\n" +
            $"Allocated since start: {FormatBytes(snapshot.TotalAllocatedBytes)}\n\n" +
            $"GC Gen0 / Gen1 / Gen2: {snapshot.Gen0Collections} / {snapshot.Gen1Collections} / {snapshot.Gen2Collections}\n" +
            $"Handles: {snapshot.HandleCount}\n" +
            $"Threads: {snapshot.ThreadCount}",
            "ProxyToAnyConnect — состояние памяти",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task ExitApplicationAsync()
    {
        if (Interlocked.Exchange(ref _exitStarted, 1) != 0)
        {
            return;
        }

        _notifyIcon.Visible = false;

        try
        {
            await _runtimeHost.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("application.shutdown.failed", "Runtime cleanup failed during application exit.", ex);
        }

        try
        {
            await _memoryHealthMonitor.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                "process.memory.monitor_shutdown_failed",
                "Process memory health monitor cleanup failed during application exit.",
                new { Error = ex.Message });
        }
        finally
        {
            _mainForm.AllowExit();
            _notifyIcon.Dispose();
            _mainForm.Close();
        }
    }
}
