using System.Text.Json;
using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class ExportModel(CurrentUser currentUser, ProfilePrivacyService privacy) : PageModel
{
    public string Json { get; private set; } = "{}";

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        var payload = await privacy.BuildExportAsync(profile.Id, cancellationToken);
        Json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
