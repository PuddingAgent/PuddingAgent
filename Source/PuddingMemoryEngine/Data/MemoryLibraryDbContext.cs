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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<LibraryEntity>(entity =>
        {
            entity.ToTable("Libraries");
            entity.HasKey(e => e.LibraryId);
            entity.HasIndex(e => e.WorkspaceId);
        });

        modelBuilder.Entity<BookEntity>(entity =>
        {
            entity.ToTable("Books");
            entity.HasKey(e => e.BookId);
            entity.HasIndex(e => new { e.LibraryId, e.UpdatedAt }).IsDescending(false, true);
            entity.HasIndex(e => new { e.Status, e.UpdatedAt }).IsDescending(false, true);
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
    }
}
