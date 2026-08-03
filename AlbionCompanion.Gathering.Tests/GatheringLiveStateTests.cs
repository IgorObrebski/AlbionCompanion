using AlbionCompanion.Core.Models;

namespace AlbionCompanion.Gathering.Tests;

public class GatheringLiveStateTests
{
    private sealed class FakeGatheringSessionService : IGatheringSessionService
    {
        public event EventHandler<GatheringSession>? OnSessionStarted;
        public event EventHandler<GatheringSession>? OnSessionEnded;
        public event EventHandler<GatheringSession>? OnLocationChanged;
        public event EventHandler<GatheredItem>? OnItemAdded;
        public event EventHandler<FameLog>? OnFameAdded;
        public event EventHandler<SilverLog>? OnSilverAdded;

        public ActiveSessionSnapshot? SnapshotToReturn { get; set; }

        public Task StartSessionAsync(string location) => Task.CompletedTask;
        public Task EndSessionAsync() => Task.CompletedTask;
        public Task AddItemAsync(string itemId, int amount) => Task.CompletedTask;
        public Task AddFameAsync(string fameType, int amount) => Task.CompletedTask;
        public Task AddSilverAsync(int amount) => Task.CompletedTask;
        public Task<GatheringSession?> GetActiveSessionAsync() => Task.FromResult<GatheringSession?>(null);
        public Task<ActiveSessionSnapshot?> GetActiveSessionSnapshotAsync() => Task.FromResult(SnapshotToReturn);

        public void RaiseSessionStarted(GatheringSession session) => OnSessionStarted?.Invoke(this, session);
        public void RaiseSessionEnded(GatheringSession session) => OnSessionEnded?.Invoke(this, session);
        public void RaiseLocationChanged(GatheringSession session) => OnLocationChanged?.Invoke(this, session);
        public void RaiseItemAdded(GatheredItem item) => OnItemAdded?.Invoke(this, item);
        public void RaiseFameAdded(FameLog fameLog) => OnFameAdded?.Invoke(this, fameLog);
        public void RaiseSilverAdded(SilverLog silverLog) => OnSilverAdded?.Invoke(this, silverLog);
    }

    private static int AmountFor(IGatheringLiveState liveState, string itemId, string location = "") =>
        liveState.ItemTotals.Single(t => t.ItemId == itemId && t.Location == location).Amount;

    [Fact]
    public async Task OnItemAdded_NewItem_AppearsInItemTotals()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });

        Assert.Equal(5, AmountFor(liveState, "T4_ORE"));
    }

    [Fact]
    public async Task OnItemAdded_SameItemTwice_AmountsSum()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseItemAdded(new AlbionCompanion.Core.Models.GatheredItem { ItemId = "T4_ORE", Amount = 3 });

        Assert.Equal(8, AmountFor(liveState, "T4_ORE"));
    }

    [Fact]
    public async Task OnItemAdded_SameItemDifferentLocation_TrackedSeparately()
    {
        // A session can roam through multiple zones without ending - the same item gathered in
        // two different locations must stay as two separate totals, not merge into one.
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5, Location = "Cairn Camain" });
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 3, Location = "Mawar Gorge" });

        Assert.Equal(5, AmountFor(liveState, "T4_ORE", "Cairn Camain"));
        Assert.Equal(3, AmountFor(liveState, "T4_ORE", "Mawar Gorge"));
    }

    [Fact]
    public async Task OnFameAdded_Twice_TotalFameAccumulates()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseFameAdded(new AlbionCompanion.Core.Models.FameLog { FameType = "Gathering", Amount = 600 });

        Assert.Equal(900, liveState.TotalFame);
    }

    [Fact]
    public async Task OnSilverAdded_Twice_TotalSilverAccumulatesByLocation()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);

        service.RaiseSilverAdded(new SilverLog { Amount = 92, Location = "Cairn Camain" });
        service.RaiseSilverAdded(new SilverLog { Amount = 40, Location = "Cairn Camain" });

        Assert.Equal(132, liveState.TotalSilver);
        Assert.Equal(132, liveState.SilverByLocation.Single(l => l.Location == "Cairn Camain").Amount);
    }

    [Fact]
    public async Task OnSessionStarted_AfterPriorActivity_ResetsState()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });

        Assert.Empty(liveState.ItemTotals);
        Assert.Equal(0, liveState.TotalFame);
        Assert.True(liveState.IsActive);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public async Task OnLocationChanged_UpdatesCurrentLocationButNotStartLocation()
    {
        // A roaming session's StartSessionAsync updates CurrentLocation without ending the
        // session - the header must track that, not stay frozen on where the session began.
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);
        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Cairn Camain" });

        service.RaiseLocationChanged(new GatheringSession { StartLocation = "Cairn Camain", CurrentLocation = "Martlock" });

        Assert.Equal("Cairn Camain", liveState.StartLocation);
        Assert.Equal("Martlock", liveState.CurrentLocation);
    }

    [Fact]
    public async Task OnSessionEnded_LeavesDataUnchangedButMarksInactive()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);
        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });

        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.False(liveState.IsActive);
        Assert.Equal(5, AmountFor(liveState, "T4_ORE"));
        Assert.Equal(300, liveState.TotalFame);
        Assert.Equal("Martlock", liveState.StartLocation);
    }

    [Fact]
    public async Task EachHandler_RaisesOnChangedExactlyOnce()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService();
        await liveState.Attach(service);
        var raiseCount = 0;
        liveState.OnChanged += (_, _) => raiseCount++;

        service.RaiseSessionStarted(new GatheringSession { StartLocation = "Martlock" });
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 5 });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 300 });
        service.RaiseSessionEnded(new GatheringSession { StartLocation = "Martlock" });

        Assert.Equal(4, raiseCount);
    }

    [Fact]
    public async Task Attach_WithAlreadyActiveSession_RehydratesStateFromSnapshot()
    {
        // Regression: found live 2026-08-02 - closing the app normally while standing in open
        // world (active session in the DB, not yet ended) and relaunching while still in open
        // world left Home showing "No session" until the next gather/zone-change action, even
        // though a session had been running the whole time. OnSessionStarted only fires for a
        // session created during *this* process's lifetime - a session that already existed in
        // the DB before startup needs to be read back explicitly.
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService
        {
            SnapshotToReturn = new ActiveSessionSnapshot(
                CurrentLocation: "Cairn Camain",
                CharacterId: null,
                TotalFameEarned: 150,
                TotalSilverEarned: 500,
                ItemTotals: new[] { new ItemLocationTotal("T4_ORE", "Cairn Camain", 12) },
                FameByLocation: new[] { new LocationTotal("Cairn Camain", 150) },
                SilverByLocation: new[] { new LocationTotal("Cairn Camain", 500) }),
        };

        await liveState.Attach(service);

        Assert.True(liveState.IsActive);
        Assert.Equal("Cairn Camain", liveState.StartLocation);
        Assert.Equal(150, liveState.TotalFame);
        Assert.Equal(12, AmountFor(liveState, "T4_ORE", "Cairn Camain"));
    }

    [Fact]
    public async Task Attach_WithNoActiveSession_LeavesStateAtDefaults()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService { SnapshotToReturn = null };

        await liveState.Attach(service);

        Assert.False(liveState.IsActive);
        Assert.Null(liveState.StartLocation);
        Assert.Equal(0, liveState.TotalFame);
        Assert.Empty(liveState.ItemTotals);
    }

    [Fact]
    public async Task Attach_WithAlreadyActiveSession_SubsequentEventsAccumulateOnTopOfSnapshot()
    {
        var liveState = new GatheringLiveState();
        var service = new FakeGatheringSessionService
        {
            SnapshotToReturn = new ActiveSessionSnapshot(
                CurrentLocation: "Cairn Camain",
                CharacterId: null,
                TotalFameEarned: 150,
                TotalSilverEarned: 500,
                ItemTotals: new[] { new ItemLocationTotal("T4_ORE", "Cairn Camain", 12) },
                FameByLocation: new[] { new LocationTotal("Cairn Camain", 150) },
                SilverByLocation: new[] { new LocationTotal("Cairn Camain", 500) }),
        };

        await liveState.Attach(service);
        service.RaiseItemAdded(new GatheredItem { ItemId = "T4_ORE", Amount = 1, Location = "Cairn Camain" });
        service.RaiseFameAdded(new FameLog { FameType = "Gathering", Amount = 15 });

        Assert.Equal(13, AmountFor(liveState, "T4_ORE", "Cairn Camain"));
        Assert.Equal(165, liveState.TotalFame);
    }
}
