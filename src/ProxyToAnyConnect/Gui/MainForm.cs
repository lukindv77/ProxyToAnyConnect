using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal sealed class MainForm : Form
{
    private readonly string _configPath;
    private readonly ProxyRuntimeCoordinator? _runtime;
    private AppOptions _options;
    private readonly DataGridView _proxyGrid = CreateGrid();
    private readonly DataGridView _vpnGrid = CreateGrid();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1000 };
    private readonly TextBox _logDirectory = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _retentionDays = new()
    {
        Minimum = 1,
        Maximum = 3650,
        Width = 100
    };
    private readonly Label _effectiveLogPath = new() { AutoSize = true };
    private readonly Label _configurationStatus = new() { AutoSize = true };
    private bool _allowExit;

    public MainForm(
        AppOptions options,
        string configPath,
        ProxyRuntimeCoordinator? runtime,
        string? configurationError)
    {
        _options = options;
        _configPath = configPath;
        _runtime = runtime;

        Text = "ProxyToAnyConnect";
        Width = 1180;
        Height = 680;
        MinimumSize = new Size(900, 520);
        StartPosition = FormStartPosition.CenterScreen;

        var menu = BuildMenu();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildProxyTab());
        tabs.TabPages.Add(BuildVpnTab());
        tabs.TabPages.Add(BuildSettingsTab(configurationError));

        Controls.Add(tabs);
        Controls.Add(menu);
        MainMenuStrip = menu;

        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        };

        _proxyGrid.CellContentClick += ProxyGridOnCellContentClick;
        _refreshTimer.Tick += (_, _) => RefreshRuntimeViews();
        _refreshTimer.Start();
        RefreshRuntimeViews();
        RefreshLoggingSettings();
    }

    public event EventHandler? ExitRequested;

    public void ShowFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    public void AllowExit()
    {
        _allowExit = true;
        _refreshTimer.Stop();
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Файл");
        var exit = new ToolStripMenuItem("Выйти");
        exit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        file.DropDownItems.Add(exit);
        menu.Items.Add(file);
        return menu;
    }

    private TabPage BuildProxyTab()
    {
        _proxyGrid.Columns.Add(TextColumn("name", "Proxy", 160));
        _proxyGrid.Columns.Add(TextColumn("bind", "Bind", 150));
        _proxyGrid.Columns.Add(TextColumn("vpn", "L2TP", 150));
        _proxyGrid.Columns.Add(TextColumn("state", "Состояние", 100));
        _proxyGrid.Columns.Add(TextColumn("rx", "RX", 110));
        _proxyGrid.Columns.Add(TextColumn("tx", "TX", 110));
        _proxyGrid.Columns.Add(TextColumn("error", "Статус / ошибка", 280));
        _proxyGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "action",
            HeaderText = "Управление",
            Width = 100,
            UseColumnTextForButtonValue = false
        });

        var page = new TabPage("Proxies");
        page.Controls.Add(_proxyGrid);
        return page;
    }

    private TabPage BuildVpnTab()
    {
        _vpnGrid.Columns.Add(TextColumn("name", "L2TP", 150));
        _vpnGrid.Columns.Add(TextColumn("mode", "Режим", 150));
        _vpnGrid.Columns.Add(TextColumn("sharing", "Тип", 90));
        _vpnGrid.Columns.Add(TextColumn("state", "Состояние", 100));
        _vpnGrid.Columns.Add(TextColumn("ip", "IPv4", 120));
        _vpnGrid.Columns.Add(TextColumn("leases", "Proxy", 60));
        _vpnGrid.Columns.Add(TextColumn("rx", "RX", 110));
        _vpnGrid.Columns.Add(TextColumn("tx", "TX", 110));
        _vpnGrid.Columns.Add(TextColumn("ping", "Ping avg 5m", 110));

        var page = new TabPage("L2TP");
        page.Controls.Add(_vpnGrid);
        return page;
    }

    private TabPage BuildSettingsTab(string? configurationError)
    {
        var page = new TabPage("Settings");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 6
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Папка логов:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_logDirectory, 1, 0);
        var browse = new Button { Text = "Выбрать...", AutoSize = true };
        browse.Click += BrowseLogDirectory;
        layout.Controls.Add(browse, 2, 0);

        layout.Controls.Add(new Label { Text = "Хранить дней:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_retentionDays, 1, 1);

        layout.Controls.Add(new Label { Text = "Текущий log root:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_effectiveLogPath, 1, 2);
        layout.SetColumnSpan(_effectiveLogPath, 2);

        var save = new Button { Text = "Сохранить настройки логов", AutoSize = true };
        save.Click += async (_, _) => await SaveLoggingSettingsAsync();
        layout.Controls.Add(save, 1, 3);

        _configurationStatus.Text = string.IsNullOrWhiteSpace(configurationError)
            ? "Конфигурация runtime: OK"
            : $"Конфигурация runtime: {configurationError}";
        layout.Controls.Add(_configurationStatus, 0, 5);
        layout.SetColumnSpan(_configurationStatus, 3);

        page.Controls.Add(layout);
        return page;
    }

    private void RefreshRuntimeViews()
    {
        RefreshProxyGrid();
        RefreshVpnGrid();
        _effectiveLogPath.Text = AppLog.LogRootDirectory ?? AppContext.BaseDirectory;
    }

    private void RefreshProxyGrid()
    {
        _proxyGrid.Rows.Clear();

        if (_runtime is null)
        {
            foreach (var proxy in _options.Proxies)
            {
                var rowIndex = _proxyGrid.Rows.Add(
                    proxy.Name,
                    $"{proxy.ListenAddress}:{proxy.ListenPort}",
                    proxy.VpnConnectionId,
                    "Error",
                    "0 B",
                    "0 B",
                    "Конфигурация runtime недействительна",
                    "Запустить");
                _proxyGrid.Rows[rowIndex].Tag = proxy.Id;
            }
            return;
        }

        foreach (var snapshot in _runtime.GetProxySnapshots())
        {
            var action = snapshot.State is ProxyInstanceState.Running or ProxyInstanceState.Starting
                ? "Пауза"
                : "Запустить";
            var rowIndex = _proxyGrid.Rows.Add(
                snapshot.Name,
                $"{snapshot.ListenAddress}:{snapshot.ListenPort}",
                snapshot.VpnConnectionId,
                snapshot.State.ToString(),
                FormatBytes(snapshot.ReceivedBytes),
                FormatBytes(snapshot.SentBytes),
                snapshot.LastError ?? string.Empty,
                action);
            _proxyGrid.Rows[rowIndex].Tag = snapshot.Id;
        }
    }

    private void RefreshVpnGrid()
    {
        _vpnGrid.Rows.Clear();
        if (_runtime is null)
        {
            foreach (var vpn in _options.VpnConnections)
            {
                _vpnGrid.Rows.Add(
                    vpn.Name,
                    vpn.Mode.ToString(),
                    vpn.Shared ? "Shared" : "Dedicated",
                    "Error",
                    string.Empty,
                    0,
                    "0 B",
                    "0 B",
                    "—");
            }
            return;
        }

        foreach (var snapshot in _runtime.GetL2tpSnapshots())
        {
            _vpnGrid.Rows.Add(
                snapshot.Name,
                snapshot.Mode.ToString(),
                snapshot.Shared ? "Shared" : "Dedicated",
                snapshot.State.ToString(),
                snapshot.LocalIPv4 ?? string.Empty,
                snapshot.ActiveProxyCount,
                FormatBytes(snapshot.ReceivedBytes),
                FormatBytes(snapshot.SentBytes),
                snapshot.AveragePingMilliseconds is double ping
                    ? $"{ping:F1} ms"
                    : "—");
        }
    }

    private async void ProxyGridOnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (_runtime is null || e.RowIndex < 0 || e.ColumnIndex != _proxyGrid.Columns["action"].Index)
        {
            return;
        }

        if (_proxyGrid.Rows[e.RowIndex].Tag is not string proxyId)
        {
            return;
        }

        var snapshot = _runtime.GetProxySnapshots().FirstOrDefault(item =>
            item.Id.Equals(proxyId, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (snapshot.State is ProxyInstanceState.Running or ProxyInstanceState.Starting)
            {
                await _runtime.PauseProxyAsync(proxyId);
            }
            else
            {
                await _runtime.StartProxyAsync(proxyId);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "ProxyToAnyConnect", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            RefreshRuntimeViews();
        }
    }

    private void BrowseLogDirectory(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите корневую папку хранения журналов ProxyToAnyConnect",
            UseDescriptionForTitle = true,
            InitialDirectory = string.IsNullOrWhiteSpace(_logDirectory.Text)
                ? AppContext.BaseDirectory
                : _logDirectory.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _logDirectory.Text = dialog.SelectedPath;
        }
    }

    private async Task SaveLoggingSettingsAsync()
    {
        var newLogging = new LoggingOptions
        {
            Directory = _logDirectory.Text.Trim(),
            RetentionDays = decimal.ToInt32(_retentionDays.Value),
            ConsoleJson = _options.Logging.ConsoleJson
        };
        var newOptions = new AppOptions
        {
            Proxies = _options.Proxies,
            VpnConnections = _options.VpnConnections,
            Logging = newLogging
        };

        try
        {
            await newOptions.SaveAsync(_configPath, CancellationToken.None);
            _options = newOptions;
            AppLog.Configure(newLogging);
            RefreshLoggingSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось сохранить настройки", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RefreshLoggingSettings()
    {
        _logDirectory.Text = _options.Logging.Directory;
        _retentionDays.Value = Math.Clamp(_options.Logging.RetentionDays, 1, 3650);
        _effectiveLogPath.Text = AppLog.LogRootDirectory ?? AppContext.BaseDirectory;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit)
        {
            return;
        }

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
    };

    private static DataGridViewTextBoxColumn TextColumn(string name, string header, int width) => new()
    {
        Name = name,
        HeaderText = header,
        Width = width,
        SortMode = DataGridViewColumnSortMode.NotSortable
    };

    private static string FormatBytes(long value)
    {
        var bytes = Math.Max(0, value);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        var number = (double)bytes;
        while (number >= 1024 && unit < units.Length - 1)
        {
            number /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{number:F2} {units[unit]}";
    }
}
