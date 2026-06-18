using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Texte.Sammlungen;

public sealed class IndexModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public IReadOnlyList<TextCollection> Collections { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Collections = await db.TextCollections
            .Where(item => item.OwnerProfileId == profile.Id || item.Visibility == TrainingTextVisibility.Organization)
            .OrderBy(item => item.Name)
            .ToListAsync(cancellationToken);
    }
}
