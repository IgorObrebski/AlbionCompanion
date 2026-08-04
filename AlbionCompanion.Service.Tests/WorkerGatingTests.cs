using AlbionCompanion.Gathering;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlbionCompanion.Service.Tests;

// Exercises Worker's actual game-presence gating logic in ExecuteAsync (via the extracted
// RunGatingCycleAsync helper), instead of only testing a fake IGameProcessWatcher's own trivial
// behavior (which is what GameProcessWatcherTests previously did - a tautological test that
// couldn't have caught a regression in Worker itself). StartPipelineAsync/StopPipeline are
// overridden here to avoid touching Npcap/real packet capture/the real shared database - only the
// gating decision (start when the game appears, stop when it disappears) is under test.
public class WorkerGatingTests
{
    private sealed class FakeGameProcessWatcher : IGameProcessWatcher
    {
        public bool Running { get; set; }
        public bool IsGameRunning() => Running;
    }

    private sealed class TestableWorker : Worker
    {
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public TestableWorker(IGameProcessWatcher gameProcessWatcher)
            : base(gameProcessWatcher, NullLogger<Worker>.Instance)
        {
        }

        protected override Task StartPipelineAsync()
        {
            StartCallCount++;
            _pipelineRunning = true;
            return Task.CompletedTask;
        }

        protected override void StopPipeline()
        {
            StopCallCount++;
            _pipelineRunning = false;
        }
    }

    [Fact]
    public async Task GatingCycle_StartsPipeline_WhenGameBecomesRunning()
    {
        var watcher = new FakeGameProcessWatcher { Running = false };
        using var worker = new TestableWorker(watcher);

        await worker.RunGatingCycleAsync();
        Assert.False(worker.IsPipelineRunning);
        Assert.Equal(0, worker.StartCallCount);

        watcher.Running = true;
        await worker.RunGatingCycleAsync();

        Assert.True(worker.IsPipelineRunning);
        Assert.Equal(1, worker.StartCallCount);
    }

    [Fact]
    public async Task GatingCycle_StopsPipeline_WhenGameStopsRunning()
    {
        var watcher = new FakeGameProcessWatcher { Running = true };
        using var worker = new TestableWorker(watcher);

        await worker.RunGatingCycleAsync();
        Assert.True(worker.IsPipelineRunning);

        watcher.Running = false;
        await worker.RunGatingCycleAsync();

        Assert.False(worker.IsPipelineRunning);
        Assert.Equal(1, worker.StopCallCount);
    }

    [Fact]
    public async Task GatingCycle_DoesNotRestartAnAlreadyRunningPipeline()
    {
        var watcher = new FakeGameProcessWatcher { Running = true };
        using var worker = new TestableWorker(watcher);

        await worker.RunGatingCycleAsync();
        await worker.RunGatingCycleAsync();
        await worker.RunGatingCycleAsync();

        Assert.Equal(1, worker.StartCallCount);
    }
}
