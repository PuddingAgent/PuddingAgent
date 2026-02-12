using Microsoft.EntityFrameworkCore;
using PuddingMemoryEngine.Entities;

namespace PuddingMemoryEngine.Data;

/// <summary>
/// 记忆图书馆的 EF Core 数据库上下文（SQLite）。
/// 管理 Libraries、Books、BookIndexes、Chapters、Pointers、Branches 六张核心表。
/// </summary>
public class MemoryLibraryDbContext : DbContext
{
    public MemoryLibraryDbContext(DbContextOptions<MemoryLibraryDbContext> options) : base(options) { }

    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<BookEntity> Books => Set<BookEntity>();
    public DbSet<BookIndexEntity> BookIndexes => Set<BookIndexEntity>();
    public DbSet<ChapterEntity> Chapters => Set<ChapterEntity>();
    public DbSet<PointerEntity> Pointers => Set<PointerEntity>();
    public DbSet<BranchEntity> Branches => Set<BranchEntity>();
    public DbSet<SourceReferenceEntity> SourceReferences => Set<SourceReferenceEntity>();
    public DbSet<MemoryTreeNodeEntity> MemoryTreeNodes => Set<MemoryTreeNodeEntity>();
    public DbSet<BookTreeMountEntity> BookTreeMounts => Set<BookTreeMountEntity>();
    public DbSet<ChapterRelationEntity> ChapterRelations => Set<ChapterRelationEntity>();
    public DbSet<MemorySpaceEntity> MemorySpaces => Set<MemorySpaceEntity>();
    public DbSet<GraphMemoryFactEntity> MemoryFacts => Set<GraphMemoryFactEntity>();
    public DbSet<MemoryFactEvidenceEntity> MemoryFactEvidence => Set<MemoryFactEvidenceEntity>();
    public DbSet<MemoryFactContextEntity> MemoryFactContexts => Set<MemoryFactContextEntity>();
    public DbSet<MemoryFactFreshnessEntity> MemoryFactFreshness => Set<MemoryFactFreshnessEntity>();
    public DbSet<MemoryFactEntityMentionEntity> MemoryFactEntityMentions => Set<MemoryFactEntityMentionEntity>();
    public DbSet<MemoryFactAssociationEntity> MemoryFactAssociations => Set<MemoryFactAssociationEntity>();
    public DbSet<MemoryFactRevisionEntity> MemoryFactRevisions => Set<MemoryFactRevisionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LibraryEntity>(entity =>
        {
            entity.ToTable("Libraries");
            entity.HasKey(e => e.LibraryId);
            entity.HasIndex(e => e.WorkspaceId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId });
        });

        modelBuilder.Entity<BookEntity>(entity =>
        {
            entity.ToTable("Books");
            entity.HasKey(e => e.BookId);
            entity.HasIndex(e => new { e.LibraryId, e.UpdatedAt }).IsDescending(false, true);
            entity.HasIndex(e => new { e.Status, e.UpdatedAt }).IsDescending(false, true);
            entity.HasIndex(e => new { e.LibraryId, e.Title })
                .IsUnique()
                .HasFilter("Status = 'active'")
                .HasDatabaseName("UX_Books_Library_Title_Active");
            entity.HasMany(e => e.Indexes)
                .WithOne()
                .HasForeignKey("BookId");
        });

        modelBuilder.Entity<BookIndexEntity>(entity =>
        {
            entity.ToTable("BookIndexes");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.TagPath);
            entity.HasIndex(e => e.BookId);
        });

        modelBuilder.Entity<ChapterEntity>(entity =>
        {
            entity.ToTable("Chapters");
            entity.HasKey(e => e.ChapterId);
            entity.HasIndex(e => new { e.BookId, e.ChapterOrder });
            entity.HasIndex(e => new { e.BookId, e.Status, e.ChapterOrder });
            entity.HasIndex(e => e.SupersededByChapterId);
        });

        modelBuilder.Entity<PointerEntity>(entity =>
        {
            entity.ToTable("Pointers");
            entity.HasKey(e => e.PointerId);
            entity.HasIndex(e => e.ChapterId);
            entity.HasIndex(e => new { e.TargetType, e.TargetId });
        });

        modelBuilder.Entity<BranchEntity>(entity =>
        {
            entity.ToTable("Branches");
            entity.HasKey(e => e.BranchId);
            entity.HasIndex(e => e.BookId);
        });

        modelBuilder.Entity<SourceReferenceEntity>(entity =>
        {
            entity.ToTable("SourceReferences");
            entity.HasKey(e => e.SourceReferenceId);
            entity.HasIndex(e => new { e.OwnerType, e.OwnerId });
            entity.HasIndex(e => new { e.WorkspaceId, e.TargetType, e.TargetId });
        });

        modelBuilder.Entity<MemoryTreeNodeEntity>(entity =>
        {
            entity.ToTable("MemoryTreeNodes");
            entity.HasKey(e => e.NodeId);
            entity.HasIndex(e => new { e.WorkspaceId, e.LibraryId });
            entity.HasIndex(e => e.ParentNodeId);
            entity.HasIndex(e => e.Path);
        });

        modelBuilder.Entity<BookTreeMountEntity>(entity =>
        {
            entity.ToTable("BookTreeMounts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.BookId);
            entity.HasIndex(e => e.NodeId);
        });

        modelBuilder.Entity<ChapterRelationEntity>(entity =>
        {
            entity.ToTable("ChapterRelations");
            entity.HasKey(e => e.RelationId);
            entity.HasIndex(e => e.SourceChapterId);
            entity.HasIndex(e => e.TargetChapterId);
            entity.HasIndex(e => new { e.SourceChapterId, e.TargetChapterId, e.RelationType }).IsUnique();
        });

        modelBuilder.Entity<MemorySpaceEntity>(entity =>
        {
            entity.ToTable("MemorySpaces");
            entity.HasKey(e => e.MemorySpaceId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.Status });
        });

        modelBuilder.Entity<GraphMemoryFactEntity>(entity =>
        {
            entity.ToTable("MemoryFacts");
            entity.HasKey(e => e.FactId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.MemorySpaceId, e.Status });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactType, e.Status });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.SupersededByFactId });
        });

        modelBuilder.Entity<MemoryFactEvidenceEntity>(entity =>
        {
            entity.ToTable("MemoryFactEvidence");
            entity.HasKey(e => e.EvidenceId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.SourceType, e.SourceId });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.EvidenceHash });
        });

        modelBuilder.Entity<MemoryFactContextEntity>(entity =>
        {
            entity.ToTable("MemoryFactContexts");
            entity.HasKey(e => e.ContextId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.ContextHash });
        });

        modelBuilder.Entity<MemoryFactFreshnessEntity>(entity =>
        {
            entity.ToTable("MemoryFactFreshness");
            entity.HasKey(e => e.FreshnessId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId });
        });

        modelBuilder.Entity<MemoryFactEntityMentionEntity>(entity =>
        {
            entity.ToTable("MemoryFactEntityMentions");
            entity.HasKey(e => e.MentionId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.EntityKey });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId });
        });

        modelBuilder.Entity<MemoryFactAssociationEntity>(entity =>
        {
            entity.ToTable("MemoryFactAssociations");
            entity.HasKey(e => e.AssociationId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.SourceKind, e.SourceKey });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.TargetKind, e.TargetKey });
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId });
        });

        modelBuilder.Entity<MemoryFactRevisionEntity>(entity =>
        {
            entity.ToTable("MemoryFactRevisions");
            entity.HasKey(e => e.RevisionId);
            entity.HasIndex(e => new { e.WorkspaceId, e.AgentId, e.FactId, e.CreatedAt });
        });
    }
}
