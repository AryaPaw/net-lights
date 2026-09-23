using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationTimelinePanel : Panel
{
    private readonly TimeProvider _time;
    private readonly Panel _stack;
    private LocationHistory _history = new();
    private GeoCountryDisplay _live = GeoCountryDisplay.Unconfirmed;
    private string _fingerprint = "";
    private Label? _liveDuration;
    private LocationSpanChart? _chart;
    private SegmentTrack? _windowBar;
    private ThemedButton[] _windowButtons = [];
    private TimeSpan _chartWindow = LocationChartWindows.Default;
    private bool _layouting;

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
        SyncWindowButtons();
        if (_chart is not null)
        {
            DateTimeOffset now = _time.GetUtcNow();
            _chart.Bind(ChartStays(now), now, now - _chartWindow);
            return true;
        }

        _fingerprint = "";
        Rebuild();
        return true;
    }

    public TimeSpan ChartWindow => _chartWindow;

    public void Relayout() => SyncScrollSize();

    public void Bind(LocationHistory history, GeoCountryDisplay? live = null)
    {
        _history = history;
        _live = live ?? LiveFromHistory(history);
        string next = Fingerprint(history, _live);
        if (next == _fingerprint && _stack.Controls.Count > 0)
        {
            Tick();
            return;
        }

        _fingerprint = next;
        Rebuild();
    }

    public void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        _chart?.Tick(now);
        if (_liveDuration is null || !GeoCountryParsers.IsIso3166Alpha2(_live.Letters))
        {
            return;
        }

        LocationStay stay = LiveStay(now);
        string text = DurationLine(stay, now);
        if (_liveDuration.Text != text)
        {
            _liveDuration.Text = text;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        SyncScrollSize();
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
            if (!ReferenceEquals(child, _chart) && !ReferenceEquals(child, _windowBar))
            {
                child.Dispose();
            }
        }

        _liveDuration = null;
        DateTimeOffset now = _time.GetUtcNow();
        bool hasLive = true;
        if (_live == GeoCountryDisplay.Disabled)
        {
            HideChart();
            HideWindowBar();
            AddRow(DisabledState());
            hasLive = false;
        }
        else if (_history.Stays.Count == 0 && !GeoCountryParsers.IsIso3166Alpha2(_live.Letters))
        {
            HideChart();
            HideWindowBar();
            AddRow(EmptyState());
            hasLive = false;
        }

        IReadOnlyList<LocationStay> chartStays = ChartStays(now);
        DateTimeOffset windowStart = now - _chartWindow;
        bool showChart = hasLive || chartStays.Count > 0;
        if (showChart)
        {
            EnsureWindowBar();
            AddRow(Kicker("Период"));
            AddRow(_windowBar!);
            _chart ??= new LocationSpanChart();
            _chart.Bind(chartStays, now, windowStart);
            AddRow(_chart);
        }
        else
        {
            HideChart();
            HideWindowBar();
        }

        if (hasLive)
        {
            AddRow(Kicker(_live.Fresh ? "Сейчас" : "Последняя известная"));
            AddRow(StayRow(LiveStay(now), now, live: true));
        }

        IReadOnlyList<LocationStay> past = _history.Past;
        if (past.Count > 0)
        {
            AddRow(Kicker("Раньше"));
            foreach (LocationStay stay in past)
            {
                AddRow(StayRow(stay, now, live: false));
            }
        }

        _stack.ResumeLayout(true);
        ResumeLayout(true);
        SyncScrollSize();
        Tick();
    }

    private void AddRow(Control child) => _stack.Controls.Add(child);

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

        int total = y + _stack.Padding.Bottom;
        if (_stack.Height != total)
        {
            _stack.Height = total;
        }

        var min = new Size(0, total);
        if (AutoScrollMinSize != min)
        {
            AutoScrollMinSize = min;
        }
    }

    private Control StayRow(LocationStay stay, DateTimeOffset now, bool live)
    {
        string name = GeoCountryParsers.IsIso3166Alpha2(stay.Iso)
            ? GeoCountryNames.Russian(stay.Iso)
            : "Неизвестно";
        string when = live
            ? (stay.StartedUtc == default ? "" : "с " + LocationCopy.When(stay.StartedUtc))
            : LocationCopy.Range(stay.StartedUtc, stay.EndedUtc ?? now);
        var card = new StayCard(stay.Iso, name, when, DurationLine(stay, now), live);
        if (live)
        {
            _liveDuration = card.Duration;
        }

        return card;
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
            return stays;
        }

        if (stays.Count > 0
            && stays[^1].Iso == live.Iso
            && stays[^1].EndedUtc is null)
        {
            return stays;
        }

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

    private static string DurationLine(LocationStay stay, DateTimeOffset now)
    {
        if (stay.StartedUtc == default)
        {
            return "Длительность: —";
        }

        return "Длительность: " + LocationCopy.Duration(LocationCopy.Elapsed(stay, now));
    }

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

    private static string Fingerprint(LocationHistory history, GeoCountryDisplay live)
        => live.Letters + "|" + string.Join('|', history.Stays.Select(stay =>
            stay.Iso + stay.StartedUtc.UtcTicks + (stay.EndedUtc?.UtcTicks.ToString() ?? "-")));
}

internal sealed class IsoChip : Control
{
    public const int Edge = 32;

    public IsoChip(string iso)
    {
        ApplyEdge(Edge);
        Text = iso;
        Font = UiTheme.IsoMono;
        ForeColor = UiTheme.OnBrand;
        BackColor = UiTheme.Card;
        TabStop = false;
        AccessibleName = "isoChip";
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
    }

    public override Size GetPreferredSize(Size proposedSize) => Size;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyEdge(LogicalToDeviceUnits(Edge));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(g, box, 6, UiTheme.Brand950, UiTheme.Brand950);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private void ApplyEdge(int edge)
    {
        var size = new Size(edge, edge);
        MinimumSize = size;
        MaximumSize = size;
        Size = size;
    }
}

internal sealed class StayCard : Panel
{
    private const TextFormatFlags TextFlags =
        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    private readonly IsoChip _chip;
    private readonly Label _title;
    private readonly Label _when;

    public StayCard(string iso, string name, string when, string duration, bool live)
    {
        Margin = new Padding(0, 0, 0, 8);
        BackColor = UiTheme.Surface;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        _chip = new IsoChip(iso) { Margin = new Padding(0, 0, 12, 0) };
        _title = TextLine(name, UiTheme.BodyBold, UiTheme.Ink);
        _when = TextLine(when, UiTheme.Caption, UiTheme.Muted);
        Duration = TextLine(duration, UiTheme.Body, UiTheme.Muted);
        Duration.Margin = new Padding(0, 6, 0, 0);
        if (live)
        {
            Duration.AccessibleName = "currentLocationDuration";
        }

        Controls.Add(_chip);
        Controls.Add(_title);
        Controls.Add(_when);
        Controls.Add(Duration);
        Padding = new Padding(14, 12, 14, 12);
    }

    public Label Duration { get; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240);
        int textWidth = TextColumnWidth(width);
        int textHeight = Measure(_title, textWidth).Height
            + Measure(_when, textWidth).Height
            + Measure(Duration, textWidth).Height
            + Duration.Margin.Vertical;
        int chipHeight = _chip.GetPreferredSize(Size.Empty).Height;
        return new Size(width, Padding.Vertical + Math.Max(chipHeight, textHeight));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (!IsHandleCreated && Controls.Count < 4)
        {
            return;
        }

        Size chip = _chip.GetPreferredSize(Size.Empty);
        int x = Padding.Left;
        int y = Padding.Top;
        _chip.SetBounds(x, y, chip.Width, chip.Height);
        x += chip.Width + _chip.Margin.Horizontal;
        int textWidth = Math.Max(32, ClientSize.Width - Padding.Right - x);
        int titleHeight = Measure(_title, textWidth).Height;
        int whenHeight = Measure(_when, textWidth).Height;
        int durationHeight = Measure(Duration, textWidth).Height;
        _title.SetBounds(x, y, textWidth, titleHeight);
        y += titleHeight;
        _when.SetBounds(x, y, textWidth, whenHeight);
        y += whenHeight + Duration.Margin.Top;
        Duration.SetBounds(x, y, textWidth, durationHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent?.BackColor ?? UiTheme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(g, box, UiTheme.CardRadius, UiTheme.Card, UiTheme.Border);
    }

    private int TextColumnWidth(int cardWidth)
    {
        int chip = _chip.GetPreferredSize(Size.Empty).Width + _chip.Margin.Horizontal;
        return Math.Max(32, cardWidth - Padding.Horizontal - chip);
    }

    private static Label TextLine(string text, Font font, Color color)
        => new()
        {
            AutoSize = false,
            Text = text,
            Font = font,
            ForeColor = color,
            BackColor = UiTheme.Card,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            UseMnemonic = false
        };

    private static Size Measure(Label label, int width)
    {
        if (string.IsNullOrEmpty(label.Text) || width <= 0)
        {
            return new Size(width, label.Font.Height);
        }

        Size text = TextRenderer.MeasureText(
            label.Text,
            label.Font,
            new Size(width, int.MaxValue),
            TextFlags);
        return new Size(width, Math.Max(text.Height, label.Font.Height));
    }
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
