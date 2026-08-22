using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Data;

/// <summary>Platform 数据库上下文，负责 AgentTemplate、用户/组织、会话和运行态持久化。</summary>
public class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    // Agent 模板
    public DbSet<CapabilityEntity> Capabilities => Set<CapabilityEntity>();
    // GlobalAgentTemplate / WorkspaceAgentTemplate 已迁移到文件管理（AgentTemplateFileService / WorkspaceAgentFileService）

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

    // 双向消息系统（ADR-045）
    public DbSet<RoomMessageEntity> RoomMessages => Set<RoomMessageEntity>();
    public DbSet<MessageDeliveryEntity> MessageDeliveries => Set<MessageDeliveryEntity>();
    public DbSet<RoomParticipantEntity> RoomParticipants => Set<RoomParticipantEntity>();

    // 消息话题索引
    public DbSet<MessageTopicEntity> MessageTopics => Set<MessageTopicEntity>();

    // 子代理状态追踪（ADR-016）
    public DbSet<SessionSubAgentEntity> SessionSubAgents => Set<SessionSubAgentEntity>();

    // KeyVault 密钥保管箱
    public DbSet<KeyVaultEntity> KeyVaults => Set<KeyVaultEntity>();

    // Token 使用统计（ADR-018 缓存可观测性）
    public DbSet<TokenUsageStatsEntity> TokenUsageStats => Set<TokenUsageStatsEntity>();

    // Token 使用事件明细账本（ADR-043 缓存统计闭环）
    public DbSet<TokenUsageEventEntity> TokenUsageEvents => Set<TokenUsageEventEntity>();

    // LLM 网关逐请求计费事实（与会话归因投影解耦）
    public DbSet<LlmGatewayUsageEventEntity> LlmGatewayUsageEvents => Set<LlmGatewayUsageEventEntity>();

    // Context layer 长期统计事实（上下文缓存可观测性）
    public DbSet<ContextLayerMetricEventEntity> ContextLayerMetricEvents => Set<ContextLayerMetricEventEntity>();

    // 已结束 UTC 日的 Token 用量聚合缓存（stats 页面渐进加载）
    public DbSet<LlmUsageDailyAggregateEntity> LlmUsageDailyAggregates => Set<LlmUsageDailyAggregateEntity>();

    // 已结束 UTC 日的上下文层级分析 rollup 缓存
    public DbSet<ContextLayerDailyRollupEntity> ContextLayerDailyRollups => Set<ContextLayerDailyRollupEntity>();

    // 按日缓存完成标记（cache_key × day）
    public DbSet<StatsDailyCacheDayEntity> StatsDailyCacheDays => Set<StatsDailyCacheDayEntity>();

    // 会话运行中引导消息（下一次 LLM 调用前注入）
    public DbSet<SessionSteeringMessageEntity> SessionSteeringMessages => Set<SessionSteeringMessageEntity>();

    // 运行时活动诊断（Runtime observability foundation）
    public DbSet<RuntimeActivityEntity> RuntimeActivities => Set<RuntimeActivityEntity>();

    // 结构化遥测事实（长期统计与 SQL 聚合）
    public DbSet<TelemetryMetricEventEntity> TelemetryMetricEvents => Set<TelemetryMetricEventEntity>();

    // 内部事件持久队列
    public DbSet<EventQueueEntity> EventQueue => Set<EventQueueEntity>();

    // 工作区扩展资源
    public DbSet<WorkspaceAgentEntity> WorkspaceAgents => Set<WorkspaceAgentEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<KnowledgeBaseEntity> KnowledgeBases => Set<KnowledgeBaseEntity>();
    public DbSet<WorkspaceSkillEntity> WorkspaceSkills => Set<WorkspaceSkillEntity>();
    public DbSet<WorkspaceChannelEntity> WorkspaceChannels => Set<WorkspaceChannelEntity>();

    // 子代理运行归档索引（ADR-021）
    public DbSet<SubAgentRunEntity> SubAgentRuns => Set<SubAgentRunEntity>();
    public DbSet<TaskPlanRunEntity> TaskPlanRuns => Set<TaskPlanRunEntity>();
    public DbSet<TaskNodeEntity> TaskNodes => Set<TaskNodeEntity>();

    // 聊天执行命令队列（ADR-056 Phase 1）
    public DbSet<ChatExecutionCommandEntity> ChatExecutionCommands => Set<ChatExecutionCommandEntity>();

    // Conversation Event Store（ADR-057）
    public DbSet<ConversationHeadEntity> ConversationHeads => Set<ConversationHeadEntity>();
    public DbSet<ConversationEventEntity> ConversationEvents => Set<ConversationEventEntity>();
    public DbSet<ConversationProjectionCheckpointEntity> ConversationProjectionCheckpoints => Set<ConversationProjectionCheckpointEntity>();
    public DbSet<ConversationCatalogEntity> ConversationCatalogs => Set<ConversationCatalogEntity>();

    // Connector streaming reply projection cursors (Feishu V1; generic storage contract)
    public DbSet<ConnectorStreamProjectionEntity> ConnectorStreamProjections => Set<ConnectorStreamProjectionEntity>();

    // Acceptance Batch（ADR-059）
    public DbSet<AcceptanceBatchEntity> AcceptanceBatches => Set<AcceptanceBatchEntity>();

    // ADR-059: Execution Kernel entities
    public DbSet<ConversationTurnEntity> ConversationTurns => Set<ConversationTurnEntity>();
    public DbSet<ExecutionRunEntity> ExecutionRuns => Set<ExecutionRunEntity>();
    public DbSet<ControlMessageEntity> ControlMessages => Set<ControlMessageEntity>();

    // Task Ledger（TB-02 SQLite Task Store）
    public DbSet<WorkspaceTaskEntity> WorkspaceTasks => Set<WorkspaceTaskEntity>();
    public DbSet<TaskEventEntity> TaskEvents => Set<TaskEventEntity>();

    // Task Assignment Attempts（TB-03 Assign/RunNow 记录）
    public DbSet<TaskAssignmentAttemptEntity> TaskAssignmentAttempts => Set<TaskAssignmentAttemptEntity>();

    // Task Comments（TB-11 评论/备注）
    public DbSet<TaskCommentEntity> TaskComments => Set<TaskCommentEntity>();

    // Task Dispatch Outbox（TB-05 手工派发持久 Outbox）
    public DbSet<TaskDispatchOutboxEntity> TaskDispatchOutbox => Set<TaskDispatchOutboxEntity>();

    // Task Execution Bindings（TB-05 Task/Assignment/Delivery/Execution 绑定）
    public DbSet<TaskExecutionBindingEntity> TaskExecutionBindings => Set<TaskExecutionBindingEntity>();

    // External Access Token（ADR-075 第三方任务看板认证）
    public DbSet<ExternalAccessTokenEntity> ExternalAccessTokens => Set<ExternalAccessTokenEntity>();
    public DbSet<ExternalAccessTokenScopeEntity> ExternalAccessTokenScopes => Set<ExternalAccessTokenScopeEntity>();
    public DbSet<ExternalAccessTokenWorkspaceEntity> ExternalAccessTokenWorkspaces => Set<ExternalAccessTokenWorkspaceEntity>();
    public DbSet<ExternalAccessTokenAuditEventEntity> ExternalAccessTokenAuditEvents => Set<ExternalAccessTokenAuditEventEntity>();

    // External Task API v1（ADR-075：追加式评价 + mutation 幂等）
    public DbSet<TaskEvaluationEntity> TaskEvaluations => Set<TaskEvaluationEntity>();
    public DbSet<ExternalApiIdempotencyEntity> ExternalApiIdempotency => Set<ExternalApiIdempotencyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("platform");

        // ── SkillPackage ──────────────────────────────────────────────
        modelBuilder.Entity<SkillPackageEntity>(e =>
        {
            e.HasIndex(s => s.SkillPackageId).IsUnique();
        });

        // ── Capability ─────────────────────────────────
        modelBuilder.Entity<CapabilityEntity>(e =>
        {
            e.HasIndex(c => c.CapabilityId).IsUnique();
        });

        // GlobalAgentTemplate / WorkspaceAgentTemplate — 已迁移到文件管理，不再在 DB 中建表

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
            e.HasIndex(ev => ev.PrefixHash);
            e.HasIndex(ev => new { ev.ProviderId, ev.ModelId });
            e.HasIndex(ev => ev.OccurredAtUtc);
            e.HasIndex(ev => ev.ParentSessionId);
            e.Property(ev => ev.InputCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.OutputCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.CacheHitCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.TotalCost).HasColumnType("decimal(18,10)");
            e.Property(ev => ev.RawUsageJson).HasColumnType("TEXT");
        });

        // ── LlmGatewayUsageEvents (gateway billing ledger) ─────
        modelBuilder.Entity<LlmGatewayUsageEventEntity>(e =>
        {
            e.ToTable("llm_gateway_usage_events");
            e.HasIndex(ev => ev.SourceId).IsUnique();
            e.HasIndex(ev => ev.YearMonth);
            e.HasIndex(ev => ev.OccurredAtUtc);
            e.HasIndex(ev => new { ev.ProviderId, ev.ModelId });
            e.Property(ev => ev.RawUsageJson).HasColumnType("TEXT");
        });

        // ── ContextLayerMetricEvents (context cache observability) ──
        modelBuilder.Entity<ContextLayerMetricEventEntity>(e =>
        {
            e.ToTable("context_layer_metric_events");
            e.HasIndex(ev => new { ev.SourceType, ev.SourceId, ev.LayerName }).IsUnique();
            e.HasIndex(ev => ev.SessionId);
            e.HasIndex(ev => new { ev.ProviderId, ev.ModelId });
            e.HasIndex(ev => ev.OccurredAtUtc);
            e.HasIndex(ev => ev.LayerName);
            e.HasIndex(ev => ev.ContentHash);
        });

        // ── LlmUsageDailyAggregates (closed-day token stats cache) ──
        modelBuilder.Entity<LlmUsageDailyAggregateEntity>(e =>
        {
            e.ToTable("llm_usage_daily_aggregates");
            e.HasIndex(a => new { a.DayUtc, a.Source, a.ProviderId, a.ModelId }).IsUnique();
            e.HasIndex(a => a.YearMonth);
        });

        // ── ContextLayerDailyRollups (closed-day layer analysis cache) ──
        modelBuilder.Entity<ContextLayerDailyRollupEntity>(e =>
        {
            e.ToTable("context_layer_daily_rollups");
            e.HasIndex(r => r.DayUtc);
        });

        // ── StatsDailyCacheDays (per-cache completed-day markers) ──
        modelBuilder.Entity<StatsDailyCacheDayEntity>(e =>
        {
            e.ToTable("stats_daily_cache_days");
            e.HasKey(d => new { d.CacheKey, d.DayUtc });
        });

        // ── SessionSteeringMessages (runtime steering injection) ──
        modelBuilder.Entity<SessionSteeringMessageEntity>(e =>
        {
            e.ToTable("session_steering_messages");
            e.HasIndex(m => m.SteeringId).IsUnique();
            e.HasIndex(m => new { m.SessionId, m.Status, m.Priority });
            e.HasIndex(m => new { m.WorkspaceId, m.CreatedAtUtc });
            e.Property(m => m.MessageText).HasColumnType("TEXT");
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

        // ── TelemetryMetricEvents (long-term metrics facts) ─────
        modelBuilder.Entity<TelemetryMetricEventEntity>(e =>
        {
            e.ToTable("telemetry_metric_events");
            e.HasIndex(m => m.MetricId).IsUnique();
            e.HasIndex(m => m.TraceId);
            e.HasIndex(m => m.SessionId);
            e.HasIndex(m => new { m.WorkspaceId, m.OccurredAtUtc });
            e.HasIndex(m => new { m.Category, m.Name, m.OccurredAtUtc });
            e.HasIndex(m => m.Status);
            e.Property(m => m.DimensionsJson).HasColumnType("TEXT");
            e.Property(m => m.DebugJson).HasColumnType("TEXT");
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

        // ── ChatMessage (ADR-058: stable business ID) ──────────
        modelBuilder.Entity<ChatMessageEntity>(e =>
        {
            e.HasIndex(m => m.SessionId);
            e.HasIndex(m => m.MessageId).IsUnique();
            e.HasIndex(m => new { m.SessionId, m.CreatedAt });
            e.HasIndex(m => new { m.WorkspaceId, m.AgentInstanceId, m.CreatedAt });
            e.HasIndex(m => new { m.SessionId, m.TurnId });
            e.Property(m => m.WorkspaceId).HasMaxLength(64);
            e.Property(m => m.AgentInstanceId).HasMaxLength(128);
            e.Property(m => m.AgentTemplateId).HasMaxLength(128);
            e.Property(m => m.MessageId).HasMaxLength(64);
        });

        // ── Message Fabric (ADR-045) ───────────────────────────
        modelBuilder.Entity<RoomMessageEntity>(e =>
        {
            e.ToTable("room_messages");
            e.HasIndex(m => m.MessageId).IsUnique();
            e.HasIndex(m => new { m.WorkspaceId, m.RoomId, m.CreatedAt });
            e.Property(m => m.Content).HasColumnType("TEXT");
        });

        modelBuilder.Entity<MessageDeliveryEntity>(e =>
        {
            e.ToTable("message_deliveries");
            e.HasIndex(d => d.DeliveryId).IsUnique();
            e.HasIndex(d => d.MessageId);
            e.HasIndex(d => new { d.MessageId, d.TargetKind, d.TargetId }).IsUnique();
            e.HasIndex(d => new { d.WorkspaceId, d.TargetKind, d.TargetId, d.Status });
            e.HasIndex(d => new { d.WorkspaceId, d.TargetKind, d.TargetId, d.Status, d.AvailableAt, d.Priority, d.CreatedAt });
            e.HasIndex(d => new { d.WorkspaceId, d.RoomId, d.CreatedAt });
            e.HasIndex(d => d.LeaseUntil);
        });

        modelBuilder.Entity<RoomParticipantEntity>(e =>
        {
            e.ToTable("room_participants");
            e.HasIndex(p => p.ParticipantId).IsUnique();
            e.HasIndex(p => new { p.WorkspaceId, p.RoomId, p.Kind, p.EndpointId }).IsUnique();
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

        // ── Task Planning Run / Node（ADR-XXX）───────────────────
        modelBuilder.Entity<TaskPlanRunEntity>(e =>
        {
            e.ToTable("task_plan_runs");
            e.HasIndex(x => x.PlanId).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<TaskNodeEntity>(e =>
        {
            e.ToTable("task_nodes");
            e.HasIndex(x => x.TaskNodeId).IsUnique();
            e.HasIndex(x => new { x.PlanId, x.ParentTaskNodeId, x.Status });
            e.HasIndex(x => new { x.PlanId, x.Depth, x.Status });
        });

        // ── Chat Execution Commands（ADR-056 Phase 1）──────────────────
        modelBuilder.Entity<ChatExecutionCommandEntity>(e =>
        {
            e.ToTable("chat_execution_commands");
            e.HasIndex(x => x.CommandId).IsUnique();
            e.HasIndex(x => new { x.BatchId, x.AgentInstanceId }).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.ClientRequestId });
            e.HasIndex(x => new { x.SessionId, x.Status });
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
            e.HasIndex(x => new { x.Status, x.CreatedAt });
        });

        // ── Acceptance Batches（ADR-059）───────────────────────────────
        modelBuilder.Entity<AcceptanceBatchEntity>(e =>
        {
            e.ToTable("acceptance_batches");
            e.HasIndex(x => x.BatchId).IsUnique();
            e.HasIndex(x => new { x.WorkspaceId, x.ClientRequestId }).IsUnique();
            e.HasIndex(x => x.ConversationId);
        });

        // ── Conversation Turns（ADR-059 Execution Kernel）──────────────
        modelBuilder.Entity<ConversationTurnEntity>(e =>
        {
            e.ToTable("conversation_turns");
            e.HasIndex(x => x.TurnId).IsUnique();
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            e.HasIndex(x => x.CommandId);
        });

        // ── Execution Runs（ADR-059 Execution Kernel）──────────────────
        modelBuilder.Entity<ExecutionRunEntity>(e =>
        {
            e.ToTable("execution_runs");
            e.HasIndex(x => x.RunId).IsUnique();
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.HasIndex(x => new { x.CommandId, x.Attempt }).IsUnique();
            e.HasIndex(x => new { x.Status, x.LeaseUntil });
        });

        // ── Control Messages（ADR-059 Execution Kernel）────────────────
        modelBuilder.Entity<ControlMessageEntity>(e =>
        {
            e.ToTable("execution_control_messages");
            e.HasIndex(x => x.ControlId).IsUnique();
            e.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            e.HasIndex(x => new { x.ConversationId, x.Status });
            e.HasIndex(x => new { x.ConversationId, x.TurnId, x.Status });
            e.Property(x => x.Payload).HasColumnType("TEXT");
        });

        // ── Conversation Event Store（ADR-057）─────────────────────
        modelBuilder.Entity<ConversationEventEntity>(e =>
        {
            e.ToTable("conversation_events");
            e.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TurnId, x.Type });
        });

        modelBuilder.Entity<ConversationHeadEntity>(e =>
        {
            e.ToTable("conversation_heads");
        });

        modelBuilder.Entity<ConversationProjectionCheckpointEntity>(e =>
        {
            e.ToTable("conversation_projection_checkpoints");
        });

        modelBuilder.Entity<ConversationCatalogEntity>(e =>
        {
            e.ToTable("conversation_catalog");
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.Status);
        });

        // ── Task Ledger（TB-02 SQLite Task Store）─────────────────────
        modelBuilder.Entity<WorkspaceTaskEntity>(e =>
        {
            e.ToTable("workspace_tasks");
            e.HasKey(t => t.TaskId);
            e.HasIndex(t => new { t.WorkspaceId, t.TaskId }).IsUnique();
            e.HasIndex(t => new { t.WorkspaceId, t.Status });
            e.HasIndex(t => new { t.WorkspaceId, t.SortOrder });
        });

        modelBuilder.Entity<TaskEventEntity>(e =>
        {
            e.ToTable("task_events");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedOnAdd();
            e.HasIndex(t => new { t.TaskId, t.Sequence }).IsUnique();
            e.HasIndex(t => t.EventId).IsUnique();
            e.HasIndex(t => new { t.WorkspaceId, t.TaskId });
        });

        // ── Task Assignment Attempts（TB-03）───────────────────────
        modelBuilder.Entity<TaskAssignmentAttemptEntity>(e =>
        {
            e.ToTable("task_assignment_attempts");
            e.HasKey(a => a.AttemptId);
            e.HasIndex(a => a.TaskId);
            e.HasIndex(a => a.WorkspaceId);
            // partial unique index：(task_id) WHERE released_at_utc IS NULL（每 task 最多一个 active assignment）。
            e.HasIndex(a => a.TaskId)
                .IsUnique()
                .HasFilter("released_at_utc IS NULL")
                .HasDatabaseName("UX_task_assignment_attempts_task_active");
        });

        // ── Task Comments（TB-11 评论/备注）────────────────────────
        modelBuilder.Entity<TaskCommentEntity>(e =>
        {
            e.ToTable("task_comments");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedOnAdd();
            e.HasIndex(c => c.CommentId).IsUnique().HasDatabaseName("UX_task_comments_comment_id");
        });

        // ── Task Dispatch Outbox（TB-05）──────────────────────────
        modelBuilder.Entity<TaskDispatchOutboxEntity>(e =>
        {
            e.ToTable("task_dispatch_outbox");
            e.HasKey(o => o.Id);
            e.HasIndex(o => o.IdempotencyKey).IsUnique();
            e.HasIndex(o => new { o.Status, o.LeaseUntilUtc });
            e.HasIndex(o => o.AssignmentId);
        });

        // ── Task Execution Bindings（TB-05）────────────────────────
        modelBuilder.Entity<TaskExecutionBindingEntity>(e =>
        {
            e.ToTable("task_execution_bindings");
            e.HasKey(b => b.Id);
            e.HasIndex(b => new { b.TaskId, b.AssignmentId, b.DeliveryId }).IsUnique();
            e.HasIndex(b => b.DeliveryId);
        });

        // ── External Access Token（ADR-075）──────────────────────────
        modelBuilder.Entity<ExternalAccessTokenEntity>(e =>
        {
            e.ToTable("external_access_tokens");
            e.HasKey(t => t.TokenId);
            e.HasIndex(t => t.KeyId).IsUnique();
            e.HasIndex(t => t.OwnerUserId);
            e.HasMany(t => t.Scopes)
                .WithOne()
                .HasForeignKey(s => s.TokenId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Workspaces)
                .WithOne()
                .HasForeignKey(w => w.TokenId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ExternalAccessTokenScopeEntity>(e =>
        {
            e.ToTable("external_access_token_scopes");
            e.HasKey(s => new { s.TokenId, s.Scope });
            e.HasIndex(s => s.Scope);
        });

        modelBuilder.Entity<ExternalAccessTokenWorkspaceEntity>(e =>
        {
            e.ToTable("external_access_token_workspaces");
            e.HasKey(w => new { w.TokenId, w.WorkspaceId });
            e.HasIndex(w => w.WorkspaceId);
        });

        modelBuilder.Entity<ExternalAccessTokenAuditEventEntity>(e =>
        {
            e.ToTable("external_access_token_audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.TokenId, x.OccurredAtUtc });
        });

        // ── External Task API v1（ADR-075）──────────────────────────
        modelBuilder.Entity<TaskEvaluationEntity>(e =>
        {
            e.ToTable("task_evaluations");
            e.HasKey(x => x.EvaluationId);
            e.HasIndex(x => new { x.TaskId, x.CreatedAtUtc });
            e.HasIndex(x => x.WorkspaceId);
        });

        modelBuilder.Entity<ExternalApiIdempotencyEntity>(e =>
        {
            e.ToTable("external_api_idempotency");
            e.HasKey(x => x.IdempotencyKeyHash);
            e.HasIndex(x => x.CreatedAtUtc);
        });

        modelBuilder.Entity<ConnectorStreamProjectionEntity>(e =>
        {
            e.ToTable("connector_stream_projections");
            e.HasIndex(x => x.ProjectionId).IsUnique();
            e.HasIndex(x => new { x.CommandId, x.ConnectorId }).IsUnique();
            e.HasIndex(x => new { x.Status, x.AvailableAt, x.UpdatedAt });
            e.Property(x => x.Content).HasColumnType("TEXT");
        });

        // ── 注意：配置类 seed 数据已废弃（ADR-036）────────────────────
        // CapabilityEntity / GlobalAgentTemplateEntity 等配置类数据的唯一来源已迁移至
        // data/config/ 和 data/agent-templates/ 文件。
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
