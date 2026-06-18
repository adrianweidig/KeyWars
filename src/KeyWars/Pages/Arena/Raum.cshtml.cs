using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class RaumModel(CurrentUser currentUser, LiveRoomManager rooms, TypingEngine typingEngine) : PageModel
{
    public LiveRoomSnapshot Snapshot { get; private set; } = new(Guid.Empty, "", "", "", LiveRoomMode.Classic, LiveRoomVisibility.Code, 1, false, false, []);

    [BindProperty]
    public string Input { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        rooms.Join(id, profile.Id, profile.DisplayName);
        Snapshot = rooms.Snapshot(id);
        return Page();
    }

    public async Task<IActionResult> OnPostReadyAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        rooms.SetReady(id, profile.Id, true);
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostStartAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            rooms.Start(id, profile.Id);
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostFinishAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var snapshot = rooms.Snapshot(id);
        var metrics = typingEngine.Analyze(snapshot.TargetText, Input, TimeSpan.FromMinutes(1), 0, 0);
        rooms.Finish(id, profile.Id, metrics);
        return RedirectToPage(new { id });
    }
}
