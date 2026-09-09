using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetLights.App;

internal static class UiDrawing
{
    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        GraphicsPath path = new();
        int diameter = Math.Max(2, radius * 2);
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        Rectangle arc = new(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void PaintRounded(Graphics graphics, Rectangle bounds, int radius, Color fill, Color border)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = RoundedRect(bounds, radius);
        using SolidBrush brush = new(fill);
        using Pen pen = new(border);
        graphics.FillPath(brush, path);
        graphics.DrawPath(pen, path);
    }

    public static void OpenHttps(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}

internal sealed class ThemedButton : Button
{
    private bool _primary;
    private bool _stretch;

    public ThemedButton(string text, bool primary)
    {
        _primary = primary;
        Text = text;
        Font = UiTheme.Button;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ApplyPalette();
        Height = UiTheme.TabHeight;
        MinimumSize = new Size(0, UiTheme.TabHeight);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Stretch
    {
        get => _stretch;
        set
        {
            _stretch = value;
            Dock = value ? DockStyle.Fill : DockStyle.None;
            if (!value)
            {
                MinimumSize = new Size(0, UiTheme.ButtonHeight);
                Height = UiTheme.ButtonHeight;
            }
        }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Primary
    {
        get => _primary;
        set
        {
            if (_primary == value)
            {
                return;
            }

            _primary = value;
            ApplyPalette();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(_stretch ? UiTheme.Track : UiTheme.Surface);
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(e.Graphics, box, UiTheme.ButtonRadius, BackColor, FlatAppearance.BorderColor);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        ApplyPalette();
        Invalidate();
    }

    private void ApplyPalette()
    {
        if (!Enabled)
        {
            BackColor = UiTheme.Track;
            ForeColor = UiTheme.Muted;
            FlatAppearance.BorderColor = UiTheme.Border;
            return;
        }

        BackColor = _primary ? UiTheme.Brand800 : UiTheme.Card;
        ForeColor = _primary ? UiTheme.OnBrand : UiTheme.Ink;
        FlatAppearance.BorderColor = _primary ? UiTheme.Brand800 : UiTheme.Border;
    }
}

internal sealed class SegmentTrack : Panel
{
    public SegmentTrack(params ThemedButton[] tabs)
    {
        DoubleBuffered = true;
        Height = UiTheme.TabHeight + (UiTheme.SegmentInset * 2);
        MinimumSize = new Size(0, Height);
        MaximumSize = new Size(int.MaxValue, Height);
        Padding = new Padding(UiTheme.SegmentInset);
        BackColor = Color.Transparent;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = tabs.Length,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        for (int i = 0; i < tabs.Length; i++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / tabs.Length));
            tabs[i].Stretch = true;
            tabs[i].Margin = new Padding(i == 0 ? 0 : 2, 0, i == tabs.Length - 1 ? 0 : 2, 0);
            grid.Controls.Add(tabs[i], i, 0);
        }

        Controls.Add(grid);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface);
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(e.Graphics, box, UiTheme.TrackRadius, UiTheme.Track, UiTheme.Border);
    }
}

internal sealed class StatusBadge : Label
{
    public StatusBadge()
    {
        AutoSize = true;
        Padding = new Padding(10, 4, 10, 4);
        Font = UiTheme.Caption;
        ForeColor = UiTheme.OnBrand;
        Margin = new Padding(0, 0, 8, 0);
        UseMnemonic = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(UiTheme.Brand950);
        Rectangle box = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        UiDrawing.PaintRounded(e.Graphics, box, Math.Max(8, Height / 2), BackColor, BackColor);
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    public void SetGroup(string group, string label, bool ru)
    {
        Text = group + ": " + label;
        BackColor = ru ? UiTheme.Brand800 : Color.FromArgb(196, 112, 32);
        Invalidate();
    }
}
