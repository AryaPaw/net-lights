using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed record LocationChartEventLabel(
    string Text,
    int CenterPx,
    int WidthPx,
    string? CountryIso,
    IReadOnlyList<LocationChartEvent> Events);

internal sealed class LocationSpanChart : Control
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private const int LogicalHeight = 126;
    private const int AxisHeight = 20;
    private const int TrackHeight = 32;
    private readonly ChartToolTip _tip = new(initialDelay: 400, autoPopDelay: 8000);
    private IReadOnlyList<LocationStay> _stays = [];
    private DateTimeOffset _now;
    private DateTimeOffset? _windowStart;
    private TimeSpan? _windowDuration;
    private LocationChartModel _model = new(default, default, []);
    private IReadOnlyList<LocationChartEventLabel> _eventLabels = [];
    private string _tipText = "";
    private LocationChartSegment? _hovered;
    private LocationChartEvent? _hoveredEvent;
    private Point? _hoverPoint;
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
        _hoverPoint = e.Location;
        LocationChartEventLabel? hitLabel = HitEventLabel(e.X, e.Y);
        Rectangle track = TrackBounds();
        LocationChartEvent? hitEvent = EventAt(e.X, e.Y, track);
        LocationChartSegment? hit = hitEvent is null ? Hit(e.X, e.Y) : null;
        SetHoveredEvent(hitEvent);
        SetHovered(hit);
        string text = hitLabel is LocationChartEventLabel label ? TipFor(label)
            : hitEvent is LocationChartEvent marker ? TipFor(marker)
            : hit is LocationChartSegment segment && segment.Iso is not null ? TipFor(segment) : "";
        if (text == _tipText)
        {
            return;
        }

        _tipText = text;
        _tip.ShowFor(this, text, e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            return;
        }

        _tipText = "";
        _tip.Clear(this);
        _hoverPoint = null;
        SetHoveredEvent(null);
        SetHovered(null);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        LocationChartCardChrome.Paint(g, this, "История страны");

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

        }

        DrawCountryLabels(g, track);

        foreach (LocationChartEvent marker in _model.Events)
        {
            int x = track.Left + marker.XPx;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using Pen stem = new(LocationChartPalette.Fill(marker.Iso), 3);
            using SolidBrush dot = new(LocationChartPalette.Fill(marker.Iso));
            using Pen dotEdge = new(Color.White, 1.5f);
            g.DrawLine(stem, x, track.Top, x, track.Bottom - 1);
            int markerRadius = LocationChartCardChrome.Scale(this, 5);
            g.FillEllipse(dot, x - markerRadius, track.Top - markerRadius - 2, markerRadius * 2, markerRadius * 2);
            g.DrawEllipse(dotEdge, x - markerRadius, track.Top - markerRadius - 2, markerRadius * 2, markerRadius * 2);
        }

        foreach (LocationChartEventLabel label in _eventLabels)
        {
            Rectangle bounds = EventLabelBounds(label, track);
            Color color = label.CountryIso is null ? UiTheme.Ink : LocationChartPalette.Fill(label.CountryIso);
            TextRenderer.DrawText(g, label.Text, UiTheme.BodyBold, bounds, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (_hoveredEvent is LocationChartEvent selectedEvent)
        {
            int x = track.Left + selectedEvent.XPx;
            using Pen halo = new(Color.FromArgb(90, UiTheme.Brand800), 8);
            using Pen edge = new(UiTheme.Brand800, 3);
            g.DrawEllipse(halo, x - 7, track.Top - 9, 14, 14);
            g.DrawEllipse(edge, x - 7, track.Top - 9, 14, 14);
        }

        if (_hovered is LocationChartSegment hovered && hovered.Iso is not null)
        {
            Rectangle highlight = SegmentBounds(hovered, track);
            if (highlight.Width > 0)
            {
                Rectangle outer = Rectangle.Inflate(highlight, 4, 4);
                outer.Width = Math.Max(1, outer.Width - 1);
                outer.Height = Math.Max(1, outer.Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using GraphicsPath path = UiDrawing.RoundedRect(outer, 6);
                using Pen glow = new(Color.FromArgb(75, UiTheme.Brand800), 8) { LineJoin = LineJoin.Round };
                using Pen outline = new(UiTheme.Brand800, 4) { LineJoin = LineJoin.Round };
                g.DrawPath(glow, path);
                g.DrawPath(outline, path);
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
        _eventLabels = BuildEventLabels(
            _model.Events,
            track.Width,
            LocationChartCardChrome.Scale(this, 120),
            LocationChartCardChrome.Scale(this, 36));
        _hoveredEvent = _hoverPoint is Point hoverEvent ? EventAt(hoverEvent.X, hoverEvent.Y, track) : null;
        _hovered = _hoveredEvent is null && _hoverPoint is Point hover ? Hit(hover.X, hover.Y) : null;
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

    private LocationChartEvent? HitEvent(int x, int y)
    {
        Rectangle track = TrackBounds();
        if (y < track.Top - 9 || y >= track.Bottom || x < track.Left - 7 || x >= track.Right + 7)
            return null;
        LocationChartEvent? nearest = null;
        int distance = 7;
        foreach (LocationChartEvent marker in _model.Events)
        {
            int next = Math.Abs(x - (track.Left + marker.XPx));
            if (next < distance)
            {
                nearest = marker;
                distance = next;
            }
        }
        return nearest;
    }

    private LocationChartEventLabel? HitEventLabel(int x, int y)
    {
        Rectangle track = TrackBounds();
        foreach (LocationChartEventLabel label in _eventLabels)
        {
            if (EventLabelBounds(label, track).Contains(x, y))
                return label;
        }

        return null;
    }

    private LocationChartEvent? EventAt(int x, int y, Rectangle track)
    {
        LocationChartEventLabel? label = HitEventLabel(x, y);
        return label is null
            ? HitEvent(x, y)
            : label.Events.OrderBy(marker => Math.Abs(x - (track.Left + marker.XPx))).FirstOrDefault();
    }

    internal static IReadOnlyList<LocationChartEventLabel> BuildEventLabels(
        IReadOnlyList<LocationChartEvent> events,
        int trackWidth,
        int maximumLabelWidth,
        int minimumLabelSpacing)
    {
        if (events.Count == 0 || trackWidth <= 0 || maximumLabelWidth <= 0)
            return [];

        maximumLabelWidth = Math.Min(trackWidth, maximumLabelWidth);
        minimumLabelSpacing = Math.Max(1, Math.Min(maximumLabelWidth, minimumLabelSpacing));
        var groups = new List<List<LocationChartEvent>>();
        foreach (LocationChartEvent marker in events.OrderBy(item => item.XPx).ThenBy(item => item.StartUtc))
        {
            if (groups.Count == 0 || marker.XPx - groups[^1][^1].XPx >= minimumLabelSpacing)
                groups.Add([marker]);
            else
                groups[^1].Add(marker);
        }

        while (groups.Count > 1)
        {
            bool merged = false;
            for (int i = 0; i < groups.Count - 1; i++)
            {
                LocationChartEventLabel left = CreateEventLabel(groups[i], maximumLabelWidth);
                LocationChartEventLabel right = CreateEventLabel(groups[i + 1], maximumLabelWidth);
                int leftStart = ClampedLabelLeft(left, trackWidth);
                int rightStart = ClampedLabelLeft(right, trackWidth);
                int gap = Math.Max(1, maximumLabelWidth * 4 / 120);
                if (rightStart >= leftStart + left.WidthPx + gap)
                    continue;

                groups[i].AddRange(groups[i + 1]);
                groups[i].Sort(static (a, b) => a.XPx != b.XPx
                    ? a.XPx.CompareTo(b.XPx)
                    : a.StartUtc.CompareTo(b.StartUtc));
                groups.RemoveAt(i + 1);
                merged = true;
                break;
            }

            if (!merged)
                break;
        }

        return groups.Select(group => CreateEventLabel(group, maximumLabelWidth)).ToArray();
    }

    private static LocationChartEventLabel CreateEventLabel(List<LocationChartEvent> events, int maximumWidth)
    {
        var countries = new List<(string Iso, int Count)>();
        foreach (LocationChartEvent marker in events)
        {
            int index = countries.FindIndex(country => string.Equals(country.Iso, marker.Iso, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                countries.Add((marker.Iso, 1));
            else
                countries[index] = (countries[index].Iso, countries[index].Count + 1);
        }

        string Format((string Iso, int Count) country)
            => country.Count > 1 ? $"{country.Iso}×{country.Count}" : country.Iso;

        string text = string.Join(" · ", countries.Select(Format));
        if (MeasureEventLabel(text) > maximumWidth)
        {
            text = "";
            for (int keep = Math.Min(2, countries.Count); keep > 0; keep--)
            {
                int represented = countries.Take(keep).Sum(country => country.Count);
                string prefix = string.Join(" · ", countries.Take(keep).Select(Format));
                string candidate = represented == events.Count ? prefix : $"{prefix} · +{events.Count - represented}";
                if (MeasureEventLabel(candidate) <= maximumWidth)
                {
                    text = candidate;
                    break;
                }
            }

            if (text.Length == 0)
                text = $"{events.Count} событий";
        }

        int padding = Math.Max(1, maximumWidth * 8 / 120);
        int minimumWidth = Math.Min(maximumWidth, Math.Max(1, maximumWidth * 36 / 120));
        int width = Math.Clamp(MeasureEventLabel(text) + padding, minimumWidth, maximumWidth);
        int center = (int)Math.Round(events.Average(marker => marker.XPx));
        string? singleCountry = countries.Count == 1 ? countries[0].Iso : null;
        return new LocationChartEventLabel(text, center, width, singleCountry, events.ToArray());
    }

    private static int MeasureEventLabel(string text)
        => TextRenderer.MeasureText(text, UiTheme.BodyBold, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;

    private static int ClampedLabelLeft(LocationChartEventLabel label, int trackWidth)
        => Math.Clamp(label.CenterPx - label.WidthPx / 2, 0, Math.Max(0, trackWidth - label.WidthPx));

    private Rectangle EventLabelBounds(LocationChartEventLabel label, Rectangle track)
        => new(track.Left + ClampedLabelLeft(label, track.Width),
            track.Top - LocationChartCardChrome.Scale(this, 29),
            label.WidthPx,
            LocationChartCardChrome.Scale(this, 20));

    private void SetHoveredEvent(LocationChartEvent? marker)
    {
        if (Equals(_hoveredEvent, marker)) return;
        Rectangle track = TrackBounds();
        if (_hoveredEvent is LocationChartEvent previous)
            Invalidate(EventBounds(previous, track));
        _hoveredEvent = marker;
        if (marker is LocationChartEvent current)
            Invalidate(EventBounds(current, track));
    }

    private static Rectangle EventBounds(LocationChartEvent marker, Rectangle track)
        => new(track.Left + marker.XPx - 20, track.Top - 27, 40, track.Height + 33);

    private void SetHovered(LocationChartSegment? segment)
    {
        if (Equals(_hovered, segment))
            return;

        Rectangle track = TrackBounds();
        if (_hovered is LocationChartSegment previous)
            Invalidate(Rectangle.Inflate(SegmentBounds(previous, track), 10, 10));
        _hovered = segment;
        if (segment is LocationChartSegment current)
            Invalidate(Rectangle.Inflate(SegmentBounds(current, track), 10, 10));
    }

    private static Rectangle SegmentBounds(LocationChartSegment segment, Rectangle track)
    {
        int left = Math.Clamp(track.X + segment.StartPx, track.Left, track.Right);
        int width = Math.Min(Math.Max(1, segment.WidthPx), track.Right - left);
        return new Rectangle(left, track.Y, Math.Max(0, width), track.Height);
    }

    private void DrawCountryLabels(Graphics graphics, Rectangle track)
    {
        int compactWidth = TextRenderer.MeasureText("RU", UiTheme.IsoMonoCompact, Size.Empty, TextFormatFlags.NoPadding).Width + 2;
        foreach (LocationChartSegment segment in CountryLabelCandidates(_model, compactWidth))
        {
            string country = segment.Iso!;
            Rectangle box = SegmentBounds(segment, track);
            int fullWidth = TextRenderer.MeasureText(country, UiTheme.IsoMono, Size.Empty, TextFormatFlags.NoPadding).Width;
            Font font = box.Width >= fullWidth + 4 ? UiTheme.IsoMono : UiTheme.IsoMonoCompact;
            int labelWidth = TextRenderer.MeasureText(country, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            if (box.Width < labelWidth + 2)
                continue;

            TextRenderer.DrawText(graphics, country, font, box,
                LocationChartPalette.Ink(country), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    internal static IReadOnlyList<LocationChartSegment> CountryLabelCandidates(LocationChartModel model, int minimumWidth)
        => model.Segments.Where(segment => segment.Iso is not null && segment.WidthPx >= minimumWidth).ToArray();

    private string TipFor(LocationChartSegment segment)
    {
        string iso = segment.Iso ?? "";
        string name = GeoCountryParsers.IsIso3166Alpha2(iso) ? GeoCountryNames.Russian(iso) : iso;
        TimeSpan elapsed = (segment.Live ? _now : segment.EndUtc) - segment.StartUtc;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return name + ": " + LocationCopy.Duration(elapsed)
            + "\nВремя на графике соответствует проверкам; фактическое переключение могло произойти между ними.";
    }

    private static string TipFor(LocationChartEvent marker)
    {
        string name = GeoCountryNames.Russian(marker.Iso) + " · " + marker.Iso;
        if (marker.StartUtc == marker.EndUtc)
            return $"{name}\nОдна подтвержденная проверка: {marker.StartUtc.ToLocalTime():dd.MM HH:mm:ss}\nДлительность неизвестна; ширина метки не означает продолжительность.";
        return $"{name}\nПроверки: {marker.StartUtc.ToLocalTime():dd.MM HH:mm:ss} – {marker.EndUtc.ToLocalTime():dd.MM HH:mm:ss}\nУчасток уже одного пикселя в выбранном периоде.";
    }

    private static string TipFor(LocationChartEventLabel label)
    {
        if (label.Events.Count == 1)
            return TipFor(label.Events[0]);

        IEnumerable<string> rows = label.Events.Take(8).Select(marker =>
        {
            string country = GeoCountryNames.Russian(marker.Iso) + " · " + marker.Iso;
            string start = marker.StartUtc.ToLocalTime().ToString("dd.MM HH:mm:ss");
            return marker.StartUtc == marker.EndUtc
                ? country + " · " + start + " · длительность неизвестна"
                : country + " · " + start + " – " + marker.EndUtc.ToLocalTime().ToString("HH:mm:ss") + " · участок уже одного пикселя";
        });
        string more = label.Events.Count > 8 ? $"\nЕщё {label.Events.Count - 8}" : "";
        return $"Короткие участки: {label.Events.Count}\n{string.Join("\n", rows)}{more}";
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

        foreach (LocationChartEvent marker in _model.Events)
            parts.Add(marker.Iso + " (короткое событие)");

        return string.Join(", ", parts);
    }

    internal DateTimeOffset LastModelBuiltAt => _lastLayoutAt;
    internal LocationChartSegment? HoveredSegmentForTests => _hovered;
    internal LocationChartEvent? HoveredEventForTests => _hoveredEvent;
    internal string HoverTipForTests => _tip.GetToolTip(this) ?? string.Empty;
    internal void DispatchMouseMoveForTests(Point point)
        => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));
    internal void DispatchMouseLeaveForTests() => OnMouseLeave(EventArgs.Empty);

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
