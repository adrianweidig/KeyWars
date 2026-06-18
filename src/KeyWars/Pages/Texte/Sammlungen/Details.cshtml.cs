using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Texte.Sammlungen;

public sealed class DetailsModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public TextCollection Collection { get; private set; } = new();
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Collection = await db.TextCollections.SingleAsync(item => item.Id == id && (item.OwnerProfileId == profile.Id || item.Visibility == TrainingTextVisibility.Organization), cancellationToken);
        var ids = await db.TextCollectionItems.Where(item => item.TextCollectionId == id).OrderBy(item => item.SortOrder).Select(item => item.TrainingTextId).ToListAsync(cancellationToken);
        Texts = await db.TrainingTexts.Where(item => ids.Contains(item.Id)).ToListAsync(cancellationToken);
        return Page();
    }
}
