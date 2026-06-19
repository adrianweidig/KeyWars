using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Data;
using KeyWars.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class EinstellungenModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    [TempData]
    public string? SavedMessage { get; set; }

    public IdentitySummary Identity { get; private set; } = IdentitySummary.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        PopulateIdentity(profile);
        Input = new SettingsInput
        {
            Motto = profile.Motto,
            PreferredMode = profile.PreferredMode,
            LeaderboardVisible = profile.LeaderboardVisible,
            GhostSharingEnabled = profile.GhostSharingEnabled,
            ShowLiveWpm = profile.ShowLiveWpm,
            ShowLiveRankChanges = profile.ShowLiveRankChanges,
            SoundEnabled = profile.SoundEnabled,
            SoundVolumePercent = profile.SoundVolumePercent,
            ReactionsEnabled = profile.ReactionsEnabled,
            ReducedMotion = profile.ReducedMotion
        };
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        PopulateIdentity(profile);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        profile.Motto = string.IsNullOrWhiteSpace(Input.Motto) ? null : Input.Motto.Trim();
        profile.PreferredMode = Input.PreferredMode;
        profile.LeaderboardVisible = Input.LeaderboardVisible;
        profile.GhostSharingEnabled = Input.GhostSharingEnabled;
        profile.ShowLiveWpm = Input.ShowLiveWpm;
        profile.ShowLiveRankChanges = Input.ShowLiveRankChanges;
        profile.SoundEnabled = Input.SoundEnabled;
        profile.SoundVolumePercent = Math.Clamp(Input.SoundVolumePercent, 0, 100);
        profile.ReactionsEnabled = Input.ReactionsEnabled;
        profile.ReducedMotion = Input.ReducedMotion;
        await db.SaveChangesAsync(cancellationToken);
        SavedMessage = "Einstellungen gespeichert.";
        return RedirectToPage("/Profil/Einstellungen");
    }

    private void PopulateIdentity(UserProfile profile)
    {
        Identity = IdentitySummary.From(profile);
    }

    public sealed class SettingsInput
    {
        [MaxLength(120)]
        public string? Motto { get; set; }
        public TrainingMode PreferredMode { get; set; } = TrainingMode.Sprint60;
        public bool LeaderboardVisible { get; set; } = true;
        public bool GhostSharingEnabled { get; set; }
        public bool ShowLiveWpm { get; set; } = true;
        public bool ShowLiveRankChanges { get; set; } = true;
        public bool SoundEnabled { get; set; }
        [Range(0, 100)]
        public int SoundVolumePercent { get; set; } = 35;
        public bool ReactionsEnabled { get; set; } = true;
        public bool ReducedMotion { get; set; }
    }

    public sealed record IdentitySummary(
        string DisplayName,
        string SamAccountName,
        string UserPrincipalName,
        string Email,
        string Department,
        string Title)
    {
        public static IdentitySummary Empty { get; } = new("-", "-", "-", "-", "-", "-");

        public static IdentitySummary From(UserProfile profile) => new(
            ValueOrPlaceholder(profile.DisplayName),
            ValueOrPlaceholder(profile.SamAccountName),
            ValueOrPlaceholder(profile.UserPrincipalName),
            ValueOrPlaceholder(profile.Email),
            ValueOrPlaceholder(profile.Department),
            ValueOrPlaceholder(profile.Title));

        private static string ValueOrPlaceholder(string? value) =>
            string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }
}
