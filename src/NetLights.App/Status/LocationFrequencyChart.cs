using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationFrequencyChart : Control
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private readonly ToolTip _tip = new() { ShowAlways = true, InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 4000, UseAnimation = false, UseFading = false };
    private IReadOnlyList<LocationStay> _stays = [];
    private long _revision = -1;
    private LocationFrequencyModel _model = new(default, default, [], 0, 0, 0, []);
    private DateTimeOffset _now;
    private DateTimeOffset _lastCalculatedAt;
    private long _calculatedRevision = -1;
    private TimeSpan _window = LocationChartWindows.Default;
    private int _calculatedWidth = -1;
    private int _hoverIndex = -1;
    private string _summary = "";
    private PointF[] _seriesPoints = [];

    public LocationFrequencyChart()
    {
        AccessibleName = "Частота смен страны";
        DoubleBuffered = true;
        BackColor = UiTheme.Surface;
        Margin = new Padding(0, 0, 0, 10);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public string Summary => _summary;

    internal DateTimeOffset LastModelBuiltAt => _lastCalculatedAt;

    internal LocationFrequencyModel Model => _model;

    internal string HoverTipForTests => _tip.GetToolTip(this) ?? string.Empty;

    internal void DispatchMouseMoveForTests(Point point)
        => OnMouseMove(new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));

    public void Bind(IReadOnlyList<LocationStay> stays, long revision, DateTimeOffset now, TimeSpan window)
    {
        _stays = stays;
        _revision = revision;
        _now = now;
        bool geometryChanged = window != _window
            || _calculatedWidth != PlotBounds().Width;
        bool dataChanged = revision != _calculatedRevision;
        bool refreshDue = now < _lastCalculatedAt || now - _lastCalculatedAt >= RefreshInterval;
        _window = window;
        if (geometryChanged || dataChanged || refreshDue)
        {
            RebuildModel();
            Invalidate();
        }
    }

    public void Tick(DateTimeOffset now)
    {
        _now = now;
        if (now >= _lastCalculatedAt && now - _lastCalculatedAt < RefreshInterval)
            return;
        RebuildModel();
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
        => new(proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240), IsHandleCreated ? LogicalToDeviceUnits(176) : 176);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (IsHandleCreated && Width > 0)
        {
            RebuildModel();
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle plot = PlotBounds();
        if (!plot.Contains(e.Location) || _model.ChangesPerInterval.Count == 0)
        {
            ClearHover();
            return;
        }

        int nextIndex = SampleIndexAtX(e.X, plot.Left, plot.Width, _model.ChangesPerInterval.Count);
        if (nextIndex == _hoverIndex)
            return;
        int previousIndex = _hoverIndex;
        _hoverIndex = nextIndex;
        (DateTimeOffset from, DateTimeOffset to) = SampleRange(_hoverIndex);
        string value = _model.HistoryAvailablePerInterval[_hoverIndex]
            ? ChangesLabel(_model.ChangesPerInterval[_hoverIndex])
            : "Нет данных";
        string tip = $"{value}\n{from.ToLocalTime():dd.MM HH:mm} – {to.ToLocalTime():dd.MM HH:mm}";
        _tip.SetToolTip(this, tip);
        InvalidateHoverColumn(plot, previousIndex, _hoverIndex);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        ClearHover();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        LocationChartCardChrome.Paint(g, this, "Частота смен страны", _summary);

        Rectangle plot = PlotBounds();
        if (plot.Width <= 1 || plot.Height <= 1 || _model.ChangesPerInterval.Count == 0)
            return;

        int step = Math.Max(1, (int)Math.Ceiling(_model.PeakChangesPerInterval / 4d));
        int yMax = step * 4;
        using (var noHistory = new SolidBrush(UiTheme.Brand50))
        {
            for (int i = 0; i < _model.HistoryAvailablePerInterval.Count; i++)
            {
                if (!_model.HistoryAvailablePerInterval[i])
                    g.FillRectangle(noHistory, BucketBounds(plot, i, _model.HistoryAvailablePerInterval.Count));
            }
        }

        using var grid = new Pen(UiTheme.Border);
        for (int tick = 0; tick <= 4; tick++)
        {
            int y = plot.Bottom - plot.Height * tick / 4;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, (tick * step).ToString(), UiTheme.Caption, new Rectangle(Scale(5), y - Scale(8), plot.Left - Scale(9), Scale(17)), UiTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        GraphicsState state = g.Save();
        g.SetClip(plot);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(UiTheme.Brand800, Scale(2)))
        {
            line.LineJoin = LineJoin.Round;
            if (_seriesPoints.Length >= 2)
                g.DrawLines(line, _seriesPoints);
        }

        if (_hoverIndex >= 0 && _hoverIndex < _model.ChangesPerInterval.Count)
        {
            Rectangle bucket = BucketBounds(plot, _hoverIndex, _model.ChangesPerInterval.Count);
            float hoverX = bucket.Left + bucket.Width / 2f;
            float hoverY = plot.Bottom - (float)_model.ChangesPerInterval[_hoverIndex] / yMax * plot.Height;
            using var markerLine = new Pen(Color.FromArgb(135, UiTheme.Brand800)) { DashStyle = DashStyle.Dash };
            using var marker = new SolidBrush(UiTheme.Brand800);
            g.DrawLine(markerLine, hoverX, plot.Top, hoverX, plot.Bottom);
            g.FillEllipse(marker, hoverX - Scale(4), hoverY - Scale(4), Scale(8), Scale(8));
        }

        g.Restore(state);
        TextRenderer.DrawText(g, LocationCopy.When(_model.StartUtc), UiTheme.Caption, new Rectangle(plot.Left, plot.Bottom + Scale(2), plot.Width / 2, Scale(18)), UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "сейчас", UiTheme.Caption, new Rectangle(plot.Left + plot.Width / 2, plot.Bottom + Scale(2), plot.Width / 2, Scale(18)), UiTheme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }

    internal Rectangle PlotBounds()
    {
        int left = Scale(LocationChartCardChrome.PlotLeftPadding), right = Scale(LocationChartCardChrome.PlotRightPadding), top = Scale(40), bottom = Scale(27);
        return new Rectangle(left, top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
    }

    private void RebuildModel()
    {
        Rectangle plot = PlotBounds();
        _model = LocationFrequencyLayout.Build(_stays, _now, _window);
        _calculatedWidth = plot.Width;
        _calculatedRevision = _revision;
        _lastCalculatedAt = _now;

        _seriesPoints = BuildLinePoints(_model.ChangesPerInterval, plot);
        _summary = $"{ChangesLabel(_model.ChangeCount)} · {CountriesLabel(_model.CountryCount)}";
        AccessibleDescription = $"{_summary}. Значения показывают смены по интервалам выбранного периода.";
        _hoverIndex = -1;
        _tip.SetToolTip(this, "");
    }

    private (DateTimeOffset Start, DateTimeOffset End) SampleRange(int index)
    {
        int count = Math.Max(1, _model.ChangesPerInterval.Count);
        DateTimeOffset start = _model.StartUtc + TimeSpan.FromTicks((_model.EndUtc - _model.StartUtc).Ticks * index / count);
        DateTimeOffset end = _model.StartUtc + TimeSpan.FromTicks((_model.EndUtc - _model.StartUtc).Ticks * (index + 1) / count);
        return (start, end);
    }

    internal static PointF[] BuildLinePoints(IReadOnlyList<int> values, Rectangle plot)
    {
        if (values.Count == 0)
            return [];

        int step = Math.Max(1, (int)Math.Ceiling(values.Max() / 4d));
        int yMax = step * 4;
        var points = new PointF[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            int left = plot.Left + plot.Width * i / values.Count;
            int right = plot.Left + plot.Width * (i + 1) / values.Count;
            float y = plot.Bottom - (float)values[i] / yMax * plot.Height;
            points[i] = new PointF((left + right) / 2f, y);
        }

        return points;
    }

    private static string ChangesLabel(int count)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string unit = lastTwo is >= 11 and <= 14 ? "смен" : last switch
        {
            1 => "смена",
            2 or 3 or 4 => "смены",
            _ => "смен"
        };
        return $"{count} {unit}";
    }

    private static string CountriesLabel(int count)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string unit = lastTwo is >= 11 and <= 14 ? "стран" : last switch
        {
            1 => "страна",
            2 or 3 or 4 => "страны",
            _ => "стран"
        };
        return $"{count} {unit}";
    }

    private static Rectangle BucketBounds(Rectangle plot, int index, int count)
    {
        int left = plot.Left + plot.Width * index / count;
        int right = plot.Left + plot.Width * (index + 1) / count;
        return new Rectangle(left, plot.Top, Math.Max(1, right - left), plot.Height);
    }

    private void InvalidateHoverColumn(Rectangle plot, int oldIndex, int newIndex)
    {
        if (oldIndex >= 0)
            Invalidate(BucketBounds(plot, oldIndex, _model.ChangesPerInterval.Count));
        Invalidate(BucketBounds(plot, newIndex, _model.ChangesPerInterval.Count));
    }

    private void ClearHover()
    {
        if (_hoverIndex < 0)
            return;
        _hoverIndex = -1;
        _tip.Hide(this);
        Invalidate(PlotBounds());
    }

    private int Scale(int logical) => IsHandleCreated ? LogicalToDeviceUnits(logical) : logical;

    internal static int SampleIndexAtX(int x, int plotLeft, int plotWidth, int sampleCount)
    {
        if (sampleCount <= 1) return 0;
        return Math.Clamp((int)((x - plotLeft) * (double)sampleCount / Math.Max(1, plotWidth)), 0, sampleCount - 1);
    }
}
