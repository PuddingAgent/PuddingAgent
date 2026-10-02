# 18 上下文缓存可观测性体系 (Context Cache Observability)

> 状态：**accepted**（2026-05-18 归档，14/14 任务全部完成）
> 作者：@architect (战略 ADR)
> 日期：2026-05-17
> 触发条件：A(新架构模式/抽象层) + B(跨3+模块不可逆数据变更) — 满足 ≥2 项
> 关联：[02PuddingCore](02PuddingCore.md)、[03PuddingRuntime](03PuddingRuntime.md)、[05PuddingPlatform](05PuddingPlatform.md)、[06PuddingAgent与客户端](06PuddingAgent与客户端.md)、[16会话状态层与客户端解耦ADR](16会话状态层与客户端解耦ADR.md)

---

## 1. 背景与现状诊断

### 1.1 DeepSeek KV Cache 机制

DeepSeek（及多数 OpenAI 兼容服务商）实现了**服务端自动前缀缓存**（KV Cache）：

- **命中规则**：后续请求的 messages 前缀与已落盘的缓存前缀单元完整匹配即命中
- **API 返回**：`usage` 对象含 `prompt_cache_hit_tokens` 和 `prompt_cache_miss_tokens`（DeepSeek 特有扩展）
- **费用差异**：缓存命中 tokens 的计费单价远低于未命中（典型 ~0.1元/M vs ~1元/M）
- **优化策略**：保持 system prompt + 早期上下文消息不变 → 最大化缓存命中率

### 1.2 当前差距

```
已实现:  LLM API usage → TokenUsageDto → ChatMessageEntity.UsageJson
缺失:    cache_hit/miss_tokens 未解析 | 无缓存命中率指示器 | 无按月统计 | 无缓存计费配置
```

### 1.3 现有资产（直接复用）

| 资产 | 位置 | 差距 |
|------|------|------|
| `TokenUsageDto` (record) | `PuddingCore/Models/` | 缺 `PromptCacheHitTokens` / `PromptCacheMissTokens` |
| `DirectLlmClient.ChatAsync()` | `PuddingRuntime/Services/` | 只解析 3 个字段，未解析 cache tokens |
| `ChatMessageEntity.UsageJson` | `PuddingPlatform/Data/Entities/` | 存储 text，可反序列化扩展后的 DTO |
| `LlmModelEntity` | `PuddingPlatform/Data/Entities/` | 缺 `CacheHitPricePer1MTokens` |
| `StatusBarTokenIndicator` | `PuddingPlatformAdmin/.../` | 只显示上下文窗口，无缓存指示 |

---

## 2. 战略架构决策

### ADR-018-A：缓存数据流架构

**决定**：Runtime 解析全部 cache 字段 → TokenUsageDto 携带 → Platform 存储 UsageJson → 前端/Admin 消费

### ADR-018-B：TokenUsageDto 契约变更

**决定**：`TokenUsageDto` 新增 `int? PromptCacheHitTokens` 和 `int? PromptCacheMissTokens`（record 向后兼容）

### ADR-018-C：LlmModelEntity 计费配置扩展

**决定**：`LlmModelEntity` 新增 `decimal CacheHitPricePer1MTokens`

### ADR-018-D：前端缓存命中指示器

**决定**：在 `StatusBarTokenIndicator` 中叠加缓存命中率外环（双层圆环）

### ADR-018-E：主会话概念

**决定**：前端 `useChatState` 新增 `mainSessionId`，与 `selectedSessionId` 独立

### ADR-018-F：管理后台 Token 统计

**决定**：新增 `StatsApiController` + 独立统计表 `TokenUsageStatsEntity`

---

## 3. 实施路线

### Phase P0：数据管道（6 任务）

| 任务 ID | 描述 | 
|---------|------|
| T-CACHE-001 | TokenUsageDto 新增 PromptCacheHitTokens / PromptCacheMissTokens |
| T-CACHE-002 | DirectLlmClient.ChatAsync() 解析 cache tokens |
| T-CACHE-003 | LlmModelEntity 新增 CacheHitPricePer1MTokens + EF 迁移 |
| T-CACHE-004 | 新增 TokenUsageStatsEntity + EF 迁移 |
| T-CACHE-005 | ChatApiController fire-and-forget 增量更新统计表 |
| T-CACHE-006 | 管理后台模型表单新增缓存价格字段 |

### Phase P1：前端缓存指示器 + 主会话（5 任务）

| 任务 ID | 描述 |
|---------|------|
| T-CACHE-007 | StatusBarTokenIndicator 双层圆环改造 |
| T-CACHE-008 | useChatState 新增 mainSessionId + 缓存统计累加 |
| T-CACHE-009 | TokenStatsIndicator 数据接入 |
| T-CACHE-010 | ChatPage 集成主会话逻辑 |
| T-CACHE-011 | GET /api/{workspaceId}/sessions/{id}/token-stats API |

### Phase P2：管理后台统计页面（3 任务）

| 任务 ID | 描述 |
|---------|------|
| T-CACHE-012 | StatsApiController 新增 GET /api/stats/tokens/monthly |
| T-CACHE-013 | Admin Token 统计页面 |
| T-CACHE-014 | 近 12 个月趋势图 |

详细信息见 ADR 完整内容。
