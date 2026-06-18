using KeyWars.Domain;
using Microsoft.EntityFrameworkCore;

namespace KeyWars.Data;

public sealed class KeyWarsDbContext(DbContextOptions<KeyWarsDbContext> options) : DbContext(options)
{
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<TrainingText> TrainingTexts => Set<TrainingText>();
    public DbSet<TextCollection> TextCollections => Set<TextCollection>();
    public DbSet<TextCollectionItem> TextCollectionItems => Set<TextCollectionItem>();
    public DbSet<TypingAttempt> TypingAttempts => Set<TypingAttempt>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<ChallengeParticipant> ChallengeParticipants => Set<ChallengeParticipant>();
    public DbSet<ChallengeRound> ChallengeRounds => Set<ChallengeRound>();
    public DbSet<ChallengeRoundResult> ChallengeRoundResults => Set<ChallengeRoundResult>();
    public DbSet<LiveRoomSummary> LiveRoomSummaries => Set<LiveRoomSummary>();
    public DbSet<LiveRoomParticipantSummary> LiveRoomParticipantSummaries => Set<LiveRoomParticipantSummary>();
    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<Achievement> Achievements => Set<Achievement>();
    public DbSet<WeaknessObservation> WeaknessObservations => Set<WeaknessObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProfile>(entity =>
        {
            entity.HasIndex(profile => profile.DirectoryObjectGuid).IsUnique();
            entity.HasIndex(profile => profile.SamAccountName);
            entity.HasIndex(profile => profile.DisplayName);
            entity.Property(profile => profile.PreferredMode).HasConversion<string>();
        });

        modelBuilder.Entity<TrainingText>(entity =>
        {
            entity.HasIndex(text => text.SourceKey);
            entity.HasIndex(text => new { text.OwnerProfileId, text.Visibility });
            entity.Property(text => text.Visibility).HasConversion<string>();
        });

        modelBuilder.Entity<TextCollection>(entity =>
        {
            entity.HasIndex(collection => collection.OwnerProfileId);
            entity.Property(collection => collection.Visibility).HasConversion<string>();
        });

        modelBuilder.Entity<TextCollectionItem>(entity =>
        {
            entity.HasKey(item => new { item.TextCollectionId, item.TrainingTextId });
            entity.HasIndex(item => new { item.TextCollectionId, item.SortOrder });
        });

        modelBuilder.Entity<TypingAttempt>(entity =>
        {
            entity.HasIndex(attempt => new { attempt.UserProfileId, attempt.Mode, attempt.CreatedAt });
            entity.HasIndex(attempt => attempt.TrainingTextId);
            entity.Property(attempt => attempt.Mode).HasConversion<string>();
        });

        modelBuilder.Entity<Challenge>(entity =>
        {
            entity.HasIndex(challenge => challenge.CreatorProfileId);
            entity.HasIndex(challenge => challenge.Status);
            entity.Property(challenge => challenge.Mode).HasConversion<string>();
            entity.Property(challenge => challenge.Status).HasConversion<string>();
        });

        modelBuilder.Entity<ChallengeParticipant>(entity =>
        {
            entity.HasKey(participant => new { participant.ChallengeId, participant.UserProfileId });
            entity.HasIndex(participant => participant.UserProfileId);
            entity.Property(participant => participant.Status).HasConversion<string>();
        });

        modelBuilder.Entity<ChallengeRound>(entity =>
        {
            entity.HasIndex(round => new { round.ChallengeId, round.RoundNumber }).IsUnique();
        });

        modelBuilder.Entity<ChallengeRoundResult>(entity =>
        {
            entity.HasIndex(result => new { result.ChallengeRoundId, result.UserProfileId }).IsUnique();
            entity.Property(result => result.Status).HasConversion<string>();
        });

        modelBuilder.Entity<LiveRoomSummary>(entity =>
        {
            entity.HasIndex(room => room.RoomCode);
            entity.Property(room => room.Mode).HasConversion<string>();
            entity.Property(room => room.Visibility).HasConversion<string>();
        });

        modelBuilder.Entity<LiveRoomParticipantSummary>(entity =>
        {
            entity.HasIndex(summary => summary.LiveRoomSummaryId);
            entity.Property(summary => summary.Status).HasConversion<string>();
        });

        modelBuilder.Entity<Mission>(entity =>
        {
            entity.HasIndex(mission => new { mission.UserProfileId, mission.MissionDate });
        });

        modelBuilder.Entity<Achievement>(entity =>
        {
            entity.HasIndex(achievement => new { achievement.UserProfileId, achievement.Key }).IsUnique();
        });

        modelBuilder.Entity<WeaknessObservation>(entity =>
        {
            entity.HasIndex(observation => new { observation.UserProfileId, observation.Pattern }).IsUnique();
        });
    }
}
