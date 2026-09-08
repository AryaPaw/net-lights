using NetLights.Core;

namespace NetLights.App;

internal sealed class StatusForm : Form
{
    private readonly StatusListView _list = new();
    private readonly Label _summary = new();
    private readonly Label _updated = new();
    private readonly Label _hint = new();
    private readonly System.Windows.Forms.Timer _ages = new();
    private MonitorSnapshot? _snapshot;
    private string _fingerprint = "";

    public StatusForm()
    {
        Text = "Net Lights";
        MinimumSize = new Size(980, 680);
        Width = 1040;
        Height = 720;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        _summary.Dock = DockStyle.Top;
        _summary.Height = 36;
        _summary.Padding = new Padding(12, 8, 12, 0);
        _summary.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);

        _updated.Dock = DockStyle.Top;
        _updated.Height = 24;
        _updated.Padding = new Padding(12, 0, 12, 0);

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 40;
        _hint.Padding = new Padding(12, 8, 12, 8);
        _hint.Text = "Проверяется HTTPS контрольных адресов, не весь интернет и не UDP/QUIC.";

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = true;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, _) => { };
        _list.DrawSubItem += DrawRow;
        _list.Columns.Add("Группа", 80);
        _list.Columns.Add("Адрес", 160);
        _list.Columns.Add("Результат", 120);
        _list.Columns.Add("HTTP", 70);
        _list.Columns.Add("Задержка HTTPS", 130);
        _list.Columns.Add("Проверено", 150);
        _list.Columns.Add("Причина", 280);

        _ages.Interval = 250;
        _ages.Tick += (_, _) => RefreshAges();

        Controls.Add(_list);
        Controls.Add(_hint);
        Controls.Add(_updated);
        Controls.Add(_summary);
    }

    public void PlaceCentered()
    {
        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle area = screen.WorkingArea;
        int width = Math.Min(Width, area.Width);
        int height = Math.Min(Height, area.Height);
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - width) / 2),
            area.Top + Math.Max(0, (area.Height - height) / 2));
    }

    public bool NeedsBind(MonitorSnapshot snapshot)
        => Fingerprint(snapshot) != _fingerprint;

    public void Bind(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        string fingerprint = Fingerprint(snapshot);
        string title = $"Net Lights - Провайдер: {DiagnosticExport.Label(snapshot.Ru.Availability)}, VPN: {DiagnosticExport.Label(snapshot.Vpn.Availability)}";
        string summary = $"Провайдер: {DiagnosticExport.Label(snapshot.Ru.Availability)}  |  VPN: {DiagnosticExport.Label(snapshot.Vpn.Availability)}";
        if (snapshot.Ru.ConfirmationActive || snapshot.Vpn.ConfirmationActive)
        {
            summary += "  |  идёт дополнительная проверка";
        }

        if (Text != title)
        {
            Text = title;
        }

        if (_summary.Text != summary)
        {
            _summary.Text = summary;
        }

        List<EndpointRow> rows = Rows(snapshot);
        bool resize = _list.Items.Count != rows.Count;
        if (resize)
        {
            _list.BeginUpdate();
        }

        try
        {
            while (_list.Items.Count < rows.Count)
            {
                _list.Items.Add(new ListViewItem(["", "", "", "", "", "", ""]));
            }

            while (_list.Items.Count > rows.Count)
            {
                _list.Items.RemoveAt(_list.Items.Count - 1);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (int i = 0; i < rows.Count; i++)
            {
                EndpointRow row = rows[i];
                ListViewItem item = _list.Items[i];
                item.Tag = row.LastCompleted;
                Set(item, 0, row.Group);
                Set(item, 1, row.Id);
                Set(item, 2, row.Result);
                Set(item, 3, row.Http);
                Set(item, 4, row.Delay);
                Set(item, 5, AgeLabel(row.LastCompleted, now));
                Set(item, 6, row.Reason);
                PaintRow(item, row.Back, row.Fore);
            }
        }
        finally
        {
            if (resize)
            {
                _list.EndUpdate();
            }
        }

        RefreshHeaderAge();
        _fingerprint = fingerprint;
        if (Visible && !_ages.Enabled)
        {
            _ages.Enabled = true;
        }
    }

    private static void Set(ListViewItem item, int index, string value)
    {
        if (index == 0)
        {
            if (item.Text != value)
            {
                item.Text = value;
            }

            return;
        }

        while (item.SubItems.Count <= index)
        {
            item.SubItems.Add("");
        }

        if (item.SubItems[index].Text != value)
        {
            item.SubItems[index].Text = value;
        }
    }

    private static void PaintRow(ListViewItem item, Color back, Color fore)
    {
        if (item.BackColor != back)
        {
            item.BackColor = back;
        }

        if (item.ForeColor != fore)
        {
            item.ForeColor = fore;
        }

        item.UseItemStyleForSubItems = true;
    }

    private static void DrawRow(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
        {
            return;
        }

        using var fill = new SolidBrush(e.Item.BackColor);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            e.Item.Font,
            textBounds,
            e.Item.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        Color grid = Color.FromArgb(55, e.Item.ForeColor);
        using var pen = new Pen(grid);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
    }

    private static List<EndpointRow> Rows(MonitorSnapshot snapshot)
    {
        var rows = new List<EndpointRow>(14);
        AddGroup(rows, "Провайдер", snapshot.Ru);
        AddGroup(rows, "VPN", snapshot.Vpn);
        return rows;
    }

    private static void AddGroup(List<EndpointRow> rows, string groupName, GroupSnapshot group)
    {
        foreach (EndpointView endpoint in group.Endpoints)
        {
            string result = ResultLabel(endpoint);
            string reason = endpoint.Paused
                ? "пауза сервера"
                : endpoint.Failure is { } failure
                    ? DiagnosticExport.FailureLabel(failure)
                    : endpoint.Outcome == ProbeOutcome.Reachable
                        ? ""
                        : endpoint.Fresh
                            ? ""
                            : "нет свежих данных";
            (Color back, Color fore) = RowStyle(group.Group, endpoint.Outcome);
            rows.Add(new EndpointRow(
                groupName,
                endpoint.Id,
                result,
                endpoint.HttpStatus?.ToString() ?? "",
                endpoint.Elapsed is { } elapsed ? $"{elapsed.TotalMilliseconds:0} мс" : "",
                endpoint.LastCompletedUtc,
                reason,
                back,
                fore));
        }
    }

    internal static (Color Back, Color Fore) RowStyle(EndpointGroup group, ProbeOutcome? outcome)
    {
        bool down = outcome is ProbeOutcome.Unreachable or ProbeOutcome.Cancelled;
        bool ok = outcome == ProbeOutcome.Reachable;
        if (group == EndpointGroup.Ru)
        {
            if (ok)
            {
                return (Color.FromArgb(214, 234, 247), Color.FromArgb(12, 54, 90));
            }

            if (down)
            {
                return (Color.FromArgb(18, 52, 86), Color.FromArgb(196, 220, 238));
            }

            return (Color.FromArgb(126, 168, 204), Color.FromArgb(14, 40, 64));
        }

        if (ok)
        {
            return (Color.FromArgb(247, 226, 196), Color.FromArgb(92, 48, 10));
        }

        if (down)
        {
            return (Color.FromArgb(90, 44, 12), Color.FromArgb(247, 220, 176));
        }

        return (Color.FromArgb(196, 140, 72), Color.FromArgb(48, 24, 8));
    }

    private static string ResultLabel(EndpointView endpoint)
    {
        if (endpoint.Outcome is null)
        {
            return endpoint.InFlight ? "проверка" : "нет данных";
        }

        return endpoint.Outcome switch
        {
            ProbeOutcome.Reachable => "доступен",
            ProbeOutcome.Unreachable => "нет ответа",
            ProbeOutcome.Indeterminate => "неясно",
            ProbeOutcome.Cancelled => "отмена",
            _ => endpoint.Outcome.ToString() ?? "нет данных"
        };
    }

    internal static string AgeLabel(DateTimeOffset? utc, DateTimeOffset now)
    {
        if (utc is null)
        {
            return "";
        }

        int seconds = (int)Math.Max(0, Math.Floor((now - utc.Value).TotalSeconds));
        return seconds + " сек назад";
    }

    public void RefreshAges()
    {
        if (!Visible || IsDisposed)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < _list.Items.Count; i++)
        {
            ListViewItem item = _list.Items[i];
            DateTimeOffset? completed = item.Tag is DateTimeOffset value ? value : null;
            Set(item, 5, AgeLabel(completed, now));
        }

        RefreshHeaderAge();
    }

    private void RefreshHeaderAge()
    {
        if (_snapshot is null)
        {
            return;
        }

        string updated = "Снимок: " + AgeLabel(_snapshot.GeneratedUtc, DateTimeOffset.UtcNow);
        if (_updated.Text != updated)
        {
            _updated.Text = updated;
        }
    }

    private static string Fingerprint(MonitorSnapshot snapshot)
        => string.Join("|", Rows(snapshot).Select(r => $"{r.Group}:{r.Id}:{r.Result}:{r.Http}:{r.Delay}:{r.LastCompleted?.UtcTicks}:{r.Reason}"))
           + "|" + snapshot.Ru.Availability + snapshot.Vpn.Availability
           + "|" + snapshot.Ru.ConfirmationActive + snapshot.Vpn.ConfirmationActive;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _ages.Enabled = Visible && !IsDisposed;
        if (Visible)
        {
            RefreshAges();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ages.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class StatusListView : ListView
    {
        public StatusListView()
        {
            DoubleBuffered = true;
        }
    }

    private readonly record struct EndpointRow(
        string Group,
        string Id,
        string Result,
        string Http,
        string Delay,
        DateTimeOffset? LastCompleted,
        string Reason,
        Color Back,
        Color Fore);
}
