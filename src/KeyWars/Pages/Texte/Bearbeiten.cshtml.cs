using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class BearbeitenModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    [BindProperty]
    public TextInput Input { get; set; } = new();

    public Guid TextId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var text = await texts.GetOwnedEditableAsync(profile.Id, id, cancellationToken);
        TextId = text.Id;
        Input = new TextInput
        {
            Title = text.Title,
            Body = text.Body,
            Visibility = text.Visibility
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken cancellationToken)
    {
        TextId = id;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        try
        {
            var text = await texts.UpdateAsync(profile.Id, id, Input.Title, Input.Body, Input.Visibility, cancellationToken);
            return RedirectToPage("/Texte/Details", new { id = text.Id });
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public sealed class TextInput
    {
        [Required(ErrorMessage = "Der Titel ist erforderlich.")]
        [MaxLength(160, ErrorMessage = "Der Titel darf maximal 160 Zeichen lang sein.")]
        public string Title { get; set; } = "";

        [Required(ErrorMessage = "Der Text ist erforderlich.")]
        public string Body { get; set; } = "";

        public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
    }
}
