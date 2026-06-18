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
        Texts = await (
            from collectionItem in db.TextCollectionItems
            join text in db.TrainingTexts on collectionItem.TrainingTextId equals text.Id
            where collectionItem.TextCollectionId == id &&
                (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == profile.Id)
            orderby collectionItem.SortOrder
            select text
        ).ToListAsync(cancellationToken);
        return Page();
    }
}
