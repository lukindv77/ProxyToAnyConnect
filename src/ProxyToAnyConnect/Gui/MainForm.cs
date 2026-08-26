using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal sealed class MainForm : Form
{
    private readonly string _configPath;
    private readonly ProxyRuntimeHost _runtimeHost;
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

    public MainForm(AppOptions options, string configPath, ProxyRuntimeHost runtimeHost)
    {
        _options = options;
        _configPath = configPath;
        _runtimeHost = runtimeHost;

        Text = "ProxyToAnyConnect";
        Width = 1220;
        Height = 720;
        MinimumSize = new Size(920, 540);
        StartPosition = FormStartPosition.CenterScreen;

        var menu = BuildMenu();
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildProxyTab());
        tabs.TabPages.Add(BuildVpnTab());
        tabs.TabPages.Add(BuildSettingsTab());

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
        _proxyGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                await EditSelectedProxyAsync();
            }
        };
        _vpnGrid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                await EditSelectedVpnAsync();
            }
        };

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

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(6),
            WrapContents = false
        };
        var add = new Button { Text = "Добавить", AutoSize = true };
        var edit = new Button { Text = "Изменить", AutoSize = true };
        var remove = new Button { Text = "Удалить", AutoSize = true };
        add.Click += async (_, _) => await AddProxyAsync();
        edit.Click += async (_, _) => await EditSelectedProxyAsync();
        remove.Click += async (_, _) => await RemoveSelectedProxyAsync();
        toolbar.Controls.AddRange([add, edit, remove]);

        var page = new TabPage("Proxies");
        page.Controls.Add(_proxyGrid);
        page.Controls.Add(toolbar);
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

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(6),
            WrapContents = false
        };
        var add = new Button { Text = "Добавить", AutoSize = true };
        var edit = new Button { Text = "Изменить", AutoSize = true };
        var remove = new Button { Text = "Удалить", AutoSize = true };
        add.Click += async (_, _) => await AddVpnAsync();
        edit.Click += async (_, _) => await EditSelectedVpnAsync();
        remove.Click += async (_, _) => await RemoveSelectedVpnAsync();
        toolbar.Controls.AddRange([add, edit, remove]);

        var page = new TabPage("L2TP");
        page.Controls.Add(_vpnGrid);
        page.Controls.Add(toolbar);
        return page;
    }

    private TabPage BuildSettingsTab()
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
        _configurationStatus.Text = string.IsNullOrWhiteSpace(_runtimeHost.ConfigurationError)
            ? "Конфигурация runtime: OK"
            : $"Конфигурация runtime: {_runtimeHost.ConfigurationError}";
    }

    private void RefreshProxyGrid()
    {
        var selectedId = SelectedRowId(_proxyGrid);
        _proxyGrid.Rows.Clear();

        if (_runtimeHost.Current is null)
        {
            foreach (var proxy in _options.Proxies)
            {
                var rowIndex = _proxyGrid.Rows.Add(
                    proxy.Name,
                    $"{proxy.ListenAddress}:{proxy.ListenPort}",
                    FindVpnName(proxy.VpnConnectionId),
                    "Error",
                    "0 B",
                    "0 B",
                    _runtimeHost.ConfigurationError ?? "Конфигурация runtime недействительна",
                    "Запустить");
                _proxyGrid.Rows[rowIndex].Tag = proxy.Id;
            }
        }
        else
        {
            foreach (var snapshot in _runtimeHost.GetProxySnapshots())
            {
                var action = snapshot.State is ProxyInstanceState.Running or ProxyInstanceState.Starting
                    ? "Пауза"
                    : "Запустить";
                var rowIndex = _proxyGrid.Rows.Add(
                    snapshot.Name,
                    $"{snapshot.ListenAddress}:{snapshot.ListenPort}",
                    FindVpnName(snapshot.VpnConnectionId),
                    snapshot.State.ToString(),
                    FormatBytes(snapshot.ReceivedBytes),
                    FormatBytes(snapshot.SentBytes),
                    snapshot.LastError ?? string.Empty,
                    action);
                _proxyGrid.Rows[rowIndex].Tag = snapshot.Id;
            }
        }

        RestoreSelection(_proxyGrid, selectedId);
    }

    private void RefreshVpnGrid()
    {
        var selectedId = SelectedRowId(_vpnGrid);
        _vpnGrid.Rows.Clear();

        if (_runtimeHost.Current is null)
        {
            foreach (var vpn in _options.VpnConnections)
            {
                var rowIndex = _vpnGrid.Rows.Add(
                    vpn.Name,
                    vpn.Mode.ToString(),
                    vpn.Shared ? "Shared" : "Dedicated",
                    "Error",
                    string.Empty,
                    0,
                    "0 B",
                    "0 B",
                    "—");
                _vpnGrid.Rows[rowIndex].Tag = vpn.Id;
            }
        }
        else
        {
            foreach (var snapshot in _runtimeHost.GetL2tpSnapshots())
            {
                var rowIndex = _vpnGrid.Rows.Add(
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
                _vpnGrid.Rows[rowIndex].Tag = snapshot.Id;
            }
        }

        RestoreSelection(_vpnGrid, selectedId);
    }

    private async void ProxyGridOnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        var actionColumn = _proxyGrid.Columns["action"];
        if (actionColumn is null || e.RowIndex < 0 || e.ColumnIndex != actionColumn.Index)
        {
            return;
        }

        if (_proxyGrid.Rows[e.RowIndex].Tag is not string proxyId)
        {
            return;
        }

        var snapshot = _runtimeHost.GetProxySnapshots().FirstOrDefault(item =>
            item.Id.Equals(proxyId, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (snapshot.State is ProxyInstanceState.Running or ProxyInstanceState.Starting)
            {
                await _runtimeHost.PauseProxyAsync(proxyId);
            }
            else
            {
                await _runtimeHost.StartProxyAsync(proxyId);
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

    private async Task AddProxyAsync()
    {
        if (_options.VpnConnections.Count == 0)
        {
            MessageBox.Show(this, "Сначала создайте L2TP соединение.", "ProxyToAnyConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ProxySettingsDialog(null, _options.VpnConnections);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var proxies = _options.Proxies.ToList();
        proxies.Add(dialog.Result);
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = proxies,
            VpnConnections = _options.VpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task EditSelectedProxyAsync()
    {
        var id = SelectedRowId(_proxyGrid);
        if (id is null)
        {
            return;
        }

        var existing = _options.Proxies.FirstOrDefault(proxy => proxy.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        using var dialog = new ProxySettingsDialog(existing, _options.VpnConnections);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var replacement = dialog.Result;
        var proxies = _options.Proxies
            .Select(proxy => proxy.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ? replacement : proxy)
            .ToList();
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = proxies,
            VpnConnections = _options.VpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task RemoveSelectedProxyAsync()
    {
        var id = SelectedRowId(_proxyGrid);
        if (id is null)
        {
            return;
        }

        if (_options.Proxies.Count <= 1)
        {
            MessageBox.Show(this, "Должна оставаться как минимум одна настройка proxy.", "ProxyToAnyConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existing = _options.Proxies.FirstOrDefault(proxy => proxy.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null || MessageBox.Show(
                this,
                $"Удалить proxy '{existing.Name}'?",
                "ProxyToAnyConnect",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var proxies = _options.Proxies
            .Where(proxy => !proxy.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = proxies,
            VpnConnections = _options.VpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task AddVpnAsync()
    {
        using var dialog = new L2tpSettingsDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var vpnConnections = _options.VpnConnections.ToList();
        vpnConnections.Add(dialog.Result);
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = _options.Proxies,
            VpnConnections = vpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task EditSelectedVpnAsync()
    {
        var id = SelectedRowId(_vpnGrid);
        if (id is null)
        {
            return;
        }

        var existing = _options.VpnConnections.FirstOrDefault(vpn => vpn.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        using var dialog = new L2tpSettingsDialog(existing);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var replacement = dialog.Result;
        var vpnConnections = _options.VpnConnections
            .Select(vpn => vpn.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ? replacement : vpn)
            .ToList();
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = _options.Proxies,
            VpnConnections = vpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task RemoveSelectedVpnAsync()
    {
        var id = SelectedRowId(_vpnGrid);
        if (id is null)
        {
            return;
        }

        var existing = _options.VpnConnections.FirstOrDefault(vpn => vpn.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return;
        }

        var usedBy = _options.Proxies
            .Where(proxy => proxy.VpnConnectionId.Equals(id, StringComparison.OrdinalIgnoreCase))
            .Select(proxy => proxy.Name)
            .ToArray();
        if (usedBy.Length > 0)
        {
            MessageBox.Show(
                this,
                $"L2TP '{existing.Name}' используется proxy: {string.Join(", ", usedBy)}. Сначала переназначьте или удалите эти proxy.",
                "ProxyToAnyConnect",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_options.VpnConnections.Count <= 1)
        {
            MessageBox.Show(this, "Должна оставаться как минимум одна настройка L2TP.", "ProxyToAnyConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (MessageBox.Show(
                this,
                $"Удалить L2TP '{existing.Name}'?",
                "ProxyToAnyConnect",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var vpnConnections = _options.VpnConnections
            .Where(vpn => !vpn.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await ApplyConfigurationAsync(new AppOptions
        {
            Proxies = _options.Proxies,
            VpnConnections = vpnConnections,
            Logging = _options.Logging
        });
    }

    private async Task ApplyConfigurationAsync(AppOptions newOptions)
    {
        try
        {
            // SaveAsync validates the full graph before touching the file.
            await newOptions.SaveAsync(_configPath, CancellationToken.None);
            await _runtimeHost.ApplyOptionsAsync(newOptions, CancellationToken.None);
            _options = newOptions;
            RefreshRuntimeViews();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Не удалось применить настройки", MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private string FindVpnName(string vpnId) =>
        _options.VpnConnections.FirstOrDefault(vpn => vpn.Id.Equals(vpnId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? vpnId;

    private static string? SelectedRowId(DataGridView grid) =>
        grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as string : null;

    private static void RestoreSelection(DataGridView grid, string? id)
    {
        if (id is null)
        {
            return;
        }

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is string rowId && rowId.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = true;
                break;
            }
        }
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
