using Microsoft.EntityFrameworkCore;
using PuddingController.Data.Entities;

namespace PuddingController.Data;

/// <summary>
/// Controller service database context for workspace, routing, and audit persistence.
/// </summary>
public sealed class ControllerDbContext : DbContext
{
    public ControllerDbContext(DbContextOptions<ControllerDbContext> options) : base(options) { }

    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<RouteDecisionEntity> RouteDecisions => Set<RouteDecisionEntity>();
    public DbSet<WorkspaceDefinitionEntity> WorkspaceDefinitions => Set<WorkspaceDefinitionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("ctrl");

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("AuditEvents");
            entity.HasKey(e => e.EventId);
            entity.HasIndex(e => e.SessionId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.WorkspaceId);
        });

        modelBuilder.Entity<RouteDecisionEntity>(entity =>
        {
            entity.ToTable("RouteDecisions");
            entity.HasKey(e => e.RouteDecisionId);
            entity.HasIndex(e => e.MessageId);
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.WorkspaceId);
        });

        modelBuilder.Entity<WorkspaceDefinitionEntity>(entity =>
        {
            entity.ToTable("WorkspaceDefinitions");
            entity.HasKey(e => e.WorkspaceId);
        });
    }
}
