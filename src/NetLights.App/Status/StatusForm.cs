using NetLights.Core;

namespace NetLights.App;

internal sealed class StatusForm : Form
{
    private readonly StatusListView _list = new();
    private readonly StatusBadge _ruBadge = new();
    private readonly StatusBadge _worldBadge = new();
    private readonly Label _confirmLine = new();
    private readonly Label _versionLabel = new();
    private readonly ComboBox _diagNode = new();
    private readonly ThemedButton _diagRun = new("Сравнить HTTPS, ICMP и TCP", true);
    private readonly TextBox _diagOut = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _autoUpdate = new();
    private readonly CheckBox _geoCountryIcon = new();
    private readonly Label _letterSizeLabel = new();
    private readonly ComboBox _letterSize = new();
    private readonly Label _updateLine = new();
    private readonly ThemedButton _checkUpdates = new("Проверить обновления", true);
    private readonly ThemedButton _exportLog = new("Экспорт журнала", false);
    private readonly Label _manualUpdateLine = new();
    private readonly ThemedButton _tabLive = new("Состояние", true);
    private readonly ThemedButton _tabLocations = new("Локации", false);
    private readonly ThemedButton _tabDiag = new("Диагностика", false);
    private readonly ThemedButton _tabSettings = new("Параметры", false);
    private readonly Panel _livePage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _locationsPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _diagPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly Panel _settingsPage = new() { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
    private readonly System.Windows.Forms.Timer _ages = new();
    private readonly TimeProvider _time;
    private readonly LocationTimelinePanel _locations;
    private readonly Icon? _windowIcon;
    private readonly Dictionary<Color, SolidBrush> _fills = [];
    private readonly Dictionary<Color, Pen> _pens = [];
    private MonitorSnapshot? _snapshot;
    private string _fingerprint = "";
    private bool _suppressSettings;
    private bool _allowShow;
    public int DataBindCount { get; private set; }
    public int ClockUpdateCount { get; private set; }
    public int LayoutCount { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? AutoStartChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? AutoUpdateChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? GeoCountryIconChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<GeoCountryLetterScale>? GeoCountryLetterScaleChanged { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, Task<string>>? DiagnoseRequested { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? ExportRequested { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? CheckUpdatesRequested { get; set; }

    public StatusForm() : this(TimeProvider.System)
    {
    }

    public StatusForm(TimeProvider time)
    {
        _time = time;
        _locations = new LocationTimelinePanel(_time);
        AutoScaleMode = AutoScaleMode.None;
        Text = ProductInfo.DisplayName();
        Font = UiTheme.Body;
        ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Surface;
        _windowIcon = AppBranding.LoadWindowIcon();
        if (_windowIcon is not null)
        {
            Icon = _windowIcon;
        }
        MinimumSize = new Size(UiTheme.WindowMinWidth, UiTheme.WindowMinHeight);
        Width = UiTheme.WindowDefaultWidth;
        Height = UiTheme.WindowDefaultHeight;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Padding = Padding.Empty;
        AccessibleName = ProductInfo.DisplayName();

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Padding = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        shell.Controls.Add(BuildHeader(), 0, 0);
        shell.Controls.Add(BuildBody(), 0, 1);
        Controls.Add(shell);

        _ages.Tick += (_, _) =>
        {
            RefreshAges();
            ArmClockTimer();
        };
        Resize += (_, _) =>
        {
            ScaleColumns(_list, [120, 140, 100, 60, 110, 90, 120, 170]);
        };
    }

    public void PlaceCentered()
    {
        Screen screen = Screen.FromPoint(Cursor.Position);
        Rectangle area = screen.WorkingArea;
        int width = Math.Min(Math.Max(MinimumSize.Width, Width), Math.Max(320, area.Width));
        int height = Math.Min(Math.Max(MinimumSize.Height, Height), Math.Max(240, area.Height));
        if (width > area.Width)
        {
            width = area.Width;
        }

        if (height > area.Height)
        {
            height = area.Height;
        }

        Size = new Size(Math.Max(320, width), Math.Max(240, height));
        Location = new Point(
            area.Left + Math.Max(0, (area.Width - Width) / 2),
            area.Top + Math.Max(0, (area.Height - Height) / 2));
    }

    public void Reveal()
    {
        _allowShow = true;
        ShowInTaskbar = true;
        Show();
        Activate();
    }

    internal bool HideToTrayIfUserClosing(CloseReason reason)
    {
        if (reason != CloseReason.UserClosing)
        {
            return false;
        }

        _allowShow = false;
        Hide();
        return true;
    }

    public bool NeedsBind(MonitorSnapshot snapshot)
        => StatusSnapshotProjector.Fingerprint(snapshot) != _fingerprint;

    public void BindSettings(
        bool autoStart,
        bool autoUpdate,
        bool geoCountryIcon,
        GeoCountryLetterScale letterScale,
        string? updateNotice)
    {
        _suppressSettings = true;
        _autoStart.Checked = autoStart;
        _autoUpdate.Checked = autoUpdate;
        _geoCountryIcon.Checked = geoCountryIcon;
        _letterSize.SelectedIndex = (int)GeoCountryLetterScales.Parse((int)letterScale);
        SyncLetterSizeVisibility();
        _updateLine.Text = string.IsNullOrWhiteSpace(updateNotice)
            ? "Обновления с GitHub ставятся тихо, когда есть сеть."
            : "Последняя ошибка обновления: " + updateNotice;
        _versionLabel.Text = "v" + ProductInfo.Version;
        _suppressSettings = false;
    }

    public void SetManualUpdateState(bool busy, string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetManualUpdateState(busy, text));
            return;
        }

        _checkUpdates.Enabled = !busy;
        _manualUpdateLine.Text = text;
    }

    public void Bind(MonitorSnapshot snapshot)
    {
        _snapshot = snapshot;
        string fingerprint = StatusSnapshotProjector.Fingerprint(snapshot);
        _ruBadge.SetGroup(GroupLabels.Provider, DiagnosticExport.Label(snapshot.Ru.Availability), true);
        _worldBadge.SetGroup(GroupLabels.World, DiagnosticExport.Label(snapshot.World.Availability), false);
        if (!string.IsNullOrWhiteSpace(snapshot.MonitorError))
        {
            _confirmLine.Text = snapshot.MonitorError;
            _confirmLine.Visible = true;
        }
        else if (snapshot.Paused)
        {
            _confirmLine.Text = "проверки на паузе";
            _confirmLine.Visible = true;
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.ConfigWarning))
        {
            _confirmLine.Text = snapshot.ConfigWarning;
            _confirmLine.Visible = true;
        }
        else if (!snapshot.UsingBuiltinPool)
        {
            _confirmLine.Text = "свой список адресов";
            _confirmLine.Visible = true;
        }
        else
        {
            bool confirm = snapshot.Ru.ConfirmationActive || snapshot.World.ConfirmationActive;
            _confirmLine.Text = confirm ? "идёт дополнительная проверка" : "";
            _confirmLine.Visible = confirm;
        }
        BindLive(StatusSnapshotProjector.Rows(snapshot));
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

        if (_locationsPage.Visible)
        {
            _locations.Tick();
        }
    }

    public void BindLocations(LocationHistory history, GeoCountryDisplay? live = null)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => BindLocations(history, live));
            return;
        }

        _locations.Bind(history, live);
    }

    private Panel BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiTheme.Brand950,
            Padding = new Padding(UiTheme.PagePad, 14, UiTheme.PagePad, 14),
            ColumnCount = 1,
            RowCount = 3,
            AccessibleName = "windowHeader"
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 3; i++)
        {
            header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        var titleRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
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
            Text = ProductInfo.Name,
            Font = UiTheme.Title,
            ForeColor = UiTheme.OnBrand,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            AccessibleName = "productTitle"
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
            Text = "Провайдер слева, мир справа. HTTPS контрольных адресов, не весь интернет.",
            Font = UiTheme.Caption,
            ForeColor = UiTheme.OnHeaderMuted,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        header.Resize += (_, _) =>
        {
            int inner = header.ClientSize.Width - header.Padding.Horizontal;
            if (inner < 240)
            {
                return;
            }

            if (subtitle.MaximumSize.Width != inner)
            {
                subtitle.MaximumSize = new Size(inner, 0);
            }
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
        statusRow.Controls.Add(_worldBadge);
        statusRow.Controls.Add(_confirmLine);
        _ruBadge.SetGroup(GroupLabels.Provider, "нет данных", true);
        _worldBadge.SetGroup(GroupLabels.World, "нет данных", false);

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
        _tabLocations.Click += (_, _) => ShowPage(1);
        _tabDiag.Click += (_, _) => ShowPage(2);
        _tabSettings.Click += (_, _) => ShowPage(3);
        _tabLocations.AccessibleName = "locationsTab";
        _tabSettings.AccessibleName = "settingsTab";
        var tabs = new SegmentTrack(_tabLive, _tabLocations, _tabDiag, _tabSettings)
        {
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12)
        };
        FillLivePage();
        FillLocationsPage();
        FillDiagPage();
        FillSettingsPage();
        var host = new Panel { Dock = DockStyle.Fill, BackColor = UiTheme.Surface };
        host.Controls.Add(_settingsPage);
        host.Controls.Add(_diagPage);
        host.Controls.Add(_locationsPage);
        host.Controls.Add(_livePage);
        body.Controls.Add(tabs, 0, 0);
        body.Controls.Add(host, 0, 1);
        ShowPage(0);
        return body;
    }

    private void ShowPage(int index)
    {
        _livePage.Visible = index == 0;
        _locationsPage.Visible = index == 1;
        _diagPage.Visible = index == 2;
        _settingsPage.Visible = index == 3;
        _tabLive.Primary = index == 0;
        _tabLocations.Primary = index == 1;
        _tabDiag.Primary = index == 2;
        _tabSettings.Primary = index == 3;
        if (index == 0)
        {
            ScaleColumns(_list, [120, 140, 100, 60, 110, 90, 120, 170]);
        }

        if (index == 1)
        {
            PerformLayout();
            _locationsPage.PerformLayout();
            _locations.Relayout();
        }

        if (index == 3 && _settingsPage.Controls.Count > 0 && _settingsPage.Controls[0] is VerticalStack stack)
        {
            stack.Relayout();
        }
    }

    private void FillLocationsPage()
    {
        _locations.Dock = DockStyle.Fill;
        _locationsPage.Controls.Add(_locations);
    }

    private void FillLivePage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = UiTheme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = Hint("Колонка «С запуска» — доля успешных HTTPS с этого запуска.");
        ConfigureList(_list, "Текущие проверки", DrawRow);
        _list.Columns.Add("Группа", 120);
        _list.Columns.Add("Адрес", 140);
        _list.Columns.Add("Результат", 100);
        _list.Columns.Add("HTTP", 60);
        _list.Columns.Add("Задержка", 110);
        _list.Columns.Add("С запуска", 90);
        _list.Columns.Add("Проверено", 120);
        _list.Columns.Add("Причина", 170);
        _list.Layout += (_, _) => LayoutCount++;
        page.Controls.Add(_list, 0, 0);
        page.Controls.Add(hint, 0, 1);
        _livePage.Controls.Add(page);
    }

    private void FillDiagPage()
    {
        var intro = Hint("HTTPS, ICMP и TCP 443 сравниваются отдельно. Разный итог не доказывает блокировку HTTPS.");
        intro.Dock = DockStyle.Top;
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 8),
            BackColor = UiTheme.Surface
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _diagNode.DropDownStyle = ComboBoxStyle.DropDownList;
        _diagNode.Dock = DockStyle.Fill;
        _diagNode.Font = UiTheme.Body;
        _diagNode.AccessibleName = "Узел для диагностики";
        _diagRun.AutoSize = false;
        _diagRun.Size = new Size(280, UiTheme.ButtonHeight);
        _diagRun.Stretch = false;
        _diagRun.Margin = new Padding(8, 0, 0, 0);
        _diagRun.Click += async (_, _) => await RunDiagnosticsAsync();
        row.Controls.Add(_diagNode, 0, 0);
        row.Controls.Add(_diagRun, 1, 0);
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
        var stack = new VerticalStack
        {
            Dock = DockStyle.Fill,
            AccessibleName = "settingsStack"
        };
        StyleCheck(_autoStart, "Запускать вместе с Windows", Padding.Empty);
        _autoStart.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettings)
            {
                AutoStartChanged?.Invoke(_autoStart.Checked);
            }
        };
        StyleCheck(_autoUpdate, "Ставить обновления с GitHub", new Padding(0, 0, 0, 8));
        _autoUpdate.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettings)
            {
                AutoUpdateChanged?.Invoke(_autoUpdate.Checked);
            }
        };
        StyleCheck(_geoCountryIcon, "Показывать страну в трее", new Padding(0, 0, 0, 8));
        _geoCountryIcon.AccessibleName = "geoCountryIcon";
        _geoCountryIcon.CheckedChanged += (_, _) =>
        {
            SyncLetterSizeVisibility();
            if (!_suppressSettings)
            {
                GeoCountryIconChanged?.Invoke(_geoCountryIcon.Checked);
            }
        };
        _letterSizeLabel.AutoSize = true;
        _letterSizeLabel.Text = "Размер букв в трее";
        _letterSizeLabel.Font = UiTheme.Caption;
        _letterSizeLabel.ForeColor = UiTheme.Muted;
        _letterSizeLabel.BackColor = UiTheme.Card;
        _letterSizeLabel.Margin = new Padding(0, 0, 0, 6);
        _letterSize.DropDownStyle = ComboBoxStyle.DropDownList;
        _letterSize.Font = UiTheme.Body;
        _letterSize.Width = 220;
        _letterSize.Margin = Padding.Empty;
        _letterSize.AccessibleName = "geoCountryLetterSize";
        _letterSize.Items.AddRange(GeoCountryLetterScales.Captions);
        _letterSize.SelectedIndex = (int)GeoCountryLetterScale.Regular;
        _letterSize.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressSettings)
            {
                GeoCountryLetterScaleChanged?.Invoke(GeoCountryLetterScales.Parse(_letterSize.SelectedIndex));
            }
        };
        SyncLetterSizeVisibility();
        _updateLine.AutoSize = true;
        _updateLine.Font = UiTheme.Caption;
        _updateLine.ForeColor = UiTheme.Muted;
        _updateLine.BackColor = UiTheme.Card;
        _updateLine.Margin = new Padding(0, 0, 0, 12);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
            Padding = Padding.Empty,
            BackColor = UiTheme.Card
        };
        _checkUpdates.Stretch = false;
        _checkUpdates.FitToText();
        _checkUpdates.Margin = new Padding(0, 0, 10, 0);
        _checkUpdates.AccessibleName = "checkUpdates";
        _checkUpdates.Click += (_, _) => CheckUpdatesRequested?.Invoke();
        _exportLog.Stretch = false;
        _exportLog.FitToText();
        _exportLog.Margin = Padding.Empty;
        _exportLog.AccessibleName = "exportLog";
        _exportLog.Click += (_, _) => ExportRequested?.Invoke();
        actions.Controls.Add(_checkUpdates);
        actions.Controls.Add(_exportLog);
        _manualUpdateLine.AutoSize = true;
        _manualUpdateLine.Font = UiTheme.Caption;
        _manualUpdateLine.ForeColor = UiTheme.Muted;
        _manualUpdateLine.BackColor = UiTheme.Card;
        _manualUpdateLine.Margin = Padding.Empty;
        _manualUpdateLine.AccessibleName = "manualUpdateStatus";
        var links = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(4, 4, 0, 0),
            Padding = Padding.Empty,
            BackColor = UiTheme.Surface
        };
        var github = QuietLink("Исходный код", "githubLink");
        var releases = QuietLink("Releases", "releasesLink");
        releases.Margin = new Padding(16, 0, 0, 0);
        github.LinkClicked += (_, _) => UiDrawing.OpenHttps(AppCredits.RepositoryUrl);
        releases.LinkClicked += (_, _) => UiDrawing.OpenHttps(AppCredits.ReleasesUrl);
        links.Controls.Add(github);
        links.Controls.Add(releases);
        stack.Controls.Add(SettingsBlock("Общие", _autoStart, _geoCountryIcon, _letterSizeLabel, _letterSize));
        stack.Controls.Add(SettingsBlock("Обновления", _autoUpdate, _updateLine, actions, _manualUpdateLine));
        stack.Controls.Add(links);
        _settingsPage.Controls.Add(stack);
        stack.Relayout();
    }

    private void SyncLetterSizeVisibility()
    {
        bool show = _geoCountryIcon.Checked;
        _letterSizeLabel.Visible = show;
        _letterSize.Visible = show;
        if (_settingsPage.Controls.Count > 0 && _settingsPage.Controls[0] is VerticalStack stack)
        {
            stack.Relayout();
        }
    }

    private static void StyleCheck(CheckBox box, string text, Padding margin)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Font = UiTheme.Body;
        box.ForeColor = UiTheme.Ink;
        box.BackColor = UiTheme.Card;
        box.Margin = margin;
        box.FlatStyle = FlatStyle.System;
        box.UseMnemonic = false;
    }

    private static LinkLabel QuietLink(string text, string accessibleName)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = UiTheme.Caption,
            LinkColor = UiTheme.Brand800,
            ActiveLinkColor = UiTheme.Brand900,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = UiTheme.Surface,
            AccessibleName = accessibleName
        };

    private static Control SettingsBlock(string title, params Control[] children)
    {
        var card = new RoundedCard
        {
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(16, 14, 16, 14)
        };
        card.Controls.Add(new Label
        {
            AutoSize = true,
            Text = title,
            Font = UiTheme.BodyBold,
            ForeColor = UiTheme.Ink,
            BackColor = UiTheme.Card,
            Margin = new Padding(0, 0, 0, 10),
            UseMnemonic = false
        });
        foreach (Control child in children)
        {
            card.Controls.Add(child);
        }

        return card;
    }

    private static Label Hint(string text)
    {
        return new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
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

    private void BindDiagNodes(MonitorSnapshot snapshot)
    {
        string? selected = _diagNode.SelectedItem as string;
        var ids = snapshot.Ru.Endpoints.Concat(snapshot.World.Endpoints).Select(e => e.Id).ToList();
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
        ScaleColumns(_list, [120, 140, 100, 60, 110, 90, 120, 170]);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ScaleColumns(_list, [120, 140, 100, 60, 110, 90, 120, 170]);
    }

    private static void ScaleColumns(ListView list, int[] weights)
    {
        if (list.Columns.Count < weights.Length)
        {
            return;
        }

        int total = weights.Sum();
        int width = list.ClientSize.Width - 2;
        if (width <= 0)
        {
            return;
        }

        int used = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            int column = i == weights.Length - 1
                ? Math.Max(40, width - used)
                : Math.Max(40, width * weights[i] / total);
            if (i < weights.Length - 1)
            {
                used += column;
            }

            list.Columns[i].Width = column;
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

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(_allowShow && value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (HideToTrayIfUserClosing(e.CloseReason))
        {
            e.Cancel = true;
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
            _windowIcon?.Dispose();
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
