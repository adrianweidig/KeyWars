using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class LiveRoomSnapshotAuthorizationTests
{
    [Fact]
    public async Task SnapshotForViewerRejectsANonMemberButAllowsAnInvitedProfile()
    {
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions()),
            TimeProvider.System,
            new TypingEngine(TimeProvider.System),
            NullLogger<LiveRoomManager>.Instance);
        ILiveRoomDispatcher dispatcher = new LocalLiveRoomDispatcher(manager);
        var host = Guid.CreateVersion7();
        var invited = Guid.CreateVersion7();
        var nonMember = Guid.CreateVersion7();
        var room = await dispatcher.CreateRoomAsync(new CreateLiveRoomRequest(
            host,
            "Host",
            "Vertraulicher Raum",
            "Nicht öffentlicher Zieltext",
            LiveRoomMode.Classic,
            LiveRoomVisibility.InvitationOnly,
            1,
            4,
            [new LiveRoomInvitation(invited, "Eingeladen")]));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.SnapshotForViewerAsync(room.RoomId, nonMember));
        var invitedSnapshot = await dispatcher.SnapshotForViewerAsync(room.RoomId, invited);

        Assert.Equal("Du bist nicht in diesem Raum.", error.Message);
        Assert.Equal(room.Code, invitedSnapshot.Code);
        Assert.Contains(invitedSnapshot.Participants, item => item.ProfileId == invited);
    }

    [Fact]
    public async Task SnapshotForViewerRejectsAnExcludedFormerParticipant()
    {
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions()),
            TimeProvider.System,
            new TypingEngine(TimeProvider.System),
            NullLogger<LiveRoomManager>.Instance);
        ILiveRoomDispatcher dispatcher = new LocalLiveRoomDispatcher(manager);
        var host = Guid.CreateVersion7();
        var participant = Guid.CreateVersion7();
        var room = await dispatcher.CreateRoomAsync(new CreateLiveRoomRequest(
            host,
            "Host",
            "Raum",
            "Zieltext",
            LiveRoomMode.Classic,
            LiveRoomVisibility.Code,
            1,
            4));
        await dispatcher.JoinByCodeAsync(room.Code, participant, "Teilnehmer");
        await dispatcher.KickAsync(room.RoomId, host, participant);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await dispatcher.SnapshotForViewerAsync(room.RoomId, participant));
    }
}
