using System.Drawing.Drawing2D;

namespace NetLights.App;

internal static class LocationChartCardChrome
{
    public const int CardInset = 14;
    public const int PlotLeftPadding = 42;
    public const int PlotRightPadding = 14;

    public static void Paint(Graphics graphics, Control control, string title, string? inlineMeta = null)
    {
        using (var background = new SolidBrush(control.Parent?.BackColor ?? UiTheme.Surface))
            graphics.FillRectangle(background, graphics.VisibleClipBounds);
        Rectangle bounds = new(0, 0, Math.Max(0, control.Width - 1), Math.Max(0, control.Height - 1));
        UiDrawing.PaintRounded(graphics, bounds, UiTheme.CardRadius, UiTheme.Card, UiTheme.Border);

        int pad = Scale(control, CardInset);
        TextRenderer.DrawText(
            graphics,
            title,
            UiTheme.BodyBold,
            new Rectangle(pad, Scale(control, 5), Math.Max(1, control.Width - pad * 2), Scale(control, 23)),
            UiTheme.Ink,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (!string.IsNullOrWhiteSpace(inlineMeta))
        {
            int metaWidth = Math.Max(1, control.Width - pad * 2 - Scale(control, 180));
            TextRenderer.DrawText(
                graphics,
                inlineMeta,
                UiTheme.Caption,
                new Rectangle(pad + Scale(control, 180), Scale(control, 5), metaWidth, Scale(control, 23)),
                UiTheme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    public static int Scale(Control control, int logical)
        => control.IsHandleCreated ? control.LogicalToDeviceUnits(logical) : logical;
}
