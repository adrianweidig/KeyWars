namespace KeyWars.Domain;

public static class ArenaDivision
{
    public static string NameFor(int rating) => rating switch
    {
        >= 1300 => "Diamant",
        >= 1200 => "Platin",
        >= 1100 => "Gold",
        >= 1050 => "Silber",
        _ => "Bronze"
    };
}
