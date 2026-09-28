namespace NetLights.App;

internal class MouseFocusCueCheckBox : CheckBox
{
    private Color? _originalBackColor;
    private bool? _originalUseVisualStyleBackColor;

    // The native dotted rectangle is inconsistent across focus paths and themes.
    // Keep keyboard focus visible with a quiet fill instead of allowing that frame.
    protected override bool ShowFocusCues => false;

    internal bool FocusFrameVisibleForTests => ShowFocusCues;

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (base.ShowFocusCues) ShowKeyboardFocusFill();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ShowKeyboardFocusFill();
        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        if (_originalBackColor is { } original)
        {
            BackColor = original;
            _originalBackColor = null;
            UseVisualStyleBackColor = _originalUseVisualStyleBackColor ?? true;
            _originalUseVisualStyleBackColor = null;
        }
        Invalidate();
        base.OnLostFocus(e);
    }

    private void ShowKeyboardFocusFill()
    {
        if (_originalBackColor is null)
        {
            _originalBackColor = BackColor;
            _originalUseVisualStyleBackColor = UseVisualStyleBackColor;
        }
        UseVisualStyleBackColor = false;
        BackColor = UiTheme.Brand50;
    }
}
