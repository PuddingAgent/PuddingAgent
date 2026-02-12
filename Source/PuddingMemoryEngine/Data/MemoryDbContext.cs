using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// Core memory EF Core context for session messages, memory entries, legacy facts, and event diagnostics.
/// </summary>
public sealed class MemoryDbContext : DbContext
{
    public MemoryDbContext(DbContextOptions<MemoryDbContext> options) : base(options) { }

    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<MemoryEntity> Memories => Set<MemoryEntity>();
    public DbSet<AgentMemoryEntity> AgentMemories => Set<AgentMemoryEntity>();
    public DbSet<MemoryFactEntity> MemoryFacts => Set<MemoryFactEntity>();
    public DbSet<MemoryPreferenceEntity> MemoryPreferences => Set<MemoryPreferenceEntity>();
    public DbSet<SubconsciousJobLogEntity> SubconsciousJobLogs => Set<SubconsciousJobLogEntity>();
    public DbSet<SubconsciousJobEntity> SubconsciousJobs => Set<SubconsciousJobEntity>();
    public DbSet<EventQueueEntity> EventQueue => Set<EventQueueEntity>();
    public DbSet<EventDiagnosticLogEntity> EventDiagnosticLogs => Set<EventDiagnosticLogEntity>();
    public DbSet<AgentCheckpointEntity> AgentCheckpoints => Set<AgentCheckpointEntity>();
    public DbSet<EventSubscriptionEntity> EventSubscriptions => Set<EventSubscriptionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SessionEntity>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(e => e.SessionId);
            entity.HasIndex(e => new { e.WorkspaceId, e.LastActivityAt }).IsDescending(false, true);
            entity.HasIndex(e => new { e.Status, e.LastActivityAt }).IsDescending(false, true);
        });

        modelBuilder.Entity<MessageEntity>(entity =>
        {
            entity.ToTable("Messages");
            entity.HasKey(e => e.MessageId);
            entity.HasIndex(e => new { e.SessionId, e.Sequence });
            entity.HasIndex(e => e.ParentId);
            entity.HasIndex(e => new { e.SessionId, e.BranchType, e.Sequence });
            entity.HasIndex(e => e.CompactedBy);
            entity.HasOne(e => e.Session)
                .WithMany(e => e.Messages)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Parent)
                .WithMany(e => e.Children)
                .HasForeignKey(e => e.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CompactingMessage)
                .WithMany()
                .HasForeignKey(e => e.CompactedBy)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MemoryEntity>(entity =>
        {
            entity.ToTable("Memories");
            entity.HasKey(e => e.MemoryId);
            entity.HasIndex(e => new { e.Scope, e.WorkspaceId, e.AgentId, e.CreatedAt }).IsDescending(false, false, false, true);
            entity.HasIndex(e => new { e.WorkspaceId, e.Tag });
            entity.HasIndex(e => e.WorkspaceId);
        });

        modelBuilder.Entity<AgentMemoryEntity>(entity =>
        {
            entity.ToTable("AgentMemories");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MemoryId).IsUnique();
            entity.HasIndex(e => new { e.AgentInstanceId, e.MemoryType, e.CreatedAt }).IsDescending(false, false, true);
        });

        modelBuilder.Entity<MemoryPreferenceEntity>(entity =>
        {
            entity.ToTable("MemoryPreferences");
            entity.HasKey(e => e.PreferenceId);
            entity.HasIndex(e => new { e.WorkspaceId, e.Category, e.Key }).IsUnique();
            entity.HasIndex(e => new { e.WorkspaceId, e.Category });
        });

        modelBuilder.Entity<MemoryFactEntity>(entity =>
        {
            entity.ToTable("LegacyMemoryFacts");
            entity.HasKey(e => e.FactId);
            entity.HasIndex(e => new { e.WorkspaceId, e.Category });
            entity.HasIndex(e => e.SourceSessionId);
        });

        modelBuilder.Entity<SubconsciousJobLogEntity>(entity =>
        {
            entity.ToTable("SubconsciousJobLogs");
            entity.HasKey(e => e.JobId);
            entity.HasIndex(e => e.SessionId);
        });

        modelBuilder.Entity<SubconsciousJobEntity>(entity =>
        {
            entity.ToTable("SubconsciousJobs");
            entity.HasKey(e => e.JobId);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.Status, e.AvailableAt });
            entity.HasIndex(e => e.LeaseUntil);
            entity.HasIndex(e => new { e.WorkspaceId, e.SessionId });
        });

        modelBuilder.Entity<EventQueueEntity>(entity =>
        {
            entity.ToTable("EventQueue");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Status, e.Priority, e.CreatedAt });
            entity.HasIndex(e => e.WorkspaceId);
        });

        modelBuilder.Entity<EventDiagnosticLogEntity>(entity =>
        {
            entity.ToTable("EventDiagnosticLogs");
            entity.HasKey(e => e.LogId);
            entity.HasIndex(e => e.EventId);
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<AgentCheckpointEntity>(entity =>
        {
            entity.ToTable("AgentCheckpoints");
            entity.HasKey(e => e.CheckpointId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.Status });
        });

        modelBuilder.Entity<EventSubscriptionEntity>(entity =>
        {
            entity.ToTable("EventSubscriptions");
            entity.HasKey(e => e.SubscriptionId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.EventTypePattern });
            entity.HasIndex(e => e.Status);
        });
    }
}
