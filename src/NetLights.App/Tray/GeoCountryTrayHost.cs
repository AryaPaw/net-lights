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

    public bool TrayIconCreated => _icon is not null;

    public Action<GeoCountryDisplay>? DisplayChanged { get; set; }

    public GeoCountryDisplay Current => _scheduler.Current;

    public int ShowWindowInvocations { get; private set; }

    public void SetEnabled(bool enabled)
    {
        if (enabled == _visible)
        {
            return;
        }

        if (enabled)
        {
            ShowAndStart();
        }
        else
        {
            HideAndStop();
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

    private void ShowAndStart()
    {
        NotifyIcon icon = EnsureIcon();
        _visible = true;
        Apply(_scheduler.Current);
        icon.Visible = true;
        _scheduler.Start();
    }

    private void HideAndStop()
    {
        _visible = false;
        _scheduler.Stop();
        if (_icon is not null)
        {
            _icon.Visible = false;
        }
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
