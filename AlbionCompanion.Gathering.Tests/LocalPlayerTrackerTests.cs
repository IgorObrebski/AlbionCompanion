using AlbionCompanion.Core.Models;
using AlbionCompanion.Sniffer.Protocol16;
using Xunit;

namespace AlbionCompanion.Gathering.Tests;

public class LocalPlayerTrackerTests
{
    private sealed class FakePhotonParser : IPhotonParser
    {
        public event EventHandler<PhotonEvent>? OnEventReceived;
        public event EventHandler<PhotonResponse>? OnResponseReceived;
        public event EventHandler<PhotonRequest>? OnRequestReceived;
        public void HandlePayload(byte[] payload) { }
        public void RaiseResponse(PhotonResponse response) => OnResponseReceived?.Invoke(this, response);
        public void RaiseEvent(PhotonEvent photonEvent) => OnEventReceived?.Invoke(this, photonEvent);
    }

    private sealed class FakeCharacterService : ICharacterService
    {
        public List<Character> Characters { get; } = new();
        public int GetAllAsyncCallCount { get; private set; }

        public event EventHandler? CharactersChanged;

        public Task<IReadOnlyList<Character>> GetAllAsync()
        {
            GetAllAsyncCallCount++;
            return Task.FromResult<IReadOnlyList<Character>>(Characters);
        }
        public Task<Character> AddAsync(string name) => throw new NotImplementedException();
        public Task DeleteAsync(Guid id) => throw new NotImplementedException();
        public Task RenameAsync(Guid id, string newName) => throw new NotImplementedException();
        public Task<IReadOnlyList<CharacterOverview>> GetAllOverviewsAsync() => throw new NotImplementedException();
        public Task<CharacterOverview?> GetOverviewAsync(Guid characterId) => throw new NotImplementedException();

        public void SimulateRegistrationChange(Character newCharacter)
        {
            Characters.Add(newCharacter);
            CharactersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static PhotonResponse ZoneJoinResponse(int ownEntityId, string nickname = "Ejnsztain") =>
        new(1, 0, string.Empty, new Dictionary<byte, object?> { [0] = ownEntityId, [2] = nickname, [253] = 2 });

    private static PhotonEvent PlayerAnnounce(int entityId, string nickname) =>
        new(1, new Dictionary<byte, object?> { [0] = entityId, [2] = nickname, [252] = 279 });

    [Fact]
    public void ZoneJoinResponse_RecordsOwnEntityId()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760));

        Assert.Equal(200760, tracker.CurrentEntityId);
    }

    [Fact]
    public void ZoneJoinResponse_AlsoRecordsCharacterName()
    {
        // The zone-join response is a RESPONSE to our own REQUEST, so it's inherently self-only -
        // it carries our nickname too, which PlayerAnnounce (code 279, broadcast to everyone
        // nearby) later needs to match against.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        Assert.Equal("Ejnsztain", tracker.CurrentCharacterName);
    }

    [Fact]
    public void SubsequentZoneJoin_UpdatesToTheNewEntityId()
    {
        // Regression: entity ids are reassigned per zone, not stable across zone changes -
        // confirmed via live capture where the same character got a different id on each join.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(ZoneJoinResponse(200760));
        parser.RaiseResponse(ZoneJoinResponse(1111937));

        Assert.Equal(1111937, tracker.CurrentEntityId);
    }

    [Fact]
    public void ResponseWithoutZoneJoinSubCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseResponse(new PhotonResponse(1, 0, string.Empty,
            new Dictionary<byte, object?> { [0] = 999, [253] = 52 }));

        Assert.Null(tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_MatchingConfirmedCharacterName_RefreshesEntityId()
    {
        // Confirmed live 2026-08-03: PlayerAnnounce (code 279) fires periodically, independent of
        // zone transitions - this is what lets CurrentEntityId recover without a zone change, the
        // known same-zone-restart bug's fix.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());
        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "Ejnsztain"));
        await Task.Delay(10);

        Assert.Equal(41390, tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_WithNonMatchingNickname_IsIgnored()
    {
        // Confirmed live 2026-08-03: PlayerAnnounce broadcasts for any nearby player, not just
        // the local one - two different nicknames were observed for two different entities in
        // the same capture window.
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());
        parser.RaiseResponse(ZoneJoinResponse(200760, "Ejnsztain"));

        parser.RaiseEvent(PlayerAnnounce(entityId: 107157, nickname: "Valdekir"));
        await Task.Delay(10);

        Assert.Equal(200760, tracker.CurrentEntityId);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_MatchingRegisteredCharacter_AdoptsIdentity()
    {
        // The cold-start case: the app was just restarted in the same zone, so no zone-join
        // response has fired yet - the only way to recover identity without a zone transition is
        // to trust a PlayerAnnounce whose nickname matches a character the user has registered.
        var parser = new FakePhotonParser();
        var characterService = new FakeCharacterService();
        characterService.Characters.Add(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
        var tracker = new LocalPlayerTracker(parser, characterService);

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "Ejnsztain"));
        await Task.Delay(10);

        Assert.Equal(41390, tracker.CurrentEntityId);
        Assert.Equal("Ejnsztain", tracker.CurrentCharacterName);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_UnregisteredNickname_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "SomeoneElse"));
        await Task.Delay(10);

        Assert.Null(tracker.CurrentEntityId);
        Assert.Null(tracker.CurrentCharacterName);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_RepeatedNonMatchingAnnounces_OnlyQueryCharactersOnce()
    {
        // Regression for the "dozens of DB round-trips/sec in a crowded city" gap: while cold
        // (no zone-join yet), every nearby PlayerAnnounce used to re-query the full character list.
        // The registered-name set should be cached after the first read.
        var parser = new FakePhotonParser();
        var characterService = new FakeCharacterService();
        characterService.Characters.Add(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
        var tracker = new LocalPlayerTracker(parser, characterService);

        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "SomeoneElse"));
        await Task.Delay(10);
        parser.RaiseEvent(PlayerAnnounce(entityId: 51391, nickname: "AnotherOne"));
        await Task.Delay(10);

        Assert.Equal(1, characterService.GetAllAsyncCallCount);
    }

    [Fact]
    public async Task PlayerAnnounce_BeforeAnyZoneJoin_NicknameRegisteredAfterCacheWarmed_StillMatches()
    {
        // A character can be registered (via ICharacterService.AddAsync) after the tracker already
        // cached an earlier (smaller) list - CharactersChanged must invalidate that cache instead
        // of permanently rejecting a name added later.
        var parser = new FakePhotonParser();
        var characterService = new FakeCharacterService();
        var tracker = new LocalPlayerTracker(parser, characterService);

        parser.RaiseEvent(PlayerAnnounce(entityId: 999, nickname: "Unrelated"));
        await Task.Delay(10);

        characterService.SimulateRegistrationChange(new Character { Name = "Ejnsztain", CreatedAt = DateTime.UtcNow });
        parser.RaiseEvent(PlayerAnnounce(entityId: 41390, nickname: "Ejnsztain"));
        await Task.Delay(10);

        Assert.Equal(41390, tracker.CurrentEntityId);
        Assert.Equal("Ejnsztain", tracker.CurrentCharacterName);
    }

    [Fact]
    public void OtherSemanticEventCode_IsIgnored()
    {
        var parser = new FakePhotonParser();
        var tracker = new LocalPlayerTracker(parser, new FakeCharacterService());

        parser.RaiseEvent(new PhotonEvent(1, new Dictionary<byte, object?> { [0] = 41390, [2] = "Ejnsztain", [252] = 61 }));

        Assert.Null(tracker.CurrentEntityId);
    }
}
