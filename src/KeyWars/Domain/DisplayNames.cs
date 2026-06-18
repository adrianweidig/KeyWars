namespace KeyWars.Domain;

public static class DisplayNames
{
    public static string For(ParticipantStatus status) => status switch
    {
        ParticipantStatus.Invited => "Eingeladen",
        ParticipantStatus.Joined => "Beigetreten",
        ParticipantStatus.Ready => "Bereit",
        ParticipantStatus.Running => "Läuft",
        ParticipantStatus.Finished => "Fertig",
        ParticipantStatus.Dnf => "Nicht beendet",
        ParticipantStatus.Disconnected => "Verbindung getrennt",
        ParticipantStatus.Declined => "Abgelehnt",
        ParticipantStatus.LeftBeforeStart => "Vor dem Start verlassen",
        ParticipantStatus.Cancelled => "Abgebrochen",
        ParticipantStatus.AbortedByServer => "Durch Serverabbruch beendet",
        _ => status.ToString()
    };

    public static string For(LiveRoomPhase phase) => phase switch
    {
        LiveRoomPhase.Lobby => "Lobby",
        LiveRoomPhase.Countdown => "Countdown",
        LiveRoomPhase.Running => "Rennen läuft",
        LiveRoomPhase.RoundResults => "Rundenergebnis",
        LiveRoomPhase.SeriesResults => "Ergebnisse",
        LiveRoomPhase.Closed => "Geschlossen",
        LiveRoomPhase.Aborted => "Abgebrochen",
        _ => phase.ToString()
    };

    public static string For(LiveRoomMode mode) => mode switch
    {
        LiveRoomMode.Classic => "Klassisches Rennen",
        LiveRoomMode.TimeDuel => "Zeitduell",
        LiveRoomMode.Precision => "Präzisionsduell",
        LiveRoomMode.BestOf => "Best-of-Serie",
        _ => mode.ToString()
    };

    public static string For(ChallengeMode mode) => mode switch
    {
        ChallengeMode.Classic => "Klassisches Rennen",
        ChallengeMode.TimeDuel => "Zeitduell",
        ChallengeMode.Precision => "Präzisionsduell",
        ChallengeMode.BestOf => "Best-of-Serie",
        _ => mode.ToString()
    };

    public static string For(TrainingMode mode) => mode switch
    {
        TrainingMode.Sprint15 => "15-Sekunden-Sprint",
        TrainingMode.Sprint30 => "30-Sekunden-Sprint",
        TrainingMode.Sprint60 => "60-Sekunden-Sprint",
        TrainingMode.Sprint120 => "120-Sekunden-Sprint",
        TrainingMode.Words10 => "10 Wörter",
        TrainingMode.Words25 => "25 Wörter",
        TrainingMode.Words50 => "50 Wörter",
        TrainingMode.Words100 => "100 Wörter",
        TrainingMode.Text => "Texttraining",
        TrainingMode.Precision => "Präzision",
        TrainingMode.Umlauts => "Umlaute und ß",
        TrainingMode.NumbersAndSymbols => "Zahlen und Sonderzeichen",
        TrainingMode.WeaknessFocus => "Fehlerfokus",
        TrainingMode.Warmup => "Aufwärmen",
        TrainingMode.Endless => "Endlosmodus",
        TrainingMode.Ghost => "Geist-Rennen",
        TrainingMode.RivalGhost => "Rivalen-Geist",
        _ => mode.ToString()
    };

    public static string For(TrainingTextVisibility visibility) => visibility switch
    {
        TrainingTextVisibility.Private => "Privat",
        TrainingTextVisibility.Organization => "Organisation",
        _ => visibility.ToString()
    };
}
