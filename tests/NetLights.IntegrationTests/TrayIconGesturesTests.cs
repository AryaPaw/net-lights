using System.Windows.Forms;
using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class TrayIconGesturesTests
{
    [Fact]
    public void LeftClickOpensWindowAndRightClickDoesNot()
    {
        Assert.True(TrayIconGestures.ShouldOpenWindow(MouseButtons.Left));
        Assert.False(TrayIconGestures.ShouldOpenWindow(MouseButtons.Right));
        Assert.False(TrayIconGestures.ShouldOpenWindow(MouseButtons.None));
        Assert.False(TrayIconGestures.ShouldOpenWindow(MouseButtons.Middle));
    }
}
