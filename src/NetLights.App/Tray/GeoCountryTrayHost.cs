using System.Windows.Forms;
using NetLights.Core;
using NetLights.Networking;

namespace NetLights.App;

internal sealed class GeoCountryTrayHost : IDisposable
{
    private readonly CountryTrayIconRenderer _renderer = new();
    private readonly GeoCountryScheduler _scheduler;
    private readonly HttpsGeoCountryClient? _ownedClient;
    private readonly SynchronizationContext? _ui;
    private readonly Action _showWindow;
    private GeoCountryLetterScale _letterScale = GeoCountryLetterScale.Regular;
    private NotifyIcon? _icon;
    private bool _visible;
    private bool _detectionEnabled;

    public GeoCountryTrayHost(Action showWindow, string version, TimeProvider time, SynchronizationContext? ui)
        : this(showWindow, HttpsGeoCountryClient.CreateProduction(version), time, ui, ownsClient: true)
    {
    }

    public GeoCountryTrayHost(
        Action showWindow,
        IGeoCountrySource source,
        TimeProvider time,
        SynchronizationContext? ui,
        bool ownsClient = false)
    {
        _showWindow = showWindow;
        _ui = ui;
        if (ownsClient && source is HttpsGeoCountryClient client)
        {
            _ownedClient = client;
        }

        _scheduler = new GeoCountryScheduler(source, time, OnDisplay);
    }

    public bool Visible => _visible && _icon is { Visible: true };

    public bool DetectionEnabled => _detectionEnabled;

    public bool TrayIconCreated => _icon is not null;

    public Action<GeoCountryDisplay>? DisplayChanged { get; set; }

    public GeoCountryDisplay Current => _scheduler.Current;

    public int ShowWindowInvocations { get; private set; }

    public void SetEnabled(bool enabled)
    {
        SetDetectionEnabled(enabled);
        SetTrayIconEnabled(enabled);
    }

    public void SetDetectionEnabled(bool enabled)
    {
        if (enabled == _detectionEnabled)
        {
            return;
        }

        _detectionEnabled = enabled;
        if (enabled)
        {
            _scheduler.Start();
        }
        else
        {
            SetTrayIconEnabled(false);
            _scheduler.Stop();
        }
    }

    public void SetTrayIconEnabled(bool enabled)
    {
        bool shouldShow = enabled && _detectionEnabled;
        if (shouldShow == _visible)
        {
            return;
        }

        if (shouldShow)
        {
            ShowIcon();
        }
        else
        {
            HideIcon();
        }
    }

    public void SetLetterScale(GeoCountryLetterScale scale)
    {
        if (_letterScale == scale)
        {
            return;
        }

        _letterScale = scale;
        Apply(_scheduler.Current);
    }

    public void NotifyNetworkOrResume() => _scheduler.NotifyNetworkOrResume();

    public void Restore()
    {
        if (!_visible || _icon is null)
        {
            return;
        }

        _icon.Visible = false;
        _icon.Visible = true;
    }

    public void RaiseClickForTests(MouseButtons button)
        => _icon?.GetType()
            .GetMethod("OnMouseClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_icon, [new MouseEventArgs(button, 1, 0, 0, 0)]);

    public void Dispose()
    {
        HideAndStop();
        if (_icon is not null)
        {
            _icon.Dispose();
            _icon = null;
        }

        _renderer.Dispose();
        _scheduler.Dispose();
        _ownedClient?.Dispose();
    }

    private void ShowIcon()
    {
        NotifyIcon icon = EnsureIcon();
        _visible = true;
        Apply(_scheduler.Current);
        icon.Visible = true;
    }

    private void HideIcon()
    {
        _visible = false;
        if (_icon is not null)
        {
            _icon.Visible = false;
        }
    }

    private void HideAndStop()
    {
        SetTrayIconEnabled(false);
        SetDetectionEnabled(false);
    }

    private NotifyIcon EnsureIcon()
    {
        if (_icon is not null)
        {
            return _icon;
        }

        int size = _renderer.SystemSmallIconSize();
        var icon = new NotifyIcon
        {
            Visible = false,
            Text = GeoCountryDisplay.Unconfirmed.Tooltip,
            Icon = _renderer.Get("??", size, _letterScale)
        };
        icon.MouseClick += (_, e) =>
        {
            if (TrayIconGestures.ShouldOpenWindow(e.Button))
            {
                ShowWindowInvocations++;
                _showWindow();
            }
        };
        _icon = icon;
        return icon;
    }

    private void OnDisplay(GeoCountryDisplay display)
    {
        if (_ui is null)
        {
            NotifyAndApply(display);
            return;
        }

        _ui.Post(_ => NotifyAndApply(display), null);
    }

    private void NotifyAndApply(GeoCountryDisplay display)
    {
        DisplayChanged?.Invoke(display);
        Apply(display);
    }

    private void Apply(GeoCountryDisplay display)
    {
        if (!_visible || _icon is null)
        {
            return;
        }

        int size = _renderer.SystemSmallIconSize();
        Icon glyph = _renderer.Get(display.Letters, size, _letterScale);
        if (!ReferenceEquals(_icon.Icon, glyph))
        {
            _icon.Icon = glyph;
        }

        if (_icon.Text != display.Tooltip)
        {
            _icon.Text = display.Tooltip;
        }
    }
}
