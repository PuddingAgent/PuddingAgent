# SKILL Hub 技能中心与 EVO MAP 进化图谱 · 设计方案（实施契约）

> 版本：v1.0 · 2026-09-21 · 状态：**待实施（契约已冻结）**
> 范围：PuddingAgent 内部私有技能中心（**不是**互联网 EvoMap）
> 授权：用户明确要求「完善 SKILL Hub 和 SKILL Manager 的工具」+「人类使用的管理界面」

---

## 一、背景与目标

### 1.1 现状（代码事实，已核实）

| 层 | 已有物 | 位置 | 局限 |
|---|---|---|---|
| 运行时本地技能 | `AgentSkillFileService`（631 行） | `Source/PuddingRuntime/Services/Skills/` | 技能**只在本机文件系统** `{DataRoot}/agents/{agentId}/skills/{skillId}/` |
| Agent 工具 | `AgentSkillTool`（id=`agent_skill`，396 行） | `Source/PuddingRuntime/Tools/BuiltIns/Skills/` | 13 个 action 全是**本地**操作；`push` 只是把 zip 上传成"技能包" |
| 进化基础设施 | `IAgentSkillEvolutionStore` / `AgentSkillEvolutionStore` / `ConversationSkillEvolutionTrajectorySource` / `SkillEnforcerService` | 同目录 | 已有「成功轨迹 → 新技能」雏形，但**无版本血缘、无跨 Agent 共享** |
| 平台"技能包" | `SkillPackageApiController` + `SkillPackageEntity` + MinIO | `Source/PuddingPlatform/Controllers/Api/` | 面向**Agent 模板选包**，是**二进制附件**（zip/tar.gz），不是可检索/可进化的技能内容 |
| 管理界面 | `skill-management/index.tsx`（17.6KB，2026-07-12） | `Source/PuddingPlatformAdmin/src/pages/` | 只有"上传 zip"表格，**无内容预览/无版本/无血缘/无统计** |

### 1.2 缺口（本方案要填的）

1. **集中式技能库**：技能内容（manifest + SKILL.md + 附件）入库，可检索、可版本化、可跨 Agent 共享。
2. **上传 / 下载**：Agent 可 `publish` 自己觉得有用的技能/经验；其他 Agent 可 `install`。
3. **更新与进化**：版本可更新；支持带**进化动作**的新版本（patch/split/compress/retire/merge/fork），并记录**血缘**。
4. **EVO MAP**：技能进化图谱（节点 = 技能版本，边 = 进化动作），供人可视化审阅。
5. **人类管理界面**：SKILL Hub 控制台（概览 / 技能库 / 进化图谱 / 安装台账 / 事件审计）。

### 1.3 非目标

- 不做互联网技能市场、不做跨 PuddingAgent 部署的联邦共享。
- 不改动 `SkillPackageEntity` / `/api/skill-packages`（保留供 Agent 模板选包使用）。
- 不引入新的前端重型依赖（不新增图可视化库；EVO MAP 用 antd `Tree` + 自绘 SVG 连线实现）。

---

## 二、外部参考（联网调研结论）

### 2.1 EvoMap（互联网，2026-04 起）

- 定位：**Agent 自我进化网络**，`evomap.ai` 提供 *live agent maps* / *evolution leaderboards* / Skill Store。
- 载体：把成功做法编码为 **gene fragments**（`genes.json`）与 **capsules**（`capsules.json`），血缘记录写 **`events.jsonl`** 审计流。
- 关键主张：**「Evolution is not optional. Adapt or die.」**；基因片段可**在 Agent 之间共享、跨模型后端继承、重组**。
- 对本方案的借鉴：**技能是可继承/可重组的一等资产**，必须带**血缘 + 审计**；Hub 是共享层，进化在本地发生。

### 2.2 Bayesian-Agent（学界，技能后验引导）

- 对每个技能维护**证据条件后验**，把后验状态映射为**可审阅动作**：

| 动作 | 默认触发条件 |
|---|---|
| **Patch** | 同一失败模式出现 **≥2 次** |
| **Split** | **≥3 个上下文 + 4 次观测** 提示单个技能覆盖异质场景 |
| **Compress** | ≥3 次观测且估计成功率 **≥0.72** |
| **Retire** | 失败证据占优，估计成功率 **<0.45** |

- 报告收益：SOP-Bench 80%→95%、Lifelong AgentBench 90%→100%、RealFin-Bench 45%→65%。
- 对本方案的借鉴：**进化动作要枚举化、要带触发阈值**，并让"阈值可替换"——本方案把动作与阈值落成**显式枚举 + 元数据**，不硬编码策略。

### 2.3 结论：本仓的落地形态

> **「本地进化 + 中央共享 + 血缘可审」**
> 技能在 Agent 本地生成/演化（已有 `SkillEnforcement`/`TrajectorySource` 基础），通过 **SKILL Hub** 发布共享；Hub 保存**内容 + 版本 + 血缘 + 安装台账 + 审计事件**，管理界面把血缘渲染成 **EVO MAP**。

---

## 三、总体架构

```
┌──────────────────────── 人类（浏览器） ────────────────────────┐
│  PuddingPlatformAdmin  ·  /skill-management                      │
│  概览 · 技能库 · EVO MAP 进化图谱 · 安装台账 · 事件审计           │
└───────────────────────────────┬─────────────────────────────────┘
                                │ /api/skill-hub/*
┌───────────────────────────────▼─────────────────────────────────┐
│  PuddingPlatform（中央、私有）                                    │
│  SkillHubController ──> SkillHubService                           │
│        ├── HubSkillEntity          技能主档（最新版本指针）        │
│        ├── HubSkillVersionEntity   版本（内容 + 进化动作 + 父版本）│
│        ├── HubSkillInstallEntity   安装台账（谁装了哪版）          │
│        └── HubSkillEventEntity     审计事件流                     │
└───────────────────────────────▲─────────────────────────────────┘
                                │ HTTP（AdminBaseUrl + X-Admin-Api-Key）
┌───────────────────────────────┴─────────────────────────────────┐
│  PuddingRuntime（各 Agent 实例内）                                │
│  SkillHubTool (id=skill_hub)                                      │
│    search / browse / get / install / publish / update /           │
│    check_updates / evolve / lineage / stats                        │
│         │                                                          │
│         ▼                                                          │
│  AgentSkillFileService（本地技能目录，已有）                       │
│  {DataRoot}/agents/{agentInstanceId}/skills/{skillId}/            │
└───────────────────────────────────────────────────────────────────┘
```

---

## 四、数据模型（PuddingPlatform，schema=`platform`）

> 命名：实体放 `Source/PuddingPlatform/Data/Entities/`，配置写进 `PlatformDbContext.OnModelCreating`。

### 4.1 `HubSkillEntity`（技能主档）

```csharp
public class HubSkillEntity
{
    [Key] public int Id { get; set; }
    [MaxLength(128)] public string SkillId { get; set; } = "";
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(512)] public string? Summary { get; set; }
    [MaxLength(2048)] public string? Description { get; set; }
    /// <summary>JSON 字符串数组</summary>
    [MaxLength(1024)] public string TagsJson { get; set; } = "[]";
    [MaxLength(2048)] public string KeywordsJson { get; set; } = "[]";
    [MaxLength(64)] public string LatestVersion { get; set; } = "1.0.0";
    /// <summary>active | deprecated | retired</summary>
    [MaxLength(32)] public string Status { get; set; } = "active";
    /// <summary>global | workspace</summary>
    [MaxLength(32)] public string Visibility { get; set; } = "global";
    [MaxLength(128)] public string? OwnerWorkspaceId { get; set; }
    [MaxLength(128)] public string? SourceAgentId { get; set; }
    /// <summary>agent-evolved | manual | imported</summary>
    [MaxLength(32)] public string OriginKind { get; set; } = "agent-evolved";
    public int VersionCount { get; set; } = 1;
    public int InstallCount { get; set; }          // 去重后的 Agent 数
    public int PublishCount { get; set; } = 1;
    [MaxLength(64)] public string? LatestContentHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 4.2 `HubSkillVersionEntity`（版本 = EVO MAP 的节点）

```csharp
public class HubSkillVersionEntity
{
    [Key] public int Id { get; set; }
    [MaxLength(128)] public string SkillId { get; set; } = "";
    [MaxLength(64)]  public string Version { get; set; } = "1.0.0";
    [MaxLength(64)]  public string ContentHash { get; set; } = "";
    public string SkillMarkdown { get; set; } = "";     // SKILL.md 全文
    public string ManifestJson { get; set; } = "{}";     // manifest 原文
    /// <summary>create|patch|split|compress|retire|merge|fork</summary>
    [MaxLength(32)] public string EvolutionAction { get; set; } = "create";
    /// <summary>同一 SkillId 内的父版本；create 时为 null</summary>
    [MaxLength(64)] public string? ParentVersion { get; set; }
    /// <summary>分叉/合并时指向的其它 SkillId（JSON 字符串数组）</summary>
    [MaxLength(512)] public string? RelatedSkillIdsJson { get; set; }
    [MaxLength(128)] public string? PublishedByAgentId { get; set; }
    [MaxLength(128)] public string? PublishedByWorkspaceId { get; set; }
    [MaxLength(512)] public string? PublishNote { get; set; }
    /// <summary>证据（触发该进化的证据摘要，JSON）</summary>
    public string? EvidenceJson { get; set; }
    public int ContentBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

**索引**：`(SkillId, Version)` 唯一；`(SkillId, ParentVersion)` 普通索引（血缘查询）。

### 4.3 `HubSkillInstallEntity`（安装台账）

```csharp
public class HubSkillInstallEntity
{
    [Key] public int Id { get; set; }
    [MaxLength(128)] public string SkillId { get; set; } = "";
    [MaxLength(128)] public string AgentInstanceId { get; set; } = "";
    [MaxLength(128)] public string? WorkspaceId { get; set; }
    [MaxLength(64)]  public string InstalledVersion { get; set; } = "";
    [MaxLength(64)]  public string? ContentHash { get; set; }
    /// <summary>agent | user | auto</summary>
    [MaxLength(32)] public string InstalledBy { get; set; } = "agent";
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

**索引**：`(SkillId, AgentInstanceId)` 唯一。

### 4.4 `HubSkillEventEntity`（审计事件流）

```csharp
public class HubSkillEventEntity
{
    [Key] public long Id { get; set; }
    [MaxLength(128)] public string SkillId { get; set; } = "";
    [MaxLength(64)]  public string? Version { get; set; }
    /// <summary>publish|update_version|install|update|status_change|delete</summary>
    [MaxLength(32)] public string EventType { get; set; } = "";
    /// <summary>agent | user | system</summary>
    [MaxLength(32)] public string ActorKind { get; set; } = "agent";
    [MaxLength(128)] public string? ActorId { get; set; }
    [MaxLength(128)] public string? WorkspaceId { get; set; }
    public string? PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 4.5 `PlatformDbContext` 需新增

```csharp
public DbSet<HubSkillEntity> HubSkills => Set<HubSkillEntity>();
public DbSet<HubSkillVersionEntity> HubSkillVersions => Set<HubSkillVersionEntity>();
public DbSet<HubSkillInstallEntity> HubSkillInstalls => Set<HubSkillInstallEntity>();
public DbSet<HubSkillEventEntity> HubSkillEvents => Set<HubSkillEventEntity>();
```

配置（`OnModelCreating`）：
```csharp
modelBuilder.Entity<HubSkillEntity>(e => e.HasIndex(s => s.SkillId).IsUnique());
modelBuilder.Entity<HubSkillVersionEntity>(e => {
    e.HasIndex(v => new { v.SkillId, v.Version }).IsUnique();
    e.HasIndex(v => v.SkillId);
    e.HasIndex(v => v.ParentVersion);
});
modelBuilder.Entity<HubSkillInstallEntity>(e => {
    e.HasIndex(i => new { i.SkillId, i.AgentInstanceId }).IsUnique();
    e.HasIndex(i => i.AgentInstanceId);
});
modelBuilder.Entity<HubSkillEventEntity>(e => e.HasIndex(ev => new { ev.SkillId, ev.CreatedAt }));
```

**迁移命令**：
```
dotnet ef migrations add AddSkillHub \
  --project Source/PuddingPlatform --startup-project Source/PuddingAgent
```

---

## 五、HTTP API 契约（**冻结，不得改名**）

Base：`/api/skill-hub` · 全部 `[Authorize]` · `[ApiController]`
新增文件：`Source/PuddingPlatform/Controllers/Api/SkillHubController.cs`
新增 DTO：`Source/PuddingPlatform/Data/Dtos/SkillHubDtos.cs`

### 5.1 DTO 定义（冻结）

```csharp
public sealed record HubSkillSummaryDto(
    string SkillId, string Name, string? Summary, string? Description,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Keywords,
    string LatestVersion, string Status, string Visibility,
    string? OwnerWorkspaceId, string? SourceAgentId, string OriginKind,
    int VersionCount, int InstallCount, int PublishCount,
    string? LatestContentHash, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record HubSkillVersionDto(
    string SkillId, string Version, string ContentHash,
    string EvolutionAction, string? ParentVersion,
    IReadOnlyList<string> RelatedSkillIds,
    string? PublishedByAgentId, string? PublishedByWorkspaceId,
    string? PublishNote, int ContentBytes, DateTimeOffset CreatedAt);

public sealed record HubSkillDetailDto(
    HubSkillSummaryDto Skill,
    IReadOnlyList<HubSkillVersionDto> Versions,
    IReadOnlyList<HubSkillInstallDto> RecentInstalls);

public sealed record HubSkillInstallDto(
    string SkillId, string AgentInstanceId, string? WorkspaceId,
    string InstalledVersion, string? ContentHash, string InstalledBy,
    DateTimeOffset InstalledAt, DateTimeOffset UpdatedAt);

public sealed record HubSkillEventDto(
    long Id, string SkillId, string? Version, string EventType,
    string ActorKind, string? ActorId, string? WorkspaceId,
    string? PayloadJson, DateTimeOffset CreatedAt);

public sealed record HubSkillStatsDto(
    int TotalSkills, int ActiveSkills, int RetiredSkills,
    int TotalVersions, int TotalInstalls, int DistinctAgents,
    int EvolvedSkills,            // VersionCount > 1 的技能数
    IReadOnlyList<HubSkillActionCountDto> EvolutionActionCounts,
    IReadOnlyList<HubSkillSummaryDto> TopInstalled,
    DateTimeOffset GeneratedAt);

public sealed record HubSkillActionCountDto(string Action, int Count);

// ── EVO MAP ────────────────────────────────────────────────
public sealed record EvoMapNodeDto(
    string NodeId,            // "{SkillId}@{Version}"
    string SkillId, string Version, string EvolutionAction,
    string? ParentNodeId,     // null = 根节点
    string Name, string Status,
    string? PublishedByAgentId, DateTimeOffset CreatedAt,
    int ContentBytes, int InstallCount);

public sealed record EvoMapEdgeDto(string FromNodeId, string ToNodeId, string Action);

public sealed record EvoMapDto(
    IReadOnlyList<EvoMapNodeDto> Nodes,
    IReadOnlyList<EvoMapEdgeDto> Edges,
    DateTimeOffset GeneratedAt);

// ── 请求体 ─────────────────────────────────────────────────
public sealed record PublishHubSkillRequest(
    string SkillId, string Name, string? Summary, string? Description,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? Keywords,
    string Version, string SkillMarkdown, string? ManifestJson,
    string? EvolutionAction, string? ParentVersion,
    IReadOnlyList<string>? RelatedSkillIds,
    string? PublishedByAgentId, string? PublishedByWorkspaceId,
    string? PublishNote, string? EvidenceJson,
    string Visibility = "global");

public sealed record UpdateHubSkillMetaRequest(
    string? Name, string? Summary, string? Description,
    IReadOnlyList<string>? Tags, IReadOnlyList<string>? Keywords,
    string? Status, string? Visibility);

public sealed record RegisterInstallRequest(
    string SkillId, string AgentInstanceId, string? WorkspaceId,
    string InstalledVersion, string? ContentHash, string? InstalledBy);
```

### 5.2 端点（冻结）

| 方法 | 路径 | 说明 | 返回 |
|---|---|---|---|
| GET | `/api/skill-hub/stats` | 概览指标 | `HubSkillStatsDto` |
| GET | `/api/skill-hub/skills?query=&tag=&status=&page=&pageSize=` | 列表/搜索（name/summary/description/tags/keywords 模糊） | `HubSkillSummaryDto[]` |
| GET | `/api/skill-hub/skills/{skillId}` | 详情（含版本列表 + 最近安装） | `HubSkillDetailDto` |
| GET | `/api/skill-hub/skills/{skillId}/versions` | 版本列表 | `HubSkillVersionDto[]` |
| GET | `/api/skill-hub/skills/{skillId}/versions/{version}` | **单版本全文**（含 `SkillMarkdown`） | `HubSkillVersionDto` + `SkillMarkdown` |
| GET | `/api/skill-hub/skills/{skillId}/lineage` | 单技能血缘（EVO MAP 子图） | `EvoMapDto` |
| GET | `/api/skill-hub/lineage?skillIds=a,b,c&limit=` | 全局/多技能 EVO MAP | `EvoMapDto` |
| POST | `/api/skill-hub/skills` | **发布新技能** | `HubSkillSummaryDto` (201) |
| POST | `/api/skill-hub/skills/{skillId}/versions` | **发布新版本 / 进化** | `HubSkillVersionDto` (201) |
| PATCH | `/api/skill-hub/skills/{skillId}` | 改元数据 / 停用 / 退役 | `HubSkillSummaryDto` |
| DELETE | `/api/skill-hub/skills/{skillId}` | 软删（→ `retired` + 事件） | 204 |
| POST | `/api/skill-hub/installs` | **登记安装**（upsert） | `HubSkillInstallDto` |
| GET | `/api/skill-hub/installs?agentInstanceId=&skillId=&page=&pageSize=` | 安装台账 | `HubSkillInstallDto[]` |
| GET | `/api/skill-hub/events?skillId=&limit=` | 审计事件 | `HubSkillEventDto[]` |
| GET | `/api/skill-hub/updates?agentInstanceId=` | **待更新清单**（本地已登记版本 < 最新版本） | `HubSkillUpdateDto[]` |

```csharp
public sealed record HubSkillUpdateDto(
    string SkillId, string Name, string InstalledVersion, string LatestVersion,
    string LatestEvolutionAction, DateTimeOffset LatestPublishedAt, string? PublishNote);
```

### 5.3 语义规则（冻结）

1. **发布新技能**：`SkillId` 必须匹配 `^[a-z0-9][a-z0-9\-]{1,127}$`；已存在 → `409 Conflict`。
2. **发布新版本**：技能不存在 → `404`；`(SkillId, Version)` 已存在 → `409`；成功后更新主档 `LatestVersion` / `VersionCount++` / `LatestContentHash` / `UpdatedAt`。
3. **版本比较**：内部实现语义版本比较器（`1.10.0 > 1.9.0`），**不要用字符串比较**。
4. **ContentHash**：`SHA256(SkillMarkdown)` 前 16 字节 hex（小写）。服务端计算，忽略请求里传来的值。
5. **`EvolutionAction`** 白名单：`create|patch|split|compress|retire|merge|fork`；非法 → `400`。缺省时：`ParentVersion == null` → `create`，否则 `patch`。
6. **`EvolutionAction == "retire"`** 时同步把主档 `Status` 置为 `retired`。
7. **血缘边**：`Version.ParentVersion != null` → 边 `"{SkillId}@{ParentVersion}" → "{SkillId}@{Version}"`，Action = 该版本 `EvolutionAction`。
8. **`OriginKind`**：首次发布时由服务端判定 —— 请求带 `PublishedByAgentId` → `agent-evolved`，否则 `manual`。
9. **`InstallCount`**：按 `HubSkillInstalls` 中**去重 AgentInstanceId 数**维护（登记/撤销时重算）。
10. **软删**：`DELETE` 不物理删除版本内容，只置 `Status = retired` 并写事件。
11. 所有写操作必须写 `HubSkillEventEntity`。

---

## 六、Agent 工具契约

### 6.1 新增 `skill_hub` 工具（PuddingRuntime）

**文件**：`Source/PuddingRuntime/Tools/BuiltIns/Skills/SkillHubTool.cs`
**DI**：`Source/PuddingRuntime/DependencyInjection.cs` 注册
**配置**：复用 `AdminBaseUrl` / `AdminApiKey`；允许 `SkillHub:BaseUrl` / `SkillHub:ApiKey` 覆盖
**HTTP 客户端名**：`SkillHubClient`（**必须设置 UA = `PuddingUserAgent.Value`**）

```csharp
[Tool(
  id: "skill_hub",
  name: "SKILL Hub",
  description: "PuddingAgent 内部私有技能中心（SKILL Hub）。【何时用】想把自己沉淀的技能/经验分享给其他 Agent（publish）、想查找并安装别人分享的技能（search/install）、想检查已装技能是否有新版本（check_updates）、想查看技能进化血缘（lineage）、或想基于已有技能演化出新版本（evolve）时使用。【怎么用】action=search 传 query/tags；install 传 skill_id（可带 version）；publish 传 skill_id+name+skill_markdown（内容取自本地技能或直接给）；evolve 传 skill_id+version+evolution_action+parent_version+skill_markdown；check_updates 无需参数。【坑】publish/evolve 会写入中央库并留审计事件，不可静默撤回；install 会覆盖本地同名技能，请先 get 预览；lineage 需要 skill_id。",
  category: ToolCategory.FileSystem,
  permission: ToolPermissionLevel.Medium,
  safety: ToolSafetyFlags.None,
  SortOrder = 46)]
```

**Actions（冻结）**：

| action | 必要参数 | 行为 |
|---|---|---|
| `search` | `query?` `tags?` `status?` | GET `/skills`，返回精简单（skill_id/name/summary/latest_version/install_count/tags） |
| `browse` | `page?` `page_size?` | 同上，不带 query |
| `get` | `skill_id` | GET 详情；`include_content=true` 时再取最新版本全文 |
| `install` | `skill_id` `version?` `overwrite?` | 取版本全文 → 用 `AgentSkillFileService.Create/Update` 落到本地 → POST `/installs` 登记 |
| `publish` | `skill_id` `name` `skill_markdown` `version?` | 若本地存在该技能且未显式给 `skill_markdown`，从本地读取 → POST `/skills` |
| `update` | `skill_id` `version` `skill_markdown?` `publish_note?` | POST `/skills/{id}/versions`（等价于 `evolve` 但 `evolution_action` 默认 `patch`） |
| `evolve` | `skill_id` `version` `evolution_action` `parent_version` `skill_markdown?` `evidence?` | 同 `update`，但显式带进化动作与证据 |
| `check_updates` | — | GET `/updates?agentInstanceId=` |
| `lineage` | `skill_id?` | GET `/lineage`，返回节点/边摘要（节点数 ≤ 200） |
| `stats` | — | GET `/stats` |
| `unpublish` | `skill_id` | DELETE `/skills/{id}`（软删 → retired） |

**返回约定**：统一 `{ status: "ok"|"error", action, ... }`；失败时 `status="error"` + `message`，**不要抛异常打断 Turn**（HTTP 失败转结构化错误，附 `statusCode` 与响应体前 500 字符）。

### 6.2 增强 `agent_skill`（PuddingRuntime）

在**不改动现有 13 个 action 语义**的前提下新增：

| 新 action | 行为 |
|---|---|
| `validate` | 校验本地技能 manifest 完整性：`skill_id` 格式、`name` 非空、`SKILL.md` 存在且非空、`keywords` 非空、`version` 可解析 → 返回 `{ valid, issues[] }` |
| `export` | 把本地技能打包为 zip 并返回 `{ zipPath, bytes, sha256 }`（写到 `{DataRoot}/tmp/skill-export/`） |
| `clone` | `from_skill_id` + `skill_id` + `name` → 复制本地技能为新技能（用于分叉实验） |

并要求：`push` 的说明中**指向** `skill_hub.publish`（保留 `push` 兼容）。

### 6.3 与既有进化设施接线（最小侵入）

- `AgentSkillEvolutionStore` **不改**。
- 新增 prompt/skill 提示词不硬编码：`skill_hub` 描述内引用动作表即可。

---

## 七、人类管理界面规格（PuddingPlatformAdmin）

**文件**：重写 `Source/PuddingPlatformAdmin/src/pages/skill-management/index.tsx`（保留原「技能包」功能为独立 Tab）
**API 封装**：在 `Source/PuddingPlatformAdmin/src/services/platform/api.ts` 末尾追加 `─── Skill Hub API ───` 段（函数名见下）
**i18n**：`menu.skillManagement` 文案保留；页面内文案用中文硬编码（与仓内既有页面一致风格）

### 7.1 页面结构（5 个 Tab）

| Tab | 内容 |
|---|---|
| **① 概览** | 指标卡：技能总数 / 活跃 / 已退役 / 版本总数 / 安装总数 / 覆盖 Agent 数 / 已进化技能数；右侧：进化动作分布（`patch/split/compress/retire/merge/fork` 计数条）；下方：Top 安装榜 |
| **② 技能库** | 搜索框 + 标签过滤 + 状态下拉；卡片/表格双视图（沿用既有 `viewMode`）；每项操作：详情、版本、血缘、停用/退役、删除 |
| **③ EVO MAP** | **技能进化图谱**：左选择技能（多选）；右侧渲染树 —— 节点 = `{SkillId}@{Version}`，颜色/图标按 `EvolutionAction` 区分（create 绿 / patch 蓝 / split 紫 / compress 青 / retire 灰 / merge 橙 / fork 品红）；点节点弹出侧栏显示：发布者、时间、字节数、安装数、**SKILL.md 全文** |
| **④ 安装台账** | 表格：技能 / Agent 实例 / 工作区 / 已装版本 / 是否落后于最新 / 安装时间；支持按 `agentInstanceId` 过滤；「全部更新」提示（只提示，不代执行） |
| **⑤ 事件审计** | 时间线/表格：事件类型、技能、版本、操作者（agent/user/system）、工作区、payload、时间 |
| **⑥ 技能包（旧）** | 原「上传 zip 管理 Skill 包」整套功能原样保留（供 Agent 模板选包） |

### 7.2 追加的 api.ts 函数（冻结签名）

```ts
// ─── Skill Hub API ───
export interface HubSkillSummaryDto { /* 对齐 5.1 */ }
export interface HubSkillVersionDto { /* … */ }
export interface HubSkillDetailDto { /* … */ }
export interface HubSkillInstallDto { /* … */ }
export interface HubSkillEventDto { /* … */ }
export interface HubSkillStatsDto { /* … */ }
export interface EvoMapNodeDto { /* … */ }
export interface EvoMapEdgeDto { /* … */ }
export interface EvoMapDto { nodes: EvoMapNodeDto[]; edges: EvoMapEdgeDto[]; generatedAt: string }
export interface HubSkillUpdateDto { /* … */ }

export async function getSkillHubStats(): Promise<HubSkillStatsDto>
export async function listHubSkills(params?: { query?: string; tag?: string; status?: string; page?: number; pageSize?: number }): Promise<HubSkillSummaryDto[]>
export async function getHubSkill(skillId: string): Promise<HubSkillDetailDto>
export async function getHubSkillVersion(skillId: string, version: string): Promise<HubSkillVersionDto & { skillMarkdown: string }>
export async function getHubSkillLineage(skillId: string): Promise<EvoMapDto>
export async function getHubLineage(skillIds?: string[]): Promise<EvoMapDto>
export async function updateHubSkillMeta(skillId: string, req: UpdateHubSkillMetaRequest): Promise<HubSkillSummaryDto>
export async function retireHubSkill(skillId: string): Promise<void>
export async function listHubInstalls(params?: { agentInstanceId?: string; skillId?: string; page?: number; pageSize?: number }): Promise<HubSkillInstallDto[]>
export async function listHubEvents(params?: { skillId?: string; limit?: number }): Promise<HubSkillEventDto[]>
```

### 7.3 实现约束

- **不新增 npm 依赖**（EVO MAP 用 antd `Tree`/`Tree.DirectoryTree` + `Tag`/`Badge` 表达层级；如需连线用纯 CSS 左边框表达「继承自」）。
- 页面必须在**数据为空时给出友好空态**（当前线上页面空白的直接原因就是空数据无空态）。
- 组件风格与 `agent-template-settings` / `storage` 页面保持一致（`PageContainer` + `ProTable` + `Card`）。
- 所有 API 调用失败要有 `message.error` 提示，不得静默。

---

## 八、分批实施与委派边界

| 批次 | 交付物 | 文件边界（**严格不得越界**） |
|---|---|---|
| **B1 后端** | 4 实体 + DbContext + EF 迁移 + DTO + `SkillHubService` + `SkillHubController` | `Source/PuddingPlatform/Data/Entities/Hub*Entity.cs`（新）/ `Data/Dtos/SkillHubDtos.cs`（新）/ `Data/PlatformDbContext.cs`（仅加 DbSet+配置）/ `Migrations/*`（新）/ `Services/SkillHubService.cs`（新）/ `Controllers/Api/SkillHubController.cs`（新） |
| **B2 工具** | `SkillHubTool` + DI 注册 + `agent_skill` 三个新 action | `Source/PuddingRuntime/Tools/BuiltIns/Skills/SkillHubTool.cs`（新）/ `Source/PuddingRuntime/DependencyInjection.cs`（仅加注册）/ `Source/PuddingRuntime/Tools/BuiltIns/Skills/AgentSkillTool.cs`（仅加 3 action） |
| **B3 前端** | `skill-management/index.tsx` 重写 + `api.ts` 追加 | `Source/PuddingPlatformAdmin/src/pages/skill-management/`（重写）/ `src/services/platform/api.ts`（仅追加） |

**批次依赖**：B2、B3 依赖 B1 的 HTTP 契约（本文档 §5 已冻结），可**并行**；B1 完成后由主 Agent 统一编译验证。

---

## 九、验收标准

1. `dotnet build PuddingAgent.slnx` **0 error**（或按仓内既有解决办法）。
2. `PuddingPlatformTests` / `PuddingRuntimeTests` 既有用例**无新增失败**。
3. 新增后端单元测试覆盖：发布幂等冲突(409)、版本冲突(409)、语义版本比较、ContentHash 计算、血缘边生成、retire 语义、安装台账 upsert + InstallCount 重算。
4. 前端 `npm run build`（或仓内既有前端构建命令）通过。
5. 手工验收路径：
   - `POST /api/skill-hub/skills` 发布 → `GET /skills` 可见 → `GET /lineage` 有根节点。
   - 再发一版 `evolution_action=patch, parent_version=1.0.0` → `GET /lineage` 出现边。
   - `POST /installs` 登记 → `GET /updates?agentInstanceId=` 在装了旧版时返回待更新。
   - Admin 页面 5 个 Tab 均能加载且空态友好。

---

## 十、风险与回滚

| 风险 | 缓解 |
|---|---|
| 新实体迁移影响既有表 | 只新增表，不改既有列；迁移可 `dotnet ef migrations remove` 回滚 |
| 前端重写破坏既有「技能包」功能 | 原功能整体保留为独立 Tab，不删代码 |
| Runtime 工具新增导致 DI 失败 | 注册为 `TryAddSingleton`/`TryAddTransient`；工具失败转结构化错误，不打断 Turn |
| Hub 写入无鉴权被滥用 | 继承 `[Authorize]`；写操作全部留审计事件 |
| 与既有 `SkillPackageApiController` 混淆 | 路径前缀不同（`/api/skill-packages` vs `/api/skill-hub`），文档与 UI 明确区分 |

---

## 十一、后续演进（本次不做，仅登记）

- 技能**评分/成功率后验**（对齐 Bayesian-Agent：Patch ≥2 失败、Split ≥3 上下文、Compress 成功率 ≥0.72、Retire <0.45）——需要把运行结果回流成证据。
- 技能**依赖声明**与安装时依赖解析。
- Hub 技能 **diff 视图**（版本间 SKILL.md 变更高亮）。
- 自动待更新提醒（接入 Goal/Heartbeat 周期）。
- 跨工作区可见性策略执行（当前仅字段占位）。
