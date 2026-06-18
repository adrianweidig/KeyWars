using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class RaumModel(CurrentUser currentUser, LiveRoomManager rooms) : PageModel
{
    public LiveRoomSnapshot Snapshot { get; private set; } = new(
        Guid.Empty,
        Guid.Empty,
        "",
        "",
        "",
        0,
        LiveRoomMode.Classic,
        LiveRoomVisibility.Code,
        1,
        1,
        1,
        LiveRoomPhase.Lobby,
        false,
        false,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        null,
        null,
        []);

    public Guid CurrentProfileId { get; private set; }

    [BindProperty]
    public string Input { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        try
        {
            Snapshot = rooms.Join(id, profile.Id, profile.DisplayName);
        }
        catch (InvalidOperationException)
        {
            return Forbid();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostReadyAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        var snapshot = rooms.Snapshot(id);
        var participant = snapshot.Participants.FirstOrDefault(item => item.ProfileId == profile.Id);
        rooms.SetReady(id, profile.Id, participant?.Ready != true);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        try
        {
            rooms.Start(id, profile.Id);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Snapshot = rooms.Snapshot(id);
            return Page();
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostFinishAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        rooms.Finish(id, profile.Id, Input, 0, 0);
        return RedirectToPage(new { id });
    }
}
