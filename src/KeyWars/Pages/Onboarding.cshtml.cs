using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages;

public sealed class OnboardingModel(CurrentUser currentUser, KeyWarsDbContext db, TimeProvider timeProvider) : PageModel
{
    public UserProfile Profile { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public int Schritt { get; set; } = 1;

    [BindProperty]
    public OnboardingInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (Profile.OnboardingCompletedAt is not null)
        {
            return RedirectToPage("/Index");
        }

        Schritt = Math.Clamp(Schritt, 1, 3);
        Input = new OnboardingInput
        {
            PreferredMode = Profile.PreferredMode,
            LeaderboardVisible = Profile.LeaderboardVisible,
            ShowLiveWpm = Profile.ShowLiveWpm,
            ReducedMotion = Profile.ReducedMotion
        };
        return Page();
    }

    public async Task<IActionResult> OnPostTrainingAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (!Enum.IsDefined(Input.PreferredMode))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.PreferredMode)}", "Wähle einen gültigen Trainingsmodus.");
        }

        if (!ModelState.IsValid)
        {
            Schritt = 1;
            return Page();
        }

        Profile.PreferredMode = Input.PreferredMode;
        Profile.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage(new { schritt = 2 });
    }

    public async Task<IActionResult> OnPostVisibilityAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Profile.LeaderboardVisible = Input.LeaderboardVisible;
        Profile.ShowLiveWpm = Input.ShowLiveWpm;
        Profile.ReducedMotion = Input.ReducedMotion;
        Profile.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage(new { schritt = 3 });
    }

    public async Task<IActionResult> OnPostFinishAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        if (!Enum.IsDefined(Input.Destination))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Destination)}", "Wähle ein gültiges Startziel.");
            Schritt = 3;
            return Page();
        }

        Complete(Profile);
        await db.SaveChangesAsync(cancellationToken);
        return Input.Destination switch
        {
            OnboardingDestination.DailyChallenge => RedirectToPage("/Tageschallenge"),
            OnboardingDestination.TextLibrary => RedirectToPage("/Texte/Index"),
            _ => RedirectToPage("/Spielen/Sprint")
        };
    }

    public async Task<IActionResult> OnPostSkipAsync(CancellationToken cancellationToken)
    {
        Profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Complete(Profile);
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("/Index");
    }

    private void Complete(UserProfile profile)
    {
        var now = timeProvider.GetUtcNow();
        profile.OnboardingCompletedAt ??= now;
        profile.UpdatedAt = now;
    }

    public sealed class OnboardingInput
    {
        public TrainingMode PreferredMode { get; set; } = TrainingMode.Sprint60;
        public bool LeaderboardVisible { get; set; } = true;
        public bool ShowLiveWpm { get; set; } = true;
        public bool ReducedMotion { get; set; }
        public OnboardingDestination Destination { get; set; } = OnboardingDestination.DailyChallenge;
    }

    public enum OnboardingDestination
    {
        DailyChallenge,
        Sprint,
        TextLibrary
    }
}
