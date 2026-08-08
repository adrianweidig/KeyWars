using System.ComponentModel.DataAnnotations;
using System.Text;
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
    public int MaxArenaTargetGraphemes { get; private set; }
    public int ExcludedTextCount { get; private set; }

    [BindProperty]
    public RoomInput Input { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ApplyConfiguredLimits();
        var profile = await currentUser.RequireProfileAsync(User, cancellationToken);
        Texts = await texts.ListVisibleAsync(profile.Id, cancellationToken);
        BuildTextOptions();
        Input.TrainingTextId = TextOptions.FirstOrDefault()?.Id ?? Guid.Empty;
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

        if (Input.Mode is not (LiveRoomMode.Classic or LiveRoomMode.Series or LiveRoomMode.Team))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.Mode)}", "Der ausgewählte Arena-Modus ist nicht verfügbar.");
        }
        else if (Input.Mode == LiveRoomMode.Series && Input.RoundCount is not (3 or 5))
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.RoundCount)}", "Wähle für ein Serienrennen drei oder fünf Runden.");
        }
        else if (Input.Mode is LiveRoomMode.Classic or LiveRoomMode.Team && Input.RoundCount != 1)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.RoundCount)}", "Dieser Modus läuft über genau eine Runde.");
        }

        if (Texts.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Erstelle zuerst einen Trainingstext, bevor du einen Live-Raum startest.");
        }
        else if (TextOptions.Count == 0)
        {
            ModelState.AddModelError(
                string.Empty,
                $"Kein sichtbarer Text erfüllt die Arena-Grenzen von {MaxArenaTargetGraphemes} Graphemen und {LiveOptions.MaximumSafeArenaTargetUtf8Bytes / 1024} KiB UTF-8.");
        }
        else if (TextOptions.All(text => text.Id != Input.TrainingTextId))
        {
            var visibleButTooLarge = Texts.Any(text => text.Id == Input.TrainingTextId);
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.TrainingTextId)}",
                visibleButTooLarge
                    ? "Der ausgewählte Text ist für eine Live-Arena zu lang. Kürze ihn oder wähle einen anderen Text."
                    : "Der ausgewählte Text ist nicht verfügbar.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var text = await texts.GetVisibleAsync(profile.Id, Input.TrainingTextId, cancellationToken);
        var normalizedTarget = TypingEngine.NormalizeText(text.Body);
        if (!IsArenaSafeTarget(normalizedTarget))
        {
            ModelState.AddModelError(
                $"{nameof(Input)}.{nameof(Input.TrainingTextId)}",
                "Der ausgewählte Text überschreitet inzwischen die Arena-Grenze. Lade die Seite neu und wähle einen kürzeren Text.");
            BuildTextOptions();
            return Page();
        }

        var snapshot = rooms.CreateRoom(new CreateLiveRoomRequest(
            profile.Id,
            profile.DisplayName,
            string.IsNullOrWhiteSpace(Input.Title) ? text.Title : Input.Title,
            normalizedTarget,
            Input.Mode,
            Input.Visibility,
            Input.RoundCount,
            Input.MaxParticipants));
        return RedirectToPage("/Arena/Raum", new { id = snapshot.RoomId });
    }

    private void ApplyConfiguredLimits()
    {
        MaxParticipantsLimit = Math.Max(2, liveOptions.Value.MaxParticipantsPerRoom);
        MaxArenaTargetGraphemes = Math.Clamp(
            liveOptions.Value.MaxArenaTargetGraphemes,
            1,
            LiveOptions.MaximumSafeArenaTargetGraphemes);
    }

    private void BuildTextOptions()
    {
        TextOptions = Texts
            .Select(ToTextOption)
            .Where(option => option is not null)
            .Cast<ArenaTextOption>()
            .ToArray();
        ExcludedTextCount = Texts.Count - TextOptions.Count;
    }

    private ArenaTextOption? ToTextOption(TrainingText text)
    {
        var normalized = TypingEngine.NormalizeText(text.Body);
        if (!IsArenaSafeTarget(normalized))
        {
            return null;
        }

        var characterCount = TypingEngine.SplitGraphemes(normalized).Count;
        var words = TypingEngine.CountWords(normalized);
        var estimatedSeconds = Math.Max(10, (int)Math.Ceiling(words / 45d * 60d));
        return new ArenaTextOption(
            text.Id,
            text.Title,
            characterCount,
            words,
            estimatedSeconds,
            BuildPreview(normalized));
    }

    private bool IsArenaSafeTarget(string normalized) =>
        TypingEngine.SplitGraphemes(normalized).Count <= MaxArenaTargetGraphemes &&
        Encoding.UTF8.GetByteCount(normalized) <= LiveOptions.MaximumSafeArenaTargetUtf8Bytes;

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
