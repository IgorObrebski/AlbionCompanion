using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.Tests;

public class GatheringLiveStateTests
{
    private sealed class FakeGatheringSessionService : IGatheringSessionService
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;

        public Task StartSessionAsync(string location) => Task.CompletedTask;
        public Task EndSessionAsync() => Task.CompletedTask;
        public Task AddItemAsync(string itemId, int amount) => Task.CompletedTask;
        public Task AddFameAsync(string fameType, int amount) => Task.CompletedTask;
        public Task<GatheringSession?> GetActiveSessionAsync() => Task.FromResult<GatheringSession?>(null);

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
        public void RaiseSessionEnded(GatheringSession session) => OnSessionEnded?.Invoke(this, session);
        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
        public void RaiseFameAdded(FameLog fameLog) => OnFameAdded?.Invoke(this, fameLog);
    }

    [Fact]
    public void OnItemAdded_NewItem_AppearsInItemTotals()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });

        Assert.Equal(5, liveState.ItemTotals["T4_ORE"]);
    }

    [Fact]
    public void OnItemAdded_SameItemTwice_AmountsSum()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 3 });

        Assert.Equal(8, liveState.ItemTotals["T4_ORE"]);
    }

    [Fact]
    public void OnFameAdded_Twice_TotalFameAccumulates()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);

        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 600 });

        Assert.Equal(900, liveState.TotalFame);
    }

    [Fact]
    public void OnSessionStarted_AfterPriorActivity_ResetsState()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });

        Assert.Empty(liveState.ItemTotals);
        Assert.Equal(0, liveState.TotalFame);
        Assert.True(liveState.IsActive);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public void OnSessionEnded_LeavesDataUnchangedButMarksInactive()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.False(liveState.IsActive);
        Assert.Equal(5, liveState.ItemTotals["T4_ORE"]);
        Assert.Equal(300, liveState.TotalFame);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public void EachHandler_RaisesOnChangedExactlyOnce()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        liveState.Attach(service);
        var raiseCount = 0;
        liveState.OnChanged += (_, _) => raiseCount++;

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.Equal(4, raiseCount);
    }
}
