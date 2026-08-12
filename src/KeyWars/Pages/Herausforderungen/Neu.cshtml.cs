using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Herausforderungen;

public sealed class NeuModel(
    CurrentUser currentUser,
    TextLibraryService texts,
    ChallengeService challenges,
    KeyWarsDbContext db) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];
    public IReadOnlyList<UserProfile> SelectedPeople { get; private set; } = [];
    public IReadOnlyList<string> Departments { get; private set; } = [];

    [BindProperty]
    public ChallengeInput Input { get; set; } = new();

    public async Task OnGetAsync(Guid? textId, CancellationToken cancellationToken)
    {
        await LoadAsync([], cancellationToken);
        Input.TrainingTextId = textId ?? Texts.FirstOrDefault()?.Id ?? Guid.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (Input.ParticipantIds.Count == 0)
        {
            ModelState.AddModelError(
                string.Empty,
                "Eine Herausforderung benötigt mindestens zwei Personen.");
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await LoadAsync(Input.ParticipantIds, cancellationToken);
            return Page();
        }

        try
        {
            var challenge = await challenges.CreateAsync(profile.Id, new CreateChallengeRequest(
                Input.Title,
                Input.TrainingTextId,
                Input.Mode,
                Input.ParticipantIds,
                Input.RoundCount,
                Input.ExpiryDays,
                Input.RequestId), cancellationToken);
            return RedirectToPage("/Herausforderungen/Details", new { id = challenge.Id });
        }
        catch (ChallengeLifecycleException exception)
        {
            Response.StatusCode = exception.StatusCode;
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(Input.ParticipantIds, cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(IReadOnlyCollection<Guid> selectedIds, CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        if (selectedIds.Count > 0)
        {
            SelectedPeople = await db.UserProfiles
                .AsNoTracking()
                .Where(person => selectedIds.Contains(person.Id) && !person.Deleted && person.Id != profile.Id)
                .OrderBy(person => person.DisplayName)
                .ThenBy(person => person.SamAccountName)
                .ToListAsync(cancellationToken);
        }

        var departments = await db.UserProfiles
            .AsNoTracking()
            .Where(person => !person.Deleted && person.ChallengesEnabled && person.Id != profile.Id && person.Department != null && person.Department != "")
            .Select(person => person.Department!)
            .Distinct()
            .ToListAsync(cancellationToken);
        Departments = departments.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public sealed class ChallengeInput
    {
        public Guid RequestId { get; set; } = Guid.CreateVersion7();
        [MaxLength(160)]
        public string Title { get; set; } = "";
        [Required]
        public Guid TrainingTextId { get; set; }
        public ChallengeMode Mode { get; set; } = ChallengeMode.Classic;
        [Range(1, 5)]
        public int RoundCount { get; set; } = 1;
        [Range(1, 30)]
        public int ExpiryDays { get; set; } = 7;
        [MinLength(1, ErrorMessage = "Wähle mindestens eine weitere Person aus.")]
        public List<Guid> ParticipantIds { get; set; } = [];
    }
}
