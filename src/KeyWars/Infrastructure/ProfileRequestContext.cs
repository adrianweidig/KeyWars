using KeyWars.Domain;

namespace KeyWars.Infrastructure;

public sealed class ProfileRequestContext
{
    private Guid? leasedProfileId;
    private UserProfile? profile;

    public bool HasLease(Guid profileId) => leasedProfileId == profileId;

    public void Begin(Guid profileId)
    {
        leasedProfileId = profileId;
        profile = null;
    }

    public void SetProfile(UserProfile value)
    {
        if (leasedProfileId != value.Id)
        {
            throw new InvalidOperationException("Das Profil gehört nicht zum aktuellen Profilzugriff.");
        }

        profile = value;
    }

    public bool TryGetProfile(Guid profileId, out UserProfile? value)
    {
        value = leasedProfileId == profileId ? profile : null;
        return value is not null;
    }

    public void Clear()
    {
        profile = null;
        leasedProfileId = null;
    }
}
