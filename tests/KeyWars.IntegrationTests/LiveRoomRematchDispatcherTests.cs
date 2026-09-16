using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KeyWars.IntegrationTests;

public sealed class LiveRoomRematchDispatcherTests
{
    [Fact]
    public async Task LocalDispatcherReturnsTheSameConfiguredRematchAndKeepsTheSourceClosed()
    {
        var manager = new LiveRoomManager(
            Options.Create(new LiveOptions()),
            TimeProvider.System,
            new TypingEngine(TimeProvider.System),
            NullLogger<LiveRoomManager>.Instance);
        ILiveRoomDispatcher dispatcher = new LocalLiveRoomDispatcher(manager);
        var host = Guid.CreateVersion7();
        var guest = Guid.CreateVersion7();
        var source = await dispatcher.CreateRoomAsync(new CreateLiveRoomRequest(
            host,
            "Host",
            "Serienraum",
            "Schlüsseltext",
            LiveRoomMode.Series,
            LiveRoomVisibility.Code,
            3,
            4));
        await dispatcher.JoinByCodeAsync(source.Code, guest, "Gast");
        await dispatcher.CloseAsync(source.RoomId, host);

        var first = await dispatcher.CreateRematchAsync(source.RoomId, host);
        var repeated = await dispatcher.CreateRematchAsync(source.RoomId, host);

        Assert.Equal(first.RoomId, repeated.RoomId);
        Assert.NotEqual(source.RoomId, first.RoomId);
        Assert.Equal(
            (LiveRoomMode.Series, 3, 4, LiveRoomVisibility.Code),
            (first.Mode, first.RoundCount, first.MaxParticipants, first.Visibility));
        Assert.Equal("Schlüsseltext", manager.ExportRoomState(first.RoomId).Text);
        Assert.Equal(ParticipantStatus.Invited, first.Participants.Single(item => item.ProfileId == guest).Status);
        var unchangedSource = await dispatcher.SnapshotAsync(source.RoomId);
        Assert.True(unchangedSource.Finished);
        Assert.Equal(LiveRoomPhase.Closed, unchangedSource.Phase);
    }
}
