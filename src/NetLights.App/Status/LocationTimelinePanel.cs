using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationTimelinePanel : Panel
{
    private readonly TimeProvider _time;
    private readonly Panel _stack;
    private LocationHistory _history = new();
    private GeoCountryDisplay _live = GeoCountryDisplay.Unconfirmed;
    private LocationHistory? _boundHistory;
    private long _boundRevision = -1;
    private GeoCountryDisplay _boundLive = GeoCountryDisplay.Unconfirmed;
    private LocationSpanChart? _chart;
    private LocationFrequencyChart? _frequency;
    private SegmentTrack? _windowBar;
    private ThemedButton[] _windowButtons = [];
    private TimeSpan _chartWindow = LocationChartWindows.Default;
    private bool _layouting;
    private HistoryRow[] _historyRows = [];
    private int _historyStartY;
    private int _historyRowHeight = 44;
    private string _historyTipText = "";
    private readonly ToolTip _historyTip = new() { ShowAlways = true, InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 5000 };
    private int HistoryHeaderHeight => Scale(12);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<int>? ChartWindowHoursChanged { get; set; }

    public LocationTimelinePanel(TimeProvider time)
    {
        _time = time;
        AutoScroll = true;
        AutoScrollMinSize = Size.Empty;
        HorizontalScroll.Enabled = false;
        HorizontalScroll.Visible = false;
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        AccessibleName = "locationTimeline";
        Padding = Padding.Empty;
        _stack = new Panel
        {
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 8),
            Location = Point.Empty
        };
        Controls.Add(_stack);
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                SyncScrollSize();
            }
        };
        Rebuild();
    }

    public void SetChartWindow(TimeSpan window)
    {
        if (!ApplyChartWindowHours(LocationChartWindows.ToHours(window)))
        {
            return;
        }

        ChartWindowHoursChanged?.Invoke(LocationChartWindows.ToHours(_chartWindow));
    }

    public bool ApplyChartWindowHours(int hours)
    {
        TimeSpan next = LocationChartWindows.ParseHours(hours);
        if (next == _chartWindow)
        {
            SyncWindowButtons();
            return false;
        }

        _chartWindow = next;
        AutoScrollPosition = Point.Empty;
        SyncWindowButtons();
        _boundRevision = -1;
        Rebuild();
        return true;
    }

    public TimeSpan ChartWindow => _chartWindow;

    internal int VisibleHistoryRowCount => _historyRows.Length;
    internal int HistoryRowHeight => _historyRowHeight;
    internal Rectangle HistoryRowBounds(int index)
    {
        if ((uint)index >= (uint)_historyRows.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        int y = _historyStartY + HistoryHeaderHeight + index * _historyRowHeight + AutoScrollPosition.Y;
        return new Rectangle(Scale(12), y, Math.Max(1, VisibleWidth() - Scale(24)), _historyRowHeight);
    }

    public void Relayout() => SyncScrollSize();

    public void Bind(LocationHistory history, GeoCountryDisplay? live = null)
    {
        _history = history;
        _live = live ?? LiveFromHistory(history);
        if (ReferenceEquals(history, _boundHistory)
            && history.Revision == _boundRevision
            && _live == _boundLive
            && _stack.Controls.Count > 0)
        {
            Tick();
            return;
        }

        _boundHistory = history;
        _boundRevision = history.Revision;
        _boundLive = _live;
        Rebuild();
    }

    public void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        _chart?.Tick(now);
        _frequency?.Tick(now);
        InvalidateCurrentHistoryRow();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SyncScrollSize();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawVirtualHistory(e.Graphics, e.ClipRectangle);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        HistoryRow? row = HistoryRowAt(e.Location);
        string tip = row is HistoryRow value ? HistoryTip(value, _time.GetUtcNow()) : "";
        if (string.Equals(_historyTipText, tip, StringComparison.Ordinal))
            return;

        _historyTipText = tip;
        _historyTip.SetToolTip(this, tip);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _historyTipText = "";
        _historyTip.SetToolTip(this, "");
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _historyTip.Dispose();
            if (_frequency?.Parent is null) _frequency?.Dispose();
            if (_chart?.Parent is null) _chart?.Dispose();
            if (_windowBar?.Parent is null) _windowBar?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    private void Rebuild()
    {
        SuspendLayout();
        _stack.SuspendLayout();
        Control[] previous = [.. _stack.Controls.Cast<Control>()];
        _stack.Controls.Clear();
        foreach (Control child in previous)
        {
            if (!ReferenceEquals(child, _chart) && !ReferenceEquals(child, _windowBar) && !ReferenceEquals(child, _frequency))
            {
                child.Dispose();
            }
        }

        DateTimeOffset now = _time.GetUtcNow();
        bool enabled = _live != GeoCountryDisplay.Disabled;
        bool hasKnownLocation = _history.Stays.Any(stay => stay.EndedUtc is not null)
            || GeoCountryParsers.IsIso3166Alpha2(_live.Letters);
        if (_live == GeoCountryDisplay.Disabled)
        {
            HideChart();
            HideWindowBar();
            AddRow(DisabledState());
        }
        else if (!hasKnownLocation)
        {
            HideChart();
            HideWindowBar();
            AddRow(EmptyState());
        }

        IReadOnlyList<LocationStay> chartStays = ChartStays(now);
        DateTimeOffset windowStart = now - _chartWindow;
        if (enabled && chartStays.Count > 0)
        {
            EnsureWindowBar();
            AddRow(_windowBar!);
            _chart ??= new LocationSpanChart();
            _chart.Bind(chartStays, now, windowStart);
            AddRow(_chart);
            _frequency ??= new LocationFrequencyChart();
            _frequency.Visible = true;
            _frequency.Bind(chartStays, _history.Revision, now, _chartWindow);
            AddRow(_frequency);
        }
        else
        {
            HideChart();
            HideWindowBar();
            if (_frequency is not null)
            {
                _frequency.Visible = false;
            }
        }

        _historyRows = BuildHistoryRows(chartStays, windowStart, enabled);
        if (_historyRows.Length > 0)
            AddRow(HistoryHeading());

        _stack.ResumeLayout(true);
        ResumeLayout(true);
        SyncScrollSize();
        Tick();
    }

    private void AddRow(Control child) => _stack.Controls.Add(child);

    private Control HistoryHeading()
    {
        return new Label
        {
            AutoSize = true,
            Text = "История смен",
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Ink,
            BackColor = UiTheme.Surface,
            Margin = new Padding(0, 10, 0, 2),
            Padding = Padding.Empty,
            UseMnemonic = false,
            AccessibleName = "locationHistoryHeading"
        };
    }

    private HistoryRow[] BuildHistoryRows(IReadOnlyList<LocationStay> stays, DateTimeOffset visibleSince, bool enabled)
    {
        var rows = new List<HistoryRow>(stays.Count);
        for (int i = stays.Count - 1; i >= 0; i--)
        {
            LocationStay stay = stays[i];
            if (!GeoCountryParsers.IsIso3166Alpha2(stay.Iso) || stay.StartedUtc == default)
                continue;

            bool current = i == stays.Count - 1 && stay.EndedUtc is null && enabled;
            if (stay.EndedUtc is null && !current)
                continue;
            if (current || stay.EndedUtc is null || stay.EndedUtc > visibleSince)
                rows.Add(new HistoryRow(stay, current));
        }

        return rows.ToArray();
    }

    private void DrawVirtualHistory(Graphics graphics, Rectangle clip)
    {
        if (_historyRows.Length == 0 || _historyStartY <= 0)
            return;

        int pad = Scale(12);
        int rowAreaTop = _historyStartY + AutoScrollPosition.Y;
        int rowTop = rowAreaTop + HistoryHeaderHeight;
        int rowsHeight = checked(_historyRows.Length * _historyRowHeight);
        int totalHeight = HistoryHeaderHeight + rowsHeight + pad;
        int left = Scale(0);
        int width = Math.Max(1, Math.Min(ClientSize.Width, VisibleWidth()));
        Rectangle card = new(left, rowAreaTop, width, totalHeight);
        Rectangle visible = Rectangle.Intersect(card, clip);
        if (visible.Width <= 0 || visible.Height <= 0)
            return;

        using var cardFill = new SolidBrush(UiTheme.Card);
        graphics.FillRectangle(cardFill, visible);
        using var border = new Pen(UiTheme.Border);
        graphics.DrawLine(border, card.Left, card.Top, card.Right - 1, card.Top);
        graphics.DrawLine(border, card.Left, card.Top, card.Left, card.Bottom - 1);
        graphics.DrawLine(border, card.Right - 1, card.Top, card.Right - 1, card.Bottom - 1);
        if (clip.Bottom >= card.Bottom - 1)
            graphics.DrawLine(border, card.Left, card.Bottom - 1, card.Right - 1, card.Bottom - 1);

        int first = Math.Clamp((clip.Top - rowTop) / _historyRowHeight, 0, _historyRows.Length - 1);
        int last = Math.Clamp((clip.Bottom - rowTop) / _historyRowHeight, 0, _historyRows.Length - 1);
        DateTimeOffset now = _time.GetUtcNow();
        for (int index = first; index <= last; index++)
        {
            int y = rowTop + index * _historyRowHeight;
            DrawHistoryRow(graphics, new Rectangle(card.Left + pad, y, card.Width - pad * 2, _historyRowHeight), _historyRows[index], now);
        }
    }

    private void DrawHistoryRow(Graphics graphics, Rectangle bounds, HistoryRow row, DateTimeOffset now)
    {
        if (row.Current)
        {
            using var currentFill = new SolidBrush(UiTheme.Brand50);
            graphics.FillRectangle(currentFill, bounds);
        }

        int edge = Scale(28);
        int insetX = Scale(2);
        int badgeY = bounds.Top + (bounds.Height - edge) / 2;
        Rectangle badge = new(bounds.Left + insetX, badgeY, edge, edge);
        UiDrawing.PaintRounded(graphics, badge, Scale(6), LocationChartPalette.Fill(row.Stay.Iso), LocationChartPalette.Fill(row.Stay.Iso));
        TextRenderer.DrawText(graphics, row.Stay.Iso, UiTheme.IsoMono, badge, LocationChartPalette.Ink(row.Stay.Iso), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        int x = badge.Right + Scale(10);
        int rightInset = Scale(4);
        int durationWidth = Scale(86);
        int available = Math.Max(1, bounds.Right - rightInset - x);
        durationWidth = Math.Min(durationWidth, Math.Max(Scale(58), available / 3));
        int nameWidth = Math.Min(Scale(160), Math.Max(Scale(90), available / 4));
        int gap = Scale(12);
        int timeWidth = Math.Max(1, available - nameWidth - durationWidth - gap * 2);
        string country = GeoCountryNames.Russian(row.Stay.Iso);
        string when = row.Current
            ? (_live.Fresh ? "Сейчас" : "Последняя известная") + " · с " + LocationCopy.When(row.Stay.StartedUtc)
            : LocationCopy.Range(row.Stay.StartedUtc, row.Stay.EndedUtc ?? now);
        string duration = LocationCopy.Duration(LocationCopy.Elapsed(row.Stay, now));
        int textHeight = Scale(22);
        int textY = bounds.Top + (bounds.Height - textHeight) / 2;
        TextRenderer.DrawText(graphics, country, UiTheme.BodyBold, new Rectangle(x, textY, nameWidth, textHeight), LocationChartPalette.HistoryLabel(row.Stay.Iso), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int whenX = x + nameWidth + gap;
        TextRenderer.DrawText(graphics, when, UiTheme.Caption, new Rectangle(whenX, textY, timeWidth, textHeight), UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        int durationX = whenX + timeWidth + gap;
        TextRenderer.DrawText(graphics, duration, UiTheme.Caption, new Rectangle(durationX, textY, durationWidth, textHeight), UiTheme.Ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        using var separator = new Pen(UiTheme.Border);
        graphics.DrawLine(separator, bounds.Left, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
    }

    private HistoryRow? HistoryRowAt(Point point)
    {
        if (_historyRows.Length == 0)
            return null;

        int virtualY = point.Y - AutoScrollPosition.Y;
        int rowStart = _historyStartY + HistoryHeaderHeight;
        if (virtualY < rowStart)
            return null;
        int index = (virtualY - rowStart) / _historyRowHeight;
        return (uint)index < (uint)_historyRows.Length ? _historyRows[index] : null;
    }

    private static string HistoryTip(HistoryRow row, DateTimeOffset now)
    {
        string country = GeoCountryNames.Russian(row.Stay.Iso);
        string when = row.Current
            ? "Текущая страна · с " + LocationCopy.When(row.Stay.StartedUtc)
            : LocationCopy.Range(row.Stay.StartedUtc, row.Stay.EndedUtc ?? now);
        return $"{country} · {row.Stay.Iso}\n{when}\n{LocationCopy.Duration(LocationCopy.Elapsed(row.Stay, now))}";
    }

    private void InvalidateCurrentHistoryRow()
    {
        if (_historyRows.Length == 0 || !_historyRows[0].Current)
            return;

        int y = _historyStartY + AutoScrollPosition.Y + HistoryHeaderHeight;
        Rectangle row = new(0, y, ClientSize.Width, _historyRowHeight);
        Rectangle visible = Rectangle.Intersect(ClientRectangle, row);
        if (visible.Height > 0)
            Invalidate(visible);
    }

    private int VisibleWidth()
    {
        int width = Math.Max(0, ClientSize.Width);
        if (width < 400 && FindForm() is Form form)
        {
            width = Math.Max(width, form.ClientSize.Width - (UiTheme.PagePad * 2));
        }

        return width;
    }

    private void SyncScrollSize()
    {
        if (_layouting || _stack is null)
        {
            return;
        }

        _layouting = true;
        try
        {
            int width = -1;
            for (int pass = 0; pass < 2; pass++)
            {
                int next = VisibleWidth();
                if (next == width && pass > 0)
                {
                    break;
                }

                width = next;
                LayoutStack(width);
            }

            if (HorizontalScroll.Visible)
            {
                HorizontalScroll.Visible = false;
            }
        }
        finally
        {
            _layouting = false;
        }
    }

    private void LayoutStack(int width)
    {
        _stack.Width = width;
        _historyRowHeight = Scale(44);
        int y = _stack.Padding.Top;
        int inner = Math.Max(0, width - _stack.Padding.Horizontal);
        foreach (Control child in _stack.Controls)
        {
            if (child is Label label)
            {
                label.MaximumSize = new Size(Math.Max(32, inner), 0);
            }

            int x = _stack.Padding.Left + child.Margin.Left;
            int childWidth = Math.Max(0, inner - child.Margin.Horizontal);
            Size pref = child.GetPreferredSize(new Size(childWidth, 0));
            int height = Math.Max(pref.Height, child.MinimumSize.Height);
            child.SetBounds(x, y + child.Margin.Top, childWidth, height);
            y += child.Margin.Vertical + height;
        }

        int topHeight = y + _stack.Padding.Bottom;
        if (_stack.Height != topHeight)
        {
            _stack.Height = topHeight;
        }

        _historyStartY = topHeight;
        long contentHeight = (long)topHeight + (_historyRows.Length == 0
            ? 0
            : HistoryHeaderHeight + (long)_historyRows.Length * _historyRowHeight + Scale(12));
        int totalHeight = (int)Math.Clamp(contentHeight, 0, int.MaxValue);
        var min = new Size(0, totalHeight);
        if (AutoScrollMinSize != min)
        {
            AutoScrollMinSize = min;
        }
    }

    private void HideChart()
    {
        if (_chart is null)
        {
            return;
        }

        _chart.Dispose();
        _chart = null;
    }

    private void HideWindowBar()
    {
        if (_windowBar is null)
        {
            return;
        }

        _windowBar.Dispose();
        _windowBar = null;
        _windowButtons = [];
    }

    private void EnsureWindowBar()
    {
        if (_windowBar is not null)
        {
            SyncWindowButtons();
            return;
        }

        _windowButtons = new ThemedButton[LocationChartWindows.All.Length];
        for (int i = 0; i < LocationChartWindows.All.Length; i++)
        {
            TimeSpan window = LocationChartWindows.All[i];
            string caption = LocationChartWindows.Captions[i];
            var button = new ThemedButton(caption, window == _chartWindow)
            {
                AccessibleName = "locationChartWindow" + LocationChartWindows.ToHours(window)
            };
            TimeSpan captured = window;
            button.Click += (_, _) => SetChartWindow(captured);
            _windowButtons[i] = button;
        }

        _windowBar = new SegmentTrack(_windowButtons)
        {
            Margin = new Padding(0, 0, 0, 10),
            AccessibleName = "locationChartWindow"
        };
        SyncWindowButtons();
    }

    private void SyncWindowButtons()
    {
        for (int i = 0; i < _windowButtons.Length; i++)
        {
            _windowButtons[i].Primary = LocationChartWindows.All[i] == _chartWindow;
        }
    }

    private IReadOnlyList<LocationStay> ChartStays(DateTimeOffset now)
    {
        var stays = new List<LocationStay>(_history.Stays);
        LocationStay live = LiveStay(now);
        if (live.StartedUtc == default || !GeoCountryParsers.IsIso3166Alpha2(live.Iso))
        {
            if (_live == GeoCountryDisplay.Unconfirmed && stays.Count > 0 && stays[^1].EndedUtc is null)
                stays.RemoveAt(stays.Count - 1);
            return stays;
        }

        if (stays.Count > 0 && stays[^1].EndedUtc is null)
        {
            LocationStay previous = stays[^1];
            if (string.Equals(previous.Iso, live.Iso, StringComparison.OrdinalIgnoreCase))
                return stays;

            DateTimeOffset transition = live.StartedUtc > previous.StartedUtc ? live.StartedUtc : now;
            if (transition < previous.StartedUtc)
                transition = previous.StartedUtc;
            stays[^1] = previous with { EndedUtc = transition };
            live = live with { StartedUtc = transition };
        }

        if (stays.Count == 0 || stays[^1].StartedUtc != live.StartedUtc || !string.Equals(stays[^1].Iso, live.Iso, StringComparison.OrdinalIgnoreCase))
            stays.Add(live);
        return stays;
    }

    private LocationStay LiveStay(DateTimeOffset now)
    {
        if (GeoCountryParsers.IsIso3166Alpha2(_live.Letters)
            && _history.Current is LocationStay current
            && current.Iso == _live.Letters)
        {
            return current;
        }

        if (GeoCountryParsers.IsIso3166Alpha2(_live.Letters))
        {
            return new LocationStay(_live.Letters, now, null);
        }

        return new LocationStay("??", default, null);
    }

    private int Scale(int logical) => LocationChartCardChrome.Scale(this, logical);

    private readonly record struct HistoryRow(LocationStay Stay, bool Current);

    private static GeoCountryDisplay LiveFromHistory(LocationHistory history)
        => history.Current is LocationStay current
            ? GeoCountryDisplay.Confirmed(current.Iso)
            : GeoCountryDisplay.Unconfirmed;

    private static Label Kicker(string text)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = UiTheme.Caption,
            ForeColor = UiTheme.Muted,
            BackColor = UiTheme.Surface,
            Margin = new Padding(4, 4, 0, 8),
            UseMnemonic = false
        };

    private static Label DisabledState()
        => new()
        {
            AutoSize = true,
            Text = "Определение страны выключено.",
            Font = UiTheme.Body,
            ForeColor = UiTheme.Muted,
            BackColor = UiTheme.Surface,
            AccessibleName = "disabledLocations",
            Padding = new Padding(4, 8, 4, 8),
            UseMnemonic = false
        };

    private static Label EmptyState()
        => new()
        {
            AutoSize = true,
            Text = "Ещё нет стран. Здесь появятся только смены страны выхода и сколько каждая держалась.",
            Font = UiTheme.Body,
            ForeColor = UiTheme.Muted,
            BackColor = UiTheme.Surface,
            AccessibleName = "emptyLocations",
            Padding = new Padding(4, 8, 4, 8),
            UseMnemonic = false
        };

}

internal sealed class RoundedCard : Panel
{
    public RoundedCard()
    {
        BackColor = UiTheme.Surface;
        Padding = new Padding(16, 14, 16, 14);
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240);
        int inner = Math.Max(32, width - Padding.Horizontal);
        int height = Padding.Vertical;
        foreach (Control child in Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            Size pref = child.GetPreferredSize(new Size(inner - child.Margin.Horizontal, 0));
            height += pref.Height + child.Margin.Vertical;
        }

        return new Size(width, height);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        int inner = Math.Max(0, ClientSize.Width - Padding.Horizontal);
        int y = Padding.Top;
        foreach (Control child in Controls)
        {
            if (!child.Visible)
            {
                continue;
            }

            Size pref = child.GetPreferredSize(new Size(Math.Max(32, inner - child.Margin.Horizontal), 0));
            child.SetBounds(
                Padding.Left + child.Margin.Left,
                y + child.Margin.Top,
                Math.Max(0, inner - child.Margin.Horizontal),
                pref.Height);
            y += pref.Height + child.Margin.Vertical;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(g, box, UiTheme.CardRadius, UiTheme.Card, UiTheme.Border);
    }
}
