using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Diagnostics;
using ProxyToAnyConnect.Runtime;

namespace ProxyToAnyConnect.Gui;

internal sealed class MainForm : Form
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private readonly string _configPath;
    private readonly ProxyRuntimeHost _runtimeHost;
    private readonly DataGridView _proxyGrid = CreateGrid();
    private readonly DataGridView _vpnGrid = CreateGrid();
    private readonly Dictionary<string, DataGridViewRow> _proxyRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DataGridViewRow> _vpnRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _vpnNames = new(StringComparer.OrdinalIgnoreCase);
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

    private AppOptions _options;
    private bool _allowExit;

    public MainForm(AppOptions options, string configPath, ProxyRuntimeHost runtimeHost)
    {
        _options = options;
        _configPath = configPath;
        _runtimeHost = runtimeHost;
        RebuildVpnNameIndex();

        Text = "ProxyToAnyConnect";
        Width = 1480;
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
        _vpnGrid.Columns.Add(TextColumn("ip", "IPv4 / ifIndex", 170));
        _vpnGrid.Columns.Add(TextColumn("leases", "Proxy", 60));
        _vpnGrid.Columns.Add(TextColumn("rx", "RX", 110));
        _vpnGrid.Columns.Add(TextColumn("tx", "TX", 110));
        _vpnGrid.Columns.Add(TextColumn("ping", "Ping avg 5m", 110));
        _vpnGrid.Columns.Add(TextColumn("status", "Статус / причина", 390));

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
        SetLabelText(_effectiveLogPath, AppLog.LogRootDirectory ?? AppContext.BaseDirectory);
        SetLabelText(
            _configurationStatus,
            string.IsNullOrWhiteSpace(_runtimeHost.ConfigurationError)
                ? "Конфигурация runtime: OK"
                : $"Конфигурация runtime: {_runtimeHost.ConfigurationError}");
    }

    private void RefreshProxyGrid()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_runtimeHost.Current is null)
        {
            foreach (var proxy in _options.Proxies)
            {
                seen.Add(proxy.Id);
                var row = GetOrCreateRow(_proxyGrid, _proxyRows, proxy.Id);
                SetCell(row, 0, proxy.Name);
                SetCell(row, 1, $"{proxy.ListenAddress}:{proxy.ListenPort}");
                SetCell(row, 2, FindVpnName(proxy.VpnConnectionId));
                SetCell(row, 3, "Error");
                SetByteCell(row.Cells[4], 0);
                SetByteCell(row.Cells[5], 0);
                SetCell(row, 6, _runtimeHost.ConfigurationError ?? "Конфигурация runtime недействительна");
                SetCell(row, 7, "Запустить");
            }
        }
        else
        {
            foreach (var snapshot in _runtimeHost.GetProxySnapshots())
            {
                seen.Add(snapshot.Id);
                var row = GetOrCreateRow(_proxyGrid, _proxyRows, snapshot.Id);
                SetCell(row, 0, snapshot.Name);
                SetCell(row, 1, $"{snapshot.ListenAddress}:{snapshot.ListenPort}");
                SetCell(row, 2, FindVpnName(snapshot.VpnConnectionId));
                SetCell(row, 3, snapshot.State.ToString());
                SetByteCell(row.Cells[4], snapshot.ReceivedBytes);
                SetByteCell(row.Cells[5], snapshot.SentBytes);
                SetCell(row, 6, snapshot.LastError ?? string.Empty);
                SetCell(
                    row,
                    7,
                    snapshot.State is ProxyInstanceState.Running or ProxyInstanceState.Starting
                        ? "Пауза"
                        : "Запустить");
            }
        }

        RemoveStaleRows(_proxyGrid, _proxyRows, seen);
    }

    private void RefreshVpnGrid()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_runtimeHost.Current is null)
        {
            foreach (var vpn in _options.VpnConnections)
            {
                seen.Add(vpn.Id);
                var row = GetOrCreateRow(_vpnGrid, _vpnRows, vpn.Id);
                SetCell(row, 0, vpn.Name);
                SetCell(row, 1, vpn.Mode.ToString());
                SetCell(row, 2, vpn.Shared ? "Shared" : "Dedicated");
                SetCell(row, 3, "Error");
                SetCell(row, 4, string.Empty);
                SetCell(row, 5, 0);
                SetByteCell(row.Cells[6], 0);
                SetByteCell(row.Cells[7], 0);
                SetCell(row, 8, "—");
                SetCell(row, 9, _runtimeHost.ConfigurationError ?? "Конфигурация runtime недействительна");
            }
        }
        else
        {
            foreach (var snapshot in _runtimeHost.GetL2tpSnapshots())
            {
                seen.Add(snapshot.Id);
                var row = GetOrCreateRow(_vpnGrid, _vpnRows, snapshot.Id);
                SetCell(row, 0, snapshot.Name);
                SetCell(row, 1, snapshot.Mode.ToString());
                SetCell(row, 2, snapshot.Shared ? "Shared" : "Dedicated");
                SetCell(row, 3, snapshot.State.ToString());
                SetCell(row, 4, FormatVpnInterface(snapshot.LocalIPv4, snapshot.InterfaceIndex));
                SetCell(row, 5, snapshot.ActiveProxyCount);
                SetByteCell(row.Cells[6], snapshot.ReceivedBytes);
                SetByteCell(row.Cells[7], snapshot.SentBytes);
                SetPingCell(row.Cells[8], snapshot.AveragePingMilliseconds);
                SetCell(row, 9, VpnLatestStatusRegistry.Get(snapshot.Id)?.Text ?? string.Empty);
            }
        }

        RemoveStaleRows(_vpnGrid, _vpnRows, seen);
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
        var persisted = false;
        try
        {
            await PersistedDesiredConfiguration.SaveThenApplyAsync(
                newOptions,
                (desired, cancellationToken) => desired.SaveAsync(_configPath, cancellationToken),
                desired =>
                {
                    _options = desired;
                    RebuildVpnNameIndex();
                    persisted = true;
                },
                (desired, cancellationToken) => _runtimeHost.ApplyOptionsAsync(desired, cancellationToken),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                persisted
                    ? "Настройки сохранены, runtime не применён"
                    : "Не удалось применить настройки",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            if (persisted)
            {
                // Show the persisted desired settings through subsequent editors and
                // refresh current runtime/Error state even when reconciliation failed.
                RefreshRuntimeViews();
            }
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
        SetLabelText(_effectiveLogPath, AppLog.LogRootDirectory ?? AppContext.BaseDirectory);
    }

    private void RebuildVpnNameIndex()
    {
        _vpnNames.Clear();
        foreach (var vpn in _options.VpnConnections)
        {
            _vpnNames[vpn.Id] = vpn.Name;
        }
    }

    private string FindVpnName(string vpnId) =>
        _vpnNames.TryGetValue(vpnId, out var name) ? name : vpnId;

    private static DataGridViewRow GetOrCreateRow(
        DataGridView grid,
        Dictionary<string, DataGridViewRow> rows,
        string id)
    {
        if (rows.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var index = grid.Rows.Add();
        var row = grid.Rows[index];
        row.Tag = id;
        rows.Add(id, row);
        return row;
    }

    private static void RemoveStaleRows(
        DataGridView grid,
        Dictionary<string, DataGridViewRow> rows,
        HashSet<string> seen)
    {
        List<string>? stale = null;
        foreach (var id in rows.Keys)
        {
            if (!seen.Contains(id))
            {
                (stale ??= []).Add(id);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var id in stale)
        {
            var row = rows[id];
            rows.Remove(id);
            grid.Rows.Remove(row);
        }
    }

    private static void SetCell(DataGridViewRow row, int index, object? value)
    {
        var cell = row.Cells[index];
        if (!Equals(cell.Value, value))
        {
            cell.Value = value;
        }
    }

    private static void SetByteCell(DataGridViewCell cell, long value)
    {
        var normalized = Math.Max(0, value);
        if (cell.Tag is long previous && previous == normalized)
        {
            return;
        }

        cell.Tag = normalized;
        cell.Value = FormatBytes(normalized);
    }

    private static string FormatVpnInterface(string? localIPv4, int? interfaceIndex)
    {
        if (string.IsNullOrWhiteSpace(localIPv4))
        {
            return interfaceIndex is null ? string.Empty : $"ifIndex {interfaceIndex.Value}";
        }

        return interfaceIndex is null
            ? localIPv4
            : $"{localIPv4} / ifIndex {interfaceIndex.Value}";
    }

    private static void SetPingCell(DataGridViewCell cell, double? value)
    {
        if (cell.Tag is double previous && value is double current && previous.Equals(current))
        {
            return;
        }

        if (cell.Tag is null && value is null)
        {
            return;
        }

        cell.Tag = value;
        cell.Value = value is double ping ? $"{ping:F1} ms" : "—";
    }

    private static void SetLabelText(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
        {
            label.Text = text;
        }
    }

    private static string? SelectedRowId(DataGridView grid) =>
        grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as string : null;

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
        var unit = 0;
        var number = (double)bytes;
        while (number >= 1024 && unit < ByteUnits.Length - 1)
        {
            number /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{number:F2} {ByteUnits[unit]}";
    }
}
