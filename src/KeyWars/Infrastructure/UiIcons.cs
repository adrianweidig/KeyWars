using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace KeyWars.Infrastructure;

public static class UiIcons
{
    private static readonly IReadOnlyDictionary<string, string> IconIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["home"] = "home",
        ["house-door"] = "house-door",
        ["play"] = "play",
        ["keyboard"] = "keyboard",
        ["trophy"] = "trophy",
        ["arena"] = "arena",
        ["people"] = "people",
        ["challenge"] = "challenge",
        ["flag"] = "flag",
        ["texts"] = "texts",
        ["file-earmark-text"] = "file-earmark-text",
        ["profile"] = "profile",
        ["person"] = "person",
        ["settings"] = "settings",
        ["gear"] = "gear",
        ["menu"] = "menu",
        ["list"] = "list",
        ["close"] = "close",
        ["x-lg"] = "x-lg",
        ["spark"] = "spark",
        ["stars"] = "stars",
        ["logout"] = "logout",
        ["box-arrow-right"] = "box-arrow-right",
        ["chevron"] = "chevron",
        ["chevron-right"] = "chevron-right",
        ["target"] = "target",
        ["bullseye"] = "bullseye",
        ["fire"] = "fire",
        ["bolt"] = "bolt",
        ["lightning-charge-fill"] = "lightning-charge-fill",
        ["type"] = "type",
        ["words"] = "words",
        ["journal-text"] = "journal-text",
        ["shield"] = "shield",
        ["shield-check"] = "shield-check",
        ["play-fill"] = "play-fill",
        ["stopwatch"] = "stopwatch",
        ["award"] = "award",
        ["clipboard"] = "clipboard",
        ["clipboard-check"] = "clipboard-check",
        ["graph"] = "graph",
        ["graph-up-arrow"] = "graph-up-arrow",
        ["magic"] = "magic",
        ["sun"] = "sun",
        ["bell"] = "bell",
        ["quest-rounds"] = "quest-rounds",
        ["quest-accuracy"] = "quest-accuracy",
        ["quest-tempo"] = "quest-tempo",
        ["quest-arena"] = "quest-arena",
        ["quest-week"] = "quest-week",
        ["quest-texts"] = "quest-texts",
        ["mission-daily"] = "mission-daily",
        ["mission-weekly"] = "mission-weekly",
        ["achievement-training"] = "achievement-training",
        ["achievement-precision"] = "achievement-precision",
        ["achievement-speed"] = "achievement-speed",
        ["achievement-streak"] = "achievement-streak",
        ["achievement-arena"] = "achievement-arena",
        ["achievement-text"] = "achievement-text",
        ["achievement-team"] = "achievement-team",
        ["achievement-mission"] = "achievement-mission",
        ["level-up"] = "level-up",
        ["xp"] = "xp",
        ["personal-best"] = "personal-best",
        ["rank"] = "rank",
        ["podium"] = "podium",
        ["empty-leaderboard"] = "empty-leaderboard",
        ["empty-achievements"] = "empty-achievements",
        ["empty-results"] = "empty-results",
        ["empty-arena"] = "empty-arena",
        ["empty-texts"] = "empty-texts",
        ["lock"] = "lock",
        ["unlock"] = "unlock",
        ["medal"] = "medal",
        ["badge"] = "badge",
        ["hexagon"] = "hexagon"
    };

    public static IHtmlContent Svg(string name, string cssClass = "app-icon")
    {
        var iconId = IconIds.TryGetValue(name, out var resolved) ? resolved : "hexagon";
        var encodedClass = HtmlEncoder.Default.Encode(cssClass);
        var encodedId = HtmlEncoder.Default.Encode($"kw-{iconId}");

        return new HtmlString($"""<svg class="{encodedClass}" aria-hidden="true" focusable="false"><use href="/vendor/keywars-assets/keywars-icons.svg#{encodedId}"></use></svg>""");
    }
}
