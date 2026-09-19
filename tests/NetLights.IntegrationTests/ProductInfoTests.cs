using NetLights.App;
using NetLights.Updates;
using Xunit;

namespace NetLights.IntegrationTests;

public sealed class ProductInfoTests
{
    [Fact]
    public void DisplayName_MarksCopyWithoutUninstallerAsLocal()
    {
        string root = Path.Combine(Path.GetTempPath(), "nl-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("Net Lights (локальная)", ProductInfo.DisplayName(root));
            File.WriteAllText(Path.Combine(root, "unins000.exe"), "x");
            Assert.Equal("Net Lights", ProductInfo.DisplayName(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
