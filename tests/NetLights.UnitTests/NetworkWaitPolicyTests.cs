using NetLights.Updates;
using Xunit;

namespace NetLights.UnitTests;

public sealed class NetworkWaitPolicyTests
{
    [Fact]
    public async Task WaitUntilOnline_ReturnsTrueWhenProbeSucceeds()
    {
        SequentialProbe probe = new(false, false, true);
        bool online = await NetworkWaitPolicy.WaitUntilOnline(
            probe,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(1),
            CancellationToken.None);
        Assert.True(online);
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task WaitUntilOnline_ReturnsFalseWhenBudgetExpires()
    {
        SequentialProbe probe = new(false, false, false);
        bool online = await NetworkWaitPolicy.WaitUntilOnline(
            probe,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);
        Assert.False(online);
        Assert.True(probe.Calls > 0);
    }

    [Fact]
    public void NextOfflineRetry_IsShort()
        => Assert.Equal(TimeSpan.FromSeconds(15), NetworkWaitPolicy.OfflineRetry);

    private sealed class SequentialProbe : IInternetProbe
    {
        private readonly Queue<bool> _answers;

        public SequentialProbe(params bool[] answers) => _answers = new Queue<bool>(answers);

        public int Calls { get; private set; }

        public Task<bool> IsReachable(CancellationToken cancellationToken)
        {
            Calls++;
            bool value = _answers.Count > 0 && _answers.Dequeue();
            if (_answers.Count == 0 && !value)
            {
                _answers.Enqueue(false);
            }

            return Task.FromResult(value);
        }
    }
}
