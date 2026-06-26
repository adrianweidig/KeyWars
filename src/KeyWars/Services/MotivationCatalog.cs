namespace KeyWars.Services;

public static class MissionKeys
{
    public const string DailyThreeRounds = "daily-three-rounds";
    public const string DailyAccuracy = "daily-accuracy";
    public const string DailyTempo = "daily-tempo";
    public const string DailyArenaOrTeam = "daily-arena-or-team";
    public const string WeeklyRounds = "weekly-ten-rounds";
    public const string WeeklyPrecision = "weekly-three-precise";
    public const string WeeklyArena = "weekly-two-arena";
    public const string WeeklyTexts = "weekly-text-training";
}

public static class MotivationCatalog
{
    public static IReadOnlyList<AchievementDefinition> AchievementDefinitions { get; } =
    [
        new("erster-versuch", "Training", "Erster gültiger Versuch", "Du hast deinen ersten gültigen KeyWars-Versuch abgeschlossen."),
        new("training-5-attempts", "Training", "Fünf Runden", "Du hast fünf gültige Trainingsrunden abgeschlossen."),
        new("training-10-attempts", "Training", "Zehn Runden", "Du hast zehn gültige Trainingsrunden abgeschlossen."),
        new("training-25-attempts", "Training", "Trainingsroutine", "Du hast 25 gültige Trainingsrunden abgeschlossen."),
        new("training-50-attempts", "Training", "Feste Gewohnheit", "Du hast 50 gültige Trainingsrunden abgeschlossen."),
        new("training-100-attempts", "Training", "Hundert Runden", "Du hast 100 gültige Trainingsrunden abgeschlossen."),
        new("training-text-round", "Training", "Text gemeistert", "Du hast eine Textrunde abgeschlossen."),
        new("training-words-round", "Training", "Worttest erledigt", "Du hast einen Worttest abgeschlossen."),
        new("training-sprint-round", "Training", "Sprint abgeschlossen", "Du hast einen Zeitsprint abgeschlossen."),
        new("training-weakness-focus", "Training", "Fehlerfokus genutzt", "Du hast eine Fehlerfokus-Runde abgeschlossen."),
        new("precision-95", "Präzision", "Saubere Runde", "Du hast eine Runde mit mindestens 95 % Genauigkeit abgeschlossen."),
        new("praezise", "Präzision", "Präzise Hände", "Ein gültiger Versuch mit mindestens 98 % Genauigkeit."),
        new("precision-100", "Präzision", "Fehlerfrei", "Du hast eine Runde ohne verbleibende Fehler abgeschlossen."),
        new("precision-3x-98", "Präzision", "Dreimal sehr präzise", "Du hast drei Runden mit mindestens 98 % Genauigkeit abgeschlossen."),
        new("precision-10x-95", "Präzision", "Verlässliche Genauigkeit", "Du hast zehn Runden mit mindestens 95 % Genauigkeit abgeschlossen."),
        new("speed-40", "Tempo", "40 WPM", "Du hast 40 WPM erreicht."),
        new("speed-60", "Tempo", "60 WPM", "Du hast 60 WPM erreicht."),
        new("speed-80", "Tempo", "80 WPM", "Du hast 80 WPM erreicht."),
        new("speed-100", "Tempo", "100 WPM", "Du hast 100 WPM erreicht."),
        new("speed-personal-best", "Tempo", "Neue Bestleistung", "Du hast deine bisherige WPM-Bestleistung verbessert."),
        new("streak-3", "Serie", "Drei Tage aktiv", "Du hast an drei Trainingstagen in Folge geübt."),
        new("streak-7", "Serie", "Sieben Tage aktiv", "Du hast an sieben Trainingstagen in Folge geübt."),
        new("streak-14", "Serie", "Zwei Wochen aktiv", "Du hast an vierzehn Trainingstagen in Folge geübt."),
        new("streak-30", "Serie", "Monatsserie", "Du hast an dreißig Trainingstagen in Folge geübt."),
        new("arena-first", "Arena", "Erstes Rennen", "Du hast deine erste Arena-Runde abgeschlossen."),
        new("arena-5", "Arena", "Fünf Rennen", "Du hast fünf Arena-Runden abgeschlossen."),
        new("arena-10", "Arena", "Zehn Rennen", "Du hast zehn Arena-Runden abgeschlossen."),
        new("arena-rating-1050", "Arena", "Rating 1050", "Du hast ein Arena-Rating von 1050 erreicht."),
        new("arena-rating-1100", "Arena", "Rating 1100", "Du hast ein Arena-Rating von 1100 erreicht."),
        new("arena-perfect-accuracy", "Arena", "Arena ohne Fehler", "Du hast eine Arena-Runde mit 100 % Genauigkeit abgeschlossen."),
        new("text-author-first", "Texte", "Erster eigener Text", "Du hast einen eigenen Trainingstext angelegt."),
        new("text-author-3", "Texte", "Textsammlung wächst", "Du hast drei eigene Trainingstexte angelegt."),
        new("text-collection-first", "Texte", "Eigene Sammlung", "Du hast eine eigene Textsammlung angelegt."),
        new("team-first-challenge", "Team", "Erste Herausforderung", "Du hast deine erste Gruppenherausforderung abgeschlossen."),
        new("team-3-challenges", "Team", "Drei Herausforderungen", "Du hast drei Gruppenherausforderungen abgeschlossen."),
        new("team-precise", "Team", "Teampräzision", "Du hast eine Teamrunde mit mindestens 98 % Genauigkeit abgeschlossen."),
        new("mission-first", "Missionen", "Erste Mission", "Du hast deine erste Mission abgeschlossen."),
        new("mission-5", "Missionen", "Fünf Missionen", "Du hast fünf Missionen abgeschlossen."),
        new("mission-weekly", "Missionen", "Wochenziel erreicht", "Du hast eine Wochenmission abgeschlossen.")
    ];

    public static IReadOnlyList<MissionDefinition> MissionDefinitions { get; } =
    [
        new(MissionKeys.DailyThreeRounds, MissionCadence.Daily, "Drei kurze Runden", "Schließe heute drei gültige Versuche ab.", 3, 30),
        new(MissionKeys.DailyAccuracy, MissionCadence.Daily, "Genauigkeit halten", "Erreiche einmal mindestens 95 % Genauigkeit.", 1, 35),
        new(MissionKeys.DailyTempo, MissionCadence.Daily, "Tempo festigen", "Beende zwei Sprint- oder Textrunden.", 2, 25),
        new(MissionKeys.DailyArenaOrTeam, MissionCadence.Daily, "Gemeinsam antreten", "Schließe heute eine Arena-Runde ab.", 1, 25),
        new(MissionKeys.WeeklyRounds, MissionCadence.Weekly, "Zehn Runden in der Woche", "Schließe in dieser Woche zehn gültige Runden ab.", 10, 80),
        new(MissionKeys.WeeklyPrecision, MissionCadence.Weekly, "Drei Präzisionsrunden", "Erreiche in dieser Woche dreimal mindestens 98 % Genauigkeit.", 3, 90),
        new(MissionKeys.WeeklyArena, MissionCadence.Weekly, "Zwei Arena-Runden", "Schließe in dieser Woche zwei Arena-Runden ab.", 2, 70),
        new(MissionKeys.WeeklyTexts, MissionCadence.Weekly, "Texte trainieren", "Schließe in dieser Woche drei Runden mit gespeicherten Texten ab.", 3, 60)
    ];
}
