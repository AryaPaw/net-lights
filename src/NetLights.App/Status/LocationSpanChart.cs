using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationSpanChart : Control
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private const int LogicalHeight = 126;
    private const int AxisHeight = 20;
    private const int TrackHeight = 32;
    private readonly ToolTip _tip = new()
    {
        ShowAlways = true,
        AutoPopDelay = 8000,
        InitialDelay = 400,
        ReshowDelay = 0,
        UseAnimation = false,
        UseFading = false
    };
    private IReadOnlyList<LocationStay> _stays = [];
    private DateTimeOffset _now;
    private DateTimeOffset? _windowStart;
    private TimeSpan? _windowDuration;
    private LocationChartModel _model = new(default, default, []);
    private string _tipText = "";
    private DateTimeOffset _lastLayoutAt;
    private TimeSpan? _laidOutWindow;
    private int _laidOutWidth = -1;
    private int _laidOutStayCount = -1;
    private DateTimeOffset _laidOutLastStart;
    private DateTimeOffset? _laidOutLastEnd;
    private string _laidOutLastIso = "";

    public LocationSpanChart()
    {
        TabStop = false;
        AccessibleName = "locationSpanChart";
        BackColor = UiTheme.Surface;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        Margin = new Padding(0, 0, 0, 10);
    }

    public void Bind(IReadOnlyList<LocationStay> stays, DateTimeOffset now, DateTimeOffset? windowStart = null)
    {
        TimeSpan? window = windowStart is DateTimeOffset windowOrigin ? now - windowOrigin : null;
        bool geometryChanged = window != _laidOutWindow || TrackBounds().Width != _laidOutWidth;
        bool dataChanged = TailChanged(stays);
        _stays = stays;
        _now = now;
        _windowStart = windowStart;
        _windowDuration = window;
        if (geometryChanged || _lastLayoutAt == default || dataChanged)
        {
            RelayoutModel();
            AccessibleDescription = Describe();
            Invalidate();
        }
    }

    public void Tick(DateTimeOffset now)
    {
        _now = now;
        if (_windowDuration is TimeSpan duration)
        {
            _windowStart = now - duration;
        }

        if (now >= _lastLayoutAt && now - _lastLayoutAt < RefreshInterval)
        {
            return;
        }

        RelayoutModel();
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240);
        int height = IsHandleCreated ? LogicalToDeviceUnits(LogicalHeight) : LogicalHeight;
        return new Size(width, height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RelayoutModel();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        LocationChartSegment? hit = Hit(e.X, e.Y);
        string text = hit is LocationChartSegment segment && segment.Iso is not null
            ? TipFor(segment)
            : "";
        if (text == _tipText)
        {
            return;
        }

        _tipText = text;
        _tip.SetToolTip(this, text);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            return;
        }

        _tipText = "";
        _tip.SetToolTip(this, "");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        LocationChartCardChrome.Paint(g, this, "История смен страны");

        Rectangle track = TrackBounds();
        if (track.Width <= 0 || track.Height <= 0)
        {
            return;
        }

        using (SolidBrush trackFill = new(UiTheme.Track))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.FillRectangle(trackFill, track);
        }

        g.SmoothingMode = SmoothingMode.None;
        foreach (LocationChartSegment segment in _model.Segments)
        {
            if (segment.Iso is null)
            {
                continue;
            }

            int displayWidth = Math.Max(1, segment.WidthPx);
            Rectangle box = new(track.X + segment.StartPx, track.Y, displayWidth, track.Height);
            g.FillRectangle(LocationChartPalette.FillBrush(segment.Iso), box);
            if (segment.Live)
            {
                using Pen live = new(UiTheme.Brand800, 2);
                g.DrawLine(live, box.Right - 1, box.Top, box.Right - 1, box.Bottom);
            }

            if (box.Width >= 28)
            {
                TextRenderer.DrawText(
                    g,
                    segment.Iso,
                    UiTheme.IsoMono,
                    box,
                    LocationChartPalette.Ink(segment.Iso),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_model.OriginUtc == default)
        {
            return;
        }

        int axisH = IsHandleCreated ? LogicalToDeviceUnits(AxisHeight) : AxisHeight;
        Rectangle leftBox = new(track.X, track.Bottom + 4, track.Width / 2, axisH);
        Rectangle rightBox = new(track.X + (track.Width / 2), track.Bottom + 4, track.Width / 2, axisH);
        TextRenderer.DrawText(g, LocationCopy.When(_model.OriginUtc), UiTheme.Caption, leftBox, UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, "сейчас", UiTheme.Caption, rightBox, UiTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void RelayoutModel()
    {
        Rectangle track = TrackBounds();
        _model = LocationChartLayout.Build(_stays, _now, Math.Max(0, track.Width), windowStart: _windowStart);
        _lastLayoutAt = _now;
        _laidOutWindow = _windowStart is DateTimeOffset start ? _now - start : null;
        _laidOutWidth = track.Width;
        _laidOutStayCount = _stays.Count;
        if (_laidOutStayCount > 0)
        {
            LocationStay last = _stays[^1];
            _laidOutLastStart = last.StartedUtc;
            _laidOutLastEnd = last.EndedUtc;
            _laidOutLastIso = last.Iso;
        }
        else
        {
            _laidOutLastStart = default;
            _laidOutLastEnd = null;
            _laidOutLastIso = "";
        }
    }

    private bool TailChanged(IReadOnlyList<LocationStay> stays)
    {
        if (stays.Count != _laidOutStayCount)
            return true;
        if (stays.Count == 0)
            return _laidOutLastStart != default;
        LocationStay last = stays[^1];
        return last.StartedUtc != _laidOutLastStart
            || last.EndedUtc != _laidOutLastEnd
            || !string.Equals(last.Iso, _laidOutLastIso, StringComparison.OrdinalIgnoreCase);
    }

    internal Rectangle TrackBounds()
    {
        int padLeft = LocationChartCardChrome.Scale(this, LocationChartCardChrome.CardInset);
        int padRight = LocationChartCardChrome.Scale(this, LocationChartCardChrome.CardInset);
        int padTop = LocationChartCardChrome.Scale(this, 57);
        int trackH = LocationChartCardChrome.Scale(this, TrackHeight);
        return new Rectangle(
            padLeft,
            padTop,
            Math.Max(0, Width - padLeft - padRight),
            trackH);
    }

    private LocationChartSegment? Hit(int x, int y)
    {
        Rectangle track = TrackBounds();
        if (!track.Contains(x, y))
        {
            return null;
        }

        long windowTicks = (_model.HorizonUtc - _model.OriginUtc).Ticks;
        if (windowTicks <= 0)
            return null;

        int localX = Math.Clamp(x - track.Left, 0, Math.Max(0, track.Width - 1));
        long startTicks = windowTicks * localX / track.Width;
        long endTicks = windowTicks * (localX + 1L) / track.Width;
        DateTimeOffset pixelStart = _model.OriginUtc + TimeSpan.FromTicks(startTicks);
        DateTimeOffset pixelEnd = _model.OriginUtc + TimeSpan.FromTicks(Math.Max(startTicks + 1, endTicks));

        LocationChartSegment? best = null;
        long bestDuration = long.MaxValue;
        foreach (LocationChartSegment segment in _model.Segments)
        {
            if (segment.Iso is null || segment.EndUtc <= pixelStart || segment.StartUtc >= pixelEnd)
                continue;

            long duration = (segment.EndUtc - segment.StartUtc).Ticks;
            if (duration < bestDuration)
            {
                best = segment;
                bestDuration = duration;
            }
        }

        return best;
    }

    private string TipFor(LocationChartSegment segment)
    {
        string iso = segment.Iso ?? "";
        string name = GeoCountryParsers.IsIso3166Alpha2(iso) ? GeoCountryNames.Russian(iso) : iso;
        TimeSpan elapsed = (segment.Live ? _now : segment.EndUtc) - segment.StartUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return name + ": " + LocationCopy.Duration(elapsed);
    }

    private string Describe()
    {
        var parts = new List<string>();
        foreach (LocationChartSegment segment in _model.Segments)
        {
            if (segment.Iso is not null)
            {
                parts.Add(segment.Iso);
            }
        }

        return string.Join(", ", parts);
    }

    internal DateTimeOffset LastModelBuiltAt => _lastLayoutAt;

}

internal static class LocationChartPalette
{
    private static readonly Dictionary<string, SolidBrush> Brushes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Color RussiaRed = Color.FromArgb(220, 0, 0);
    private static readonly IReadOnlyDictionary<string, Color> FamiliarColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
    {
        ["FI"] = Color.FromArgb(32, 108, 148),
        ["DE"] = Color.FromArgb(15, 126, 118),
        ["FR"] = Color.FromArgb(200, 120, 36),
        ["NL"] = Color.FromArgb(168, 56, 80),
        ["PL"] = Color.FromArgb(108, 80, 168),
        ["RU"] = RussiaRed,
        ["TR"] = Color.FromArgb(210, 108, 78)
    };

    public static Color Fill(string iso)
    {
        if (FamiliarColors.TryGetValue(iso, out Color familiar))
            return familiar;

        uint hash = Hash(iso);
        int hue = (int)(hash % 360);
        int saturation = 48 + (int)((hash >> 9) % 13);
        int lightness = 62 + (int)((hash >> 17) % 7);
        return HslToColor(hue, saturation / 100d, lightness / 100d);
    }

    public static Color HistoryLabel(string iso)
        => string.Equals(iso, "RU", StringComparison.OrdinalIgnoreCase) ? RussiaRed : UiTheme.Ink;

    public static SolidBrush FillBrush(string iso)
    {
        if (!Brushes.TryGetValue(iso, out SolidBrush? brush))
        {
            brush = new SolidBrush(Fill(iso));
            Brushes[iso] = brush;
        }

        return brush;
    }

    public static Color Ink(string iso)
    {
        Color fill = Fill(iso);
        Color dark = Color.FromArgb(22, 32, 48);
        double luminance = RelativeLuminance(fill);
        double whiteContrast = 1.05 / (luminance + 0.05);
        double darkContrast = (luminance + 0.05) / (RelativeLuminance(dark) + 0.05);
        return whiteContrast >= darkContrast ? Color.White : dark;
    }

    public static int Slot(string iso)
        => (int)(Hash(iso) % 360);

    private static uint Hash(string iso)
    {
        uint hash = 2166136261;
        foreach (char c in iso)
        {
            hash ^= c;
            hash = unchecked(hash * 16777619);
        }

        hash ^= hash >> 16;
        hash = unchecked(hash * 0x7feb352d);
        hash ^= hash >> 15;
        return hash;
    }

    private static Color HslToColor(int hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60d) % 2 - 1));
        double m = lightness - chroma / 2;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte component)
        {
            double value = component / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }
}
