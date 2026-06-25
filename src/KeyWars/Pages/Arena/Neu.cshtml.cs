using System.ComponentModel.DataAnnotations;
using KeyWars.Auth;
using KeyWars.Domain;
using KeyWars.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace KeyWars.Pages.Arena;

public sealed class NeuModel(CurrentUser currentUser, TextLibraryService texts, LiveRoomManager rooms, IOptions<LiveOptions> liveOptions) : PageModel
{
    public IReadOnlyList<TrainingText> Texts { get; private set; } = [];
    public IReadOnlyList<ArenaTextOption> TextOptions { get; private set; } = [];
    public ArenaTextOption? SelectedTextOption => TextOptions.FirstOrDefault(text => text.Id == Input.TrainingTextId) ?? TextOptions.FirstOrDefault();
    public int MaxParticipantsLimit { get; private set; }

    [BindProperty]
    public RoomInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ApplyConfiguredLimits();
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        BuildTextOptions();
        Input.TrainingTextId = Texts.FirstOrDefault()?.Id ?? Guid.Empty;
        Input.MaxParticipants = Math.Min(Input.MaxParticipants, MaxParticipantsLimit);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ApplyConfiguredLimits();
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        BuildTextOptions();
        if (Input.MaxParticipants < 2 || Input.MaxParticipants > MaxParticipantsLimit)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.MaxParticipants)}", $"Erlaubt sind 2 bis {MaxParticipantsLimit} Personen.");
        }

        if (Texts.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Erstelle zuerst einen Trainingstext, bevor du einen Live-Raum startest.");
        }
        else if (Texts.All(text => text.Id != Input.TrainingTextId))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.TrainingTextId)}", "Der ausgewählte Text ist nicht verfügbar.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var text = await texts.GetVisibleAsync(profile.Id, Input.TrainingTextId, cancellationToken);
        var snapshot = rooms.CreateRoom(new CreateLiveRoomRequest(
            profile.Id,
            profile.DisplayName,
            string.IsNullOrWhiteSpace(Input.Title) ? text.Title : Input.Title,
            text.Body,
            Input.Mode,
            Input.Visibility,
            Input.RoundCount,
            Input.MaxParticipants));
        return RedirectToPage("/Arena/Raum", new { id = snapshot.RoomId });
    }

    private void ApplyConfiguredLimits()
    {
        MaxParticipantsLimit = Math.Max(2, liveOptions.Value.MaxParticipantsPerRoom);
    }

    private void BuildTextOptions()
    {
        TextOptions = Texts.Select(ToTextOption).ToArray();
    }

    private static ArenaTextOption ToTextOption(TrainingText text)
    {
        var words = TypingEngine.CountWords(text.Body);
        var estimatedSeconds = Math.Max(10, (int)Math.Ceiling(words / 45d * 60d));
        return new ArenaTextOption(
            text.Id,
            text.Title,
            text.CharacterCount,
            words,
            estimatedSeconds,
            BuildPreview(text.Body));
    }

    private static string BuildPreview(string body)
    {
        var normalized = TypingEngine.NormalizeText(body).Replace('\n', ' ');
        const int maxPreviewCharacters = 280;
        if (normalized.Length <= maxPreviewCharacters)
        {
            return normalized;
        }

        return normalized[..maxPreviewCharacters].TrimEnd() + " ...";
    }

    public sealed record ArenaTextOption(Guid Id, string Title, int CharacterCount, int WordCount, int EstimatedSeconds, string Preview);

    public sealed class RoomInput
    {
        [MaxLength(160)]
        public string Title { get; set; } = "";
        [Required]
        public Guid TrainingTextId { get; set; }
        public LiveRoomVisibility Visibility { get; set; } = LiveRoomVisibility.Code;
        public LiveRoomMode Mode { get; set; } = LiveRoomMode.Classic;
        public int RoundCount { get; set; } = 1;
        public int MaxParticipants { get; set; } = 16;
    }
}
