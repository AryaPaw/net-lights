using NetLights.Core;

namespace NetLights.App;

internal sealed class StatusForm : Form
{
    private readonly StatusListView _list = new();
    private readonly Label _summary = new();
    private readonly Label _hint = new();
    private readonly System.Windows.Forms.Timer _ages = new();
    private readonly TimeProvider _time;
    private readonly Font _summaryFont;
    private readonly Dictionary<Color, SolidBrush> _fills = [];
    private readonly Dictionary<Color, Pen> _pens = [];
    private MonitorSnapshot? _snapshot;
    private string _fingerprint = "";
    public int DataBindCount { get; private set; }
    public int ClockUpdateCount { get; private set; }
    public int LayoutCount { get; private set; }

    public StatusForm() : this(TimeProvider.System)
    {
    }

    public StatusForm(TimeProvider time)
    {
        _time = time;
        Text = "Net Lights";
        MinimumSize = new Size(980, 680);
        Width = 1040;
        Height = 720;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        AccessibleName = "Состояние Net Lights";

        _summaryFont = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        _summary.Dock = DockStyle.Top;
        _summary.Height = 36;
        _summary.Padding = new Padding(12, 8, 12, 0);
        _summary.Font = _summaryFont;
        _summary.AccessibleName = "Сводка групп";

        _hint.Dock = DockStyle.Bottom;
        _hint.Height = 40;
        _hint.Padding = new Padding(12, 8, 12, 8);
        _hint.Text = "Проверяется HTTPS контрольных адресов, не весь интернет и не UDP/QUIC.";

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = false;
        _list.HideSelection = false;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.OwnerDraw = true;
        _list.AccessibleName = "Результаты проверки адресов";
        _list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _list.DrawItem += (_, e) =>
        {
            if (e.Item is not null && e.Item.Selected)
            {
                e.DrawDefault = false;
            }
        };
        _list.DrawSubItem += DrawRow;
        _list.Columns.Add("Группа", 80);
        _list.Columns.Add("Адрес", 160);
        _list.Columns.Add("Результат", 120);
        _list.Columns.Add("HTTP", 70);
        _list.Columns.Add("Задержка HTTPS", 130);
        _list.Columns.Add("Проверено", 150);
        _list.Columns.Add("Причина", 280);
        _list.Layout += (_, _) => LayoutCount++;

        _ages.Tick += (_, _) =>
        {
            RefreshAges();
            ArmClockTimer();
        };

        Controls.Add(_list);
        Controls.Add(_hint);
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
        => StatusSnapshotProjector.Fingerprint(snapshot) != _fingerprint;

    public void Bind(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        string fingerprint = StatusSnapshotProjector.Fingerprint(snapshot);
        string title = StatusSnapshotProjector.Title(snapshot);
        string summary = StatusSnapshotProjector.Summary(snapshot);
        if (Text != title)
        {
            Text = title;
        }

        if (_summary.Text != summary)
        {
            _summary.Text = summary;
        }

        List<EndpointRow> rows = StatusSnapshotProjector.Rows(snapshot);
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

            DateTimeOffset now = _time.GetUtcNow();
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
                Set(item, 5, StatusSnapshotProjector.AgeLabel(row.LastCompleted, now));
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

        DataBindCount++;
        _fingerprint = fingerprint;
        if (Visible && !_ages.Enabled)
        {
            ArmClockTimer();
            _ages.Enabled = true;
        }
    }

    internal static string AgeLabel(DateTimeOffset? utc, DateTimeOffset now)
        => StatusSnapshotProjector.AgeLabel(utc, now);

    internal static (Color Back, Color Fore) RowStyle(EndpointGroup group, ProbeOutcome? outcome)
        => StatusSnapshotProjector.RowStyle(group, outcome, false);

    public void RefreshAges()
    {
        if (!Visible || IsDisposed)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        bool changed = false;
        for (int i = 0; i < _list.Items.Count; i++)
        {
            ListViewItem item = _list.Items[i];
            DateTimeOffset? completed = item.Tag is DateTimeOffset value ? value : null;
            string next = StatusSnapshotProjector.AgeLabel(completed, now);
            if (item.SubItems.Count > 5 && item.SubItems[5].Text != next)
            {
                item.SubItems[5].Text = next;
                changed = true;
            }
        }

        if (changed)
        {
            ClockUpdateCount++;
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

    private void DrawRow(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
        {
            return;
        }

        Color back = e.Item.Selected ? SystemColors.Highlight : e.Item.BackColor;
        Color fore = e.Item.Selected ? SystemColors.HighlightText : e.Item.ForeColor;
        e.Graphics.FillRectangle(Fill(back), e.Bounds);
        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            e.Item.Font,
            textBounds,
            fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        Color grid = Color.FromArgb(55, e.Item.ForeColor);
        e.Graphics.DrawLine(Grid(grid), e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        e.Graphics.DrawLine(Grid(grid), e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
    }

    private SolidBrush Fill(Color color)
    {
        if (_fills.TryGetValue(color, out SolidBrush? brush))
        {
            return brush;
        }

        brush = new SolidBrush(color);
        _fills[color] = brush;
        return brush;
    }

    private Pen Grid(Color color)
    {
        if (_pens.TryGetValue(color, out Pen? pen))
        {
            return pen;
        }

        pen = new Pen(color);
        _pens[color] = pen;
        return pen;
    }

    private void ArmClockTimer()
    {
        DateTimeOffset now = _time.GetUtcNow();
        int ms = 1000 - now.Millisecond;
        if (ms < 50)
        {
            ms += 1000;
        }

        _ages.Interval = ms;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ScaleColumns();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScaleColumns();
    }

    private void ScaleColumns()
    {
        if (_list.Columns.Count < 7)
        {
            return;
        }

        int[] weights = [80, 160, 120, 70, 130, 150, 280];
        int total = weights.Sum();
        int width = Math.Max(_list.ClientSize.Width, 700);
        for (int i = 0; i < weights.Length; i++)
        {
            _list.Columns[i].Width = Math.Max(50, width * weights[i] / total);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _ages.Enabled = Visible && !IsDisposed;
        if (Visible)
        {
            ArmClockTimer();
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
            _summaryFont.Dispose();
            foreach (SolidBrush brush in _fills.Values)
            {
                brush.Dispose();
            }

            foreach (Pen pen in _pens.Values)
            {
                pen.Dispose();
            }

            _fills.Clear();
            _pens.Clear();
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
}
