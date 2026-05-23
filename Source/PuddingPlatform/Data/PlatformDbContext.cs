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
    public DbSet<CapabilityEntity> Capabilities => Set<CapabilityEntity>();
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

    // 全局 Skill 包
    public DbSet<SkillPackageEntity> SkillPackages => Set<SkillPackageEntity>();

    // 聊天消息持久化
    public DbSet<ChatMessageEntity> ChatMessages => Set<ChatMessageEntity>();

    // 会话事件日志（ADR-016）
    public DbSet<SessionEventLogEntity> SessionEventLogs => Set<SessionEventLogEntity>();

    // 子代理状态追踪（ADR-016）
    public DbSet<SessionSubAgentEntity> SessionSubAgents => Set<SessionSubAgentEntity>();

    // KeyVault 密钥保管箱
    public DbSet<KeyVaultEntity> KeyVaults => Set<KeyVaultEntity>();

    // Token 使用统计（ADR-018 缓存可观测性）
    public DbSet<TokenUsageStatsEntity> TokenUsageStats => Set<TokenUsageStatsEntity>();

    // Token 使用事件明细账本（ADR-043 缓存统计闭环）
    public DbSet<TokenUsageEventEntity> TokenUsageEvents => Set<TokenUsageEventEntity>();

    // 运行时活动诊断（Runtime observability foundation）
    public DbSet<RuntimeActivityEntity> RuntimeActivities => Set<RuntimeActivityEntity>();

    // 内部事件持久队列
    public DbSet<EventQueueEntity> EventQueue => Set<EventQueueEntity>();

    // 系统预置头像（ADR-034）
    public DbSet<AgentAvatarEntity> AgentAvatars => Set<AgentAvatarEntity>();

    // 工作区扩展资源
    public DbSet<WorkspaceAgentEntity> WorkspaceAgents => Set<WorkspaceAgentEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<KnowledgeBaseEntity> KnowledgeBases => Set<KnowledgeBaseEntity>();
    public DbSet<WorkspaceSkillEntity> WorkspaceSkills => Set<WorkspaceSkillEntity>();
    public DbSet<WorkspaceChannelEntity> WorkspaceChannels => Set<WorkspaceChannelEntity>();

    // 子代理运行归档索引（ADR-021）
    public DbSet<SubAgentRunEntity> SubAgentRuns => Set<SubAgentRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("platform");

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
            e.Property(m => m.CacheHitPricePer1MTokens).HasColumnType("decimal(18,6)");
        });

        // ── SkillPackage ──────────────────────────────────────────────
        modelBuilder.Entity<SkillPackageEntity>(e =>
        {
            e.HasIndex(s => s.SkillPackageId).IsUnique();
        });

        // ── AgentAvatar（ADR-034）────────────────────────────────────
        modelBuilder.Entity<AgentAvatarEntity>(e =>
        {
            e.ToTable("AgentAvatars", "platform");
            e.HasIndex(a => a.AvatarId).IsUnique();
            e.HasIndex(a => new { a.IsEnabled, a.SortOrder });
            e.Property(a => a.VisualTraitsJson).HasColumnType("TEXT");
        });

        // ── GlobalAgentTemplate ───────────────────────────────────────
        modelBuilder.Entity<CapabilityEntity>(e =>
        {
            e.HasIndex(c => c.CapabilityId).IsUnique();
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
        // ── WorkspaceAgent ────────────────────────────────────────
        modelBuilder.Entity<WorkspaceAgentEntity>(e =>
        {
            e.HasIndex(a => a.AgentId).IsUnique();
            e.HasOne(a => a.Workspace)
             .WithMany()
             .HasForeignKey(a => a.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Workflow ──────────────────────────────────────────────
        modelBuilder.Entity<WorkflowEntity>(e =>
        {
            e.HasIndex(w => w.WorkflowId).IsUnique();
            e.HasOne(w => w.Workspace)
             .WithMany()
             .HasForeignKey(w => w.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── KnowledgeBase ─────────────────────────────────────────
        modelBuilder.Entity<KnowledgeBaseEntity>(e =>
        {
            e.HasIndex(k => k.KbId).IsUnique();
            e.HasOne(k => k.Workspace)
             .WithMany()
             .HasForeignKey(k => k.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── WorkspaceSkill ────────────────────────────────────────
        modelBuilder.Entity<WorkspaceSkillEntity>(e =>
        {
            e.HasIndex(s => s.SkillId).IsUnique();
            e.HasOne(s => s.Workspace)
             .WithMany()
             .HasForeignKey(s => s.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── WorkspaceChannel ──────────────────────────────────────
        modelBuilder.Entity<WorkspaceChannelEntity>(e =>
        {
            e.HasIndex(c => c.ChannelId).IsUnique();
            e.HasOne(c => c.Workspace)
             .WithMany()
             .HasForeignKey(c => c.WorkspaceEntityId)
             .OnDelete(DeleteBehavior.Cascade);
        });
        // ── TokenUsageStats (ADR-018) ─────────────────────────
        modelBuilder.Entity<TokenUsageStatsEntity>(e =>
        {
            e.ToTable("TokenUsageStats", "platform");
            e.HasIndex(s => new { s.YearMonth, s.ProviderId, s.ModelId }).IsUnique();
            e.Property(s => s.TotalCost).HasColumnType("decimal(18,6)");
        });

        // ── TokenUsageEvents (ADR-043) ─────────────────────────
        modelBuilder.Entity<TokenUsageEventEntity>(e =>
        {
            e.ToTable("TokenUsageEvents", "platform");
            e.HasIndex(ev => new { ev.SourceType, ev.SourceId }).IsUnique();
            e.HasIndex(ev => ev.YearMonth);
            e.HasIndex(ev => ev.SessionId);
            e.HasIndex(ev => new { ev.ProviderId, ev.ModelId });
            e.HasIndex(ev => ev.OccurredAtUtc);
            e.Property(ev => ev.InputCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.OutputCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.CacheHitCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.TotalCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.RawUsageJson).HasColumnType("TEXT");
        });

        // ── RuntimeActivity (observability foundation) ──────────
        modelBuilder.Entity<RuntimeActivityEntity>(e =>
        {
            e.ToTable("runtime_activity");
            e.HasIndex(a => a.ActivityId).IsUnique();
            e.HasIndex(a => a.TraceId);
            e.HasIndex(a => a.SessionId);
            e.HasIndex(a => a.ExecutionId);
            e.HasIndex(a => a.Component);
            e.HasIndex(a => a.StartedAtUtc);
            e.Property(a => a.MetadataJson).HasColumnType("TEXT");
        });

        // ── EventQueue ───────────────────────────────────────
        modelBuilder.Entity<EventQueueEntity>(e =>
        {
            e.ToTable("event_queue");
            e.HasIndex(q => q.EventId).IsUnique();
            e.HasIndex(q => new { q.Status, q.AvailableAt, q.Priority, q.CreatedAt });
            e.HasIndex(q => q.TraceId);
            e.HasIndex(q => q.SessionId);
            e.HasIndex(q => q.WorkspaceId);
            e.Property(q => q.Payload).HasColumnType("TEXT");
        });

        // ── ChatMessage ───────────────────────────────────────
        modelBuilder.Entity<ChatMessageEntity>(e =>
        {
            e.HasIndex(m => m.SessionId);
            e.HasIndex(m => new { m.SessionId, m.CreatedAt });
        });

        // ── SessionEventLog (ADR-016) ─────────────────────────
        modelBuilder.Entity<SessionEventLogEntity>(e =>
        {
            e.ToTable("session_event_log");
            e.HasIndex(e => new { e.SessionId, e.SequenceNum }).IsUnique();
            e.HasIndex(e => new { e.WorkspaceId, e.RecordedAt });
            e.Property(e => e.Data).HasColumnType("TEXT");
        });

        // ── SessionSubAgent (ADR-016) ─────────────────────────
        modelBuilder.Entity<SessionSubAgentEntity>(e =>
        {
            e.ToTable("session_sub_agents");
            e.HasIndex(e => e.SubSessionId).IsUnique();
            e.HasIndex(e => new { e.ParentSessionId, e.Status });
        });

        // ── KeyVault ──────────────────────────────────────────
        modelBuilder.Entity<KeyVaultEntity>(e =>
        {
            e.HasIndex(k => k.KeyVaultId).IsUnique();
            e.HasIndex(k => k.Name).IsUnique();
            e.Property(k => k.Name).HasMaxLength(128);
            e.Property(k => k.Description).HasMaxLength(1024);
            e.Property(k => k.Category).HasMaxLength(64);
        });

        // ── SubAgentRun（ADR-021：运行归档 DB 索引）──────────────
        modelBuilder.Entity<SubAgentRunEntity>(e =>
        {
            e.ToTable("sub_agent_runs");
            e.HasIndex(r => r.RunId).IsUnique();
            e.HasIndex(r => r.ParentSessionId);
            e.HasIndex(r => r.WorkspaceId);
            e.HasIndex(r => r.Status);
        });

        // ── 注意：配置类 seed 数据已废弃（ADR-036）────────────────────
        // LlmProviderEntity / LlmModelEntity / CapabilityEntity / GlobalAgentTemplateEntity
        // 等配置类数据的唯一来源已迁移至 data/config/ 和 data/agent-templates/ 文件。
        // 不再通过 DB seed 维护。旧 SQLite 配置数据可直接丢弃。
        // 此处保留 AppRole seed（属于运行态/业务态数据）。
        SeedBuiltInRoles(modelBuilder);
    }

    private static void SeedBuiltInRoles(ModelBuilder modelBuilder)
    {

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
