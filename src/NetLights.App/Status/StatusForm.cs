using NetLights.Core;

namespace NetLights.App;

internal sealed class StatusForm : Form
{
    private readonly StatusListView _list = new();
    private readonly StatusListView _stats = new();
    private readonly StatusBadge _ruBadge = new();
    private readonly StatusBadge _vpnBadge = new();
    private readonly Label _confirmLine = new();
    private readonly Label _versionLabel = new();
    private readonly Label _footerHint = new();
    private readonly ComboBox _diagNode = new();
    private readonly ThemedButton _diagRun = new("Сравнить HTTPS, ICMP и TCP", true);
    private readonly TextBox _diagOut = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _autoUpdate = new();
    private readonly Label _updateLine = new();
    private readonly ThemedButton _tabLive = new("Состояние", true);
    private readonly ThemedButton _tabStats = new("Узлы", false);
    private readonly ThemedButton _tabDiag = new("Диагностика", false);
    private readonly ThemedButton _tabSettings = new("Параметры", false);
    private readonly Panel _livePage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _statsPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _diagPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _settingsPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly System.Windows.Forms.Timer _ages = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<Color, SolidBrush> _fills = [];
    private readonly Dictionary<Color, Pen> _pens = [];
    private MonitorSnapshot? _snapshot;
    private string _fingerprint = "";
    private bool _suppressSettings;
    public int DataBindCount { get; private set; }
    public int ClockUpdateCount { get; private set; }
    public int LayoutCount { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? AutoStartChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? AutoUpdateChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, Task<string>>? DiagnoseRequested { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? ExportRequested { get; set; }

    public StatusForm() : this(TimeProvider.System)
    {
    }

    public StatusForm(TimeProvider time)
    {
        _time = time;
        Text = "Net Lights";
        Font = UiTheme.Body;
        ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Surface;
        MinimumSize = new Size(920, 700);
        Width = 1000;
        Height = 800;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        DoubleBuffered = true;
        Padding = Padding.Empty;
        AccessibleName = "Net Lights";

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = UiTheme.Surface,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(BuildHeader(), 0, 0);
        shell.Controls.Add(BuildBody(), 0, 1);
        shell.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(shell);

        _ages.Tick += (_, _) =>
        {
            RefreshAges();
            ArmClockTimer();
        };
        Resize += (_, _) =>
        {
            ScaleColumns(_list, [70, 140, 100, 60, 110, 90, 120, 220]);
            ScaleColumns(_stats, [70, 120, 160, 80, 80, 80, 140, 260]);
        };
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

    public void BindSettings(bool autoStart, bool autoUpdate, string? updateNotice)
    {
        _suppressSettings = true;
        _autoStart.Checked = autoStart;
        _autoUpdate.Checked = autoUpdate;
        _updateLine.Text = string.IsNullOrWhiteSpace(updateNotice)
            ? "Проверка GitHub при запуске. Установка при обычном выходе, без автоперезапуска."
            : "Последняя ошибка обновления: " + updateNotice;
        _versionLabel.Text = "v" + ProductInfo.Version;
        _suppressSettings = false;
    }

    public void Bind(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        string fingerprint = StatusSnapshotProjector.Fingerprint(snapshot);
        _ruBadge.SetGroup("Провайдер", DiagnosticExport.Label(snapshot.Ru.Availability), true);
        _vpnBadge.SetGroup("VPN", DiagnosticExport.Label(snapshot.Vpn.Availability), false);
        if (snapshot.Paused)
        {
            _confirmLine.Text = "проверки на паузе";
            _confirmLine.Visible = true;
        }
        else
        {
            bool confirm = snapshot.Ru.ConfirmationActive || snapshot.Vpn.ConfirmationActive;
            _confirmLine.Text = confirm ? "идёт дополнительная проверка" : "";
            _confirmLine.Visible = confirm;
        }
        BindLive(StatusSnapshotProjector.Rows(snapshot));
        BindStats(NodeStatsProjector.Rows(snapshot));
        BindDiagNodes(snapshot);
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
            if (item.SubItems.Count > 6 && item.SubItems[6].Text != next)
            {
                item.SubItems[6].Text = next;
                changed = true;
            }
        }

        if (changed)
        {
            ClockUpdateCount++;
        }
    }

    private Panel BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Brand950,
            Padding = new Padding(UiTheme.PagePad, 16, UiTheme.PagePad, 16),
            ColumnCount = 1,
            RowCount = 3
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 3; i++)
        {
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        titleRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label
        {
            AutoSize = true,
            Text = "Net Lights",
            Font = UiTheme.Title,
            ForeColor = UiTheme.OnBrand,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        _versionLabel.AutoSize = true;
        _versionLabel.Text = "v" + ProductInfo.Version;
        _versionLabel.Font = UiTheme.Caption;
        _versionLabel.ForeColor = UiTheme.OnHeaderMuted;
        _versionLabel.BackColor = Color.Transparent;
        _versionLabel.Margin = new Padding(12, 10, 0, 0);
        _versionLabel.Cursor = Cursors.Hand;
        _versionLabel.Click += (_, _) => UiDrawing.OpenHttps(AppCredits.ReleasesUrl);
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(_versionLabel, 1, 0);

        var subtitle = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(860, 0),
            Text = "HTTPS HEAD контрольных адресов: провайдер слева, VPN справа. Это не весь интернет и не UDP/QUIC.",
            Font = UiTheme.Body,
            ForeColor = UiTheme.OnHeaderMuted,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };

        var statusRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        _confirmLine.AutoSize = true;
        _confirmLine.Font = UiTheme.Caption;
        _confirmLine.ForeColor = UiTheme.OnHeaderMuted;
        _confirmLine.BackColor = Color.Transparent;
        _confirmLine.Margin = new Padding(0, 6, 0, 0);
        statusRow.Controls.Add(_ruBadge);
        statusRow.Controls.Add(_vpnBadge);
        statusRow.Controls.Add(_confirmLine);
        _ruBadge.SetGroup("Провайдер", "нет данных", true);
        _vpnBadge.SetGroup("VPN", "нет данных", false);

        header.Controls.Add(titleRow, 0, 0);
        header.Controls.Add(subtitle, 0, 1);
        header.Controls.Add(statusRow, 0, 2);
        return header;
    }

    private Panel BuildBody()
    {
        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Padding = new Padding(UiTheme.PagePad, 16, UiTheme.PagePad, 8)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _tabLive.Click += (_, _) => ShowPage(0);
        _tabStats.Click += (_, _) => ShowPage(1);
        _tabDiag.Click += (_, _) => ShowPage(2);
        _tabSettings.Click += (_, _) => ShowPage(3);
        var tabs = new SegmentTrack(_tabLive, _tabStats, _tabDiag, _tabSettings)
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12)
        };
        FillLivePage();
        FillStatsPage();
        FillDiagPage();
        FillSettingsPage();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_settingsPage);
        host.Controls.Add(_diagPage);
        host.Controls.Add(_statsPage);
        host.Controls.Add(_livePage);
        body.Controls.Add(tabs, 0, 0);
        body.Controls.Add(host, 0, 1);
        ShowPage(0);
        return body;
    }

    private Panel BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BackColor = UiTheme.Brand50,
            Padding = new Padding(UiTheme.PagePad, 12, UiTheme.PagePad, 14),
            ColumnCount = 1,
            RowCount = 2
        };
        _footerHint.AutoSize = true;
        _footerHint.MaximumSize = new Size(900, 0);
        _footerHint.Text = "Крестик прячет окно в трей. Пауза и выход — из меню иконки. Статистика узлов считается с запуска.";
        _footerHint.Font = UiTheme.Caption;
        _footerHint.ForeColor = UiTheme.Muted;
        _footerHint.BackColor = Color.Transparent;
        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent
        };
        var github = new LinkLabel
        {
            AutoSize = true,
            Text = "Исходный код",
            Font = UiTheme.Caption,
            LinkColor = UiTheme.Brand800,
            ActiveLinkColor = UiTheme.Brand900,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent
        };
        var releases = new LinkLabel
        {
            AutoSize = true,
            Text = "Releases",
            Font = UiTheme.Caption,
            LinkColor = UiTheme.Brand800,
            ActiveLinkColor = UiTheme.Brand900,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = Color.Transparent,
            Margin = new Padding(16, 0, 0, 0)
        };
        github.LinkClicked += (_, _) => UiDrawing.OpenHttps(AppCredits.RepositoryUrl);
        releases.LinkClicked += (_, _) => UiDrawing.OpenHttps(AppCredits.ReleasesUrl);
        links.Controls.Add(github);
        links.Controls.Add(releases);
        footer.Controls.Add(_footerHint, 0, 0);
        footer.Controls.Add(links, 0, 1);
        return footer;
    }

    private void ShowPage(int index)
    {
        _livePage.Visible = index == 0;
        _statsPage.Visible = index == 1;
        _diagPage.Visible = index == 2;
        _settingsPage.Visible = index == 3;
        _tabLive.Primary = index == 0;
        _tabStats.Primary = index == 1;
        _tabDiag.Primary = index == 2;
        _tabSettings.Primary = index == 3;
        if (index == 0)
        {
            ScaleColumns(_list, [70, 140, 100, 60, 110, 90, 120, 220]);
        }

        if (index == 1)
        {
            ScaleColumns(_stats, [70, 120, 160, 80, 80, 80, 140, 260]);
        }
    }

    private void FillLivePage()
    {
        var hint = Hint("Проверки идут сами по расписанию. Колонка «С запуска» — доля успешных HTTPS с этого запуска.");
        ConfigureList(_list, "Текущие проверки", DrawRow);
        _list.Columns.Add("Группа", 70);
        _list.Columns.Add("Адрес", 140);
        _list.Columns.Add("Результат", 100);
        _list.Columns.Add("HTTP", 60);
        _list.Columns.Add("Задержка", 110);
        _list.Columns.Add("С запуска", 90);
        _list.Columns.Add("Проверено", 120);
        _list.Columns.Add("Причина", 220);
        _list.Layout += (_, _) => LayoutCount++;
        _livePage.Controls.Add(_list);
        _livePage.Controls.Add(hint);
    }

    private void FillStatsPage()
    {
        var hint = Hint("Худшие узлы сверху. 403/429 — похоже на ограничение. Низкий успех — кандидат на замену в пуле.");
        ConfigureList(_stats, "Статистика узлов", DrawPlain);
        _stats.Columns.Add("Группа", 70);
        _stats.Columns.Add("Узел", 120);
        _stats.Columns.Add("Хост", 160);
        _stats.Columns.Add("Проверок", 80);
        _stats.Columns.Add("Успех", 80);
        _stats.Columns.Add("403/429", 80);
        _stats.Columns.Add("Последнее", 140);
        _stats.Columns.Add("Вывод", 260);
        _statsPage.Controls.Add(_stats);
        _statsPage.Controls.Add(hint);
    }

    private void FillDiagPage()
    {
        var intro = Hint("Сравнение HTTPS с ICMP и TCP:443. Если ping есть, а HTTPS нет — режут HTTPS, а не «весь интернет». Обычный Online/Offline ICMP не использует.");
        intro.Dock = DockStyle.Top;
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 0, 0, 8)
        };
        _diagNode.DropDownStyle = ComboBoxStyle.DropDownList;
        _diagNode.Width = 280;
        _diagNode.Font = UiTheme.Body;
        _diagNode.AccessibleName = "Узел для диагностики";
        _diagRun.AutoSize = false;
        _diagRun.Size = new Size(280, UiTheme.ButtonHeight);
        _diagRun.Stretch = false;
        _diagRun.Click += async (_, _) => await RunDiagnosticsAsync();
        row.Controls.Add(_diagNode);
        row.Controls.Add(_diagRun);
        _diagOut.Dock = DockStyle.Fill;
        _diagOut.Multiline = true;
        _diagOut.ReadOnly = true;
        _diagOut.BorderStyle = BorderStyle.FixedSingle;
        _diagOut.Font = UiTheme.Body;
        _diagOut.BackColor = UiTheme.Card;
        _diagOut.ScrollBars = ScrollBars.Vertical;
        _diagOut.AccessibleName = "Результат диагностики";
        _diagPage.Controls.Add(_diagOut);
        _diagPage.Controls.Add(row);
        _diagPage.Controls.Add(intro);
    }

    private void FillSettingsPage()
    {
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(4)
        };
        _autoStart.Text = "Запускать вместе с Windows";
        _autoStart.AutoSize = true;
        _autoStart.Font = UiTheme.Body;
        _autoStart.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettings)
            {
                AutoStartChanged?.Invoke(_autoStart.Checked);
            }
        };
        _autoUpdate.Text = "Ставить обновления с GitHub при выходе";
        _autoUpdate.AutoSize = true;
        _autoUpdate.Font = UiTheme.Body;
        _autoUpdate.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettings)
            {
                AutoUpdateChanged?.Invoke(_autoUpdate.Checked);
            }
        };
        _updateLine.AutoSize = true;
        _updateLine.MaximumSize = new Size(820, 0);
        _updateLine.Font = UiTheme.Caption;
        _updateLine.ForeColor = UiTheme.Muted;
        var export = new ThemedButton("Экспорт журнала", false);
        export.Stretch = false;
        export.Size = new Size(180, UiTheme.ButtonHeight);
        export.Click += (_, _) => ExportRequested?.Invoke();
        stack.Controls.Add(_autoStart);
        stack.Controls.Add(_autoUpdate);
        stack.Controls.Add(_updateLine);
        stack.Controls.Add(export);
        _settingsPage.Controls.Add(stack);
    }

    private static Label Hint(string text)
    {
        return new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Padding = new Padding(0, 8, 0, 0),
            Text = text,
            Font = UiTheme.Caption,
            ForeColor = UiTheme.Muted,
            BackColor = Color.Transparent
        };
    }

    private void BindLive(List<EndpointRow> rows)
    {
        SyncCount(_list, rows.Count, 8);
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
            Set(item, 5, row.Rate);
            Set(item, 6, StatusSnapshotProjector.AgeLabel(row.LastCompleted, now));
            Set(item, 7, row.Reason);
            PaintRow(item, row.Back, row.Fore);
        }
    }

    private void BindStats(List<NodeStatRow> rows)
    {
        SyncCount(_stats, rows.Count, 8);
        for (int i = 0; i < rows.Count; i++)
        {
            NodeStatRow row = rows[i];
            ListViewItem item = _stats.Items[i];
            Set(item, 0, row.Group);
            Set(item, 1, row.Id);
            Set(item, 2, row.Uri);
            Set(item, 3, row.Samples.ToString());
            Set(item, 4, row.Success);
            Set(item, 5, row.Restricted.ToString());
            Set(item, 6, row.Last);
            Set(item, 7, row.Verdict);
        }
    }

    private void BindDiagNodes(MonitorSnapshot snapshot)
    {
        string? selected = _diagNode.SelectedItem as string;
        var ids = snapshot.Ru.Endpoints.Concat(snapshot.Vpn.Endpoints).Select(e => e.Id).ToList();
        if (_diagNode.Items.Count == ids.Count && ids.SequenceEqual(_diagNode.Items.Cast<string>()))
        {
            return;
        }

        _diagNode.Items.Clear();
        foreach (string id in ids)
        {
            _diagNode.Items.Add(id);
        }

        if (selected is not null && ids.Contains(selected))
        {
            _diagNode.SelectedItem = selected;
        }
        else if (_diagNode.Items.Count > 0)
        {
            _diagNode.SelectedIndex = 0;
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        if (DiagnoseRequested is null || _diagNode.SelectedItem is not string id)
        {
            return;
        }

        _diagRun.Enabled = false;
        _diagOut.Text = "Идёт сравнение...";
        try
        {
            _diagOut.Text = await DiagnoseRequested(id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _diagOut.Text = "Не удалось выполнить диагностику: " + ex.Message;
        }
        finally
        {
            _diagRun.Enabled = true;
        }
    }

    private static void ConfigureList(StatusListView list, string accessibleName, DrawListViewSubItemEventHandler draw)
    {
        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = false;
        list.HideSelection = false;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.OwnerDraw = true;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Font = UiTheme.Body;
        list.BackColor = UiTheme.Card;
        list.AccessibleName = accessibleName;
        list.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        list.DrawItem += (_, e) =>
        {
            if (e.Item is not null && e.Item.Selected)
            {
                e.DrawDefault = false;
            }
        };
        list.DrawSubItem += draw;
    }

    private static void SyncCount(ListView list, int count, int columns)
    {
        bool resize = list.Items.Count != count;
        if (resize)
        {
            list.BeginUpdate();
        }

        try
        {
            string[] blank = Enumerable.Repeat("", columns).ToArray();
            while (list.Items.Count < count)
            {
                list.Items.Add(new ListViewItem(blank));
            }

            while (list.Items.Count > count)
            {
                list.Items.RemoveAt(list.Items.Count - 1);
            }
        }
        finally
        {
            if (resize)
            {
                list.EndUpdate();
            }
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

    private void DrawRow(object? sender, DrawListViewSubItemEventArgs e) => DrawCell(e, true);

    private void DrawPlain(object? sender, DrawListViewSubItemEventArgs e) => DrawCell(e, false);

    private void DrawCell(DrawListViewSubItemEventArgs e, bool useItemColors)
    {
        if (e.Item is null || e.SubItem is null)
        {
            return;
        }

        Color back = e.Item.Selected
            ? SystemColors.Highlight
            : useItemColors ? e.Item.BackColor : SystemColors.Window;
        Color fore = e.Item.Selected
            ? SystemColors.HighlightText
            : useItemColors ? e.Item.ForeColor : SystemColors.WindowText;
        e.Graphics.FillRectangle(Fill(back), e.Bounds);
        var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem.Text,
            e.Item.Font,
            textBounds,
            fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        Color grid = Color.FromArgb(55, fore);
        e.Graphics.DrawLine(Grid(grid), e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
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
        ScaleColumns(_list, [70, 140, 100, 60, 110, 90, 120, 220]);
        ScaleColumns(_stats, [70, 120, 160, 80, 80, 80, 140, 260]);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScaleColumns(_list, [70, 140, 100, 60, 110, 90, 120, 220]);
        ScaleColumns(_stats, [70, 120, 160, 80, 80, 80, 140, 260]);
    }

    private static void ScaleColumns(ListView list, int[] weights)
    {
        if (list.Columns.Count < weights.Length)
        {
            return;
        }

        int total = weights.Sum();
        int width = Math.Max(list.ClientSize.Width, 700);
        for (int i = 0; i < weights.Length; i++)
        {
            list.Columns[i].Width = Math.Max(50, width * weights[i] / total);
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
