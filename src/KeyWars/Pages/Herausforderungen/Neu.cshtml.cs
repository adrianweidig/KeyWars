using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Herausforderungen;

public sealed class NeuModel(CurrentUser currentUser, TextLibraryService texts, ChallengeService challenges) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];
    public IReadOnlyList<UserProfile> People { get; private set; } = [];

    [BindProperty]
    public ChallengeInput Input { get; set; } = new();

    public async Task OnGetAsync(Guid? textId, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        Input.TrainingTextId = textId ?? Texts.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var challenge = await challenges.CreateAsync(profile.Id, new CreateChallengeRequest(
            Input.Title,
            Input.TrainingTextId,
            Input.Mode,
            Input.ParticipantIds,
            Input.RoundCount,
            Input.ExpiryDays), cancellationToken);
        return RedirectToPage("/Herausforderungen/Details", new { id = challenge.Id });
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        People = await texts.SearchPeopleAsync(profile.Id, null, 50, cancellationToken);
    }

    public sealed class ChallengeInput
    {
        [MaxLength(160)]
        public string Title { get; set; } = "";
        [Required]
        public Guid TrainingTextId { get; set; }
        public ChallengeMode Mode { get; set; } = ChallengeMode.Classic;
        public int RoundCount { get; set; } = 1;
        public int ExpiryDays { get; set; } = 7;
        public List<Guid> ParticipantIds { get; set; } = [];
    }
}
