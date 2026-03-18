using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Data;

/// <summary>Platform 数据库上下文，负责 LLM 资源池、AgentTemplate、用户/组织持久化。</summary>
public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    // LLM 资源池
    public DbSet<LlmProviderEntity> LlmProviders => Set<LlmProviderEntity>();
    public DbSet<LlmModelEntity> LlmModels => Set<LlmModelEntity>();
    public DbSet<LlmProviderQuotaEntity> LlmProviderQuotas => Set<LlmProviderQuotaEntity>();

    // Agent 模板
    public DbSet<GlobalAgentTemplateEntity> GlobalAgentTemplates => Set<GlobalAgentTemplateEntity>();
    public DbSet<WorkspaceAgentTemplateEntity> WorkspaceAgentTemplates => Set<WorkspaceAgentTemplateEntity>();

    // 用户 & 权限组
    public DbSet<AppUserEntity> AppUsers => Set<AppUserEntity>();
    public DbSet<AppRoleEntity> AppRoles => Set<AppRoleEntity>();
    public DbSet<AppUserRoleEntity> AppUserRoles => Set<AppUserRoleEntity>();

    // 团队 & 工作区
    public DbSet<TeamEntity> Teams => Set<TeamEntity>();
    public DbSet<TeamMemberEntity> TeamMembers => Set<TeamMemberEntity>();
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();
    public DbSet<WorkspaceMemberEntity> WorkspaceMembers => Set<WorkspaceMemberEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── LlmProvider ──────────────────────────────────────────────
        modelBuilder.Entity<LlmProviderEntity>(e =>
        {
            e.HasIndex(p => p.ProviderId).IsUnique();

            // 1:N Provider -> Models
            e.HasMany(p => p.Models)
             .WithOne(m => m.Provider)
             .HasForeignKey(m => m.ProviderId)
             .OnDelete(DeleteBehavior.Cascade);

            // 1:1 Provider -> Quota
            e.HasOne(p => p.Quota)
             .WithOne(q => q.Provider)
             .HasForeignKey<LlmProviderQuotaEntity>(q => q.ProviderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── LlmModel ─────────────────────────────────────────────────
        modelBuilder.Entity<LlmModelEntity>(e =>
        {
            e.HasIndex(m => new { m.ProviderId, m.ModelId }).IsUnique();
            e.Property(m => m.InputPricePer1MTokens).HasColumnType("decimal(18,6)");
            e.Property(m => m.OutputPricePer1MTokens).HasColumnType("decimal(18,6)");
        });

        // ── GlobalAgentTemplate ───────────────────────────────────────
        modelBuilder.Entity<GlobalAgentTemplateEntity>(e =>
        {
            e.HasIndex(t => t.TemplateId).IsUnique();
        });

        // ── WorkspaceAgentTemplate ────────────────────────────────────
        modelBuilder.Entity<WorkspaceAgentTemplateEntity>(e =>
        {
            e.HasIndex(t => new { t.WorkspaceId, t.TemplateId }).IsUnique();
        });

        // ── AppUser ───────────────────────────────────────────────────
        modelBuilder.Entity<AppUserEntity>(e =>
        {
            e.HasIndex(u => u.UserId).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        // ── AppRole ───────────────────────────────────────────────────
        modelBuilder.Entity<AppRoleEntity>(e =>
        {
            e.HasIndex(r => r.RoleId).IsUnique();
        });

        // ── AppUserRole (composite PK) ────────────────────────────────
        modelBuilder.Entity<AppUserRoleEntity>(e =>
        {
            e.HasKey(ur => new { ur.UserEntityId, ur.RoleEntityId });
            e.HasOne(ur => ur.User)
             .WithMany(u => u.UserRoles)
             .HasForeignKey(ur => ur.UserEntityId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(ur => ur.Role)
             .WithMany(r => r.UserRoles)
             .HasForeignKey(ur => ur.RoleEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Team ──────────────────────────────────────────────────────
        modelBuilder.Entity<TeamEntity>(e =>
        {
            e.HasIndex(t => t.TeamId).IsUnique();
        });

        // ── TeamMember (composite PK) ─────────────────────────────────
        modelBuilder.Entity<TeamMemberEntity>(e =>
        {
            e.HasKey(m => new { m.TeamEntityId, m.UserEntityId });
            e.HasOne(m => m.Team)
             .WithMany(t => t.Members)
             .HasForeignKey(m => m.TeamEntityId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User)
             .WithMany(u => u.TeamMemberships)
             .HasForeignKey(m => m.UserEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Workspace ─────────────────────────────────────────────────
        modelBuilder.Entity<WorkspaceEntity>(e =>
        {
            e.HasIndex(w => w.WorkspaceId).IsUnique();
            e.HasIndex(w => new { w.TeamEntityId, w.Slug }).IsUnique();
            e.HasOne(w => w.Team)
             .WithMany(t => t.Workspaces)
             .HasForeignKey(w => w.TeamEntityId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── WorkspaceMember ───────────────────────────────────────────
        modelBuilder.Entity<WorkspaceMemberEntity>(e =>
        {
            e.HasIndex(m => new { m.WorkspaceEntityId, m.UserEntityId }).IsUnique();
            e.HasOne(m => m.Workspace)
             .WithMany(w => w.Members)
             .HasForeignKey(m => m.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User)
             .WithMany(u => u.WorkspaceMemberships)
             .HasForeignKey(m => m.UserEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Seed：内置 OpenAI Provider + 常用模型 ────────────────────
        SeedBuiltInData(modelBuilder);
    }

    private static void SeedBuiltInData(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LlmProviderEntity>().HasData(
            new LlmProviderEntity
            {
                Id = 1,
                ProviderId = "openai",
                Name = "OpenAI",
                Protocol = "openai",
                BaseUrl = "https://api.openai.com/v1",
                Description = "OpenAI 官方 API",
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );

        modelBuilder.Entity<LlmModelEntity>().HasData(
            new LlmModelEntity
            {
                Id = 1,
                ProviderId = 1,
                ModelId = "gpt-4o-mini",
                Name = "GPT-4o Mini",
                Description = "轻量级多模态模型，适合一般对话和任务，性价比高。",
                MaxContextTokens = 128000,
                InputPricePer1MTokens = 0.15m,
                OutputPricePer1MTokens = 0.60m,
                CapabilityTagsJson = "[\"text\",\"vision\",\"function-calling\",\"json-mode\"]",
                IsDefault = true,
                SortOrder = 10,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new LlmModelEntity
            {
                Id = 2,
                ProviderId = 1,
                ModelId = "gpt-4o",
                Name = "GPT-4o",
                Description = "高性能多模态旗舰模型，支持视觉、函数调用与结构化输出。",
                MaxContextTokens = 128000,
                InputPricePer1MTokens = 5.00m,
                OutputPricePer1MTokens = 15.00m,
                CapabilityTagsJson = "[\"text\",\"vision\",\"function-calling\",\"json-mode\"]",
                IsDefault = false,
                SortOrder = 20,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );

        modelBuilder.Entity<GlobalAgentTemplateEntity>().HasData(
            new GlobalAgentTemplateEntity
            {
                Id = 1,
                TemplateId = "general-assistant",
                Name = "通用助手",
                Description = "通用型对话助手，适合日常问答、文案写作等场景。",
                Role = "Service",
                SystemPrompt = "你是一个专业、友好的 AI 助手。请直接、准确地回答用户的问题。",
                PreferredProviderId = "openai",
                PreferredModelId = "gpt-4o-mini",
                MaxContextTokens = 32768,
                MaxReplyTokens = 2048,
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 10,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );

        // ── Seed：系统内置角色 ────────────────────────────────────────
        modelBuilder.Entity<AppRoleEntity>().HasData(
            new AppRoleEntity
            {
                Id = 1, RoleId = "workspace-admin", Name = "Workspace 管理员",
                Description = "可管理所属 Workspace 的配置、成员和 Agent 模板",
                PermissionsJson = "[\"workspace:manage\",\"workspace:write\",\"workspace:read\",\"agent:manage\",\"template:manage\"]",
                IsSystemRole = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new AppRoleEntity
            {
                Id = 2, RoleId = "workspace-editor", Name = "Workspace 编辑",
                Description = "可在 Workspace 内创建/使用 Session 和 Agent",
                PermissionsJson = "[\"workspace:write\",\"workspace:read\",\"agent:run\",\"template:read\"]",
                IsSystemRole = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new AppRoleEntity
            {
                Id = 3, RoleId = "workspace-viewer", Name = "Workspace 查看者",
                Description = "只读访问 Workspace 内容",
                PermissionsJson = "[\"workspace:read\",\"template:read\"]",
                IsSystemRole = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            },
            new AppRoleEntity
            {
                Id = 4, RoleId = "llm-admin", Name = "LLM 资源管理员",
                Description = "可管理 LLM 资源池（服务商/模型/配额）",
                PermissionsJson = "[\"llm:manage\",\"llm:read\"]",
                IsSystemRole = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );

        // ── Seed：默认团队 ────────────────────────────────────────────
        modelBuilder.Entity<TeamEntity>().HasData(
            new TeamEntity
            {
                Id = 1, TeamId = "platform-team", Name = "平台团队",
                Description = "平台默认团队",
                IsEnabled = true,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );

        // ── Seed：默认 Workspace ──────────────────────────────────────
        modelBuilder.Entity<WorkspaceEntity>().HasData(
            new WorkspaceEntity
            {
                Id = 1, WorkspaceId = "default", Slug = "default",
                TeamEntityId = 1,
                Name = "默认工作空间",
                Description = "平台内置默认工作空间",
                TeamAccessPolicy = WorkspaceAccessPolicy.Write,
                CompanyAccessPolicy = WorkspaceAccessPolicy.ReadOnly,
                IsEnabled = true, IsFrozen = false,
                CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            }
        );
    }
}
