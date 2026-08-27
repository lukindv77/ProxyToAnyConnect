using System.Net;
using ProxyToAnyConnect.Configuration;
using ProxyToAnyConnect.Security;
using ProxyToAnyConnect.Vpn;

namespace ProxyToAnyConnect.Gui;

internal sealed class L2tpSettingsDialog : Form
{
    private readonly string _id;
    private readonly L2tpOptions? _existing;
    private readonly WindowsVpnProfileInspector _profileInspector = new();
    private CancellationTokenSource? _profileLoadCancellation;
    private Task _profileLoadTask = Task.CompletedTask;
    private int _profileLoadStopping;
    private L2tpOptions? _acceptedResult;

    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _shared = new() { Text = "Общее L2TP для нескольких proxy", AutoSize = true };
    private readonly ComboBox _mode = EnumCombo<L2tpConnectionMode>();
    private readonly ComboBox _windowsProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _refreshProfiles = new() { Text = "Обновить", AutoSize = true };

    private readonly NumericUpDown _monitorInterval = Numeric(250, 60000);
    private readonly NumericUpDown _routeMonitorInterval = Numeric(1000, 300000);
    private readonly NumericUpDown _reconnectCooldown = Numeric(0, 300000);

    private readonly TextBox _publicAddress = new() { Dock = DockStyle.Fill };
    private readonly TextBox _probeHost = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _probePort = Numeric(1, 65535);
    private readonly TextBox _probePath = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _verificationTimeout = Numeric(1, 60);
    private readonly NumericUpDown _verificationMaxResponse = Numeric(
        VerificationOptions.MinimumResponseLimitBytes,
        VerificationOptions.MaximumResponseLimitBytes);

    private readonly ComboBox _keepaliveMode = EnumCombo<L2tpKeepaliveMode>();
    private readonly TextBox _keepaliveCustomIPv4 = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _keepaliveInterval = Numeric(1, 3600);
    private readonly NumericUpDown _keepaliveTimeout = Numeric(100, 60000);
    private readonly NumericUpDown _keepaliveFailures = Numeric(1, 100);

    private readonly GroupBox _existingGroup = new() { Text = "Windows L2TP profile", Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _customGroup = new() { Text = "Custom ephemeral L2TP", Dock = DockStyle.Top, AutoSize = true };

    private readonly TextBox _serverAddress = new() { Dock = DockStyle.Fill };
    private readonly TextBox _userName = new() { Dock = DockStyle.Fill };
    private readonly TextBox _domain = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _useWindowsCredentials = new() { Text = "Использовать текущие Windows credentials", AutoSize = true };
    private readonly TextBox _password = SecretBox();
    private readonly ComboBox _ipsecAuth = EnumCombo<L2tpIpsecAuthentication>();
    private readonly TextBox _preSharedKey = SecretBox();
    private readonly ComboBox _encryption = EnumCombo<L2tpEncryptionMode>();
    private readonly CheckBox _allowPap = new() { Text = "PAP", AutoSize = true };
    private readonly CheckBox _allowChap = new() { Text = "CHAP", AutoSize = true };
    private readonly CheckBox _allowMsChapV2 = new() { Text = "MS-CHAPv2", AutoSize = true };

    private readonly Label _profileStatus = new() { AutoSize = true };

    public L2tpSettingsDialog(L2tpOptions? existing)
    {
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid().ToString("N");

        Text = existing is null ? "Новое L2TP соединение" : $"L2TP — {existing.Name}";
        Width = 760;
        Height = 820;
        MinimumSize = new Size(680, 640);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            AutoScroll = true
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        AddRow(root, row++, "Имя:", _name);
        root.Controls.Add(_shared, 1, row++);
        AddRow(root, row++, "Режим:", _mode);

        BuildExistingGroup();
        root.Controls.Add(_existingGroup, 0, row);
        root.SetColumnSpan(_existingGroup, 2);
        row++;

        BuildCustomGroup();
        root.Controls.Add(_customGroup, 0, row);
        root.SetColumnSpan(_customGroup, 2);
        row++;

        var monitoring = BuildMonitoringGroup();
        root.Controls.Add(monitoring, 0, row);
        root.SetColumnSpan(monitoring, 2);
        row++;

        var verification = BuildVerificationGroup();
        root.Controls.Add(verification, 0, row);
        root.SetColumnSpan(verification, 2);
        row++;

        var keepalive = BuildKeepaliveGroup();
        root.Controls.Add(keepalive, 0, row);
        root.SetColumnSpan(keepalive, 2);
        row++;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 0)
        };
        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            try
            {
                ValidateEditor();

                // Materialize the accepted immutable options exactly once. In
                // CustomEphemeral mode this is also the one DPAPI protection boundary
                // for newly entered password/PSK values; MainForm later reads the
                // cached object rather than protecting the same plaintext again.
                _acceptedResult = BuildResult();
                ClearPlaintextSecrets();
            }
            catch (Exception ex)
            {
                DialogResult = DialogResult.None;
                MessageBox.Show(this, ex.Message, "Проверка настроек", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, row);
        root.SetColumnSpan(buttons, 2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        _mode.SelectedIndexChanged += (_, _) => RefreshModeVisibility();
        _keepaliveMode.SelectedIndexChanged += (_, _) => RefreshKeepaliveVisibility();
        _ipsecAuth.SelectedIndexChanged += (_, _) => RefreshCustomAuthVisibility();
        _useWindowsCredentials.CheckedChanged += (_, _) => RefreshCredentialVisibility();
        _refreshProfiles.Click += (_, _) => StartWindowsProfileLoad();
        Shown += (_, _) => StartWindowsProfileLoad();
        FormClosed += (_, _) =>
        {
            CancelProfileLoad();
            ClearPlaintextSecrets();
        };

        LoadExisting(existing);
        RefreshModeVisibility();
        RefreshKeepaliveVisibility();
        RefreshCustomAuthVisibility();
        RefreshCredentialVisibility();
    }

    public L2tpOptions Result => _acceptedResult ?? BuildResult();

    internal async Task StopBackgroundOperationsAsync()
    {
        Interlocked.Exchange(ref _profileLoadStopping, 1);
        CancelProfileLoad();

        // StartWindowsProfileLoad stores the exact task synchronously before control
        // returns to the WinForms message loop. Awaiting the current task therefore
        // drains the helper process/redirected streams owned by this dialog before
        // MainForm lets the enclosing configuration generation complete.
        var task = Volatile.Read(ref _profileLoadTask);
        await task.ConfigureAwait(true);
    }

    private L2tpOptions BuildResult()
    {
        var mode = SelectedEnum<L2tpConnectionMode>(_mode);
        var ipsecAuth = SelectedEnum<L2tpIpsecAuthentication>(_ipsecAuth);
        var useWindowsCredentials = _useWindowsCredentials.Checked;
        var existingProtectedPassword = _existing?.Custom.ProtectedPassword ?? string.Empty;
        var existingProtectedPsk = _existing?.Custom.ProtectedPreSharedKey ?? string.Empty;
        var protectedPassword = ResolveProtectedSecret(
            credentialRequired: mode == L2tpConnectionMode.CustomEphemeral && !useWindowsCredentials,
            enteredPlaintext: _password.Text,
            existingProtected: existingProtectedPassword);
        var protectedPsk = ResolveProtectedSecret(
            credentialRequired: mode == L2tpConnectionMode.CustomEphemeral &&
                                ipsecAuth == L2tpIpsecAuthentication.PreSharedKey,
            enteredPlaintext: _preSharedKey.Text,
            existingProtected: existingProtectedPsk);

        var entryName = mode == L2tpConnectionMode.ExistingWindowsProfile
            ? (_windowsProfile.SelectedItem as ProfileItem)?.Profile.Name ?? string.Empty
            : $"ProxyToAnyConnect-{_id}";

        return new L2tpOptions
        {
            Id = _id,
            Name = _name.Text.Trim(),
            Shared = _shared.Checked,
            Mode = mode,
            EntryName = entryName,
            MonitorIntervalMilliseconds = decimal.ToInt32(_monitorInterval.Value),
            RouteMonitorIntervalMilliseconds = decimal.ToInt32(_routeMonitorInterval.Value),
            ReconnectCooldownMilliseconds = decimal.ToInt32(_reconnectCooldown.Value),
            Verification = new VerificationOptions
            {
                PublicAddress = _publicAddress.Text.Trim(),
                ProbeHost = _probeHost.Text.Trim(),
                ProbePort = decimal.ToInt32(_probePort.Value),
                ProbePath = _probePath.Text.Trim(),
                TimeoutSeconds = decimal.ToInt32(_verificationTimeout.Value),
                MaxResponseBytes = decimal.ToInt32(_verificationMaxResponse.Value)
            },
            Keepalive = new KeepaliveOptions
            {
                Mode = SelectedEnum<L2tpKeepaliveMode>(_keepaliveMode),
                CustomIPv4 = _keepaliveCustomIPv4.Text.Trim(),
                IntervalSeconds = decimal.ToInt32(_keepaliveInterval.Value),
                TimeoutMilliseconds = decimal.ToInt32(_keepaliveTimeout.Value),
                FailureThreshold = decimal.ToInt32(_keepaliveFailures.Value)
            },
            Custom = new CustomL2tpOptions
            {
                ServerAddress = _serverAddress.Text.Trim(),
                UserName = _userName.Text.Trim(),
                Domain = _domain.Text.Trim(),
                UseCurrentWindowsCredentials = useWindowsCredentials,
                ProtectedPassword = protectedPassword,
                IpsecAuthentication = ipsecAuth,
                ProtectedPreSharedKey = protectedPsk,
                Encryption = SelectedEnum<L2tpEncryptionMode>(_encryption),
                AllowPap = _allowPap.Checked,
                AllowChap = _allowChap.Checked,
                AllowMsChapV2 = _allowMsChapV2.Checked
            }
        };
    }

    internal static string ResolveProtectedSecret(
        bool credentialRequired,
        string enteredPlaintext,
        string existingProtected)
    {
        if (!credentialRequired)
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(enteredPlaintext)
            ? existingProtected
            : WindowsSecretProtector.Protect(enteredPlaintext);
    }

    internal static int ClampNumericValue(int value, int minimum, int maximum) =>
        Math.Clamp(value, minimum, maximum);

    private void ClearPlaintextSecrets()
    {
        // TextBox.Text values are immutable strings, so they cannot be zeroed in
        // place. Clearing both controls immediately after the one DPAPI handoff (and
        // again on every close path) releases the UI's long-lived references instead
        // of retaining plaintext until MainForm disposes the dialog.
        _password.Clear();
        _preSharedKey.Clear();
    }

    private static void SetNumericValue(NumericUpDown control, int value)
    {
        control.Value = ClampNumericValue(
            value,
            decimal.ToInt32(control.Minimum),
            decimal.ToInt32(control.Maximum));
    }

    private void BuildExistingGroup()
    {
        var layout = InnerLayout();
        var profilePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        profilePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        profilePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        profilePanel.Controls.Add(_windowsProfile, 0, 0);
        profilePanel.Controls.Add(_refreshProfiles, 1, 0);
        AddRow(layout, 0, "Профиль:", profilePanel);
        layout.Controls.Add(_profileStatus, 1, 1);
        _existingGroup.Controls.Add(layout);
    }

    private void BuildCustomGroup()
    {
        var layout = InnerLayout();
        AddRow(layout, 0, "L2TP server:", _serverAddress);
        AddRow(layout, 1, "Пользователь:", _userName);
        AddRow(layout, 2, "Домен:", _domain);
        layout.Controls.Add(_useWindowsCredentials, 1, 3);
        AddRow(layout, 4, "Пароль:", _password);
        layout.Controls.Add(new Label
        {
            Text = "Пустое поле сохраняет ранее заданный пароль, пока выбран этот способ входа.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        }, 1, 5);
        AddRow(layout, 6, "IPsec auth:", _ipsecAuth);
        AddRow(layout, 7, "Pre-shared key:", _preSharedKey);
        layout.Controls.Add(new Label
        {
            Text = "Пустое поле сохраняет ранее заданный PSK только в режиме PreSharedKey.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        }, 1, 8);
        AddRow(layout, 9, "Encryption:", _encryption);

        var authProtocols = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        authProtocols.Controls.Add(_allowPap);
        authProtocols.Controls.Add(_allowChap);
        authProtocols.Controls.Add(_allowMsChapV2);
        AddRow(layout, 10, "PPP auth:", authProtocols);
        _customGroup.Controls.Add(layout);
    }

    private GroupBox BuildMonitoringGroup()
    {
        var group = new GroupBox { Text = "Monitoring / reconnect", Dock = DockStyle.Top, AutoSize = true };
        var layout = InnerLayout();
        AddRow(layout, 0, "RAS monitor, ms:", _monitorInterval);
        AddRow(layout, 1, "Route monitor, ms:", _routeMonitorInterval);
        AddRow(layout, 2, "Reconnect cooldown, ms:", _reconnectCooldown);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildVerificationGroup()
    {
        var group = new GroupBox { Text = "Verification before Ready", Dock = DockStyle.Top, AutoSize = true };
        var layout = InnerLayout();
        AddRow(layout, 0, "Public IP / DNS:", _publicAddress);
        AddRow(layout, 1, "Probe host:", _probeHost);
        AddRow(layout, 2, "Probe port:", _probePort);
        AddRow(layout, 3, "Probe path:", _probePath);
        AddRow(layout, 4, "Timeout, sec:", _verificationTimeout);
        AddRow(layout, 5, "Max response, bytes:", _verificationMaxResponse);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox BuildKeepaliveGroup()
    {
        var group = new GroupBox { Text = "Keepalive", Dock = DockStyle.Top, AutoSize = true };
        var layout = InnerLayout();
        AddRow(layout, 0, "Режим:", _keepaliveMode);
        AddRow(layout, 1, "Custom IPv4:", _keepaliveCustomIPv4);
        AddRow(layout, 2, "Период, sec:", _keepaliveInterval);
        AddRow(layout, 3, "Timeout, ms:", _keepaliveTimeout);
        AddRow(layout, 4, "Ошибок до reconnect:", _keepaliveFailures);
        group.Controls.Add(layout);
        return group;
    }

    private void LoadExisting(L2tpOptions? existing)
    {
        _name.Text = existing?.Name ?? "L2TP";
        _shared.Checked = existing?.Shared ?? false;
        SelectEnum(_mode, existing?.Mode ?? L2tpConnectionMode.ExistingWindowsProfile);

        SetNumericValue(_monitorInterval, existing?.MonitorIntervalMilliseconds ?? 1000);
        SetNumericValue(_routeMonitorInterval, existing?.RouteMonitorIntervalMilliseconds ?? 5000);
        SetNumericValue(_reconnectCooldown, existing?.ReconnectCooldownMilliseconds ?? 5000);

        _publicAddress.Text = existing?.Verification.PublicAddress ?? string.Empty;
        _probeHost.Text = existing?.Verification.ProbeHost ?? "api.ipify.org";
        SetNumericValue(_probePort, existing?.Verification.ProbePort ?? 443);
        _probePath.Text = existing?.Verification.ProbePath ?? "/";
        SetNumericValue(_verificationTimeout, existing?.Verification.TimeoutSeconds ?? 10);
        SetNumericValue(
            _verificationMaxResponse,
            existing?.Verification.MaxResponseBytes ?? VerificationOptions.DefaultResponseLimitBytes);

        SelectEnum(_keepaliveMode, existing?.Keepalive.Mode ?? L2tpKeepaliveMode.Off);
        _keepaliveCustomIPv4.Text = existing?.Keepalive.CustomIPv4 ?? string.Empty;
        SetNumericValue(_keepaliveInterval, existing?.Keepalive.IntervalSeconds ?? 10);
        SetNumericValue(_keepaliveTimeout, existing?.Keepalive.TimeoutMilliseconds ?? 2000);
        SetNumericValue(_keepaliveFailures, existing?.Keepalive.FailureThreshold ?? 3);

        _serverAddress.Text = existing?.Custom.ServerAddress ?? string.Empty;
        _userName.Text = existing?.Custom.UserName ?? string.Empty;
        _domain.Text = existing?.Custom.Domain ?? string.Empty;
        _useWindowsCredentials.Checked = existing?.Custom.UseCurrentWindowsCredentials ?? false;
        SelectEnum(_ipsecAuth, existing?.Custom.IpsecAuthentication ?? L2tpIpsecAuthentication.PreSharedKey);
        SelectEnum(_encryption, existing?.Custom.Encryption ?? L2tpEncryptionMode.Required);
        _allowPap.Checked = existing?.Custom.AllowPap ?? false;
        _allowChap.Checked = existing?.Custom.AllowChap ?? false;
        _allowMsChapV2.Checked = existing?.Custom.AllowMsChapV2 ?? true;
    }

    private void StartWindowsProfileLoad()
    {
        if (Volatile.Read(ref _profileLoadStopping) != 0)
        {
            return;
        }

        // LoadWindowsProfilesAsync disables the refresh button before its first
        // asynchronous yield, so WinForms cannot admit overlapping refresh clicks.
        // Store its exact task immediately so close/Exit can cancel and drain it.
        var task = LoadWindowsProfilesAsync();
        Volatile.Write(ref _profileLoadTask, task);
    }

    private async Task LoadWindowsProfilesAsync()
    {
        if (Volatile.Read(ref _profileLoadStopping) != 0 ||
            SelectedEnum<L2tpConnectionMode>(_mode) != L2tpConnectionMode.ExistingWindowsProfile ||
            IsDisposed || Disposing)
        {
            return;
        }

        CancelProfileLoad();
        var cancellation = new CancellationTokenSource();
        _profileLoadCancellation = cancellation;
        _refreshProfiles.Enabled = false;
        _profileStatus.Text = "Чтение Windows VPN profiles...";

        try
        {
            var profiles = await _profileInspector.ListL2tpProfilesAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!OwnsProfileLoad(cancellation))
            {
                return;
            }

            var selectedName = (_windowsProfile.SelectedItem as ProfileItem)?.Profile.Name ?? _existing?.EntryName;
            _windowsProfile.Items.Clear();
            foreach (var profile in profiles)
            {
                _windowsProfile.Items.Add(new ProfileItem(profile));
            }

            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                for (var index = 0; index < _windowsProfile.Items.Count; index++)
                {
                    if (_windowsProfile.Items[index] is ProfileItem item &&
                        item.Profile.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        _windowsProfile.SelectedIndex = index;
                        break;
                    }
                }
            }

            if (_windowsProfile.SelectedIndex < 0 && _windowsProfile.Items.Count > 0)
            {
                _windowsProfile.SelectedIndex = 0;
            }

            _profileStatus.Text = profiles.Count == 0
                ? "L2TP profiles не найдены."
                : $"Найдено L2TP profiles: {profiles.Count}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the dialog, switching mode or application Exit owns
            // cancellation. WindowsVpnProfileInspector drains its process tree before
            // propagating cancellation, and StopBackgroundOperationsAsync awaits this
            // exact task before the dialog's configuration generation may finish.
        }
        catch (Exception ex)
        {
            if (OwnsProfileLoad(cancellation))
            {
                _profileStatus.Text = $"Ошибка: {ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_profileLoadCancellation, cancellation))
            {
                _profileLoadCancellation = null;
                if (!IsDisposed &&
                    !Disposing &&
                    Volatile.Read(ref _profileLoadStopping) == 0)
                {
                    _refreshProfiles.Enabled = true;
                }
            }

            cancellation.Dispose();
        }
    }

    private bool OwnsProfileLoad(CancellationTokenSource cancellation) =>
        ReferenceEquals(_profileLoadCancellation, cancellation) &&
        !cancellation.IsCancellationRequested &&
        Volatile.Read(ref _profileLoadStopping) == 0 &&
        !IsDisposed &&
        !Disposing &&
        SelectedEnum<L2tpConnectionMode>(_mode) == L2tpConnectionMode.ExistingWindowsProfile;

    private void CancelProfileLoad()
    {
        var cancellation = _profileLoadCancellation;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
        {
            cancellation.Cancel();
        }
    }

    private void RefreshModeVisibility()
    {
        var existingMode = SelectedEnum<L2tpConnectionMode>(_mode) == L2tpConnectionMode.ExistingWindowsProfile;
        _existingGroup.Visible = existingMode;
        _customGroup.Visible = !existingMode;
        if (!existingMode)
        {
            CancelProfileLoad();
        }
    }

    private void RefreshKeepaliveVisibility()
    {
        _keepaliveCustomIPv4.Enabled = SelectedEnum<L2tpKeepaliveMode>(_keepaliveMode) == L2tpKeepaliveMode.CustomIPv4;
    }

    private void RefreshCustomAuthVisibility()
    {
        _preSharedKey.Enabled = SelectedEnum<L2tpIpsecAuthentication>(_ipsecAuth) == L2tpIpsecAuthentication.PreSharedKey;
    }

    private void RefreshCredentialVisibility()
    {
        var enabled = !_useWindowsCredentials.Checked;
        _userName.Enabled = enabled;
        _domain.Enabled = enabled;
        _password.Enabled = enabled;
    }

    private void ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            throw new InvalidOperationException("Укажите имя L2TP соединения.");
        }

        var mode = SelectedEnum<L2tpConnectionMode>(_mode);
        if (mode == L2tpConnectionMode.ExistingWindowsProfile && _windowsProfile.SelectedItem is null)
        {
            throw new InvalidOperationException("Выберите существующий Windows L2TP profile.");
        }

        if (mode == L2tpConnectionMode.CustomEphemeral)
        {
            var serverAddress = _serverAddress.Text.Trim();
            if (string.IsNullOrWhiteSpace(serverAddress) ||
                (!IPAddress.TryParse(serverAddress, out _) && Uri.CheckHostName(serverAddress) != UriHostNameType.Dns))
            {
                throw new InvalidOperationException("Адрес custom L2TP сервера должен быть IP адресом или DNS именем.");
            }

            if (!_useWindowsCredentials.Checked && string.IsNullOrWhiteSpace(_userName.Text))
            {
                throw new InvalidOperationException("Укажите имя пользователя custom L2TP.");
            }

            if (!_useWindowsCredentials.Checked &&
                string.IsNullOrEmpty(_password.Text) &&
                string.IsNullOrWhiteSpace(_existing?.Custom.ProtectedPassword))
            {
                throw new InvalidOperationException("Укажите пароль custom L2TP.");
            }

            if (SelectedEnum<L2tpIpsecAuthentication>(_ipsecAuth) == L2tpIpsecAuthentication.PreSharedKey &&
                string.IsNullOrEmpty(_preSharedKey.Text) &&
                string.IsNullOrWhiteSpace(_existing?.Custom.ProtectedPreSharedKey))
            {
                throw new InvalidOperationException("Укажите IPsec pre-shared key.");
            }

            if (!_allowPap.Checked && !_allowChap.Checked && !_allowMsChapV2.Checked)
            {
                throw new InvalidOperationException("Включите хотя бы один PPP authentication protocol.");
            }
        }

        var publicAddress = _publicAddress.Text.Trim();
        if (string.IsNullOrWhiteSpace(publicAddress) ||
            (IPAddress.TryParse(publicAddress, out var publicIp)
                ? publicIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                : Uri.CheckHostName(publicAddress) != UriHostNameType.Dns))
        {
            throw new InvalidOperationException("Public address для verification должен быть IPv4 адресом или DNS именем.");
        }

        if (Uri.CheckHostName(_probeHost.Text.Trim()) != UriHostNameType.Dns)
        {
            throw new InvalidOperationException("Probe host для verification должен быть DNS именем.");
        }

        if (string.IsNullOrWhiteSpace(_probePath.Text) ||
            !_probePath.Text.Trim().StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Probe path для verification должен начинаться с '/'.");
        }

        if (SelectedEnum<L2tpKeepaliveMode>(_keepaliveMode) == L2tpKeepaliveMode.CustomIPv4 &&
            (!IPAddress.TryParse(_keepaliveCustomIPv4.Text.Trim(), out var keepaliveIp) ||
             keepaliveIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
        {
            throw new InvalidOperationException("Keepalive CustomIPv4 должен быть корректным IPv4 адресом.");
        }
    }

    private static TableLayoutPanel InnerLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(8),
            ColumnCount = 2
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

    private static TextBox SecretBox() => new()
    {
        Dock = DockStyle.Fill,
        UseSystemPasswordChar = true
    };

    private static ComboBox EnumCombo<TEnum>() where TEnum : struct, Enum
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        foreach (var value in Enum.GetValues<TEnum>())
        {
            combo.Items.Add(value);
        }
        combo.SelectedIndex = 0;
        return combo;
    }

    private static TEnum SelectedEnum<TEnum>(ComboBox combo) where TEnum : struct, Enum =>
        combo.SelectedItem is TEnum value ? value : default;

    private static void SelectEnum<TEnum>(ComboBox combo, TEnum value)
        where TEnum : struct, Enum
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is TEnum candidate && EqualityComparer<TEnum>.Default.Equals(candidate, value))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
    }

    private sealed record ProfileItem(VpnProfileInfo Profile)
    {
        public override string ToString() => Profile.DisplayName;
    }
}
