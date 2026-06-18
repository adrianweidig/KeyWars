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
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(text => text.OwnerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TextCollection>(entity =>
        {
            entity.HasIndex(collection => collection.OwnerProfileId);
            entity.Property(collection => collection.Visibility).HasConversion<string>();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(collection => collection.OwnerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TextCollectionItem>(entity =>
        {
            entity.HasKey(item => new { item.TextCollectionId, item.TrainingTextId });
            entity.HasIndex(item => new { item.TextCollectionId, item.SortOrder });
            entity.HasOne<TextCollection>()
                .WithMany()
                .HasForeignKey(item => item.TextCollectionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<TrainingText>()
                .WithMany()
                .HasForeignKey(item => item.TrainingTextId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TypingAttempt>(entity =>
        {
            entity.HasIndex(attempt => new { attempt.UserProfileId, attempt.Mode, attempt.CreatedAt });
            entity.HasIndex(attempt => attempt.TrainingTextId);
            entity.Property(attempt => attempt.Mode).HasConversion<string>();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(attempt => attempt.UserProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TrainingText>()
                .WithMany()
                .HasForeignKey(attempt => attempt.TrainingTextId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Challenge>(entity =>
        {
            entity.HasIndex(challenge => challenge.CreatorProfileId);
            entity.HasIndex(challenge => challenge.Status);
            entity.Property(challenge => challenge.Mode).HasConversion<string>();
            entity.Property(challenge => challenge.Status).HasConversion<string>();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(challenge => challenge.CreatorProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TrainingText>()
                .WithMany()
                .HasForeignKey(challenge => challenge.TrainingTextId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChallengeParticipant>(entity =>
        {
            entity.HasKey(participant => new { participant.ChallengeId, participant.UserProfileId });
            entity.HasIndex(participant => participant.UserProfileId);
            entity.Property(participant => participant.Status).HasConversion<string>();
            entity.HasOne<Challenge>()
                .WithMany()
                .HasForeignKey(participant => participant.ChallengeId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(participant => participant.UserProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChallengeRound>(entity =>
        {
            entity.HasIndex(round => new { round.ChallengeId, round.RoundNumber }).IsUnique();
            entity.HasOne<Challenge>()
                .WithMany()
                .HasForeignKey(round => round.ChallengeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChallengeRoundResult>(entity =>
        {
            entity.HasIndex(result => new { result.ChallengeRoundId, result.UserProfileId }).IsUnique();
            entity.Property(result => result.Status).HasConversion<string>();
            entity.HasOne<ChallengeRound>()
                .WithMany()
                .HasForeignKey(result => result.ChallengeRoundId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(result => result.UserProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<TypingAttempt>()
                .WithMany()
                .HasForeignKey(result => result.TypingAttemptId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<LiveRoomSummary>(entity =>
        {
            entity.HasIndex(room => room.RoomCode);
            entity.Property(room => room.Mode).HasConversion<string>();
            entity.Property(room => room.Visibility).HasConversion<string>();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(room => room.CreatorProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LiveRoomParticipantSummary>(entity =>
        {
            entity.HasIndex(summary => summary.LiveRoomSummaryId);
            entity.Property(summary => summary.Status).HasConversion<string>();
            entity.HasOne<LiveRoomSummary>()
                .WithMany()
                .HasForeignKey(summary => summary.LiveRoomSummaryId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(summary => summary.UserProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Mission>(entity =>
        {
            entity.HasIndex(mission => new { mission.UserProfileId, mission.MissionDate });
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(mission => mission.UserProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Achievement>(entity =>
        {
            entity.HasIndex(achievement => new { achievement.UserProfileId, achievement.Key }).IsUnique();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(achievement => achievement.UserProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WeaknessObservation>(entity =>
        {
            entity.HasIndex(observation => new { observation.UserProfileId, observation.Pattern }).IsUnique();
            entity.HasOne<UserProfile>()
                .WithMany()
                .HasForeignKey(observation => observation.UserProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
