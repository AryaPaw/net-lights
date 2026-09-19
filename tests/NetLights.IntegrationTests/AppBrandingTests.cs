using System.Reflection;
using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class AppBrandingTests
{
    [Fact]
    public void WindowIcon_LoadsEmbeddedAppIco()
    {
        using Icon? icon = AppBranding.LoadWindowIcon();
        Assert.NotNull(icon);
        Assert.True(icon.Width >= 16);
        Assert.True(icon.Height >= 16);
        using Bitmap bmp = icon.ToBitmap();
        Color pixel = bmp.GetPixel(bmp.Width / 3, bmp.Height / 2);
        Assert.True(pixel.B > 80 || pixel.G > 80, "brand pixel " + pixel);
    }
}
