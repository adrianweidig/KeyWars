using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Moderation;

[Authorize(Policy = KeyWarsPolicies.ContentModerator)]
public sealed class IndexModel(ContentModerationService moderation) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Suche { get; set; }

    [BindProperty(SupportsGet = true)]
    public ContentModerationTargetType? Typ { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Seite { get; set; } = 1;

    [BindProperty]
    public ModerationInput Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public ContentModerationQueuePage Queue { get; private set; } = new([], 0, 1, 30, 1);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostModerateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            await moderation.ModerateAsync(
                User,
                Input.TargetType!.Value,
                Input.TargetId!.Value,
                Input.Action!.Value,
                Input.Reason,
                cancellationToken);
            StatusMessage = Input.Action == ContentModerationAction.Quarantine
                ? "Der Inhalt wurde quarantänisiert."
                : "Der Inhalt ist nicht mehr organisationsweit sichtbar.";
            return RedirectToPage(new { Suche, Typ, Seite });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Queue = await moderation.GetQueueAsync(User, Suche, Typ, Seite, 30, cancellationToken);
        Seite = Queue.Page;
    }

    public sealed class ModerationInput
    {
        [Required]
        public Guid? TargetId { get; set; }

        [Required]
        [EnumDataType(typeof(ContentModerationTargetType))]
        public ContentModerationTargetType? TargetType { get; set; }

        [Required]
        [EnumDataType(typeof(ContentModerationAction))]
        public ContentModerationAction? Action { get; set; }

        [Required(ErrorMessage = "Eine kurze Begründung ist erforderlich.")]
        [StringLength(500, MinimumLength = 3, ErrorMessage = "Die Begründung muss zwischen 3 und 500 Zeichen lang sein.")]
        public string Reason { get; set; } = "";
    }
}
