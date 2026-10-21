using NetLights.Networking;

namespace NetLights.App;

internal sealed class NetworkTrafficDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Dictionary<(string Category, string Host), DataGridViewRow> _rows = [];
    private readonly Label _summary = new() { AutoSize = true, Font = UiTheme.BodyBold, ForeColor = UiTheme.Ink };

    public NetworkTrafficDialog()
    {
        Text = "Трафик по источникам";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiTheme.Body;
        BackColor = UiTheme.Surface;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(900, 560);
        MinimumSize = new Size(650, 400);
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12),
            Font = UiTheme.Caption, ForeColor = UiTheme.Muted, AccessibleName = "trafficExplanation",
            Text = "Измерение с запуска приложения. Считаются данные соединений, включая TLS. "
                + "DNS, служебные пакеты TCP/IP, повторная передача и обновления программы не включены. "
                + "Счётчики не сохраняются на диск."
        }, 0, 0);
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = UiTheme.Card;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AccessibleName = "trafficSources";
        _grid.Margin = Padding.Empty;
        _grid.MultiSelect = false;
        _grid.EnableHeadersVisualStyles = false;
        _grid.GridColor = UiTheme.Border;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Track, ForeColor = UiTheme.Ink, Font = UiTheme.BodyBold,
            Padding = new Padding(8, 8, 8, 8), WrapMode = DataGridViewTriState.True,
            SelectionBackColor = UiTheme.Track, SelectionForeColor = UiTheme.Ink
        };
        _grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = UiTheme.Card, ForeColor = UiTheme.Ink, Font = UiTheme.Body,
            Padding = new Padding(8, 4, 8, 4), Alignment = DataGridViewContentAlignment.MiddleLeft,
            SelectionBackColor = UiTheme.Track, SelectionForeColor = UiTheme.Ink
        };
        _grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.Brand50;
        _grid.RowTemplate.Height = LogicalToDeviceUnits(32);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Add("category", "Источник");
        _grid.Columns.Add("host", "Сервер");
        _grid.Columns.Add("received", "Получено");
        _grid.Columns.Add("sent", "Отправлено");
        _grid.Columns[0].FillWeight = 150;
        _grid.Columns[1].FillWeight = 180;
        foreach (int index in new[] { 2, 3 })
        {
            _grid.Columns[index].MinimumWidth = 110;
            _grid.Columns[index].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns[index].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        foreach (DataGridViewColumn column in _grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        layout.Controls.Add(_grid, 0, 1);
        _summary.AccessibleName = "trafficSummary";
        _summary.Margin = new Padding(0, 12, 0, 0);
        layout.Controls.Add(_summary, 0, 2);
        Controls.Add(layout);
        _timer.Tick += (_, _) => RefreshTraffic();
        Shown += (_, _) => { RefreshTraffic(); _timer.Start(); };
    }

    private void RefreshTraffic()
    {
        IReadOnlyList<NetworkTrafficRow> snapshot = NetworkTraffic.Snapshot();
        _summary.Text = "Всего: получено " + FormatBytes(snapshot.Sum(row => row.ReceivedBytes))
            + ", отправлено " + FormatBytes(snapshot.Sum(row => row.SentBytes));
        foreach (NetworkTrafficRow row in snapshot)
        {
            var key = (row.Category, row.Host);
            if (!_rows.TryGetValue(key, out DataGridViewRow? existing))
            {
                int index = _grid.Rows.Add(row.Category, row.Host, "", "");
                _rows[key] = existing = _grid.Rows[index];
            }
            existing.Cells[2].Value = FormatBytes(row.ReceivedBytes);
            existing.Cells[3].Value = FormatBytes(row.SentBytes);
        }
    }

    internal static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F2} ГиБ"
            : bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):F2} МиБ"
            : bytes >= 1024 ? $"{bytes / 1024d:F1} КиБ" : $"{bytes} Б";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
