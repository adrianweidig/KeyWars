using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class NeuModel(CurrentUser currentUser, TextLibraryService texts, LiveRoomManager rooms) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    [BindProperty]
    public RoomInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        Input.TrainingTextId = Texts.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        var text = await texts.GetVisibleAsync(profile.Id, Input.TrainingTextId, cancellationToken);
        var snapshot = rooms.CreateRoom(new CreateLiveRoomRequest(
            profile.Id,
            profile.DisplayName,
            string.IsNullOrWhiteSpace(Input.Title) ? text.Title : Input.Title,
            text.Body,
            Input.Mode,
            Input.Visibility,
            Input.RoundCount,
            Input.MaxParticipants));
        return RedirectToPage("/Arena/Raum", new { id = snapshot.RoomId });
    }

    public sealed class RoomInput
    {
        [MaxLength(160)]
        public string Title { get; set; } = "";
        [Required]
        public Guid TrainingTextId { get; set; }
        public LiveRoomVisibility Visibility { get; set; } = LiveRoomVisibility.Code;
        public LiveRoomMode Mode { get; set; } = LiveRoomMode.Classic;
        public int RoundCount { get; set; } = 1;
        public int MaxParticipants { get; set; } = 64;
    }
}
