using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class UpdateHealthSignalTests
{
    [Fact]
    public void ValidSignalNameIsParsedAndWakesWaitingUpdater()
    {
        string name = UpdateHealthSignal.CreateEventName();
        using var waitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, name);

        Assert.True(UpdateHealthSignal.TryReadName([UpdateHealthSignal.ArgumentName, name], out string? parsed));
        Assert.Equal(name, parsed);
        Assert.True(UpdateHealthSignal.Signal(parsed));
        Assert.True(waitHandle.WaitOne(TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Global\\NetLights.UpdateHealth.00000000000000000000000000000000")]
    [InlineData("Local\\NetLights.UpdateHealth.not-a-guid")]
    [InlineData("")]
    public void InvalidEventNamesAreRejected(string name)
    {
        Assert.False(UpdateHealthSignal.TryReadName([UpdateHealthSignal.ArgumentName, name], out _));
        Assert.False(UpdateHealthSignal.Signal(name));
    }
}
