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
        var exitItem = new ToolStripMenuItem("Выйти");
        openItem.Click += (_, _) => _mainForm.ShowFromTray();
        exitItem.Click += async (_, _) => await ExitApplicationAsync();
        trayMenu.Items.Add(openItem);
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
            catch (Exception ex)
            {
                AppLog.Error("runtime.start.failed", "Runtime startup failed.", ex);
            }
        });
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
