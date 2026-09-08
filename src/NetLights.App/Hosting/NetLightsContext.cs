using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using NetLights.Core;
using NetLights.Networking;
using NetLights.Updates;

namespace NetLights.App;

internal sealed class NetLightsContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly TrayIconRenderer _renderer = new();
    private readonly StatusForm _status = new();
    private readonly AppSettings _settings;
    private readonly MonitorHost _host;
    private readonly HttpsProbe _probe;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _autoUpdateItem;
    private readonly System.Windows.Forms.Timer _heartbeat;
    private readonly TaskbarRestartWindow _taskbar;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _diagnosticsCts = new();
    private readonly UpdateCoordinator _updates;
    private DateTimeOffset _lastNetworkEvent = DateTimeOffset.MinValue;
    private MonitorSnapshot _snapshot;
    private GroupAvailability _historyRu;
    private GroupAvailability _historyVpn;
    private bool _exiting;
    private string? _updateNotice;

    public NetLightsContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
        (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
        _probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, ProductInfo.Version);
        var kernel = new MonitorKernel(config, TimeProvider.System);
        _host = new MonitorHost(kernel, _probe, TimeProvider.System, () => _probe.RecycleConnections(), _ui);
        _snapshot = kernel.Snapshot;
        _historyRu = _snapshot.Ru.Availability;
        _historyVpn = _snapshot.Vpn.Availability;
        _updates = new UpdateCoordinator(new GitHubReleaseFeed(), new PendingUpdateStore(), ProductInfo.Version);
        try
        {
            StateHistoryStore.Prune();
        }
        catch (Exception ex)
        {
            kernel.Log.Add(DateTimeOffset.UtcNow, "history", ex.Message);
        }

        if (_updates.TryReadLastFailure(out string? updateError))
        {
            _updateNotice = updateError;
        }

        _host.SnapshotChanged += OnSnapshot;
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Состояние", null, (_, _) => ShowStatus());
        _menu.Items.Add("Проверить сейчас", null, (_, _) => _host.RequestCheckNow());
        _menu.Items.Add("Диагностика", null, (_, _) => _ = RunDiagnosticsAsync());
        _autoStartItem = new ToolStripMenuItem("Автозапуск");
        _autoStartItem.CheckOnClick = true;
        _autoStartItem.Checked = AutoStartStore.IsEnabled();
        _autoStartItem.CheckedChanged += (_, _) =>
        {
            AutoStartStore.Set(_autoStartItem.Checked, Application.ExecutablePath);
            _settings.AutoStart = _autoStartItem.Checked;
            SettingsStore.Save(_settings);
        };
        _menu.Items.Add(_autoStartItem);
        _autoUpdateItem = new ToolStripMenuItem("Автообновление");
        _autoUpdateItem.CheckOnClick = true;
        _autoUpdateItem.Checked = _settings.AutoUpdateEnabled;
        _autoUpdateItem.CheckedChanged += (_, _) =>
        {
            _settings.AutoUpdateEnabled = _autoUpdateItem.Checked;
            SettingsStore.Save(_settings);
        };
        _menu.Items.Add(_autoUpdateItem);
        _menu.Items.Add("Экспорт", null, (_, _) => Export());
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());
        int iconSize = _renderer.SystemSmallIconSize();
        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = DiagnosticExport.FormatTooltip(_snapshot),
            Icon = _renderer.Get(_snapshot.Ru.Availability, _snapshot.Vpn.Availability, iconSize)
        };
        _icon.DoubleClick += (_, _) => ShowStatus();
        _heartbeat = new System.Windows.Forms.Timer { Interval = 1000 };
        _heartbeat.Tick += (_, _) =>
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                _host.NotifyUnavailable(true);
            }

            CheckSnapshotAge();
            if (_settings.AutoUpdateEnabled)
            {
                _ = _updates.CheckInBackgroundAsync(CancellationToken.None);
            }
        };
        _heartbeat.Start();
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        NetworkChange.NetworkAddressChanged += OnNetwork;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
        _taskbar = new TaskbarRestartWindow(RestoreIcon);
        _host.Start();
        if (!string.IsNullOrEmpty(warning))
        {
            _icon.BalloonTipTitle = "Net Lights";
            _icon.BalloonTipText = warning;
            _icon.ShowBalloonTip(4000);
        }
        else if (!string.IsNullOrEmpty(_updateNotice))
        {
            _icon.BalloonTipTitle = "Net Lights";
            _icon.BalloonTipText = _updateNotice;
            _icon.ShowBalloonTip(4000);
        }
    }

    private void OnSnapshot(MonitorSnapshot snapshot) => ApplySnapshot(snapshot);

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        int iconSize = _renderer.SystemSmallIconSize();
        Icon icon = _renderer.Get(snapshot.Ru.Availability, snapshot.Vpn.Availability, iconSize);
        if (!ReferenceEquals(_icon.Icon, icon))
        {
            _icon.Icon = icon;
        }

        string tip = DiagnosticExport.FormatTooltip(snapshot);
        if (_icon.Text != tip)
        {
            _icon.Text = tip;
        }

        if (snapshot.Ru.Availability != _historyRu || snapshot.Vpn.Availability != _historyVpn)
        {
            _historyRu = snapshot.Ru.Availability;
            _historyVpn = snapshot.Vpn.Availability;
            try
            {
                StateHistoryStore.Append(snapshot.GeneratedUtc, snapshot.Ru.Availability, snapshot.Vpn.Availability);
            }
            catch (Exception ex)
            {
                _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "history", ex.Message);
            }
        }

        if (_status.Visible)
        {
            if (_status.NeedsBind(snapshot))
            {
                _status.Bind(snapshot);
            }
        }
    }

    private void CheckSnapshotAge()
    {
        TimeSpan age = TimeProvider.System.GetElapsedTime(_snapshot.GeneratedTimestamp);
        if (age > MonitorConstants.Freshness)
        {
            _icon.Text = "РФ: нет свежих данных | VPN: нет свежих данных";
        }
    }

    private void ShowStatus()
    {
        if (!_status.Visible)
        {
            _status.Size = new Size(Math.Max(_settings.WindowWidth, _status.MinimumSize.Width), Math.Max(_settings.WindowHeight, _status.MinimumSize.Height));
            _status.PlaceCentered();
        }

        _status.Bind(_snapshot);
        _status.Show();
        _status.Activate();
    }

    private async Task RunDiagnosticsAsync()
    {
        EndpointView? first = _snapshot.Ru.Endpoints.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        EndpointDefinition endpoint = new(first.Id, first.Group, first.Uri, first.InfrastructureId);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_diagnosticsCts.Token);
            ManualDiagnosticResult result = await ManualDiagnostics.RunAsync(
                _probe,
                endpoint,
                TimeProvider.System,
                MonitorConstants.ManualDiagnosticsDeadline,
                linked.Token).ConfigureAwait(true);
            MessageBox.Show(
                $"{result.Note}\nHTTPS: {result.Https.Outcome} {result.Https.HttpStatus}\nICMP: {result.Icmp?.Status}\nTCP: {result.Tcp}\nIP: {result.Address}",
                "Диагностика",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("Диагностика отменена.", "Диагностика", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "diag", ex.Message);
            MessageBox.Show("Не удалось выполнить диагностику: " + ex.Message, "Диагностика", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Export()
    {
        try
        {
            string path = DiagnosticExport.Export(_snapshot, _host.Kernel.Log, _snapshot.ConfigWarning);
            MessageBox.Show("Сохранено: " + path, "Экспорт", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "export", ex.Message);
            MessageBox.Show("Не удалось экспортировать: " + ex.Message, "Экспорт", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnNetwork(object? sender, EventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            _lastNetworkEvent = now;
            _host.NotifyUnavailable(true);
            return;
        }

        if (now - _lastNetworkEvent < MonitorConstants.NetworkDebounce)
        {
            return;
        }

        _lastNetworkEvent = now;
        _host.NotifyNetworkChange();
    }

    private void OnPower(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode is Microsoft.Win32.PowerModes.Suspend or Microsoft.Win32.PowerModes.Resume)
        {
            _host.NotifyNetworkChange();
        }
    }

    private void RestoreIcon()
    {
        _icon.Visible = false;
        _icon.Visible = true;
    }

    protected override void ExitThreadCore()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _heartbeat.Stop();
        _diagnosticsCts.Cancel();
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
        _settings.WindowX = _status.Location.X;
        _settings.WindowY = _status.Location.Y;
        _settings.WindowWidth = _status.Width;
        _settings.WindowHeight = _status.Height;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "settings", ex.Message);
        }

        try
        {
            _host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }

        try
        {
            if (_settings.AutoUpdateEnabled)
            {
                UpdateAgentLauncher.TryStart(_updates);
            }
        }
        catch (Exception ex)
        {
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "update", ex.Message);
        }

        _probe.Dispose();
        _diagnosticsCts.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
        _status.Dispose();
        _taskbar.DestroyHandle();
        base.ExitThreadCore();
    }

    private sealed class TaskbarRestartWindow : NativeWindow
    {
        private readonly Action _restore;
        private readonly uint _taskbarCreated;

        public TaskbarRestartWindow(Action restore)
        {
            _restore = restore;
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _taskbarCreated)
            {
                _restore();
            }

            base.WndProc(ref m);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);
    }
}
