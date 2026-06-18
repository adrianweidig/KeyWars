using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Arena;

public sealed class BeitretenModel(CurrentUser currentUser, LiveRoomManager rooms) : PageModel
{
    [BindProperty]
    public string Code { get; set; } = "";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            var snapshot = rooms.JoinByCode(Code.Trim(), profile.Id, profile.DisplayName);
            return RedirectToPage("/Arena/Raum", new { id = snapshot.RoomId });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }
}
