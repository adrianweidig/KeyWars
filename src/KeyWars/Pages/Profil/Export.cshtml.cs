using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Pages.Profil;

public sealed class ExportModel(CurrentUser currentUser, KeyWarsDbContext db) : PageModel
{
    public string Json { get; private set; } = "{}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var payload = new
        {
            Profile = profile,
            Attempts = await db.TypingAttempts.Where(item => item.UserProfileId == profile.Id).ToListAsync(cancellationToken),
            Missions = await db.Missions.Where(item => item.UserProfileId == profile.Id).ToListAsync(cancellationToken),
            Achievements = await db.Achievements.Where(item => item.UserProfileId == profile.Id).ToListAsync(cancellationToken)
        };
        Json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
