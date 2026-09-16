namespace KeyWars.Services;

public sealed class SeasonOptions
{
    public bool Enabled { get; set; } = true;
    public int MonthsPerSeason { get; set; } = 1;
    public int RolloverCheckMinutes { get; set; } = 60;

    public void Validate()
    {
        if (MonthsPerSeason is < 1 or > 12 || 12 % MonthsPerSeason != 0)
        {
            throw new InvalidOperationException("Saisons müssen 1, 2, 3, 4, 6 oder 12 Monate dauern.");
        }

        if (RolloverCheckMinutes is < 5 or > 1440)
        {
            throw new InvalidOperationException("Das Saison-Rollover-Intervall muss zwischen 5 und 1440 Minuten liegen.");
        }
    }
}
