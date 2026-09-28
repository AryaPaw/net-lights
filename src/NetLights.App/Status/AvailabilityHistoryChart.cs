using NetLights.Core;

namespace NetLights.App;

internal class AvailabilityHistoryChart : Control
{
    private static readonly TimeSpan RenderTimeRefresh = TimeSpan.FromSeconds(5);
    private static readonly SolidBrush OnlineFill = new(Color.FromArgb(36, 154, 104));
    private static readonly SolidBrush LimitedFill = new(Color.FromArgb(220, 154, 42));
    private static readonly SolidBrush OfflineFill = new(Color.FromArgb(198, 62, 66));
    private static readonly SolidBrush UnknownFill = new(Color.FromArgb(130, 138, 150));
    private static readonly SolidBrush NoDataFill = new(Color.FromArgb(232, 236, 241));
    private static readonly SolidBrush PausedFill = new(Color.FromArgb(42, 116, 206));
    internal const int ShortSpanMinimumWidth = 4;
    private const int BaseCardHeight = 160;
    private const int LegendTop = 112;
    private static readonly (string Label, SolidBrush Brush)[] LegendItems =
    [
        ("В сети", OnlineFill),
        ("Ограничено", LimitedFill),
        ("Нет связи", OfflineFill),
        ("Нет данных", UnknownFill),
        ("Пауза", PausedFill)
    ];
    internal const TextFormatFlags LegendTextFlags = TextFormatFlags.Left
        | TextFormatFlags.VerticalCenter
        | TextFormatFlags.NoPrefix
        | TextFormatFlags.NoPadding
        | TextFormatFlags.EndEllipsis;
    private readonly ToolTip _tip = new()
    {
        ShowAlways = true,
        InitialDelay = 250,
        ReshowDelay = 100,
        AutoPopDelay = 5000,
        UseAnimation = false,
        UseFading = false
    };
    private AvailabilityHistory _history = new();
    private AvailabilityRenderSpan[] _providerSegments = [];
    private AvailabilityRenderSpan[] _worldSegments = [];
    private AvailabilityShortMarker[] _providerMarkers = [];
    private AvailabilityShortMarker[] _worldMarkers = [];
    private AvailabilityRenderSpan?[] _providerHitMap = [];
    private AvailabilityRenderSpan?[] _worldHitMap = [];
    private TimeSpan _window = LocationChartWindows.Default;
    private DateTimeOffset _now;
    private long _historyRevision = -1;
    private DateTimeOffset _segmentsBuiltAt;
    private int _renderedWidth = -1;
    private bool _emphasizeShortSpans = true;
    private AvailabilityRenderSpan? _hovered;
    private CheckBox? _shortStatusesToggle;
    private FooterLayout? _cachedFooterLayout;
    private int _cachedFooterWidth = -1;
    private Size _cachedTogglePreferredSize;

    internal int SegmentBuildCount { get; private set; }
    internal GroupAvailability? HoveredState => _hovered?.State;
    internal bool HoveredIsPaused => _hovered?.Paused ?? false;
    internal int? HoveredLineX => _hovered is AvailabilityRenderSpan span ? span.Left + span.Width / 2 : null;
    internal int EmphasizedSpanCount => _providerSegments.Count(s => s.Short) + _worldSegments.Count(s => s.Short);
    internal int EmphasizedMarkerCount => _emphasizeShortSpans ? EmphasizedSpanCount : 0;
    internal Rectangle TrackFor(EndpointGroup group) => TrackBounds(group);
    internal Rectangle[] ShortMarkerRectangles(EndpointGroup group)
        => (group == EndpointGroup.Ru ? _providerMarkers : _worldMarkers).Select(marker => marker.Bounds).ToArray();
    internal Rectangle[] LegendItemTextBounds()
        => GetFooterLayout(Width).Items.Select(item => item.TextBounds).ToArray();
    internal static string[] LegendLabels => LegendItems.Select(item => item.Label).ToArray();
    internal Rectangle HoverInvalidationBounds(int x)
    {
        int top = TrackBounds(EndpointGroup.Ru).Top - Scale(3);
        int bottom = TrackBounds(EndpointGroup.World).Bottom + Scale(3);
        int padding = Scale(ShortSpanMinimumWidth / 2 + 2);
        return new Rectangle(x - padding, top, padding * 2 + 1, bottom - top + 1);
    }

    public AvailabilityHistoryChart()
    {
        AccessibleName = "availabilityHistoryChart";
        DoubleBuffered = true;
        AutoSize = true;
        Dock = DockStyle.Top;
        BackColor = UiTheme.Surface;
        Margin = new Padding(0, 0, 0, 8);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    internal CheckBox? ShortStatusesToggle => _shortStatusesToggle;

    internal void SetShortStatusesToggle(CheckBox toggle)
    {
        if (_shortStatusesToggle is not null && !ReferenceEquals(_shortStatusesToggle, toggle))
            Controls.Remove(_shortStatusesToggle);

        _shortStatusesToggle = toggle;
        _cachedFooterLayout = null;
        _tip.SetToolTip(toggle, "Короткие интервалы получают минимальную ширину; наведите на отметку, чтобы увидеть точную длительность.");
        if (!Controls.Contains(toggle))
            Controls.Add(toggle);
        LayoutFooter();
    }

    public void Bind(AvailabilityHistory history, DateTimeOffset now, TimeSpan window)
    {
        bool dataChanged = !ReferenceEquals(_history, history) || _historyRevision != history.Revision;
        bool windowChanged = _window != window;
        bool timeChanged = _segmentsBuiltAt == default || now < _segmentsBuiltAt || now - _segmentsBuiltAt >= RenderTimeRefresh;
        _history = history;
        _historyRevision = history.Revision;
        _now = now;
        _window = window;
        if (dataChanged || windowChanged || timeChanged || _renderedWidth != Width)
        {
            RebuildSegments();
            Invalidate();
        }
    }

    public void SetEmphasizeShortSpans(bool enabled)
    {
        if (_emphasizeShortSpans == enabled)
            return;

        _emphasizeShortSpans = enabled;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240);
        return new(width, CreateFooterLayout(width).PreferredHeight);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        _cachedFooterLayout = null;
        LayoutFooter();
        if (Width > 0 && Height > 0 && _renderedWidth != Width)
        {
            RebuildSegments();
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _cachedFooterLayout = null;
        LayoutFooter();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        LocationChartCardChrome.Paint(graphics, this, "История доступности");
        int pad = Scale(14), rowHeight = Scale(19), width = Math.Max(1, Width - pad * 2);
        DrawRow(graphics, EndpointGroup.Ru, GroupLabels.Provider, pad, Scale(34), width, rowHeight);
        DrawRow(graphics, EndpointGroup.World, GroupLabels.World, pad, Scale(56), width, rowHeight);
        Rectangle providerTrack = TrackBounds(EndpointGroup.Ru);
        if (_now != default)
            DrawTimeAxis(graphics, providerTrack.Left, providerTrack.Width, Scale(81), _now - _window);
        DrawLegend(graphics, GetFooterLayout(Width));

        if (_hovered is AvailabilityRenderSpan hovered)
        {
            Rectangle track = TrackBounds(hovered.Group);
            int x = hovered.Left + hovered.Width / 2;
            using var marker = new Pen(Color.FromArgb(190, UiTheme.Ink));
            e.Graphics.DrawLine(marker, x, track.Top - Scale(3), x, track.Bottom + Scale(3));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        AvailabilityRenderSpan? next = Hit(e.Location);
        if (next == _hovered)
            return;

        int oldX = _hovered is AvailabilityRenderSpan old ? old.Left + old.Width / 2 : -1;
        _hovered = next;
        if (next is AvailabilityRenderSpan span)
        {
            string group = span.Group == EndpointGroup.Ru ? GroupLabels.Provider : GroupLabels.World;
            string state = span.Paused ? "Пауза" : StateLabel(span.State);
            TimeSpan duration = span.EndUtc - span.StartUtc;
            _tip.SetToolTip(
                this,
                $"{group} · {state}\n{span.StartUtc.ToLocalTime():dd.MM HH:mm:ss} – {span.EndUtc.ToLocalTime():dd.MM HH:mm:ss}\n{LocationCopy.Duration(duration)}");
        }
        else
        {
            _tip.SetToolTip(this, "");
        }

        InvalidateHoverColumn(oldX, next is AvailabilityRenderSpan current ? current.Left + current.Width / 2 : -1);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        int oldX = _hovered is AvailabilityRenderSpan old ? old.Left + old.Width / 2 : -1;
        _hovered = null;
        _tip.SetToolTip(this, "");
        if (oldX >= 0)
            InvalidateHoverColumn(oldX, -1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RebuildSegments()
    {
        if (_now == default)
            return;

        DateTimeOffset start = _now - _window;
        Rectangle providerTrack = TrackBounds(EndpointGroup.Ru);
        Rectangle worldTrack = TrackBounds(EndpointGroup.World);
        var provider = new List<AvailabilityRenderSpan>();
        var world = new List<AvailabilityRenderSpan>();
        foreach (AvailabilitySpan span in _history.Spans)
        {
            DateTimeOffset end = span.EndedUtc ?? _now;
            if (span.StartedUtc >= _now || end <= start)
                continue;

            DateTimeOffset clippedStart = span.StartedUtc < start ? start : span.StartedUtc;
            if (end > _now)
                end = _now;
            Rectangle track = span.Group == EndpointGroup.Ru ? providerTrack : worldTrack;
            double startRatio = (clippedStart - start).Ticks / (double)_window.Ticks;
            double endRatio = (end - start).Ticks / (double)_window.Ticks;
            int left = track.Left + (int)Math.Floor(Math.Clamp(startRatio, 0, 1) * track.Width);
            int right = track.Left + (int)Math.Ceiling(Math.Clamp(endRatio, 0, 1) * track.Width);
            int width = right - left;
            if (width <= 0)
                continue;

            double exactWidth = (end - clippedStart).Ticks / (double)_window.Ticks * track.Width;
            var segment = new AvailabilityRenderSpan(
                span.Group,
                span.State,
                span.Paused,
                left,
                width,
                exactWidth < Scale(ShortSpanMinimumWidth),
                clippedStart,
                end);
            (span.Group == EndpointGroup.Ru ? provider : world).Add(segment);
        }

        _providerSegments = provider.ToArray();
        _worldSegments = world.ToArray();
        _providerMarkers = BuildShortMarkers(_providerSegments, EndpointGroup.Ru);
        _worldMarkers = BuildShortMarkers(_worldSegments, EndpointGroup.World);
        _providerHitMap = BuildHitMap(_providerSegments);
        _worldHitMap = BuildHitMap(_worldSegments);
        SegmentBuildCount++;
        _segmentsBuiltAt = _now;
        _renderedWidth = Width;
        _hovered = null;
        _tip.Hide(this);
    }

    private void LayoutFooter()
    {
        if (_shortStatusesToggle is null || Width <= 0)
            return;

        FooterLayout layout = GetFooterLayout(Width);
        _shortStatusesToggle.SetBounds(
            layout.ToggleBounds.X,
            layout.ToggleBounds.Y,
            layout.ToggleBounds.Width,
            layout.ToggleBounds.Height);
    }

    private FooterLayout CreateFooterLayout(int width)
    {
        int left = Scale(14);
        int right = Math.Max(left + 1, width - Scale(14));
        int available = right - left;
        Size togglePreferred = _shortStatusesToggle?.GetPreferredSize(Size.Empty) ?? Size.Empty;
        int toggleWidth = Math.Min(togglePreferred.Width, available);
        int toggleHeight = Math.Max(Scale(18), Math.Min(Scale(22), togglePreferred.Height));
        int fullLegendWidth = LegendItems.Sum(item => LegendItemWidth(item.Label));
        int itemGap = Scale(12);
        bool beside = _shortStatusesToggle is not null && available >= fullLegendWidth + toggleWidth + itemGap;
        int legendWidth = Math.Max(1, available - (beside ? toggleWidth + itemGap : 0));
        int swatchWidth = Scale(10), swatchGap = Scale(5), rowHeight = Scale(18);
        int x = left, y = Scale(LegendTop), rows = 1;
        var placements = new List<LegendItemPlacement>(LegendItems.Length);
        for (int i = 0; i < LegendItems.Length; i++)
        {
            (string label, SolidBrush brush) = LegendItems[i];
            int textWidth = TextRenderer.MeasureText(label, UiTheme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width;
            int contentWidth = swatchWidth + swatchGap + textWidth;
            if (x > left && x + contentWidth > left + legendWidth)
            {
                x = left;
                y += rowHeight;
                rows++;
            }

            int availableText = Math.Max(1, left + legendWidth - x - swatchWidth - swatchGap);
            placements.Add(new LegendItemPlacement(
                new Rectangle(x, y + (rowHeight - swatchWidth) / 2, swatchWidth, swatchWidth),
                new Rectangle(x + swatchWidth + swatchGap, y, Math.Min(textWidth, availableText), rowHeight),
                brush));
            x += contentWidth + (i < LegendItems.Length - 1 ? itemGap : 0);
        }

        int toggleY = beside
            ? Scale(LegendTop) - Scale(2)
            : Scale(LegendTop) + rows * rowHeight + Scale(4);
        Rectangle toggleBounds = new(
            Math.Max(left, right - toggleWidth),
            toggleY,
            toggleWidth,
            toggleHeight);
        int footerBottom = _shortStatusesToggle is null
            ? Scale(LegendTop) + rows * rowHeight + Scale(8)
            : toggleBounds.Bottom + Scale(10);
        int preferredHeight = Math.Max(Scale(BaseCardHeight), footerBottom);
        return new FooterLayout(placements.ToArray(), toggleBounds, preferredHeight);
    }

    private FooterLayout GetFooterLayout(int width)
    {
        Size togglePreferred = _shortStatusesToggle?.GetPreferredSize(Size.Empty) ?? Size.Empty;
        if (width == _cachedFooterWidth
            && togglePreferred == _cachedTogglePreferredSize
            && _cachedFooterLayout is FooterLayout cached)
        {
            return cached;
        }

        FooterLayout layout = CreateFooterLayout(width);
        if (width == Width)
        {
            _cachedFooterWidth = width;
            _cachedTogglePreferredSize = togglePreferred;
            _cachedFooterLayout = layout;
        }

        return layout;
    }

    private int LegendItemWidth(string label)
        => Scale(10) + Scale(5) + TextRenderer.MeasureText(label, UiTheme.Caption, Size.Empty, TextFormatFlags.NoPadding).Width + Scale(12);

    private void DrawTimeAxis(Graphics graphics, int x, int width, int y, DateTimeOffset start)
    {
        string format = _window <= TimeSpan.FromHours(24) ? "HH:mm" : "dd.MM";
        DateTimeOffset middle = start + TimeSpan.FromTicks(_window.Ticks / 2);
        string[] labels = [start.ToLocalTime().ToString(format), middle.ToLocalTime().ToString(format), _now.ToLocalTime().ToString(format)];
        Rectangle[] bounds =
        [
            new Rectangle(x, y, width / 3, Scale(18)),
            new Rectangle(x + width / 3, y, width / 3, Scale(18)),
            new Rectangle(x + (width * 2 / 3), y, width - (width * 2 / 3), Scale(18))
        ];
        TextFormatFlags[] flags = [TextFormatFlags.Left, TextFormatFlags.HorizontalCenter, TextFormatFlags.Right];
        for (int i = 0; i < labels.Length; i++)
            TextRenderer.DrawText(graphics, labels[i], UiTheme.Caption, bounds[i], UiTheme.Muted, flags[i] | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private void DrawLegend(Graphics graphics, FooterLayout layout)
    {
        for (int i = 0; i < LegendItems.Length; i++)
        {
            LegendItemPlacement placement = layout.Items[i];
            graphics.FillRectangle(placement.Brush, placement.SwatchBounds);
            TextRenderer.DrawText(graphics, LegendItems[i].Label, UiTheme.Caption, placement.TextBounds, UiTheme.Muted, LegendTextFlags);
        }
    }

    private void DrawRow(Graphics graphics, EndpointGroup group, string label, int x, int y, int width, int height)
    {
        int labelWidth = Scale(74);
        TextRenderer.DrawText(graphics, label, UiTheme.Caption, new Rectangle(x, y - 1, labelWidth, height), UiTheme.Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        Rectangle track = TrackBounds(group);
        graphics.FillRectangle(NoDataFill, track);
        AvailabilityRenderSpan[] segments = group == EndpointGroup.Ru ? _providerSegments : _worldSegments;
        Rectangle clip = Rectangle.Ceiling(graphics.VisibleClipBounds);
        int low = 0;
        int high = segments.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (segments[middle].Left + segments[middle].Width <= clip.Left)
                low = middle + 1;
            else
                high = middle;
        }

        for (int i = low; i < segments.Length && segments[i].Left < clip.Right; i++)
        {
            AvailabilityRenderSpan segment = segments[i];
            int left = Math.Clamp(segment.Left, track.Left, track.Right);
            int right = Math.Clamp(segment.Left + segment.Width, track.Left, track.Right);
            if (right > left)
                graphics.FillRectangle(FillFor(segment.State, segment.Paused), left, track.Top, right - left, track.Height);
        }

        if (_emphasizeShortSpans)
            DrawShortMarkers(graphics, group, clip);
    }

    private AvailabilityRenderSpan? Hit(Point point)
    {
        if (_emphasizeShortSpans)
        {
            AvailabilityShortMarker? marker = HitShortMarker(point, _providerMarkers)
                ?? HitShortMarker(point, _worldMarkers);
            if (marker is AvailabilityShortMarker shortMarker)
                return shortMarker.Span;
        }

        EndpointGroup group;
        if (TrackBounds(EndpointGroup.Ru).Contains(point))
            group = EndpointGroup.Ru;
        else if (TrackBounds(EndpointGroup.World).Contains(point))
            group = EndpointGroup.World;
        else
            return null;

        Rectangle track = TrackBounds(group);
        AvailabilityRenderSpan?[] hitMap = group == EndpointGroup.Ru ? _providerHitMap : _worldHitMap;
        return (uint)point.X < (uint)hitMap.Length ? hitMap[point.X] : null;
    }

    private AvailabilityShortMarker[] BuildShortMarkers(AvailabilityRenderSpan[] segments, EndpointGroup group)
    {
        Rectangle track = TrackBounds(group);
        var markers = new List<AvailabilityShortMarker>();
        foreach (AvailabilityRenderSpan segment in segments)
        {
            if (!segment.Short)
                continue;

            int markerWidth = Math.Min(Math.Max(Scale(ShortSpanMinimumWidth), segment.Width), track.Width);
            int center = segment.Left + segment.Width / 2;
            int left = Math.Clamp(center - markerWidth / 2, track.Left, track.Right - markerWidth);
            var bounds = new Rectangle(left, track.Top, markerWidth, track.Height);
            markers.Add(new AvailabilityShortMarker(segment, bounds));
        }

        return markers.ToArray();
    }

    private void DrawShortMarkers(Graphics graphics, EndpointGroup group, Rectangle clip)
    {
        AvailabilityShortMarker[] markers = group == EndpointGroup.Ru ? _providerMarkers : _worldMarkers;
        int low = 0, high = markers.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (markers[middle].Bounds.Right <= clip.Left)
                low = middle + 1;
            else
                high = middle;
        }

        using var outline = new Pen(Color.FromArgb(205, UiTheme.Ink));
        for (int priority = 0; priority < 2; priority++)
        {
            for (int i = low; i < markers.Length && markers[i].Bounds.Left < clip.Right; i++)
            {
                AvailabilityShortMarker item = markers[i];
                bool red = !item.Span.Paused && item.Span.State == GroupAvailability.Offline;
                if (red != (priority == 1))
                    continue;

                Rectangle marker = item.Bounds;
                if (!clip.IntersectsWith(marker))
                    continue;

                graphics.FillRectangle(FillFor(item.Span.State, item.Span.Paused), marker);
                if (marker.Width > 1 && marker.Height > 1)
                    graphics.DrawRectangle(outline, marker.X, marker.Y, marker.Width - 1, marker.Height - 1);
            }
        }
    }

    private static AvailabilityShortMarker? HitShortMarker(Point point, AvailabilityShortMarker[] markers)
    {
        int low = 0, high = markers.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (markers[middle].Bounds.Right <= point.X)
                low = middle + 1;
            else
                high = middle;
        }

        AvailabilityShortMarker? hit = null;
        AvailabilityShortMarker? redHit = null;
        for (int i = low; i < markers.Length && markers[i].Bounds.Left <= point.X; i++)
        {
            AvailabilityShortMarker marker = markers[i];
            if (!marker.Bounds.Contains(point))
                continue;

            if (!marker.Span.Paused && marker.Span.State == GroupAvailability.Offline)
                redHit = marker;
            else
                hit = marker;
        }

        return redHit ?? hit;
    }

    private AvailabilityRenderSpan?[] BuildHitMap(AvailabilityRenderSpan[] segments)
    {
        var hitMap = new AvailabilityRenderSpan?[Math.Max(0, Width)];
        foreach (AvailabilityRenderSpan segment in segments)
        {
            int start = Math.Clamp(segment.Left, 0, hitMap.Length);
            int end = Math.Clamp(segment.Left + segment.Width, 0, hitMap.Length);
            for (int x = start; x < end; x++)
            {
                AvailabilityRenderSpan? existing = hitMap[x];
                if (existing is null || segment.StartUtc > existing.Value.StartUtc)
                    hitMap[x] = segment;
            }
        }

        return hitMap;
    }

    private void InvalidateHoverColumn(int oldX, int newX)
    {
        if (oldX >= 0)
            Invalidate(HoverInvalidationBounds(oldX));
        if (newX >= 0)
            Invalidate(HoverInvalidationBounds(newX));
    }

    private Rectangle TrackBounds(EndpointGroup group)
    {
        int left = Scale(14 + 80);
        int right = Math.Max(left + 1, Width - Scale(14));
        int y = Scale(group == EndpointGroup.Ru ? 39 : 61);
        int height = Scale(9);
        return new Rectangle(left, y, right - left, height);
    }

    private static SolidBrush FillFor(GroupAvailability state, bool paused)
        => paused ? PausedFill : state switch
        {
            GroupAvailability.Online => OnlineFill,
            GroupAvailability.Limited => LimitedFill,
            GroupAvailability.Offline => OfflineFill,
            _ => UnknownFill
        };

    private static string StateLabel(GroupAvailability state)
        => state switch
        {
            GroupAvailability.Online => "В сети",
            GroupAvailability.Limited => "Ограничено",
            GroupAvailability.Offline => "Нет связи",
            _ => "Нет данных"
        };

    private int Scale(int logical) => LocationChartCardChrome.Scale(this, logical);

    private readonly record struct LegendItemPlacement(Rectangle SwatchBounds, Rectangle TextBounds, SolidBrush Brush);
    private readonly record struct FooterLayout(LegendItemPlacement[] Items, Rectangle ToggleBounds, int PreferredHeight);

    private readonly record struct AvailabilityRenderSpan(
        EndpointGroup Group,
        GroupAvailability State,
        bool Paused,
        int Left,
        int Width,
        bool Short,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    private readonly record struct AvailabilityShortMarker(AvailabilityRenderSpan Span, Rectangle Bounds);
}
