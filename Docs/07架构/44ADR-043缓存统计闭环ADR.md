# 44 ADR-043：DeepSeek 上下文硬盘缓存统计闭环

> 状态：**accepted / implemented**
> 作者：@architect
> 日期：2026-05-23
> 最后修订：2026-07-18
> 触发条件：A(新架构模式/抽象层) — ADR-018 统计口径不一、缺失明细账本、fire-and-forget 不可靠
> 关联：[18上下文缓存可观测性ADR](./18上下文缓存可观测性ADR.md)、[05PuddingPlatform](./05PuddingPlatform.md)

---

## 1. 背景与问题

Pudding 已实现 ADR-018 的全部 14 项任务，包括 `TokenUsageDto` 缓存字段、chat 状态栏缓存命中率、`/api/stats/tokens/monthly` 和 `/admin/stats/tokens` 页面。

但现有实现存在以下问题：

| 问题 | 影响 | 位置 |
|------|------|------|
| `MessageApiController` 反序列化未传 `JsonOpts` | camelCase `UsageJson` 可能读不出字段 | `MessageApiController.cs:134` |
| 月度统计 fire-and-forget 中断后无法恢复 | 统计口径永久丢失 | `ChatApiController.cs:279` |
| miss token 口径不一致 | ChatApiController cost 用派生 miss，聚合写原始 miss | `ChatApiController.cs:312` |
| 缺少明细账本 | 无法校验聚合数据正确性、无法重建 | — |
| chat 页缺少完整会话分析入口 | 用户看不到每轮缓存命中明细 | — |
| 主 Agent 的 `usage.recorded` 只写 Event Store，未进入 Token 账本 | 页面只显示潜意识/记忆调用，主模型长期为 0 | `TurnExecutorAdapter` / `ConversationProjector` |
| 历史重建从 ChatMessages 回扫并猜测默认 Provider/Model | 破坏执行快照唯一配置源，可能把费用归到错误模型 | `TokenUsageRebuildService` |
| 非 Conversation LLM 用量以 fire-and-forget 写入并使用随机 sourceId | 进程退出或写入失败会静默丢失，调用重试也无法稳定去重 | `MemoryLlmInvocationClient` |
| 重建先删除全部派生明细，再跳过无法归因的旧事实 | 重建本身会扩大数据丢失 | `TokenUsageRebuildService` |

## 2. 核心决策

### ADR-043-A：新增 TokenUsageEventEntity 明细账本

**决策**：新增 `TokenUsageEventEntity` 作为长期统计事实表，记录每一次 LLM 调用的 usage 明细。

理由：
- 现有 `TokenUsageStatsEntity` 是物化汇总，不可逆推明细
- 需要支持重建、审计、历史查询
- 唯一索引 `(SourceType, SourceId)` 保证幂等

### ADR-043-B：TokenUsageNormalizer 统一口径

**决策**：统一所有入口（前端 API、统计写入、Admin 页面）使用 `TokenUsageNormalizer` 计算缓存口径。

归一化规则（与用户方案一致）：

```
promptTokens = usage.promptTokens ?? 0
completionTokens = usage.completionTokens ?? 0
cacheHitTokens = usage.promptCacheHitTokens ?? 0
cacheMissTokens = usage.promptCacheMissTokens ?? max(promptTokens - cacheHitTokens, 0)
cacheEligibleTokens = cacheHitTokens + cacheMissTokens
cacheHitRate = cacheEligibleTokens > 0 ? cacheHitTokens / cacheEligibleTokens : null
billableInputTokens = cacheMissTokens
```

成本计算：
```
cost = cacheHitTokens / 1_000_000 * cacheHitPrice
     + cacheMissTokens / 1_000_000 * inputPrice
     + completionTokens / 1_000_000 * outputPrice
```

### ADR-043-C：Event Store 是主 Agent 用量事实源

**决策**：主 Agent 每次 LLM 调用产生 `usage.recorded` v2 事件，载荷必须包含：

```json
{
  "usage": {},
  "providerId": "deepseek",
  "profileId": "agent:default:conscious",
  "modelId": "deepseek-v4-pro",
  "role": "conscious",
  "invocationIndex": 1
}
```

- Provider/Profile/Model 来自 `TurnExecutionContext.LlmProfile` 不可变执行快照。
- `TurnOutputChunker` 只能聚合 delta，不得把非 delta 领域事件的 `SchemaVersion` 重置为 1。
- `ConversationProjector` 以 `(sourceType=agent_llm, sourceId=eventId)` 幂等写入 `TokenUsageEventEntity`。
- 账本写入失败属于投影失败，checkpoint 不得越过该事件，后台 Worker 重试。
- 潜意识、记忆等非 Conversation 工作负载必须为每次逻辑调用创建稳定 `InvocationId`，并在向调用方报告成功前等待 `TokenUsageRecorder.RecordRequiredAsync` 完成。
- `RecordAsync` 仅允许用于不参与计费、审计和容量统计的非权威遥测；业务用量事实不得 fire-and-forget。

### ADR-043-D：TokenUsageRebuildService 可重建

**决策**：`TokenUsageRebuildService` 从 Conversation Event Store 的 `usage.recorded` v2 回扫重建 `agent_llm` 明细账本，再从完整 `TokenUsageEvents` 重建月度汇总。

- 用于修复历史数据、验证聚合一致性
- 通过 `POST /api/stats/tokens/rebuild` 触发（仅管理员）
- 禁止回查当前 Agent 配置、模板或默认 Provider/Model 猜测历史调用归因
- v1 无路由归因事件必须显式计入 `unattributedEventsSkipped`，不得制造错误费用
- 重建只替换已经成功构造出新明细的 `agent_llm` 和遗留 `chat_message` sourceId；未归因或构造失败的事实不得触发删除
- 旧明细删除、新明细插入和月度汇总重建必须处于同一数据库事务；任何持久化失败均回滚整个替换
- `subconscious_memory` 等非 Conversation 事实来源始终保留

### ADR-043-E：TokenUsageStatsEntity 定位调整为物化汇总

**决策**：`TokenUsageStatsEntity` 保留现有结构，但定位明确为"物化汇总"：
- 由 TokenUsageRecorder 增量更新
- 可从 TokenUsageEventEntity 重建
- 查询走聚合表（性能优先）

## 3. 数据模型

### 3.1 TokenUsageEventEntity

```csharp
public class TokenUsageEventEntity
{
    public long Id { get; set; }

    /// <summary>数据来源类型：agent_llm / subconscious_memory / 其他明确工作负载</summary>
    [Required, MaxLength(32)]
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源 ID（messageId 或 eventId），唯一标识</summary>
    [Required, MaxLength(128)]
    public string SourceId { get; set; } = string.Empty;

    /// <summary>工作区 ID</summary>
    [MaxLength(64)]
    public string? WorkspaceId { get; set; }

    /// <summary>会话 ID</summary>
    [MaxLength(64)]
    public string? SessionId { get; set; }

    /// <summary>服务商 ID（slug）</summary>
    [MaxLength(64)]
    public string? ProviderId { get; set; }

    /// <summary>模型 ID</summary>
    [MaxLength(128)]
    public string? ModelId { get; set; }

    /// <summary>调用发生时间（UTC）</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>统计月份，格式 yyyy-MM</summary>
    [Required, MaxLength(7)]
    public string YearMonth { get; set; } = string.Empty;

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public long CacheEligibleTokens { get; set; }

    /// <summary>缓存命中率 0.0 ~ 1.0，无缓存事件时为 null</summary>
    public double? CacheHitRate { get; set; }

    public decimal InputCost { get; set; }
    public decimal OutputCost { get; set; }
    public decimal CacheHitCost { get; set; }
    public decimal TotalCost { get; set; }

    /// <summary>原始 usage JSON，用于审计和后续扩展</summary>
    public string? RawUsageJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
```

唯一索引：`(SourceType, SourceId)`

### 3.2 TokenUsageStatsEntity 调整

现有结构不变，新增注释说明其物化汇总定位。无需变更字段。

## 4. 后端设计

### 4.1 TokenUsageNormalizer（新服务）

```csharp
public class TokenUsageNormalizer
{
    /// <summary>归一化计算"实际"未命中 tokens</summary>
    public int ResolveMissTokens(TokenUsageDto usage);

    /// <summary>归一化计算成本明细</summary>
    public TokenCostResult CalculateCost(
        TokenUsageDto usage,
        decimal inputPrice,
        decimal outputPrice,
        decimal cacheHitPrice);

    /// <summary>归一化完整计算，返回标准化结果</summary>
    public NormalizedUsage Normalize(TokenUsageDto usage);
}
```

### 4.2 TokenUsageRecorder（新服务）

```csharp
public class TokenUsageRecorder(
    IServiceScopeFactory scopeFactory,
    TokenUsageNormalizer normalizer,
    ILogger<TokenUsageRecorder> logger)
{
    /// <summary>仅用于非权威遥测；失败可记录日志并吞掉</summary>
    public async Task RecordAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        DateTimeOffset? occurredAtUtc = null);

    /// <summary>写入计费/审计事实；失败必须向拥有该调用的工作流传播</summary>
    public Task RecordRequiredAsync(
        TokenUsageDto usage,
        string sourceType,
        string sourceId,
        string? workspaceId,
        string? sessionId,
        string? providerId,
        string? modelId,
        DateTimeOffset? occurredAtUtc = null);
}
```

非 Conversation LLM 调用的 `sourceId` 使用 `llm:{InvocationId}`。`InvocationId` 在
`LlmInvocationRequest` 创建时生成，在调用服务内部重试期间保持不变。

### 4.3 TokenUsageRebuildService（新服务）

```csharp
public class TokenUsageRebuildService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    TokenUsageNormalizer normalizer,
    ILogger<TokenUsageRebuildService> logger)
{
    /// <summary>从 usage.recorded v2 回扫重建 agent_llm 明细和 TokenUsageStatsEntity</summary>
    public Task<RebuildResult> RebuildAsync(
        string? yearMonth = null,
        CancellationToken ct = default);
}
```

### 4.4 修复点

1. **MessageApiController.cs:134**：反序列化必须传 `JsonOpts`
2. **TurnExecutorAdapter**：将 usage 帧升级为带不可变 LLM 路由身份的 schema v2 事件
3. **ConversationProjector**：以 required 语义将 v2 usage 事件写入明细账本，成功后才推进 checkpoint
4. **MemoryLlmInvocationClient**：取消 fire-and-forget；以稳定 InvocationId 同步写入 required usage 事实
5. **TokenUsageRebuildService**：只替换成功重建的 sourceId，删除/新增/汇总在同一事务内完成

### 4.5 API 扩展

保留现有接口：

```
GET /api/sessions/{sessionId}/token-stats        ← MessageApiController
GET /api/stats/tokens/monthly                     ← StatsApiController
```

新增：

```
GET /api/stats/tokens/events?from=&to=&providerId=&modelId=&sessionId=  ← TokenUsageEventEntity 查询
POST /api/stats/tokens/rebuild                                          ← 触发重建（仅管理员）
```

## 5. 前端设计

### 5.1 Chat 会话缓存分析面板

- 消息气泡下方或详情浮层显示：
  - 本轮 prompt / completion / total
  - 本轮缓存命中 / 未命中 / 命中率
  - 当前会话累计缓存命中率
- 调用已有 `getSessionTokenStats(sessionId)` API

### 5.2 Admin 统计页面增强

- 保留现有月度总览卡片和 ProTable 明细
- 增加 Provider / Model 筛选（已有 API 参数支持）
- 表格列不变，确保口径一致性
- 后续可加趋势图（P3）

## 6. 影响面

| 模块 | 影响 | 风险 |
|------|------|------|
| PuddingPlatform.Data | 新增 TokenUsageEventEntity + DbSet + 配置 | 低 — 新表 |
| PuddingPlatform.Services | 新增 3 个服务类 | 低 — 新文件 |
| PuddingPlatform.Controllers | StatsApiController 提供查询和权威重建入口 | 低 |
| PuddingRuntime | TurnExecutorAdapter 生成带不可变 LLM 路由身份的 usage v2 事件 | 中 |
| Conversation Projection | ConversationProjector 以必达语义投影 Token 明细 | 中 |
| PuddingPlatformAdmin | 新增 API 调用、会话统计面板 | 低 |

## 7. 实施优先级

### P0：修 bug 和统一口径

1. MessageApiController 传 JsonOpts
2. 引入 TokenUsageNormalizer 
3. TurnExecutorAdapter 写入 usage.recorded v2 不可变归因

### P1：引入明细账本

4. 新增 TokenUsageEventEntity + DbSet
5. 新增 TokenUsageRecorder
6. 新增 TokenUsageRebuildService
7. 新增 events/rebuild API

### P2：补齐 UI

8. chat 会话缓存分析面板
9. Admin 统计页筛选增强

### P3：运营增强（暂不实施）

10. 趋势图、导出、交叉分析

## 8. 验证清单

| # | 验证项 | 方法 |
|---|--------|------|
| 1 | MessageApiController 反序列化 camelCase UsageJson 正确 | 手动构造历史数据测试 |
| 2 | TokenUsageNormalizer 口径一致 | 单元测试 |
| 3 | TokenUsageRecorder 幂等写入不重复 | 同一 sourceId 重放测试 |
| 4 | rebuild 后明细与聚合一致 | QA-001-xxx |
| 5 | events API 按时间/Provider/Model/Session 筛选 | 集成测试 |
| 6 | Chat 会话面板数据正确 | 目视检查 |
| 7 | 非 Conversation 调用等待 required usage 写入，稳定 InvocationId 可幂等 | `MemoryLlmInvocationClientUsageTests` |
| 8 | 无归因旧事件不会删除现有明细；重建替换保持事务原子性 | `TokenUsageRebuildServiceTests` |
