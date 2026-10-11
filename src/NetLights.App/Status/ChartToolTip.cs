namespace NetLights.App;

internal sealed class ChartToolTip : ToolTip
{
    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

    public ChartToolTip(int initialDelay = 250, int autoPopDelay = 6000)
    {
        ShowAlways = true;
        InitialDelay = initialDelay;
        ReshowDelay = 80;
        AutoPopDelay = autoPopDelay;
        UseAnimation = false;
        UseFading = false;
        OwnerDraw = true;
        Popup += (_, e) =>
        {
            if (e.AssociatedControl is Control control)
                e.ToolTipSize = Measure(control, GetToolTip(control) ?? string.Empty);
        };
        Draw += (_, e) =>
        {
            int horizontal = Scale(e.AssociatedControl, 9);
            int vertical = Scale(e.AssociatedControl, 7);
            using var background = new SolidBrush(UiTheme.Card);
            using var border = new Pen(UiTheme.Border);
            e.Graphics.FillRectangle(background, e.Bounds);
            e.Graphics.DrawRectangle(border, e.Bounds.Left, e.Bounds.Top,
                Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
            Rectangle textBounds = Rectangle.Inflate(e.Bounds, -horizontal, -vertical);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, UiTheme.ToolTip, textBounds, UiTheme.Ink, TextFlags);
        };
    }

    public void ShowFor(Control control, string text, Point cursor)
    {
        if (string.IsNullOrEmpty(text))
        {
            Clear(control);
            return;
        }

        SetToolTip(control, text);
        Size size = Measure(control, text);
        Point desired = control.PointToScreen(new Point(cursor.X + Scale(control, 12), cursor.Y + Scale(control, 12)));
        Rectangle area = Screen.FromControl(control).WorkingArea;
        desired.X = Math.Clamp(desired.X, area.Left, Math.Max(area.Left, area.Right - size.Width));
        desired.Y = Math.Clamp(desired.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        Point anchored = control.PointToClient(desired);
        Show(text, control, anchored.X, anchored.Y, AutoPopDelay);
    }

    public void Clear(Control control)
    {
        Hide(control);
        SetToolTip(control, string.Empty);
    }

    private static Size Measure(Control control, string text)
    {
        int horizontal = Scale(control, 9);
        int vertical = Scale(control, 7);
        int maxWidth = Scale(control, 420);
        Size measured = TextRenderer.MeasureText(text, UiTheme.ToolTip,
            new Size(maxWidth - horizontal * 2, int.MaxValue), TextFlags);
        return new Size(Math.Min(maxWidth, measured.Width + horizontal * 2), measured.Height + vertical * 2);
    }

    private static int Scale(Control? control, int logical)
        => (int)Math.Ceiling(logical * (control?.DeviceDpi ?? 96) / 96d);
}
