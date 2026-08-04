using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class GameProcessWatcherTests
{
    private sealed class FakeGameProcessWatcher : IGameProcessWatcher
    {
        public bool Running { get; set; }
        public bool IsGameRunning() => Running;
    }

    [Fact]
    public void FakeWatcher_ReflectsRunningFlag()
    {
        var watcher = new FakeGameProcessWatcher { Running = true };

        Assert.True(watcher.IsGameRunning());
    }
}
