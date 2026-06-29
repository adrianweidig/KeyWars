using KeyWars.Domain;
using KeyWars.Services;

namespace KeyWars.Infrastructure;

public sealed record MotivationVisual(string VisualKey, string Accent, string Illustration);

public static class MotivationVisuals
{
    public static MotivationVisual ForMissionKey(string key) => key switch
    {
        MissionKeys.DailyThreeRounds => new("quest-rounds", "training", "motivator-results"),
        MissionKeys.DailyAccuracy => new("quest-accuracy", "precision", "motivator-achievements"),
        MissionKeys.DailyTempo => new("quest-tempo", "speed", "motivator-results"),
        MissionKeys.DailyArenaOrTeam => new("quest-arena", "arena", "motivator-arena"),
        MissionKeys.WeeklyRounds => new("quest-week", "weekly", "motivator-leaderboard"),
        MissionKeys.WeeklyPrecision => new("achievement-precision", "precision", "motivator-achievements"),
        MissionKeys.WeeklyArena => new("achievement-arena", "arena", "motivator-arena"),
        MissionKeys.WeeklyTexts => new("quest-texts", "texts", "motivator-texts"),
        _ when key.StartsWith("weekly-", StringComparison.Ordinal) => new("mission-weekly", "weekly", "motivator-leaderboard"),
        _ => new("mission-daily", "training", "motivator-results")
    };

    public static MotivationVisual ForAchievementKey(string key) => key switch
    {
        _ when key.StartsWith("speed-", StringComparison.Ordinal) => new("achievement-speed", "speed", "motivator-results"),
        _ when key.StartsWith("precision-", StringComparison.Ordinal) || key == "praezise" => new("achievement-precision", "precision", "motivator-achievements"),
        _ when key.StartsWith("streak-", StringComparison.Ordinal) => new("achievement-streak", "streak", "motivator-achievements"),
        _ when key.StartsWith("arena-", StringComparison.Ordinal) => new("achievement-arena", "arena", "motivator-arena"),
        _ when key.StartsWith("text-", StringComparison.Ordinal) => new("achievement-text", "texts", "motivator-texts"),
        _ when key.StartsWith("team-", StringComparison.Ordinal) => new("achievement-team", "team", "motivator-arena"),
        _ when key.StartsWith("mission-", StringComparison.Ordinal) => new("achievement-mission", "mission", "motivator-leaderboard"),
        _ => new("achievement-training", "training", "motivator-achievements")
    };

    public static MotivationVisual ForEvent(GamificationEvent item) => item.Type switch
    {
        GamificationEventType.LevelUp => new("level-up", "level", "reward-burst"),
        GamificationEventType.AchievementUnlocked => ForAchievementKey(item.SourceId),
        GamificationEventType.MissionCompleted => new("achievement-mission", RarityAccent(item.Rarity), "motivator-leaderboard"),
        GamificationEventType.PersonalBest => new("personal-best", "speed", "reward-burst"),
        GamificationEventType.ArenaResult => new("achievement-arena", "arena", "motivator-arena"),
        _ => new("xp", RarityAccent(item.Rarity), "reward-burst")
    };

    public static MotivationVisual ForTrainingMode(TrainingMode mode) => mode switch
    {
        TrainingMode.WeaknessFocus => new("bolt", "focus", "motivator-results"),
        TrainingMode.Words10 or TrainingMode.Words25 or TrainingMode.Words50 or TrainingMode.Words100 => new("words", "texts", "motivator-texts"),
        TrainingMode.Text => new("type", "texts", "motivator-texts"),
        TrainingMode.Ghost or TrainingMode.RivalGhost => new("personal-best", "speed", "motivator-results"),
        TrainingMode.Precision => new("achievement-precision", "precision", "motivator-achievements"),
        _ when mode.ToString().StartsWith("Sprint", StringComparison.Ordinal) => new("stopwatch", "speed", "motivator-results"),
        _ => new("keyboard", "training", "motivator-results")
    };

    private static string RarityAccent(GamificationRarity rarity) => rarity switch
    {
        GamificationRarity.Epic => "epic",
        GamificationRarity.Rare => "rare",
        _ => "common"
    };
}
