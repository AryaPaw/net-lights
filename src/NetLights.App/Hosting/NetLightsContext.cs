using System.Diagnostics;
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
    private readonly System.Windows.Forms.Timer _heartbeat;
    private readonly TaskbarRestartWindow _taskbar;
    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _diagnosticsCts = new();
    private readonly ToolStripMenuItem _pauseItem;
    private DateTimeOffset _lastNetworkEvent = DateTimeOffset.MinValue;
    private MonitorSnapshot _snapshot;
    private bool _exiting;
    private bool _syncingToggles;
    private string? _updateNotice;

    public NetLightsContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = SettingsStore.Load();
        if (_settings.SettingsVersion < 1)
        {
            _settings.AutoStart = true;
            _settings.SettingsVersion = 1;
            SettingsStore.Save(_settings);
        }

        AutoStartStore.Set(_settings.AutoStart, Application.ExecutablePath);
        (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
        _probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, ProductInfo.Version);
        var kernel = new MonitorKernel(config, TimeProvider.System);
        _host = new MonitorHost(kernel, _probe, TimeProvider.System, () => _probe.RecycleConnections(), _ui);
        _snapshot = kernel.Snapshot;
        try
        {
            SettingsStore.DeleteLegacyStateHistory();
        }
        catch (Exception ex)
        {
            kernel.Log.Add(DateTimeOffset.UtcNow, "history", ex.Message);
        }

        if (new PendingUpdateStore().TryReadLastFailure(out string? updateError))
        {
            _updateNotice = updateError;
        }

        _host.SnapshotChanged += OnSnapshot;
        _status.AutoStartChanged = OnAutoStartFromWindow;
        _status.AutoUpdateChanged = OnAutoUpdateFromWindow;
        _status.DiagnoseRequested = DiagnoseAsync;
        _status.ExportRequested = Export;
        _status.CheckUpdatesRequested = () => _ = CheckUpdatesManualAsync();
        _status.BindSettings(AutoStartStore.IsEnabled(), _settings.AutoUpdateEnabled, _updateNotice);
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Открыть окно", null, (_, _) => ShowStatus());
        _pauseItem = new ToolStripMenuItem("Пауза")
        {
            CheckOnClick = true
        };
        _pauseItem.CheckedChanged += (_, _) =>
        {
            if (_syncingToggles)
            {
                return;
            }

            _host.SetPaused(_pauseItem.Checked);
        };
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());
        int iconSize = _renderer.SystemSmallIconSize();
        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = DiagnosticExport.FormatTooltip(_snapshot),
            Icon = _renderer.Get(_snapshot.Ru.Availability, _snapshot.Vpn.Availability, iconSize, _snapshot.Paused)
        };
        _icon.DoubleClick += (_, _) => ShowStatus();
        _heartbeat = new System.Windows.Forms.Timer { Interval = 1000 };
        _heartbeat.Tick += (_, _) =>
        {
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                _host.NotifyUnavailable(true);
            }

            if (!_snapshot.Paused)
            {
                CheckSnapshotAge();
            }
        };
        NetworkChange.NetworkAvailabilityChanged += OnNetwork;
        NetworkChange.NetworkAddressChanged += OnNetwork;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPower;
        _taskbar = new TaskbarRestartWindow(RestoreIcon);
        _heartbeat.Start();
        _host.Start();
        SilentUpdateRuntime.Start(
            () => _settings.AutoUpdateEnabled,
            ProductInfo.Version,
            Application.ExecutablePath,
            () => _ui.Post(_ => ExitThread(), null),
            _updateGate,
            _diagnosticsCts.Token);
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
        Icon icon = _renderer.Get(snapshot.Ru.Availability, snapshot.Vpn.Availability, iconSize, snapshot.Paused);
        if (!ReferenceEquals(_icon.Icon, icon))
        {
            _icon.Icon = icon;
        }

        string tip = DiagnosticExport.FormatTooltip(snapshot);
        if (_icon.Text != tip)
        {
            _icon.Text = tip;
        }

        if (_pauseItem.Checked != snapshot.Paused)
        {
            _syncingToggles = true;
            _pauseItem.Checked = snapshot.Paused;
            _syncingToggles = false;
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
            _icon.Text = $"{GroupLabels.Provider}: нет свежих данных | {GroupLabels.Vpn}: нет свежих данных";
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
        _status.BindSettings(AutoStartStore.IsEnabled(), _settings.AutoUpdateEnabled, _updateNotice);
        _status.Reveal();
    }

    private void OnAutoStartFromWindow(bool enabled)
    {
        AutoStartStore.Set(enabled, Application.ExecutablePath);
        _settings.AutoStart = enabled;
        SettingsStore.Save(_settings);
    }

    private void OnAutoUpdateFromWindow(bool enabled)
    {
        _settings.AutoUpdateEnabled = enabled;
        SettingsStore.Save(_settings);
    }

    private async Task<string> DiagnoseAsync(string endpointId)
    {
        EndpointView? view = _snapshot.Ru.Endpoints.Concat(_snapshot.Vpn.Endpoints)
            .FirstOrDefault(e => string.Equals(e.Id, endpointId, StringComparison.Ordinal));
        if (view is null)
        {
            return "Узел не найден.";
        }

        EndpointDefinition endpoint = new(view.Id, view.Group, view.Uri, view.InfrastructureId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_diagnosticsCts.Token);
        ManualDiagnosticResult result = await ManualDiagnostics.RunAsync(
            _probe,
            endpoint,
            TimeProvider.System,
            MonitorConstants.ManualDiagnosticsDeadline,
            linked.Token).ConfigureAwait(true);
        string icmp = result.Icmp is null ? "нет" : result.Icmp.Status.ToString();
        string tcp = result.Tcp is null ? "нет" : result.Tcp.Value ? "есть" : "нет";
        string https = $"{result.Https.Outcome} HTTP {result.Https.HttpStatus?.ToString() ?? "—"}";
        return $"{view.Id} ({view.Uri.Host})\r\n{result.Note}\r\nHTTPS: {https}\r\nICMP: {icmp}\r\nTCP 443: {tcp}\r\nIP: {result.Address}";
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

    private async Task CheckUpdatesManualAsync()
    {
        if (!await _updateGate.WaitAsync(0).ConfigureAwait(true))
        {
            _status.SetManualUpdateState(false, ManualUpdateCopy.AlreadyRunning);
            return;
        }

        bool exitRequested = false;
        SilentUpdateOutcome outcome = SilentUpdateOutcome.Failed;
        try
        {
            _status.SetManualUpdateState(true, ManualUpdateCopy.Checking);
            string? applicationDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrWhiteSpace(applicationDirectory))
            {
                outcome = SilentUpdateOutcome.Failed;
                return;
            }

            string downloadDirectory = Path.Combine(
                Path.GetTempPath(),
                "NetLights",
                "updates",
                Guid.NewGuid().ToString("N"));
            using GitHubReleaseFeed probe = new(timeout: NetworkWaitPolicy.ProbeTimeout, githubApi: false);
            using GitHubReleaseFeed feed = new();
            outcome = await SilentUpdateCoordinator.RunOnce(new SilentUpdateContext(
                true,
                ProductInfo.Version,
                Process.GetCurrentProcess().ProcessName,
                applicationDirectory,
                downloadDirectory,
                RuntimeInformation.ProcessArchitecture,
                probe,
                feed,
                new CmdSilentSetupInstaller(),
                () => exitRequested = true,
                _diagnosticsCts.Token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "update", ex.Message);
            outcome = SilentUpdateOutcome.Failed;
        }
        finally
        {
            _updateGate.Release();
        }

        _status.SetManualUpdateState(false, ManualUpdateCopy.For(outcome));
        if (exitRequested)
        {
            _ui.Post(_ => ExitThread(), null);
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

        _probe.Dispose();
        _diagnosticsCts.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
        _status.Dispose();
        _updateGate.Dispose();
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
