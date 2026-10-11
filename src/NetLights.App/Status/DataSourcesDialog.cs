namespace NetLights.App;

internal sealed class DataSourcesDialog : Form
{
    public DataSourcesDialog()
    {
        Text = "Источники данных и лицензии";
        AccessibleName = "dataSourcesDialog";
        Font = UiTheme.Body;
        ForeColor = UiTheme.Ink;
        BackColor = UiTheme.Surface;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 460);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(20),
            BackColor = UiTheme.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = UiTheme.Card,
            Padding = new Padding(16)
        };
        content.Controls.Add(Line("Определение страны", UiTheme.BodyBold, new Padding(0, 0, 0, 8)));
        content.Controls.Add(Line("Онлайн: country.is проверяет публичный IP. При споре или сбое используется ipwho.is.", UiTheme.Body, new Padding(0, 0, 0, 12)));
        content.Controls.Add(Line("Локальная база", UiTheme.BodyBold, new Padding(0, 0, 0, 8)));
        content.Controls.Add(Line("Страны по IP: DB-IP Lite в пакете v2fly/geoip. База сопоставляет страну с публичным IP, который сообщает онлайн-проверка.", UiTheme.Body, new Padding(0, 0, 0, 8)));
        content.Controls.Add(Link("DB-IP Lite · CC BY 4.0", "https://db-ip.com/db/lite.php", "dbIpGeoIpAttribution"));
        content.Controls.Add(Link("v2fly/geoip · CC BY-SA 4.0", "https://github.com/v2fly/geoip", "v2FlyGeoIpAttribution"));
        content.Controls.Add(Line("Сравнение (не участвует в выборе основной страны)", UiTheme.BodyBold, new Padding(0, 8, 0, 8)));
        content.Controls.Add(Line("IPLocate Free Country: дополнительная локальная база для визуального сравнения. Скачивается публичное GitHub-зеркало; IP-адреса для поиска не отправляются. Зеркало может отставать от основной ежедневной базы поставщика.", UiTheme.Body, new Padding(0, 0, 0, 8)));
        content.Controls.Add(Link("IPLocate.io · CC BY-SA 4.0", "https://www.iplocate.io/free-databases", "ipLocateAttribution"));

        var close = new ThemedButton("Закрыть", true)
        {
            Stretch = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
            AccessibleName = "closeDataSources"
        };
        close.FitToText();
        close.Click += (_, _) => Close();
        AcceptButton = close;
        CancelButton = close;

        layout.Controls.Add(content, 0, 0);
        layout.Controls.Add(close, 0, 1);
        Controls.Add(layout);
    }

    private static Label Line(string text, Font font, Padding margin) => new()
    {
        Text = text,
        Font = font,
        ForeColor = UiTheme.Ink,
        BackColor = UiTheme.Card,
        AutoSize = true,
        MaximumSize = new Size(460, 0),
        Margin = margin
    };

    private static LinkLabel Link(string text, string url, string name)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Font = UiTheme.Body,
            LinkColor = UiTheme.Brand800,
            ActiveLinkColor = UiTheme.Brand900,
            LinkBehavior = LinkBehavior.HoverUnderline,
            BackColor = UiTheme.Card,
            Margin = new Padding(0, 0, 0, 8),
            AccessibleName = name
        };
        link.LinkClicked += (_, _) => UiDrawing.OpenHttps(url);
        return link;
    }
}
