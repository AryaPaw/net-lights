using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationSpanChart : Control
{
    private const int LogicalHeight = 88;
    private const int PadX = 16;
    private const int PadTop = 14;
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
    private LocationChartModel _model = new(default, default, []);
    private string _tipText = "";

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
        _stays = stays;
        _now = now;
        _windowStart = windowStart;
        RelayoutModel();
        AccessibleDescription = Describe();
        Invalidate();
    }

    public void Tick(DateTimeOffset now)
    {
        _now = now;
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
        Rectangle card = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(g, card, UiTheme.CardRadius, UiTheme.Card, UiTheme.Border);

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
            if (segment.WidthPx <= 0 || segment.Iso is null)
            {
                continue;
            }

            Rectangle box = new(track.X + segment.StartPx, track.Y, segment.WidthPx, track.Height);
            using SolidBrush fill = new(LocationChartPalette.Fill(segment.Iso));
            g.FillRectangle(fill, box);
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
    }

    private Rectangle TrackBounds()
    {
        int padX = IsHandleCreated ? LogicalToDeviceUnits(PadX) : PadX;
        int padTop = IsHandleCreated ? LogicalToDeviceUnits(PadTop) : PadTop;
        int trackH = IsHandleCreated ? LogicalToDeviceUnits(TrackHeight) : TrackHeight;
        return new Rectangle(
            padX,
            padTop,
            Math.Max(0, Width - (padX * 2) - 1),
            trackH);
    }

    private LocationChartSegment? Hit(int x, int y)
    {
        Rectangle track = TrackBounds();
        if (!track.Contains(x, y))
        {
            return null;
        }

        foreach (LocationChartSegment segment in _model.Segments)
        {
            if (segment.Iso is null || segment.WidthPx <= 0)
            {
                continue;
            }

            int left = track.X + segment.StartPx;
            if (x >= left && x < left + segment.WidthPx)
            {
                return segment;
            }
        }

        return null;
    }

    private string TipFor(LocationChartSegment segment)
    {
        string iso = segment.Iso ?? "";
        string name = GeoCountryParsers.IsIso3166Alpha2(iso) ? GeoCountryNames.Russian(iso) : iso;
        TimeSpan elapsed = segment.EndUtc - segment.StartUtc;
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
}

internal static class LocationChartPalette
{
    private static readonly Color[] Fills =
    [
        Color.FromArgb(14, 124, 194),
        Color.FromArgb(196, 112, 32),
        Color.FromArgb(16, 140, 132),
        Color.FromArgb(168, 56, 80),
        Color.FromArgb(72, 132, 64),
        Color.FromArgb(108, 80, 168),
        Color.FromArgb(70, 90, 118),
        Color.FromArgb(200, 92, 64),
        Color.FromArgb(32, 108, 148),
        Color.FromArgb(168, 132, 36),
        Color.FromArgb(176, 72, 112),
        Color.FromArgb(36, 100, 88)
    ];

    public static Color Fill(string iso) => Fills[Slot(iso)];

    public static Color Ink(string iso)
    {
        _ = iso;
        return UiTheme.OnBrand;
    }

    public static int Slot(string iso)
    {
        int index = Hash(iso) % Fills.Length;
        if (index < 0)
        {
            index += Fills.Length;
        }

        return index;
    }

    private static int Hash(string iso)
    {
        int hash = unchecked((int)2166136261);
        foreach (char c in iso)
        {
            hash ^= c;
            hash = unchecked(hash * 16777619);
        }

        return hash;
    }
}
