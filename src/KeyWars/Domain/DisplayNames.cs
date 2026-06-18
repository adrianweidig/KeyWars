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
        _ => status.ToString()
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

    public static string For(TrainingTextVisibility visibility) => visibility switch
    {
        TrainingTextVisibility.Private => "Privat",
        TrainingTextVisibility.Organization => "Organisation",
        _ => visibility.ToString()
    };
}
