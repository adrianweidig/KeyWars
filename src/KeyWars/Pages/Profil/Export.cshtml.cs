using KeyWars.Auth;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KeyWars.Pages.Profil;

public sealed class ExportModel(CurrentUser currentUser, ProfileExportService exports) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public DateOnly? Von { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? Bis { get; set; }

    public ProfileExportPreview? Preview { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? VonRoute => Von?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    public string? BisRoute => Bis?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadPreviewAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetDownloadAsync(CancellationToken cancellationToken)
    {
        if (!TryCreateRange(out var range))
        {
            return Page();
        }

        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        return exports.CreateDownload(profile.Id, range!);
    }

    private async Task<ProfileExportRange?> LoadPreviewAsync(CancellationToken cancellationToken)
    {
        if (!TryCreateRange(out var range))
        {
            return null;
        }

        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Preview = await exports.GetPreviewAsync(profile.Id, range!, cancellationToken);
        return range;
    }

    private bool TryCreateRange(out ProfileExportRange? range)
    {
        range = null;
        if (!ModelState.IsValid)
        {
            StatusMessage = "Bitte korrigiere die Datumsangaben.";
            return false;
        }

        try
        {
            range = ProfileExportRange.Create(Von, Bis);
            return true;
        }
        catch (ProfileExportValidationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            StatusMessage = "Der Zeitraum ist ungültig.";
            return false;
        }
    }
}
