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

    public string Input { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        try
        {
            Snapshot = rooms.Join(id, profile.Id, profile.DisplayName);
        }
        catch (InvalidOperationException ex)
        {
            return ArenaError(ex);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostReadyAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        CurrentProfileId = profile.Id;
        try
        {
            var snapshot = rooms.Snapshot(id);
            var participant = snapshot.Participants.FirstOrDefault(item => item.ProfileId == profile.Id);
            rooms.SetReady(id, profile.Id, participant?.Ready != true);
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException ex)
        {
            return ArenaError(ex);
        }
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

    private IActionResult ArenaError(InvalidOperationException exception)
    {
        if (exception.Message.Contains("nicht gefunden", StringComparison.OrdinalIgnoreCase))
        {
            return NotFound(exception.Message);
        }

        if (exception.Message.Contains("Raumcode", StringComparison.OrdinalIgnoreCase))
        {
            return RedirectToPage("/Arena/Beitreten");
        }

        return StatusCode(StatusCodes.Status409Conflict, exception.Message);
    }
}
