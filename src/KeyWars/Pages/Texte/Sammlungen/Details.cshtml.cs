using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Texte.Sammlungen;

public sealed class DetailsModel(CurrentUser currentUser, KeyWarsDbContext db, TextLibraryService textLibrary) : PageModel
{
    public TextCollection Collection { get; private set; } = new();
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];
    public bool CanManage { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await textLibrary.DeleteCollectionAsync(profile.Id, id, cancellationToken);
        return RedirectToPage("/Texte/Sammlungen/Index");
    }

    private async Task LoadAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Collection = await textLibrary.GetVisibleCollectionAsync(profile.Id, id, cancellationToken);
        CanManage = Collection.OwnerProfileId == profile.Id;
        Texts = await (
            from collectionItem in db.TextCollectionItems
            join text in db.TrainingTexts on collectionItem.TrainingTextId equals text.Id
            where collectionItem.TextCollectionId == id &&
                !text.IsQuarantined &&
                (text.IsStandard || text.Visibility == TrainingTextVisibility.Organization || text.OwnerProfileId == profile.Id)
            orderby collectionItem.SortOrder
            select text
        ).ToListAsync(cancellationToken);
    }
}
