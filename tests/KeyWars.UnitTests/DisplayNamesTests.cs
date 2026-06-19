using KeyWars.Domain;

namespace KeyWars.UnitTests;

public sealed class DisplayNamesTests
{
    public static TheoryData<Enum> PublicDomainEnumValues()
    {
        var values = new TheoryData<Enum>();
        AddValues<TrainingMode>(values);
        AddValues<AttemptPhase>(values);
        AddValues<TypingErrorKind>(values);
        AddValues<TrainingTextVisibility>(values);
        AddValues<ChallengeMode>(values);
        AddValues<ChallengeStatus>(values);
        AddValues<ParticipantStatus>(values);
        AddValues<LiveRoomPhase>(values);
        AddValues<LiveRoomVisibility>(values);
        AddValues<LiveRoomMode>(values);
        return values;
    }

    [Theory]
    [MemberData(nameof(PublicDomainEnumValues))]
    public void PublicDomainEnumValuesHaveStableGermanDisplayNames(Enum value)
    {
        var displayName = DisplayNameFor(value);

        Assert.False(string.IsNullOrWhiteSpace(displayName));
        Assert.NotEqual(value.ToString(), displayName);
    }

    private static void AddValues<TEnum>(TheoryData<Enum> values)
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            values.Add(value);
        }
    }

    private static string DisplayNameFor(Enum value) => value switch
    {
        TrainingMode mode => DisplayNames.For(mode),
        AttemptPhase phase => DisplayNames.For(phase),
        TypingErrorKind kind => DisplayNames.For(kind),
        TrainingTextVisibility visibility => DisplayNames.For(visibility),
        ChallengeMode mode => DisplayNames.For(mode),
        ChallengeStatus status => DisplayNames.For(status),
        ParticipantStatus status => DisplayNames.For(status),
        LiveRoomPhase phase => DisplayNames.For(phase),
        LiveRoomVisibility visibility => DisplayNames.For(visibility),
        LiveRoomMode mode => DisplayNames.For(mode),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Kein DisplayName-Mapping vorhanden.")
    };
}
