using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class PublicModel(KeyWarsDbContext db) : PageModel
{
    public UserProfile Profile { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Profile = await db.UserProfiles.SingleOrDefaultAsync(item => item.Id == id && !item.Deleted, cancellationToken) ?? new UserProfile { DisplayName = "Unbekannt" };
        return Page();
    }
}
