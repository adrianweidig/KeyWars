using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte.Sammlungen;

public sealed class NeuModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];

    [BindProperty]
    public CollectionInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (!ModelState.IsValid)
        {
            Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
            return Page();
        }

        try
        {
            var collection = await texts.CreateCollectionAsync(profile.Id, Input.Name, Input.Description, Input.Visibility, Input.TextIds, cancellationToken);
            return RedirectToPage("/Texte/Sammlungen/Details", new { id = collection.Id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken: cancellationToken);
            return Page();
        }
    }

    public sealed class CollectionInput
    {
        [Required]
        [MaxLength(160)]
        public string Name { get; set; } = "";
        [MaxLength(400)]
        public string? Description { get; set; }
        public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
        public List<Guid> TextIds { get; set; } = [];
    }
}
