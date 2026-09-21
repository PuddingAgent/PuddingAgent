# EF 迁移机制性不可用 —— 现场测量与方案取舍（2026-09-21）

- 任务卡：`9c25d3ec57f6446eb6ddaa80e9233117`（P2·基础设施，6a8 施工域）
- 来源：dsh(0e0) 实测报告 `Docs/Reports/dsh-协作协议v1.1-确认与修订建议-2026-09-21.md` §A5（commit `fe43e9e`）
- 测量方式：**纯静态**（不编译、不连库、不改任何数据库结构）—— 脚本 `TestScripts/ef-migration-audit.py`（可复现）
- 本轮**未改动任何生产代码、未触发任何 DB 操作**

---

## 0. 结论摘要

**推荐方案 B（放弃 EF 迁移，把幂等 bootstrapper 制度化），并给出可验证的演进路径。**

核心理由只有一句话：**生产运行时根本不用 EF 迁移**（`EnsureCreatedAsync` + 23 个 bootstrapper），
EF 迁移链已是**从未参与生产的死资产**；选择 A 等于**去激活一个从未运行的机制**，
还要额外把 52 张 bootstrapper 表反向搬进 EF 模型 —— 成本与风险都远大于 B。

---

## 1. 现场测量（验收标准 1：漂移条目数与阻塞点）

复现命令（纯静态，秒级）：

```cmd
set "PYTHONUTF8=1" && python TestScripts\ef-migration-audit.py
```

### 1.1 漂移条目数（`PuddingPlatform`）

| 指标 | 实测值 |
|------|--------|
| 快照知道的实体数（`Migrations/PlatformDbContextModelSnapshot.cs`） | **36** |
| 当前模型声明的实体数（`Data/PlatformDbContext.cs`） | **76** |
| **C−S：模型有、快照无** | **46** |
| **S−C：快照有、模型无** | **6** |
| **漂移条目合计** | **52** |

C−S 的 46 个里包含**整块的业务域**（说明不是零星遗漏，而是多轮演进整体没进快照）：

- SKILL Hub：`HubSkillEntity` / `HubSkillVersionEntity` / `HubSkillInstallEntity` / `HubSkillEventEntity`
- Goal 域：`GoalRunEntity` / `GoalIterationEntity` / `GoalVerificationEntity` / `GoalAcceptanceContractEntity` / `GoalCheckRecordEntity` / `GoalOutboxEntity`
- Task 域：`TaskAssignmentAttemptEntity` / `TaskCommentEntity` / `TaskDependencyEntity` / `TaskDispatchOutboxEntity` / `TaskEvaluationEntity` / `TaskEventEntity` / `TaskExecutionBindingEntity` …
- 会话/消息域：`ConversationTurnEntity` / `ConversationHeadEntity` / `ConversationEventEntity` / `ConversationCatalogEntity` / `ConversationProjectionCheckpointEntity` / `MessageTopicEntity` / `ControlMessageEntity`
- 执行/用量域：`ExecutionRunEntity` / `LlmGatewayUsageEventEntity` / `LlmUsageDailyAggregateEntity` / `ContextLayerDailyRollupEntity` / `StatsDailyCacheDayEntity`
- 外部接入：`ExternalAccessTokenEntity`(+Scope/Workspace/AuditEvent) / `ExternalApiIdempotencyEntity` / `ProviderFileRefEntity`

S−C 的 6 个（快照残留，模型侧已迁走）：
`AgentAvatarEntity` / `GlobalAgentTemplateEntity` / `LlmModelEntity` / `LlmProviderEntity` / `LlmProviderQuotaEntity` / `WorkspaceAgentTemplateEntity`
（`PlatformDbContext.cs:891` 有注释明示这些配置类实体「唯一来源已迁移至」别处 ⇒ **确为快照残留，不是测量误差**。）

### 1.2 阻塞点（为什么 `dotnet ef migrations add` 会挡住后续迁移）

EF 的 `migrations add` 是**拿模型减快照**来生成 DDL。当前两侧差 52 条，于是：

1. **46 条会被生成 `CREATE TABLE`** —— 但这些表**早就由 bootstrapper 建好了**（见 §1.3）。
   生成出来的迁移一旦执行就是 `table already exists`；而它作为**下游所有迁移的前置**，会把整条链堵死。
2. **6 条会被生成 `DROP TABLE`** —— 那 6 个实体在模型侧已迁走，EF 会认为该删表；
   若真执行就是**删掉仍在使用的表**（`LlmModelEntity` 等明显仍在业务中使用）。
   ⇒ 这是**危险方向**的漂移：不是"改不动"，而是"一执行就破坏数据"。
3. 因此 dsh 的处置（改用 `SkillHubSchemaBootstrapper` 幂等建表绕过）是**当时唯一能推进的路径**，
   不是偷懒 —— 本次测量为这一判断提供了量化依据。

### 1.3 迁移链与 bootstrapper 的规模对比（`PuddingPlatform`）

| 机制 | 规模 | 备注 |
|------|------|------|
| EF 迁移 | **22 个基迁移**（32 个文件） | 其中 **12 个缺 `.Designer.cs`** ⇒ 非 EF 生成（手工迁移） |
| bootstrapper | **24 个类**（19 个含 `CREATE TABLE`），**共 52 张表** | 全部 `public static class *SchemaBootstrapper` + `EnsureCreatedAsync` |

- 缺 `.Designer.cs` 的 12 个迁移：`AddKeyVault`、`AddAgentPersonaAndWorkspaceUserProfile`、
  `AddAgentTemplateMemoryLlmConfig`、`SeedAllBuiltInCapabilities`、`UseLlmPoolForAgentTemplateMemoryModel`、
  `CreateTokenUsageStatsTable`、`SeedMissingToolCapabilities`、`AddMaxConcurrentRequestsToLlmModel`、
  `AddTokenLayerBreakdownColumns`、`AddTokenUsageHistoryMessageEntropy`、
  `AddMessageQueueProjectionColumns`、`AddMessageDeliveryHandlingMode`
  （多数时间戳是**整点**如 `20260503123000`，而 EF 生成的是 `20260319022823` 这种带秒的 ⇒ 手工特征明显）
- 即：**迁移链本身早已半手工化**，同时 bootstrapper 数量已超过迁移数量。

---

## 2. ⭐ 决定性事实：生产运行时不用 EF 迁移

`Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs`：

```csharp
L45: await platformDb.Database.EnsureCreatedAsync(cancellationToken);   // ← 用 EnsureCreated，不是 Migrate()
L46: await AppUserSchemaBootstrapper.EnsureCreatedAsync(platformDb, schemaLogger, cancellationToken);
L47: await TokenUsageSchemaBootstrapper.EnsureCreatedAsync(...);
...
L68: await ProviderFileRefSchemaBootstrapper.EnsureCreatedAsync(...);   // ← 硬编码串行调用 23 个 bootstrapper
```

三点推论：

1. **EF 迁移在生产从未参与**：`EnsureCreated()` 与迁移机制在设计上互斥
   （EF 明确：`EnsureCreated` 不使用迁移，与之不能共存）。全部 22 个迁移文件是**死资产**。
2. **现有 schema 演进路径是**：全新库由 `EnsureCreated` 按**模型**建表 → 已有库由 23 个 bootstrapper **幂等补建/补列**。
3. **"碎片化"的准确形态不是"24 处散落调用"，而是**：
   **单一入口 + 23 行硬编码 + 每加一张表就要新增一个类、一行调用、一套测试**。
   传播方式是**复制粘贴 + 互相引用注释**（每个 bootstrapper 的 XML 注释都写着"与 X 同风格"，
   已构成事实上的惯例，但**没有任何规范文本、版本表或统一接口**）。

---

## 3. 方案 A vs 方案 B

| 维度 | A. 重建基线快照，恢复标准 EF 迁移链 | B. 放弃 EF 迁移，制度化 bootstrapper |
|------|-----------------------------------|--------------------------------------|
| 前置工作量 | 必须把 **52 张 bootstrapper 表 + 46 个实体**反向搬进 EF 模型（写实体映射/配置），否则基线无法重建含这些表的库 | 把既有 24 个类收敛到**统一接口 + 注册表 + 版本表**，改造量集中在"外壳" |
| 生产运行时改动 | 必须把 `EnsureCreatedAsync()` 换成 `MigrateAsync()`，并**删除**现有 bootstrapper 调用 ⇒ 行为变更面大 | **零行为变更**（同一套幂等 DDL，只是加了注册与留痕） |
| 现存库处理 | 现存库没有 `__EFMigrationsHistory` 记录 ⇒ 需在**每个环境**做一次"基线盖章"DB 操作；漏盖/多盖都会导致后续误判 | **无需任何 DB 操作**（版本表由幂等 DDL 自建，与本轮验收标准 3 完全兼容） |
| 失败模式 | `table already exists`（46 条）/ **误删仍在用的表**（6 条快照残留 ⇒ `DROP TABLE`） | 幂等 DDL 重复执行即跳过；风险是**缺中央留痕**（本次要补的正是这块） |
| 可验证性 | 依赖 EF 的模型 diff（工具链现成，但需先补齐模型映射才准） | 依赖**自建**校验：版本表 + 每 bootstrapper 单测（仓内**已有** `SchemaBootstrapperFreshDatabaseColumnTests` 等一批测试） |
| 长期收益 | 恢复"模型即真源"+自动 diff | 保住"已有库可幂等自愈"这一**已在生产验证**的能力；但需自建 diff/校验 |
| 与现状的连续性 | **断裂**：要激活一个 5 个月没运行过的机制 | **连续**：只是把已在跑的做法写进规范 |

**取舍判断**：A 的唯一优势是"恢复 EF 的自动 diff"，但要先付出"把 52 张表搬进 EF + 全环境盖章"的代价，
而这一步本身就是**当初绕行迁移的原因**；B 则是在**已经赢了的事实标准**上补齐治理外壳。
⇒ **选 B**。A 不作废，但应作为"若未来决定把 bootstrapper 表统一纳管"的长期选项保留（属独立项目级决策）。

---

## 4. 方案 B 的落地设计（若采纳）

1. **统一契约**：新增 `ISchemaBootstrapper { string Component { get; } int Version { get; } Task EnsureCreatedAsync(...) }`，
   24 个既有类实现之（保持现有 DDL 不动，只加外壳）。
2. **单一注册表**：把 `PuddingApplicationInitializer` 的 23 行硬编码换成注册表遍历（顺序显式声明），
   新增表只需**加一个实现类**，不再改初始化器。
3. **版本表 + 留痕**：`SchemaComponentVersion(Component TEXT PK, Version INT, AppliedAtUtc TEXT, Checksum TEXT)`，
   由幂等 DDL 自建；每次执行记录"已应用到第几版 + 该版 DDL 校验和"⇒ 这才让"演进"可审计（当前完全无留痕）。
4. **回滚策略**：限定演进为**加性变更**（加表/加列/加索引）默认安全；
   破坏性变更（删列/改类型）必须走显式 `Down` 清单 + 执行前备份 + 人工闸门。
5. **门禁（关键）**：CI 检查
   （a）每个 `*SchemaBootstrapper` 都在注册表中；（b）每个都有对应测试；
   （c）**禁止**把新表只写在迁移里（迁移文件数不再增长）；
   （d）`SchemaBootstrapperFreshDatabaseColumnTests` 扩展到覆盖全部 bootstrapper（当前只覆盖 3 个）。
6. **文档**：写入协作协议/开发规范前，先由蜜糖复核本取舍（验收标准 5）。

---

## 5. 本次测量的边界（不夸大证据强度）

- **已是实测**：§1 的全部计数、§2 的调用链事实（均来自文件级证据，可复现）。
- **未测**（明确留给后续，且都需要越权或重型动作）：
  1. **属性/列级漂移**：本脚本只比较**实体级**集合，未比较表内列差异 ⇒ 真实漂移条目**只会更多**。
  2. **历史迁移是否真的执行过**：需只读查询现存库的 `__EFMigrationsHistory`
     （本轮按验收标准 3/4 未连库；建议在非工作时段由持库者执行只读查询）。
  3. `PuddingController/Migrations`（仅 1 个 InitialCreate + 快照，3 个文件）未深挖。
  4. 未运行 `dotnet ef migrations has-pending-model-changes`（需编译，属重型动作，按 §12 排非工作时段）。
- **本轮零副作用**：未改数据库结构、未改生产代码、未新建迁移文件。

---

## 6. 证据索引

| 结论 | 证据位置 |
|------|----------|
| 快照 36 实体 / 模型 76 实体 / 差 52 | `TestScripts/ef-migration-audit.py` 输出（可复现） |
| 22 基迁移、12 个缺 Designer | `Source/PuddingPlatform/Migrations/` |
| 24 个 bootstrapper、52 张表 | `Source/PuddingPlatform/Services/**/*SchemaBootstrapper.cs` |
| 生产用 EnsureCreated + 23 行硬编码 | `Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs:45-68` |
| 快照残留 6 实体为真 | `Source/PuddingPlatform/Data/PlatformDbContext.cs:891` 注释 |
| bootstrapper 已有测试基础 | `Source/PuddingPlatformTests/Services/SchemaBootstrapperFreshDatabaseColumnTests.cs` 等 |
| 原始现象报告 | `Docs/Reports/dsh-协作协议v1.1-确认与修订建议-2026-09-21.md` §A5 |
