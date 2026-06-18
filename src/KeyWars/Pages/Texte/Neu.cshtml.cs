using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Texte;

public sealed class NeuModel(CurrentUser currentUser, TextLibraryService texts) : PageModel
{
    [BindProperty]
    public TextInput Input { get; set; } = new();

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public TrainingTextVisibility UploadVisibility { get; set; } = TrainingTextVisibility.Private;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var text = await texts.CreateAsync(profile.Id, Input.Title, Input.Body, Input.Visibility, cancellationToken);
        return RedirectToPage("/Texte/Details", new { id = text.Id });
    }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellationToken)
    {
        if (Upload is null)
        {
            ModelState.AddModelError(string.Empty, "Bitte wähle eine .txt-Datei aus.");
            return Page();
        }

        var text = await texts.CreateFromUploadAsync(HttpContext, Upload, UploadVisibility, cancellationToken);
        return RedirectToPage("/Texte/Details", new { id = text.Id });
    }

    public sealed class TextInput
    {
        [Required(ErrorMessage = "Der Titel ist erforderlich.")]
        [MaxLength(160)]
        public string Title { get; set; } = "";

        [Required(ErrorMessage = "Der Text ist erforderlich.")]
        public string Body { get; set; } = "";

        public TrainingTextVisibility Visibility { get; set; } = TrainingTextVisibility.Private;
    }
}
