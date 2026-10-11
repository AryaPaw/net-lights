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
    private StatusForm? _status;
    private readonly GeoCountryTrayHost _countryTray;
    private readonly LocationHistory _locations;
    private readonly AvailabilityHistory _availabilityHistory;
    private long _locationsSavedAtTicks;
    private long _availabilitySavedAtTicks;
    private long _locationRetryAfterTicks;
    private long _availabilityRetryAfterTicks;
    private int _availabilitySavePending;
    private int _locationSavePending;
    private int _locationSaveFailed;
    private int _availabilitySaveFailed;
    private readonly AppSettings _settings;
    private readonly MonitorHost _host;
    private readonly HttpsProbe _probe;
    private readonly System.Windows.Forms.Timer _heartbeat;
    private readonly TaskbarRestartWindow _taskbar;
    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly CancellationTokenSource _diagnosticsCts = new();
    private readonly int _uiThreadId;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly NetworkAvailabilityProcessor _network;
    private MonitorSnapshot _snapshot;
    private bool _exiting;
    private bool _shutdownStarted;
    private bool _shutdownReady;
    private bool _resourcesDisposed;
    private bool _syncingToggles;
    private bool _windowWasShown;
    private bool _locationGapOpen;
    private string? _updateNotice;
    private string? _persistenceWarning;
    private readonly string? _updateHealthEvent;
    private int _updateHealthSignaled;

    public NetLightsContext(string? updateHealthEvent = null)
    {
        _updateHealthEvent = updateHealthEvent;
        _uiThreadId = Environment.CurrentManagedThreadId;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        (AppSettings loadedSettings, SettingsLoadStatus loadStatus) = SettingsStore.LoadDetailed();
        _settings = loadedSettings;
        if (loadStatus is SettingsLoadStatus.Corrupt or SettingsLoadStatus.IoError)
            _persistenceWarning = "Настройки не читаются. Исходный файл сохранен; изменения настроек пока не записываются.";
        else if (loadStatus == SettingsLoadStatus.Recovered)
            _persistenceWarning = "Настройки восстановлены из резервной копии.";
        _locations = LocationHistoryStore.Load();
        _availabilityHistory = AvailabilityHistoryStore.Load();
        if (LocationHistoryStore.LastLoadStatus is HistoryLoadStatus.Corrupt or HistoryLoadStatus.Recovered)
        {
            RuntimeJournal.Write("locations-load-" + LocationHistoryStore.LastLoadStatus.ToString().ToLowerInvariant());
            _persistenceWarning = LocationHistoryStore.LastLoadStatus == HistoryLoadStatus.Corrupt
                ? "История стран повреждена; исходный файл сохранен."
                : "История стран восстановлена из резервной копии.";
        }
        if (AvailabilityHistoryStore.LastLoadStatus is HistoryLoadStatus.Corrupt or HistoryLoadStatus.Recovered)
        {
            RuntimeJournal.Write("availability-load-" + AvailabilityHistoryStore.LastLoadStatus.ToString().ToLowerInvariant());
            string availabilityWarning = AvailabilityHistoryStore.LastLoadStatus == HistoryLoadStatus.Corrupt
                ? "История доступности повреждена; исходный файл сохранен."
                : "История доступности восстановлена из резервной копии.";
            _persistenceWarning = _persistenceWarning is null
                ? availabilityWarning
                : _persistenceWarning + " " + availabilityWarning;
        }
        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && _settings.SettingsVersion < 2)
        {
            if (_settings.SettingsVersion < 1)
            {
                _settings.AutoStart = true;
            }

            _settings.GeoCountryIconEnabled = false;
            _settings.SettingsVersion = 2;
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && _settings.SettingsVersion < 3)
        {
            _settings.GeoCountryDetectionEnabled = _settings.GeoCountryIconEnabled;
            _settings.SettingsVersion = 3;
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SettingsStore.MigrateToSettingsVersion4(_settings))
        {
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SettingsStore.MigrateToSettingsVersion5(_settings))
        {
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SettingsStore.MigrateToSettingsVersion6(_settings))
        {
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SettingsStore.MigrateToSettingsVersion7(_settings))
        {
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SettingsStore.MigrateToSettingsVersion8(_settings))
        {
            TrySaveSettings();
        }

        if (_settings.GeoCountryIconEnabled && !_settings.GeoCountryDetectionEnabled)
        {
            _settings.GeoCountryIconEnabled = false;
            TrySaveSettings();
        }

        if (loadStatus is not SettingsLoadStatus.Corrupt and not SettingsLoadStatus.IoError
            && SilentUpdatePolicy.HasInnoUninstaller(Path.GetDirectoryName(Application.ExecutablePath) ?? "")
            && !AutoStartStore.HasForeignEntry(Application.ExecutablePath))
            AutoStartStore.Set(_settings.AutoStart, Application.ExecutablePath);
        (MonitorConfiguration config, string? warning) = SettingsStore.LoadPool();
        _probe = HttpsProbeFactory.CreateProduction(TimeProvider.System, ProductInfo.Version);
        var kernel = new MonitorKernel(config, TimeProvider.System);
        _host = new MonitorHost(kernel, _probe, TimeProvider.System, () => _probe.RecycleConnections(), _ui);
        _snapshot = kernel.Snapshot;
        if (new PendingUpdateStore().TryReadLastFailure(out string? updateError))
        {
            _updateNotice = updateError;
        }

        _host.SnapshotChanged += OnSnapshot;
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
        _menu.Items.Add("Выход", null, (_, _) =>
        {
            RuntimeJournal.Write("tray-exit-requested");
            BeginGracefulExit();
        });
        int iconSize = _renderer.SystemSmallIconSize();
        _icon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = DiagnosticExport.FormatTooltip(_snapshot),
            Icon = _renderer.Get(_snapshot.Ru.Availability, _snapshot.World.Availability, iconSize, _snapshot.Paused)
        };
        _icon.MouseClick += (_, e) =>
        {
            if (TrayIconGestures.ShouldOpenWindow(e.Button))
            {
                ShowStatus(0);
            }
        };
        _countryTray = new GeoCountryTrayHost(() => ShowStatus(1), ProductInfo.Version, SettingsStore.RootDirectory, TimeProvider.System, _ui);
        _countryTray.DisplayChanged = OnCountryDisplay;
        _network = new NetworkAvailabilityProcessor(
            _ui,
            _uiThreadId,
            MonitorConstants.NetworkDebounce,
            ApplyNetworkSignal,
            () => _exiting,
            ex => RuntimeJournal.Write("network-signal-failed", ex));
        _availabilityHistory.Observe(_snapshot, _snapshot.GeneratedUtc);
        _heartbeat = new System.Windows.Forms.Timer { Interval = 1000 };
        _heartbeat.Tick += (_, _) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _network.ObserveHeartbeat(NetworkInterface.GetIsNetworkAvailable(), now);
            long locationRevision = _locations.Revision;
            _locations.SplitIfStale(now);
            if (_locations.Revision != locationRevision)
            {
                TrySaveLocations();
                _status?.BindLocations(_locations, GeoCountryDisplay.Unconfirmed);
            }
            if (now.UtcTicks >= Interlocked.Read(ref _locationRetryAfterTicks)
                && SaveDue(_locationsSavedAtTicks, now)) TrySaveLocations();

            if (_snapshot.Paused)
                _availabilityHistory.Touch(now);
            else
                _availabilityHistory.SealStale(now);
            _availabilityHistory.Prune(now);
            if (now.UtcTicks >= Interlocked.Read(ref _availabilityRetryAfterTicks)
                && SaveDue(_availabilitySavedAtTicks, now)) QueueSaveAvailabilityHistory();

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
        _countryTray.SetLetterScale(GeoCountryLetterScales.Parse(_settings.GeoCountryLetterSize));
        _countryTray.SetDetectionEnabled(_settings.GeoCountryDetectionEnabled);
        _countryTray.SetTrayIconEnabled(_settings.GeoCountryIconEnabled);
        SilentUpdateRuntime.Start(
            () => _settings.AutoUpdateEnabled,
            ProductInfo.Version,
            Application.ExecutablePath,
            () => _ui.Post(_ => BeginGracefulExit(), null),
            _updateGate,
            _diagnosticsCts.Token);
        if (!string.IsNullOrEmpty(warning))
        {
            _icon.BalloonTipTitle = ProductInfo.Name;
            _icon.BalloonTipText = warning;
            _icon.ShowBalloonTip(4000);
        }
        else if (!string.IsNullOrEmpty(_updateNotice))
        {
            _icon.BalloonTipTitle = ProductInfo.Name;
            _icon.BalloonTipText = _updateNotice;
            _icon.ShowBalloonTip(4000);
        }
    }

    private void OnSnapshot(MonitorSnapshot snapshot)
    {
        if (_exiting)
            return;

        ApplySnapshot(snapshot);
        bool probed = snapshot.Ru.Endpoints.Concat(snapshot.World.Endpoints)
            .Any(endpoint => endpoint.LastCompletedUtc is not null);
        if (probed && _icon.Visible && Interlocked.Exchange(ref _updateHealthSignaled, 1) == 0)
        {
            RuntimeJournal.Write("monitor-ready");
            UpdateHealthSignal.Signal(_updateHealthEvent);
        }
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        if (_availabilityHistory.Observe(snapshot, snapshot.GeneratedUtc))
        {
            QueueSaveAvailabilityHistory();
        }

        int iconSize = _renderer.SystemSmallIconSize();
        Icon icon = _renderer.Get(snapshot.Ru.Availability, snapshot.World.Availability, iconSize, snapshot.Paused);
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

        if (_status is { Visible: true } status)
        {
            status.BindAvailabilityHistory(_availabilityHistory, LocationChartWindows.ParseHours(_settings.LocationChartWindowHours));
            if (status.NeedsBind(snapshot))
            {
                status.Bind(snapshot);
            }
        }
    }

    private void CheckSnapshotAge()
    {
        TimeSpan age = TimeProvider.System.GetElapsedTime(_snapshot.GeneratedTimestamp);
        if (age > MonitorConstants.Freshness)
        {
            int iconSize = _renderer.SystemSmallIconSize();
            Icon stale = _renderer.Get(GroupAvailability.Unknown, GroupAvailability.Unknown, iconSize, false);
            if (!ReferenceEquals(_icon.Icon, stale))
            {
                _icon.Icon = stale;
            }

            _icon.Text = $"{GroupLabels.Provider}: нет свежих данных | {GroupLabels.World}: нет свежих данных";
            if (_status is { Visible: true } status)
            {
                status.Bind(_snapshot with
                {
                    MonitorError = "монитор не отвечает",
                    Ru = _snapshot.Ru with { Availability = GroupAvailability.Unknown },
                    World = _snapshot.World with { Availability = GroupAvailability.Unknown }
                });
            }
        }
    }

    private void ShowStatus(int page = 0)
    {
        StatusForm status = _status ??= CreateStatusForm();
        if (!status.Visible)
        {
            status.RestoreWindowBounds(new Rectangle(
                _settings.WindowX,
                _settings.WindowY,
                _settings.WindowWidth,
                _settings.WindowHeight));
        }

        status.Bind(_snapshot);
        status.BindSettings(
            AutoStartStore.IsEnabled(Application.ExecutablePath),
            _settings.AutoUpdateEnabled,
            _settings.GeoCountryIconEnabled,
            GeoCountryLetterScales.Parse(_settings.GeoCountryLetterSize),
            _updateNotice,
            _settings.LocationChartWindowHours,
            _settings.GeoCountryDetectionEnabled,
            _settings.EmphasizeShortStatuses,
            _settings.ShowLocationDebugSources);
        status.BindLocations(
            _locations,
            _settings.GeoCountryDetectionEnabled
                ? _locations.Current is null ? GeoCountryDisplay.Unconfirmed : _countryTray.Current
                : GeoCountryDisplay.Disabled);
        status.BindAvailabilityHistory(_availabilityHistory, LocationChartWindows.ParseHours(_settings.LocationChartWindowHours));
        status.SetPersistenceWarning(_persistenceWarning);
        status.ShowPageAndReveal(page);
        _windowWasShown = true;
    }

    private StatusForm CreateStatusForm()
    {
        var status = new StatusForm
        {
            AutoStartChanged = OnAutoStartFromWindow,
            AutoUpdateChanged = OnAutoUpdateFromWindow,
            GeoCountryIconChanged = OnGeoCountryIconFromWindow,
            GeoCountryDetectionChanged = OnGeoCountryDetectionFromWindow,
            EmphasizeShortStatusesChanged = OnEmphasizeShortStatusesFromWindow,
            LocationDebugSourcesChanged = OnLocationDebugSourcesFromWindow,
            GeoCountryLetterScaleChanged = OnGeoCountryLetterScaleFromWindow,
            LocationChartWindowHoursChanged = OnLocationChartWindowFromWindow,
            DiagnoseRequested = DiagnoseAsync,
            ExportRequested = Export,
            CheckUpdatesRequested = () => _ = CheckUpdatesManualAsync(),
            OpenSettingsFolderRequested = OpenSettingsFolder,
            ResetWindowSizeRequested = ResetWindowSize,
            WindowGeometryChanged = SaveWindowGeometry,
            HiddenToTray = () => _ui.Post(_ => DisposeHiddenStatusForm(), null)
        };
        return status;
    }

    private void DisposeHiddenStatusForm()
    {
        if (_status is not { Visible: false } status || _exiting) return;
        SaveWindowGeometry();
        _status = null;
        status.Dispose();
    }

    internal void ShowLocalWindowOnStart() => ShowStatus(1);

    private void OnAutoStartFromWindow(bool enabled)
    {
        if (!SilentUpdatePolicy.HasInnoUninstaller(Path.GetDirectoryName(Application.ExecutablePath) ?? ""))
        {
            RuntimeJournal.Write("autostart-portable-ignored");
            MessageBox.Show("Автозапуск можно изменить в установленной версии Net Lights.",
                ProductInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _status?.BindSettings(AutoStartStore.IsEnabled(Application.ExecutablePath), _settings.AutoUpdateEnabled,
                _settings.GeoCountryIconEnabled, GeoCountryLetterScales.Parse(_settings.GeoCountryLetterSize),
                _updateNotice, _settings.LocationChartWindowHours, _settings.GeoCountryDetectionEnabled,
                _settings.EmphasizeShortStatuses, _settings.ShowLocationDebugSources);
            return;
        }
        AutoStartStore.Set(enabled, Application.ExecutablePath);
        _settings.AutoStart = enabled;
        TrySaveSettings();
    }

    private void OnAutoUpdateFromWindow(bool enabled)
    {
        _settings.AutoUpdateEnabled = enabled;
        TrySaveSettings();
    }

    private void TrySaveLocations()
    {
        if (Interlocked.CompareExchange(ref _locationSavePending, 1, 0) != 0)
            return;
        _ = SaveLocationHistoryAsync(LocationHistoryStore.Snapshot(_locations));
    }

    private static bool SaveDue(long savedAtTicks, DateTimeOffset now)
    {
        long ticks = Interlocked.Read(ref savedAtTicks);
        return ticks == 0 || now.UtcTicks - ticks >= TimeSpan.FromMinutes(1).Ticks;
    }

    private async Task SaveLocationHistoryAsync(LocationHistorySnapshot snapshot)
    {
        try
        {
            await LocationHistoryStore.SaveAsync(snapshot).ConfigureAwait(false);
            Interlocked.Exchange(ref _locationsSavedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref _locationRetryAfterTicks, 0);
            Interlocked.Exchange(ref _locationSaveFailed, 0);
            ClearSaveWarningWhenRecovered();
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("location-save-failed", ex);
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "locations", ex.Message);
            Interlocked.Exchange(ref _locationRetryAfterTicks, DateTimeOffset.UtcNow.AddSeconds(10).UtcTicks);
            NotifySaveFailure(ref _locationSaveFailed, "Не удалось сохранить историю стран. Программа повторит попытку.");
        }
        finally
        {
            Interlocked.Exchange(ref _locationSavePending, 0);
        }
    }

    private void QueueSaveAvailabilityHistory()
    {
        if (Interlocked.CompareExchange(ref _availabilitySavePending, 1, 0) != 0)
            return;
        AvailabilityHistorySnapshot snapshot = AvailabilityHistoryStore.Snapshot(_availabilityHistory);
        _ = SaveAvailabilityHistoryAsync(snapshot);
    }

    private async Task SaveAvailabilityHistoryAsync(AvailabilityHistorySnapshot snapshot)
    {
        try
        {
            await AvailabilityHistoryStore.SaveAsync(snapshot).ConfigureAwait(false);
            Interlocked.Exchange(ref _availabilitySavedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
            Interlocked.Exchange(ref _availabilityRetryAfterTicks, 0);
            Interlocked.Exchange(ref _availabilitySaveFailed, 0);
            ClearSaveWarningWhenRecovered();
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("availability-save-failed", ex);
            _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "availability-history", ex.Message);
            Interlocked.Exchange(ref _availabilityRetryAfterTicks, DateTimeOffset.UtcNow.AddSeconds(10).UtcTicks);
            NotifySaveFailure(ref _availabilitySaveFailed, "Не удалось сохранить историю состояния. Программа повторит попытку.");
        }
        finally
        {
            Interlocked.Exchange(ref _availabilitySavePending, 0);
        }
    }

    private void NotifySaveFailure(ref int flag, string message)
    {
        if (Interlocked.Exchange(ref flag, 1) != 0) return;
        _ui.Post(_ =>
        {
            if (_exiting) return;
            _icon.BalloonTipTitle = ProductInfo.Name;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(5000);
            _persistenceWarning = message;
            _status?.SetPersistenceWarning(message);
        }, null);
    }

    private void ClearSaveWarningWhenRecovered()
    {
        _ui.Post(_ =>
        {
            if (!_exiting && Volatile.Read(ref _locationSaveFailed) == 0
                && Volatile.Read(ref _availabilitySaveFailed) == 0)
            {
                _persistenceWarning = null;
                _status?.SetPersistenceWarning(null);
            }
        }, null);
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("settings-save-failed", ex);
            if (_host is null)
            {
                Debug.WriteLine("settings: " + ex.Message);
            }
            else
            {
                _host.Kernel.Log.Add(DateTimeOffset.UtcNow, "settings", ex.Message);
            }
        }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.RootDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.RootDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(_status, "Не удалось открыть папку настроек: " + ex.Message, ProductInfo.DisplayName(), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetWindowSize()
    {
        if (_status is not { } status) return;
        status.ResetWindowSize();
        _settings.WindowX = status.Location.X;
        _settings.WindowY = status.Location.Y;
        _settings.WindowWidth = status.Width;
        _settings.WindowHeight = status.Height;
        TrySaveSettings();
    }

    private void SaveWindowGeometry()
    {
        if (!_windowWasShown || _status is not { WindowState: FormWindowState.Normal } status)
            return;

        _settings.WindowX = status.Left;
        _settings.WindowY = status.Top;
        _settings.WindowWidth = status.Width;
        _settings.WindowHeight = status.Height;
        TrySaveSettings();
    }

    private void OnLocationChartWindowFromWindow(int hours)
    {
        _settings.LocationChartWindowHours = LocationChartWindows.ToHours(LocationChartWindows.ParseHours(hours));
        _status?.BindAvailabilityHistory(_availabilityHistory, LocationChartWindows.ParseHours(hours));
        TrySaveSettings();
    }

    private void OnGeoCountryIconFromWindow(bool enabled)
    {
        _settings.GeoCountryIconEnabled = enabled && _settings.GeoCountryDetectionEnabled;
        TrySaveSettings();
        _countryTray.SetTrayIconEnabled(_settings.GeoCountryIconEnabled);
    }

    private void OnGeoCountryDetectionFromWindow(bool enabled)
    {
        _settings.GeoCountryDetectionEnabled = enabled;
        if (!enabled)
        {
            _settings.GeoCountryIconEnabled = false;
            _locations.CloseOpen(TimeProvider.System.GetUtcNow());
            TrySaveLocations();
            _status?.BindLocations(_locations, GeoCountryDisplay.Disabled);
        }

        TrySaveSettings();
        _countryTray.SetDetectionEnabled(enabled);
        if (enabled)
        {
            _countryTray.SetTrayIconEnabled(_settings.GeoCountryIconEnabled);
        }
    }

    private void OnEmphasizeShortStatusesFromWindow(bool enabled)
    {
        _settings.EmphasizeShortStatuses = enabled;
        TrySaveSettings();
    }

    private void OnLocationDebugSourcesFromWindow(bool enabled)
    {
        _settings.ShowLocationDebugSources = enabled;
        TrySaveSettings();
    }

    private void OnGeoCountryLetterScaleFromWindow(GeoCountryLetterScale scale)
    {
        _settings.GeoCountryLetterSize = (int)scale;
        TrySaveSettings();
        _countryTray.SetLetterScale(scale);
    }

    private void OnCountryDisplay(GeoCountryDisplay display)
    {
        if (_exiting || !_settings.GeoCountryDetectionEnabled)
        {
            return;
        }

        DateTimeOffset now = display.Observation?.ObservedUtc ?? TimeProvider.System.GetUtcNow();
        bool newObservation = false;
        if (display.Observation is { } observation)
            newObservation = _locations.NoteObservation(observation);
        long afterObservation = _locations.Revision;
        if (display.Fresh)
        {
            _locations.NoteIso(display.Letters, now, mergeShortGap: !_locationGapOpen);
            _locationGapOpen = false;
        }
        else
        {
            _locations.CloseOpen(display.ConfirmedThroughUtc ?? now);
            _locationGapOpen = true;
        }
        bool meaningfulChange = newObservation || _locations.Revision != afterObservation;
        if (meaningfulChange)
        {
            TrySaveLocations();
            _status?.BindLocations(_locations, display);
        }
    }

    private async Task<string> DiagnoseAsync(string endpointId)
    {
        EndpointView? view = _snapshot.Ru.Endpoints.Concat(_snapshot.World.Endpoints)
            .FirstOrDefault(e => string.Equals(e.Id, endpointId, StringComparison.Ordinal));
        if (view is null)
        {
            return "Узел не найден.";
        }

        string? blocked = _host.Kernel.ManualBlockReason(endpointId);
        if (blocked is not null)
        {
            return blocked;
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
        return $"{view.Id} ({view.Uri.Host})\r\n{result.Note}\r\nHTTPS: {https}\r\nICMP: {icmp}\r\nTCP 443: {tcp}\r\nIP: {result.Address} (последний известный адрес сессии, может быть устаревшим)";
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
            _status?.SetManualUpdateState(false, ManualUpdateCopy.AlreadyRunning);
            return;
        }

        bool exitRequested = false;
        SilentUpdateOutcome outcome = SilentUpdateOutcome.Failed;
        string? downloadDirectory = null;
        try
        {
            _status?.SetManualUpdateState(true, ManualUpdateCopy.Checking);
            string? applicationDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrWhiteSpace(applicationDirectory))
            {
                throw new InvalidOperationException("Не найден каталог приложения.");
            }

            downloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NetLights", "updates", Guid.NewGuid().ToString("N"));
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
                new AgentSilentSetupInstaller(Application.ExecutablePath),
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
            if (!exitRequested && downloadDirectory is not null)
            {
                SilentUpdateRuntime.TryDeleteStaging(downloadDirectory);
            }
            _updateGate.Release();
        }

        _status?.SetManualUpdateState(false, ManualUpdateCopy.For(outcome));
        if (exitRequested)
        {
            RuntimeJournal.Write("manual-update-exit-requested");
            _ui.Post(_ => BeginGracefulExit(), null);
        }
    }

    private void ApplyNetworkSignal(NetworkAvailabilitySignal signal)
    {
        switch (signal)
        {
            case NetworkAvailabilitySignal.None:
                return;
            case NetworkAvailabilitySignal.Unavailable:
                _host.NotifyUnavailable(true);
                _countryTray.NotifyNetworkOrResume();
                return;
            case NetworkAvailabilitySignal.Available:
                _host.NotifyUnavailable(false);
                _countryTray.NotifyNetworkOrResume();
                return;
            default:
                throw new InvalidOperationException($"Unhandled network signal {signal}.");
        }
    }

    private void OnNetwork(object? sender, EventArgs e)
    {
        try
        {
            _network.ObserveChange(NetworkInterface.GetIsNetworkAvailable(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("network-state-read-failed", ex);
        }
    }

    private void OnPower(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        DateTimeOffset occurredUtc = TimeProvider.System.GetUtcNow();
        (LocationHistorySnapshot Locations, AvailabilityHistorySnapshot Availability)? snapshots = null;
        void ApplyPowerEvent()
        {
            if (_exiting)
                return;

            if (e.Mode is Microsoft.Win32.PowerModes.Suspend)
            {
                _availabilityHistory.CloseOpen(occurredUtc);
                _locations.CloseOpen(occurredUtc);
                snapshots = (
                    LocationHistoryStore.Snapshot(_locations),
                    AvailabilityHistoryStore.Snapshot(_availabilityHistory));
                _host.NotifyNetworkChange();
                _countryTray.NotifyNetworkOrResume();
            }
            else if (e.Mode is Microsoft.Win32.PowerModes.Resume)
            {
                _taskbar.RequestRecovery();
                _host.NotifyNetworkChange();
                _countryTray.NotifyNetworkOrResume();
            }
        }

        try
        {
            if (Environment.CurrentManagedThreadId == _uiThreadId)
            {
                ApplyPowerEvent();
            }
            else
            {
                var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                int dispatchPending = 1;
                _ui.Post(_ =>
                {
                    if (Interlocked.Exchange(ref dispatchPending, 0) == 0)
                    {
                        dispatched.TrySetCanceled();
                        return;
                    }

                    try
                    {
                        ApplyPowerEvent();
                        dispatched.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        dispatched.TrySetException(ex);
                    }
                }, null);
                if (!dispatched.Task.Wait(TimeSpan.FromSeconds(2)))
                {
                    Interlocked.Exchange(ref dispatchPending, 0);
                    RuntimeJournal.Write("power-event-ui-timeout");
                    return;
                }
                dispatched.Task.GetAwaiter().GetResult();
            }

            if (snapshots is { } pending)
            {
                Task save = PersistPowerSnapshotsAsync(pending.Locations, pending.Availability);
                try
                {
                    if (!save.Wait(TimeSpan.FromSeconds(5)))
                    {
                        ObserveFailure(save);
                        RuntimeJournal.Write("power-event-save-timeout");
                    }
                }
                catch (AggregateException ex)
                {
                    RuntimeJournal.Write("power-event-save-failed", ex.GetBaseException());
                }
            }
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("power-event-failed", ex);
        }
    }

    private async Task PersistPowerSnapshotsAsync(
        LocationHistorySnapshot locations,
        AvailabilityHistorySnapshot availability)
    {
        await Task.WhenAll(
            SavePowerHistoryAsync("availability", () => AvailabilityHistoryStore.SaveAsync(availability, TimeSpan.FromSeconds(2)),
                ticks => Interlocked.Exchange(ref _availabilitySavedAtTicks, ticks)),
            SavePowerHistoryAsync("locations", () => LocationHistoryStore.SaveAsync(locations, TimeSpan.FromSeconds(2)),
                ticks => Interlocked.Exchange(ref _locationsSavedAtTicks, ticks)));
    }

    private static async Task SavePowerHistoryAsync(string name, Func<Task> save, Action<long> onSaved)
    {
        try
        {
            await save().ConfigureAwait(false);
            onSaved(DateTimeOffset.UtcNow.UtcTicks);
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("power-save-" + name + "-failed", ex);
        }
    }

    private void RestoreIcon()
    {
        if (_exiting) return;
        try
        {
            _icon.Visible = false;
            _icon.Visible = true;
        }
        catch (Exception ex) { RuntimeJournal.Write("tray-main-restore-failed", ex); }
        try { _countryTray.Restore(); }
        catch (Exception ex) { RuntimeJournal.Write("tray-country-restore-failed", ex); }
    }

    internal void RequestExit() => _ui.Post(_ => BeginGracefulExit(), null);

    private void BeginGracefulExit()
    {
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        _exiting = true;
        RuntimeJournal.Write("graceful-exit-started");
        _heartbeat.Stop();
        _diagnosticsCts.Cancel();
        _countryTray.SetDetectionEnabled(false);
        _countryTray.DisplayChanged = null;
        _host.SnapshotChanged -= OnSnapshot;
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
        if (_windowWasShown && _status is { WindowState: FormWindowState.Normal } status)
        {
            _settings.WindowX = status.Left;
            _settings.WindowY = status.Top;
            _settings.WindowWidth = status.Width;
            _settings.WindowHeight = status.Height;
        }

        _ = CompleteGracefulExitAsync();
    }

    private async Task CompleteGracefulExitAsync()
    {
        try
        {
            Task monitorShutdown = _host.DisposeAsync().AsTask();
            try
            {
                await monitorShutdown.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException ex)
            {
                ObserveFailure(monitorShutdown);
                RuntimeJournal.Write("monitor-shutdown-timeout", ex);
            }
            catch (Exception ex)
            {
                RuntimeJournal.Write("monitor-shutdown-failed", ex);
            }

            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            _locations.CloseOpen(now);
            _availabilityHistory.CloseOpen(now);
            LocationHistorySnapshot locations = LocationHistoryStore.Snapshot(_locations);
            AvailabilityHistorySnapshot availability = AvailabilityHistoryStore.Snapshot(_availabilityHistory);

            await Task.WhenAll(
                SaveBoundedAsync("settings", () => Task.Run(() => SettingsStore.Save(_settings))),
                SaveBoundedAsync("location-history", () => LocationHistoryStore.SaveAsync(locations, TimeSpan.FromSeconds(5))),
                SaveBoundedAsync("availability-history", () => AvailabilityHistoryStore.SaveAsync(availability, TimeSpan.FromSeconds(5))));
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("graceful-exit-failed", ex);
        }
        finally
        {
            _shutdownReady = true;
            _ui.Post(_ => ExitThread(), null);
        }
    }

    private static async Task SaveBoundedAsync(string name, Func<Task> save)
    {
        Task? operation = null;
        try
        {
            operation = save();
            await operation.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException ex)
        {
            if (operation is not null)
                ObserveFailure(operation);
            RuntimeJournal.Write("shutdown-" + name + "-timeout", ex);
        }
        catch (Exception ex)
        {
            RuntimeJournal.Write("shutdown-" + name + "-failed", ex);
        }
    }

    private static void ObserveFailure(Task operation)
        => _ = operation.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    protected override void ExitThreadCore()
    {
        if (_resourcesDisposed)
        {
            base.ExitThreadCore();
            return;
        }

        _resourcesDisposed = true;
        if (!_shutdownReady)
            RuntimeJournal.Write("exit-without-graceful-flush");
        _heartbeat.Stop();
        _diagnosticsCts.Cancel();
        _countryTray.SetDetectionEnabled(false);
        NetworkChange.NetworkAvailabilityChanged -= OnNetwork;
        NetworkChange.NetworkAddressChanged -= OnNetwork;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPower;
        _probe.Dispose();
        _diagnosticsCts.Dispose();
        _countryTray.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _renderer.Dispose();
        _status?.Dispose();
        _updateGate.Dispose();
        _taskbar.Dispose();
        base.ExitThreadCore();
        RuntimeJournal.Write("exit-complete");
    }

}
