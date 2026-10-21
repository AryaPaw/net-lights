using NetLights.Networking;

namespace NetLights.App;

internal sealed class NetworkTrafficDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly Dictionary<(string Category, string Host), DataGridViewRow> _rows = [];

    public NetworkTrafficDialog()
    {
        Text = "Трафик по источникам";
        Font = UiTheme.Body;
        BackColor = UiTheme.Surface;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 560);
        MinimumSize = new Size(650, 400);
        ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12),
            Text = "Измерение с запуска приложения. Считаются данные соединений, включая TLS. "
                + "DNS, служебные пакеты TCP/IP, повторная передача и обновления программы не включены. "
                + "Счётчики не сохраняются на диск."
        });
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = UiTheme.Card;
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Add("category", "Источник");
        _grid.Columns.Add("host", "Сервер");
        _grid.Columns.Add("received", "Получено");
        _grid.Columns.Add("sent", "Отправлено");
        _grid.Columns[0].FillWeight = 150;
        _grid.Columns[1].FillWeight = 180;
        foreach (DataGridViewColumn column in _grid.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
        layout.Controls.Add(_grid);
        Controls.Add(layout);
        _timer.Tick += (_, _) => RefreshTraffic();
        Shown += (_, _) => { RefreshTraffic(); _timer.Start(); };
    }

    private void RefreshTraffic()
    {
        foreach (NetworkTrafficRow row in NetworkTraffic.Snapshot())
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
