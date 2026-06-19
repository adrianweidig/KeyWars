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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
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
        return RedirectToPage("/Profil/Index");
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
}
