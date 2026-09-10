using System.Windows.Forms;

namespace NetLights.App;

internal static class TrayIconGestures
{
    public static bool ShouldOpenWindow(MouseButtons button) => button == MouseButtons.Left;
}
