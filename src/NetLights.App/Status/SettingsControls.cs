namespace NetLights.App;

internal sealed class ScrollSafeComboBox : ComboBox
{
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x020A) // WM_MOUSEWHEEL must scroll the page, never change a setting.
        {
            if (!DroppedDown)
            {
                for (Control? ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
                    if (ancestor is VerticalStack stack)
                    {
                        stack.ScrollWheel(unchecked((short)(message.WParam.ToInt64() >> 16)));
                        break;
                    }
            }
            message.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref message);
    }
}

internal sealed class SettingsRow : Panel
{
    private readonly Label _label;
    private readonly Control _editor;

    public SettingsRow(Label label, Control editor)
    {
        _label = label;
        _editor = editor;
        Margin = new Padding(0, 3, 0, 5);
        BackColor = UiTheme.Card;
        label.Margin = Padding.Empty;
        label.Font = UiTheme.Body;
        label.ForeColor = UiTheme.Ink;
        label.Padding = Padding.Empty;
        label.UseMnemonic = false;
        editor.Margin = Padding.Empty;
        Controls.Add(label);
        Controls.Add(editor);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width > 0 ? proposedSize.Width : Width;
        int editorWidth = Math.Min(LogicalToDeviceUnits(220), Math.Max(100, width / 2));
        int labelWidth = Math.Max(1, width - editorWidth - LogicalToDeviceUnits(16));
        int labelHeight = TextRenderer.MeasureText(_label.Text, _label.Font, new Size(labelWidth, int.MaxValue), TextFormatFlags.WordBreak).Height;
        return new Size(width, Math.Max(labelHeight, _editor.GetPreferredSize(Size.Empty).Height) + LogicalToDeviceUnits(4));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int editorWidth = Math.Min(LogicalToDeviceUnits(220), Math.Max(100, ClientSize.Width / 2));
        int labelWidth = Math.Max(1, ClientSize.Width - editorWidth - LogicalToDeviceUnits(16));
        _label.MaximumSize = new Size(labelWidth, 0);
        int labelHeight = _label.GetPreferredSize(new Size(labelWidth, 0)).Height;
        int editorHeight = _editor.GetPreferredSize(Size.Empty).Height;
        _label.SetBounds(0, Math.Max(0, (Height - labelHeight) / 2), labelWidth, labelHeight);
        _editor.SetBounds(ClientSize.Width - editorWidth, Math.Max(0, (Height - editorHeight) / 2), editorWidth, editorHeight);
    }
}
