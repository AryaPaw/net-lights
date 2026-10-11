using System.Drawing.Drawing2D;
using NetLights.Core;

namespace NetLights.App;

internal sealed class LocationSourceHistoryChart : Control
{
    private static readonly TimeSpan RenderRefresh = TimeSpan.FromSeconds(30);
    private static readonly SolidBrush NoDataFill = new(Color.FromArgb(226, 237, 248));
    private static readonly Color ConflictColor = Color.FromArgb(210, 202, 45, 50);
    private readonly LocationSourceKind _source;
    private readonly ChartToolTip _tip = new();
    private LocationSourceSpan[] _spans = [];
    private IReadOnlyList<LocationSourceCountryRun> _countryRuns = [];
    private IReadOnlyList<LocationObservation> _observations = [];
    private IReadOnlyList<LocationStay> _stays = [];
    private DateTimeOffset _now;
    private DateTimeOffset _builtAt;
    private TimeSpan _window = LocationChartWindows.Default;
    private string _tipText = "";

    internal int SegmentCount => _spans.Length;

    public LocationSourceHistoryChart(LocationSourceKind source)
    {
        _source = source;
        AccessibleName = source switch
        {
            LocationSourceKind.LocalDatabase => "geoIpDatabaseHistoryChart",
            LocationSourceKind.Online => "onlineCountryHistoryChart",
            _ => "iplocateDatabaseHistoryChart"
        };
        BackColor = UiTheme.Surface;
        DoubleBuffered = true;
        Margin = new Padding(0, 0, 0, 8);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void Bind(IReadOnlyList<LocationObservation> observations, IReadOnlyList<LocationStay> stays, DateTimeOffset now, TimeSpan window)
    {
        _observations = observations;
        _stays = stays;
        _now = now;
        _window = window;
        Rebuild();
    }

    public void Tick(DateTimeOffset now)
    {
        _now = now;
        if (_builtAt == default || now < _builtAt || now - _builtAt >= RenderRefresh)
            Rebuild();
    }

    private void Rebuild()
    {
        _spans = LocationSourceHistoryLayout.Build(_observations, _now, _window, _stays).ToArray();
        _countryRuns = LocationSourceHistoryLayout.CountryRuns(_spans, _source);
        _builtAt = _now;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
        => new(proposedSize.Width > 0 ? proposedSize.Width : Math.Max(Width, 240), Scale(112));

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        LocationChartCardChrome.Paint(g, this, Title);
        Rectangle track = TrackBounds();
        g.FillRectangle(NoDataFill, track);
        if (_spans.Length == 1 && _spans[0].LocalCountry is null && _spans[0].OnlineCountry is null
            && _spans[0].IplocateCountry is null)
        {
            TextRenderer.DrawText(g, _spans[0].FirstObservedUtc is null ? "Нет проверок" : "Нет результата", UiTheme.Caption, track, UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        DateTimeOffset start = _now - _window;
        using var conflictHatch = new HatchBrush(HatchStyle.ForwardDiagonal, Color.FromArgb(220, ConflictColor), Color.FromArgb(55, Color.White));
        using var conflictLine = new Pen(ConflictColor, Math.Max(1, Scale(2)));
        foreach (LocationSourceCountryRun run in _countryRuns)
        {
            Rectangle countryBounds = SpanBounds(track, start, run.StartedUtc, run.EndedUtc, minimumWidth: 1);
            if (countryBounds.Width > 0)
                g.FillRectangle(LocationChartPalette.FillBrush(run.Country), countryBounds);
        }

        DrawCountryLabels(g, track, start);

        foreach (LocationSourceSpan span in _spans)
        {
            if (!span.Conflict) continue;
            Rectangle conflictBounds = SpanBounds(track, start, span.StartedUtc, span.EndedUtc, minimumWidth: Scale(4));
            g.FillRectangle(conflictHatch, conflictBounds);
            g.DrawRectangle(conflictLine, conflictBounds.X, conflictBounds.Y, Math.Max(0, conflictBounds.Width - 1), Math.Max(0, conflictBounds.Height - 1));
            if (conflictBounds.Width >= Scale(16))
                TextRenderer.DrawText(g, "!", UiTheme.BodyBold, conflictBounds, Color.FromArgb(180, 30, 34),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        DrawAxis(g, track, start);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        LocationSourceSpan? span = HitTest(e.Location);
        string tip = span is LocationSourceSpan value ? BuildTip(value) : "";
        if (tip == _tipText) return;
        _tipText = tip;
        _tip.ShowFor(this, tip, e.Location);
    }

    internal LocationSourceSpan? HitTest(Point point)
    {
        Rectangle track = TrackBounds();
        if (!track.Contains(point)) return null;
        DateTimeOffset start = _now - _window;
        // Conflict markers are painted on top and can be wider than the event.
        // Their hit area must take the same priority as their visual layer.
        for (int i = _spans.Length - 1; i >= 0; i--)
        {
            LocationSourceSpan item = _spans[i];
            if (item.Conflict && SpanBounds(track, start, item.StartedUtc, item.EndedUtc, Scale(4)).Contains(point))
                return item;
        }
        foreach (LocationSourceSpan item in _spans)
        {
            if (SpanBounds(track, start, item.StartedUtc, item.EndedUtc, 1).Contains(point))
                return item;
        }
        return null;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _tipText = "";
        _tip.Clear(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    private string BuildTip(LocationSourceSpan span)
    {
        if (_source == LocationSourceKind.IplocateDatabase)
        {
            string result = span.IplocateCountry is null ? "Нет результата" : GeoCountryNames.Russian(span.IplocateCountry) + " · " + span.IplocateCountry;
            string version = span.IplocateDatabaseVersion is null ? "База еще не загружена" : "Кэш: " + span.IplocateDatabaseVersion;
            return result + "\n" + version + "\nGitHub-зеркало может отставать от свежей базы.\nIPLocate.io · CC BY-SA 4.0\nЛокальная проверка: IP не отправляется поставщику; источник не влияет на основную страну.";
        }
        string local = span.LocalCountry is null ? "нет результата" : GeoCountryNames.Russian(span.LocalCountry) + " · " + span.LocalCountry;
        string online = span.OnlineCountry is null ? "нет результата" : GeoCountryNames.Russian(span.OnlineCountry) + " · " + span.OnlineCountry;
        string current = _source == LocationSourceKind.LocalDatabase ? local : online;
        string third = span.ThirdCountry is null ? "" : "\nПроверка ipwho.is: "
            + GeoCountryNames.Russian(span.ThirdCountry) + " · " + span.ThirdCountry;
        string conflict = span.Conflict ? "\nКОНФЛИКТ: источники показывают разные страны" : "";
        string ip = span.PublicIp is null ? "" : "\nПубличный IP: " + span.PublicIp;
        string evidence = span.FirstObservedUtc is DateTimeOffset first && span.LastObservedUtc is DateTimeOffset last
            ? first == last
                ? "\nПроверка: " + last.ToLocalTime().ToString("dd.MM HH:mm:ss")
                : $"\nПроверки: {first.ToLocalTime():dd.MM HH:mm:ss} – {last.ToLocalTime():dd.MM HH:mm:ss}"
            : "\nПроверок в этом промежутке нет";
        string inferred = span.InferredFromStay
            ? "\nДальнейшие проверки не сохранены; цвет восстановлен по истории подтвержденной страны."
            : "";
        string ipChanged = span.PublicIpChanged ? "\nПубличный IP изменился при этой проверке." : "";
        string sourceNote = current == "нет результата"
            ? "\nУ этого источника нет результата. Это не означает потерю интернета."
            : "";
        return $"{current}{evidence}{ip}{ipChanged}{inferred}{sourceNote}\nЛокальная база: {local}\nОнлайн country.is: {online}{third}\nМежду проверками показано последнее известное значение; точное время смены неизвестно.{conflict}";
    }

    internal string BuildTipForTests(LocationSourceSpan span) => BuildTip(span);

    private string Title => _source switch
    {
        LocationSourceKind.LocalDatabase => "Debug · V2Fly",
        LocationSourceKind.Online => "Debug · country.is",
        _ => "Debug · IPLocate"
    };

    private void DrawCountryLabels(Graphics graphics, Rectangle track, DateTimeOffset start)
    {
        LocationSourceCountryRun? best = null;
        LocationSourceCountryRun? previous = null;
        void DrawBest()
        {
            if (best is not LocationSourceCountryRun selected) return;
            Rectangle bounds = SpanBounds(track, start, selected.StartedUtc, selected.EndedUtc, 1);
            if (bounds.Width < Scale(30)) return;
            TextRenderer.DrawText(graphics, selected.Country, UiTheme.IsoMono, bounds,
                LocationChartPalette.Ink(selected.Country), TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        foreach (LocationSourceCountryRun run in _countryRuns)
        {
            bool sameSequence = previous is LocationSourceCountryRun prior
                && string.Equals(prior.Country, run.Country, StringComparison.OrdinalIgnoreCase)
                && run.StartedUtc >= prior.EndedUtc
                && run.StartedUtc - prior.EndedUtc <= LocationHistory.ResumeMergeWindow;
            if (!sameSequence)
            {
                DrawBest();
                best = run;
            }
            else if (best is null || run.EndedUtc - run.StartedUtc > best.EndedUtc - best.StartedUtc)
                best = run;
            previous = run;
        }
        DrawBest();
    }

    internal Rectangle TrackBounds()
    {
        int left = Scale(LocationChartCardChrome.CardInset);
        int top = Scale(37);
        int right = Math.Max(left + 1, Width - Scale(LocationChartCardChrome.PlotRightPadding));
        return new Rectangle(left, top, right - left, Scale(24));
    }

    private Rectangle SpanBounds(Rectangle track, DateTimeOffset origin, DateTimeOffset from, DateTimeOffset to, int minimumWidth)
    {
        if (track.Width <= 0 || _window <= TimeSpan.Zero) return Rectangle.Empty;
        double begin = Math.Clamp((from - origin).Ticks / (double)_window.Ticks, 0, 1);
        double end = Math.Clamp((to - origin).Ticks / (double)_window.Ticks, 0, 1);
        int left = track.Left + (int)Math.Floor(begin * track.Width);
        int right = track.Left + (int)Math.Ceiling(end * track.Width);
        int width = Math.Max(0, right - left);
        if (width > 0 && width < minimumWidth)
        {
            int center = left + width / 2;
            width = Math.Min(minimumWidth, track.Width);
            left = Math.Clamp(center - width / 2, track.Left, track.Right - width);
        }
        return new Rectangle(left, track.Top, width, track.Height);
    }

    private void DrawAxis(Graphics g, Rectangle track, DateTimeOffset start)
    {
        using var grid = new Pen(UiTheme.Border);
        int y = track.Bottom + Scale(3);
        for (int i = 0; i < 3; i++)
        {
            double fraction = i / 2d;
            int x = track.Left + (int)(track.Width * fraction);
            g.DrawLine(grid, x, track.Bottom + 1, x, track.Bottom + Scale(4));
            DateTimeOffset time = start + TimeSpan.FromTicks((long)(_window.Ticks * fraction));
            Rectangle label = i == 0 ? new Rectangle(x, y, Scale(110), Scale(18))
                : i == 2 ? new Rectangle(x - Scale(110), y, Scale(110), Scale(18))
                : new Rectangle(x - Scale(55), y, Scale(110), Scale(18));
            TextRenderer.DrawText(g, i == 2 ? "сейчас" : time.ToLocalTime().ToString("d MMM, HH:mm"), UiTheme.Caption, label,
                UiTheme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        int legendX = Scale(14), legendY = Height - Scale(17);
        g.FillRectangle(NoDataFill, legendX, legendY + Scale(3), Scale(12), Scale(9));
        TextRenderer.DrawText(g, "Нет результата", UiTheme.Caption,
            new Rectangle(legendX + Scale(17), legendY, Scale(105), Scale(16)), UiTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        using var hatch = new HatchBrush(HatchStyle.ForwardDiagonal, Color.FromArgb(220, ConflictColor), Color.FromArgb(55, Color.White));
        int conflictX = legendX + Scale(133);
        g.FillRectangle(hatch, conflictX, legendY + Scale(3), Scale(12), Scale(9));
        TextRenderer.DrawText(g, "Конфликт источников", UiTheme.Caption,
            new Rectangle(conflictX + Scale(17), legendY, Math.Max(1, Width - conflictX - Scale(31)), Scale(16)), UiTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private int Scale(int logical) => LocationChartCardChrome.Scale(this, logical);
}
