using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal sealed class ProxyApplicationContext : ApplicationContext
{
    private readonly MainForm _mainForm;
    private readonly ProxyRuntimeCoordinator? _runtime;
    private readonly NotifyIcon _notifyIcon;
    private int _exitStarted;

    public ProxyApplicationContext(MainForm mainForm, ProxyRuntimeCoordinator? runtime)
    {
        _mainForm = mainForm;
        _runtime = runtime;

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

        if (_runtime is not null)
        {
            _mainForm.BeginInvoke(async () =>
            {
                try
                {
                    await _runtime.StartEnabledAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Error("runtime.start.failed", "Runtime startup failed.", ex);
                }
            });
        }
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
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("application.shutdown.failed", "Runtime cleanup failed during application exit.", ex);
        }
        finally
        {
            _mainForm.AllowExit();
            _notifyIcon.Dispose();
            _mainForm.Close();
        }
    }
}
