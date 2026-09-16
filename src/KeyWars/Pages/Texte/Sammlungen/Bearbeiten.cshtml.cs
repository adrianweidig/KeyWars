using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte.Sammlungen;

public sealed class BearbeitenModel(CurrentUser currentUser, TextLibraryService textLibrary) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    [BindProperty]
    public CollectionInput Input { get; set; } = new();

    public Guid CollectionId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var collection = await textLibrary.GetOwnedCollectionAsync(profile.Id, id, cancellationToken);
        CollectionId = collection.Id;
        Texts = await textLibrary.ListVisibleAsync(profile.Id, cancellationToken);
        Input = new CollectionInput
        {
            Name = collection.Name,
            Description = collection.Description,
            Visibility = collection.Visibility,
            TextIds = await textLibrary.GetOwnedCollectionTextIdsAsync(profile.Id, collection.Id, cancellationToken)
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        await textLibrary.GetOwnedCollectionAsync(profile.Id, id, cancellationToken);
        CollectionId = id;
        if (!ModelState.IsValid)
        {
            Texts = await textLibrary.ListVisibleAsync(profile.Id, cancellationToken);
            return Page();
        }

        try
        {
            var collection = await textLibrary.UpdateCollectionAsync(
                profile.Id,
                id,
                Input.Name,
                Input.Description,
                Input.Visibility,
                Input.TextIds,
                cancellationToken);
            return RedirectToPage("/Texte/Sammlungen/Details", new { id = collection.Id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Texts = await textLibrary.ListVisibleAsync(profile.Id, cancellationToken);
            return Page();
        }
    }

    public sealed class CollectionInput
    {
        [Required(ErrorMessage = "Der Name ist erforderlich.")]
        [MaxLength(160, ErrorMessage = "Der Name darf maximal 160 Zeichen lang sein.")]
        public string Name { get; set; } = "";

        [MaxLength(400, ErrorMessage = "Die Beschreibung darf maximal 400 Zeichen lang sein.")]
        public string? Description { get; set; }

        public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
        public List<Guid> TextIds { get; set; } = [];
    }
}
