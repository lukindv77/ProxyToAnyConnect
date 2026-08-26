using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ProxyToAnyConnect.Configuration;

namespace ProxyToAnyConnect.Gui;

internal sealed class ProxySettingsDialog : Form
{
    private readonly string _id;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _enabled = new() { Text = "Запускать автоматически", AutoSize = true };
    private readonly ComboBox _listenAddress = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _listenPort = Numeric(1, 65535);
    private readonly NumericUpDown _maxConcurrentConnections = Numeric(1, 100000);
    private readonly ComboBox _vpn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _maxHeaderBytes = Numeric(4096, 1024 * 1024);
    private readonly NumericUpDown _clientHeaderTimeout = Numeric(1, 300);
    private readonly NumericUpDown _outboundConnectTimeout = Numeric(1, 300);
    private readonly NumericUpDown _dnsTimeout = Numeric(250, 60000);

    public ProxySettingsDialog(
        ProxyOptions? existing,
        IReadOnlyList<L2tpOptions> vpnConnections)
    {
        _id = existing?.Id ?? Guid.NewGuid().ToString("N");

        Text = existing is null ? "Новый proxy" : $"Proxy — {existing.Name}";
        Width = 580;
        Height = 540;
        MinimumSize = new Size(520, 470);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;

        foreach (var address in GetLocalIPv4Addresses(existing?.ListenAddress))
        {
            _listenAddress.Items.Add(address);
        }

        foreach (var vpn in vpnConnections.OrderBy(vpn => vpn.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _vpn.Items.Add(new VpnItem(vpn.Id, vpn.Name));
        }

        var layout = CreateLayout();
        AddRow(layout, 0, "Имя:", _name);
        AddRow(layout, 1, "Bind IPv4:", _listenAddress);
        AddRow(layout, 2, "Bind port:", _listenPort);
        AddRow(layout, 3, "Max concurrent connections:", _maxConcurrentConnections);
        AddRow(layout, 4, "L2TP:", _vpn);
        AddRow(layout, 5, "Max HTTP header, bytes:", _maxHeaderBytes);
        AddRow(layout, 6, "Header timeout, sec:", _clientHeaderTimeout);
        AddRow(layout, 7, "Connect timeout, sec:", _outboundConnectTimeout);
        AddRow(layout, 8, "DNS timeout, ms:", _dnsTimeout);
        layout.Controls.Add(_enabled, 1, 9);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            try
            {
                ValidateEditor();
            }
            catch (Exception ex)
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(this, ex.Message, "Проверка настроек", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 11);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        LoadExisting(existing, vpnConnections);
    }

    public ProxyOptions Result => new()
    {
        Id = _id,
        Name = _name.Text.Trim(),
        Enabled = _enabled.Checked,
        ListenAddress = _listenAddress.SelectedItem?.ToString() ?? string.Empty,
        ListenPort = decimal.ToInt32(_listenPort.Value),
        MaxConcurrentConnections = decimal.ToInt32(_maxConcurrentConnections.Value),
        VpnConnectionId = (_vpn.SelectedItem as VpnItem)?.Id ?? string.Empty,
        MaxHeaderBytes = decimal.ToInt32(_maxHeaderBytes.Value),
        ClientHeaderTimeoutSeconds = decimal.ToInt32(_clientHeaderTimeout.Value),
        OutboundConnectTimeoutSeconds = decimal.ToInt32(_outboundConnectTimeout.Value),
        DnsTimeoutMilliseconds = decimal.ToInt32(_dnsTimeout.Value)
    };

    private void LoadExisting(ProxyOptions? existing, IReadOnlyList<L2tpOptions> vpnConnections)
    {
        _name.Text = existing?.Name ?? $"Proxy {vpnConnections.Count + 1}";
        _enabled.Checked = existing?.Enabled ?? true;
        _listenPort.Value = existing?.ListenPort ?? 18080;
        _maxConcurrentConnections.Value = existing?.MaxConcurrentConnections ?? 512;
        _maxHeaderBytes.Value = existing?.MaxHeaderBytes ?? 65536;
        _clientHeaderTimeout.Value = existing?.ClientHeaderTimeoutSeconds ?? 15;
        _outboundConnectTimeout.Value = existing?.OutboundConnectTimeoutSeconds ?? 15;
        _dnsTimeout.Value = existing?.DnsTimeoutMilliseconds ?? 3000;

        SelectComboText(_listenAddress, existing?.ListenAddress ?? IPAddress.Loopback.ToString());

        if (existing is not null)
        {
            SelectVpn(existing.VpnConnectionId);
        }
        else if (_vpn.Items.Count > 0)
        {
            _vpn.SelectedIndex = 0;
        }
    }

    private void ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            throw new InvalidOperationException("Укажите имя proxy.");
        }

        if (_listenAddress.SelectedItem is null)
        {
            throw new InvalidOperationException("Выберите локальный IPv4 для bind.");
        }

        if (_vpn.SelectedItem is null)
        {
            throw new InvalidOperationException("Выберите L2TP соединение.");
        }
    }

    private void SelectVpn(string id)
    {
        for (var index = 0; index < _vpn.Items.Count; index++)
        {
            if (_vpn.Items[index] is VpnItem item &&
                item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            {
                _vpn.SelectedIndex = index;
                return;
            }
        }
    }

    private static IReadOnlyList<string> GetLocalIPv4Addresses(string? configured)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            IPAddress.Loopback.ToString()
        };

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    values.Add(unicast.Address.ToString());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            values.Add(configured);
        }

        return values
            .OrderBy(value => value.Equals(IPAddress.Loopback.ToString(), StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TableLayoutPanel CreateLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 12,
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddRow(TableLayoutPanel layout, int row, string caption, Control control)
    {
        layout.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 12, 3)
        }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        layout.Controls.Add(control, 1, row);
    }

    private static NumericUpDown Numeric(int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Width = 140,
        ThousandsSeparator = true
    };

    private static void SelectComboText(ComboBox combo, string value)
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (string.Equals(combo.Items[index]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
    }

    private sealed record VpnItem(string Id, string Name)
    {
        public override string ToString() => Name;
    }
}
