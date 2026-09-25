# PuddingHost CodeMAP

> 唯一 Host 组合根 | Console 与 Desktop 共用 DI · Browser Bridge · 飞书连接器

## 组合根

| 文件 | 用途 |
|------|------|
| `PuddingHostAssemblyMarker.cs` | 程序集标记 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 成品 Host 的 Platform/Runtime 组合注册；内置 Agent 模板直接使用 PuddingCore 唯一权威源；包含 MOA、V2 component registry/compiler、SQLite store/signal、Admin 手动 Run/HTTP Hook command service、SubAgent/图片生成/展示 executor、临时子代理目录两阶段 GC、hosted worker 与 replay-to-live follower；`TaskAgentCommandService` 与 Singleton `task_*` 工具同生命周期，服务内部每次调用通过 DbContextFactory 创建独立 DbContext；不能只在未被产品入口调用的 Runtime 扩展里注册 worker |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 成品 Host 的 Runtime/Tool 组合注册；assembly scan 自动发现的新工具，其构造依赖也必须在这里注册（例如 `SavePreferenceTool` → `IUserPreferenceService`、`SkillEnforcerService` 的可选 `ISkillUsageTelemetrySink`：注册缺失时该可选参数**静默为 null**，不报错也不抛异常） |
| `Tools/ImageReaderTool.cs` + `Tools/ImageReaderSourceResolver.cs` | 原生阅读与预处理：metadata/read/prepare，四档 detail，缩略图、裁剪、90度旋转、灰度、Gaussian降噪、jpeg/png/webp编码；源支持本地/URL/聊天artifact；复用 Platform ImagePreprocessing 和派生缓存，不调用模型/Agent，无 helper 路由。低权限只读源文件，URL每跳SSRF校验；输出源/派生引用供多次区域读取 |
| `Hosting/PuddingApplicationInitializer.cs` | 启动期数据库初始化；包含 AppUsers、WorkspaceTask、TaskPlanning/WorkUnit/AwaitHandle、Goal 及通用编排 SQLite schema bootstrap，已有数据库也必须幂等升级；GoalSchemaBootstrapper 后执行 GoalRestartReconciler 启动 reconcile（按 `goal_runs.resume_policy` 分流：默认 disarm 为 paused；`auto_resume_on_restart` 则保持 Active 并换发 activation fence；输出 disarmed / auto-resumed 计数） |
| `Storage/StorageMaintenanceService.cs` | 🔑 Core 所有的 SQLite/代码索引明细与安全清理；固定语义白名单、服务端预览、批量删除、checkpoint/VACUUM |
| `Controllers/StorageManagementController.cs` | `/api/admin/storage/databases` 分析、清理预览与执行 API |
| `Hosting/StorageManagementAuthorization.cs` | 平台 admin JWT，或 DesktopChild Loopback + ControlToken 的管理策略 |

## Browser Bridge（Phase 2A）

| 文件 | 用途 |
|------|------|
| `BrowserBridge/RemoteBrowserRuntime.cs` | Core 侧 Browser 代理（→ 认证 Bridge） |
| `BrowserBridge/RemoteBrowserContext.cs` | Remote Context 代理 |
| `BrowserBridge/RemoteBrowserPage.cs` | Remote Page 代理 |
| `BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs` | 条件注册（仅 DesktopChild + BrowserAutomationEnabled） |

## 飞书连接器

| 文件 | 用途 |
|------|------|
| `Services/FeishuConnectorFactory.cs` | 飞书连接器工厂 |
| `Services/FeishuStreamingProjectionWorker.cs` | 飞书流式投影（31KB） |
| `Services/FeishuImageUploadPreparationService.cs` | 飞书图片上传准备 |
| `Services/FeishuTtsDeliveryService.cs` | 飞书 TTS 投递 |
| `Services/FeishuConnectorIdentity.cs` | 飞书身份标识 |

## 连接器 & 消息

| 文件 | 用途 |
|------|------|
| `Connectors/` | 连接器实现 |
| `Services/ConnectorHost.cs` | 连接器宿主 |
| `Hosting/ConnectorHostLifecycleService.cs` | 连接器生命周期 hosted service：本地注册同步、`StartAllAsync` 后台执行（ApplicationStopping 绑定），Ready 不被 Feishu WS 握手阻塞；单连接器失败隔离进 Faulted |
| `Services/ConnectorDeliveryDispatcher.cs` | 投递分发 |
| `Services/MessageGatewayIngress.cs` | 消息网关入口（19KB） |
| `Extensions/` | 扩展注册 |

飞书 WS 底座在 `../../src/HarnessAgent/Core/Connectors/Feishu/FeishuWebSocket.cs`：端点发现
HttpClient 与 WS 握手各 15s 上限，避免外网黑洞把连接器卡在 Starting 100s。

## 服务治理

| 文件 | 用途 |
|------|------|
| `Services/HeartbeatService.cs` | 当前 Agent 心跳编排（类名 `HeartbeatOrchestrator`，与文件名不一致；日志分类字符串亦为 `[HeartbeatOrchestrator]`）。启动时 + 每次空闲 tick 对**全部**「启用 + 未冻结 + 已绑定主会话」的 Agent 幂等补全登记（2026-09-20；此前只登记单个“默认 Agent”，且“队列为空才补全”不可达，导致其余 Agent 永远没有心跳）；实例提示词后追加自主执行契约；2026-08-26 增加持久 Availability gate，等待 SubAgent/Task/Goal、消息排队、Reservation、Unknown 或重建失败均跳过并重新排队，避免把 runtime 暂停误判为空闲 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 组合 Goal outbox/settlement workers、Task-bound 原子 Store、Availability/Reservation/Dependency/Window/Auto Worker；authoritative flag 前置条件 ValidateOnStart |
| `Services/CronSchedulerService.cs` | Cron 调度 |
| `Services/ConfigHotReloadService.cs` | 配置热重载 |
| `Services/IndexPrebuildService.cs` | 索引预构建 |
| `Hosting/PuddingHostOptionsFactory.cs` | DesktopChild 固定 `0.0.0.0:<port>` 启动约束 |
| `Hosting/PuddingServerAddressAccessor.cs` | 全网卡监听地址投影为同端口 Loopback 控制地址 |
| `Hosting/PuddingApplicationHost.cs` | 组合根、Kestrel 地址绑定与本机控制地址捕获；在发布包 appsettings 默认之上加载 `<DataRoot>/config/system.json` 并支持 hot reload，环境变量/命令行仍为最高优先级；注册 ADR-075/082 External Token 的 tasks/workspaces/agents/messages scope+workspace Policies |
| `Config/` | 默认配置 |
| `Prompts/` | 系统提示模板 |
| `P2P/` | P2P 通信 |

## 测试

`../Tests/PuddingHost.Tests/` — Browser Bridge、Remote proxy、Storage 管理与 DesktopChild 产品组合根构建验证；组合根测试显式验证 Singleton `task_*` 工具及其命令服务生命周期；Storage 定向测试 4/4 ✅

## U3-B2a（2026-09-23）— 代码索引维护的宿主生命周期驱动

| 文件 | 用途 |
|------|------|
| `Hosting/CodeIndexMaintenanceHostedService.cs` | 🔑 U3-B2a：索引维护组件的**唯一生命周期驱动**。`StartAsync` 非阻塞启动组件驱动（`ICodeIndexMaintenance`），并把「已注册 scope」挂上变更源 —— 附着在启动路径之外（`ScopeAttachmentCompleted` 可观测），失败只记日志，无 scope 时安全 no-op；`StopAsync` 有界（外层 10s 上限）且不抛异常逃逸、不丢已入队请求。**本身不含任何循环 / 队列 / 索引逻辑**：泵与变更捕获留在 `PuddingCodeIndex`（ADR-089 §2 驱动归属）。注册点：`PuddingServiceCollectionExtensions.Platform.cs` 紧邻 `AddPuddingCodeIntelligence()`。 |

组合根同时新增 `../Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs`（3 用例）：驱动可解析、泵端口与 `ICodeIndexScheduler` 同实例、驱动已注册且持有同一实例、无 scope 时安全 no-op、`Enqueue → 泵 → ICodeIndexer`（替身计数）闭合、已注册 scope 被挂上变更源。

顺带修复：`Storage/StorageMaintenanceServiceTests.cs` 与 `Storage/StorageManagementAdministrationTests.cs` 各补 1 行 `using PuddingCodeIndex.Contracts;` —— 此前 `ICodeIndexScheduler` 已迁出 `PuddingCodeIntelligence.Contracts`，整个 `PuddingHost.Tests` 编排期编译不过。

## U4-7（2026-09-25）— 全文索引「供给参数」配置化 + fail-closed 校验（**默认关闭**）

用户裁定（2026-09-25）：「1GB 请使用配置文件确定参数，方便后期替换为 XXGB……用**项目目录统计 json** 或 **Data 目录配置文件**决定，而不是选择一个固定值。」
⇒ 本刀取 **Data 目录配置文件**：`<DataRoot>/config/system.json` 的 `FullTextIndex` 节（宿主 `PuddingApplicationHost.CreateBuilder` 已加载它并支持 hot reload）。

| 文件 | 用途 |
|------|------|
| `Hosting/FullTextIndexSupplyOptions.cs` | 🔑 供给参数类型（节名 `FullTextIndex`）：`Enabled`（**默认 `false`**）/ `Scopes`（目标目录，可为绝对或相对 `WorkspaceRoot`）/ `WorkspaceRoot`（相对项的**显式绝对基准**，**禁用进程 CWD**）/ `MaxIndexBytes`（**默认 `1_073_741_824` = 1 GiB = 2^30**；硬天花板 `1L << 40` = 1 TiB）/ `MinRebuildInterval`（默认 12h，JSON 写 `"hh:mm:ss"`）。**单一真源**：1 GiB 字面量全仓生产代码只在此出现一次。 |
| `Hosting/FullTextIndexSupplyResolver.cs` | 🔑 fail-closed **纯函数**校验（不依赖 DI / 文件系统 / 网络）：关闭 ⇒ 成功且空动作、**连 scope 探针都不调用**；开启而 `Scopes` 为空 / 含空串·不存在·非目录·重复项 / 相对项无绝对基准 / `MaxIndexBytes <= 0` 或超 1 TiB / `MinRebuildInterval < 0` ⇒ **结构化拒绝**（`ParameterName` + `Value` + 原因枚举 + 可读消息），且 accepted / rejected **分别列出**。scope 探针是三态委托（`Missing`/`NotDirectory`/`Directory`），可注入替身 ⇒ 单测零文件系统访问。 |
| `Hosting/IndexPrebuildFreshness.cs` | `MinRebuildInterval` 的消费点（纯函数、注入时钟）：无索引 / 索引过旧 / 间隔 ≤ 0 ⇒ 重建；索引足够新 ⇒ 跳过。⚠️ 仪器口径 = **索引根目录 mtime**（per-scope 索引目录在 `PuddingFullTextIndex` 内且 `internal`）⇒ 粗粒度代理，已在交付报告登记留白。 |
| `Services/IndexPrebuildService.cs` | 由配置门控的预建服务（此前是 `HOSTED-DISABLED`）。`StartAsync` **永不阻塞宿主**；**默认配置下不建索引、零索引 I/O、不记 Error**；校验不过 ⇒ 记 Error 且什么都不做（fail-closed，不部分生效）；通过 ⇒ 启动路径之外按 `Scopes` 逐个预建 —— **目标来自配置，不再是 `Directory.GetCurrentDirectory()`**（历史缺陷）。 |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 接线：`Configure<FullTextIndexSupplyOptions>(builder.Configuration.GetSection(FullTextIndexSupplyOptions.SectionName))`。**必须是 `builder.Configuration`**：`bootstrapConfiguration` 只含 appsettings/环境变量，`system.json` 只加在 `builder.Configuration` 上。同处把 `IndexPrebuildService` 从 `HOSTED-DISABLED` 注释改为常驻注册（默认配置下等价 no-op，现网行为不变）。 |

测试（`../Tests/PuddingHost.Tests/Hosting/`，**20 用例**）：`FullTextIndexSupplyResolverTests`（A1~A5 + 相对/绝对基准 + 零间隔边界，10 条）、`IndexPrebuildServiceTests`（A6 + 开启路径 + 拒绝可观测，3 条）、`IndexPrebuildFreshnessTests`（5 条）、`FullTextIndexSupplyHostBindingTests`（system.json → IOptions 绑定 + hosted 注册 + 无该节即默认关闭，2 条）。证据与变异输出见 `temp/U4-7-REPORT.md`、`temp/u4-7-evidence/`。

⚠️ **留白（R5）**：体积护栏的**执行**行为（达 `MaxIndexBytes` 时告警 / 拒写 / GC）**未实现**，属后续切片（ADR-089 §7.2「触发 GC 留到后续切片」）。本刀只提供参数与校验。

## S5（2026-09-25）— 预建索引改走「协调器 + 暂存供给」（**默认关闭 ⇒ 零 I/O**）

U4-7 的预建**直写** `IFullTextSearchEngine.BuildIndexAsync`，且用**索引根 mtime** 判所有 scope 的新鲜度。
S5 把两条都换掉：写路径统一进组件协调器（跨进程租约 / 幂等合并 / 预算硬限 / staging / 原子切换），
新鲜度改为 **per-scope**（该 scope **自己**的 live 索引目录）。

| 文件 | 用途 |
|------|------|
| `Hosting/IFullTextIndexSupplyComposition.cs` | 🔑 宿主侧供给端口：`IFullTextIndexSupplyComposition`（`Coordinator` / `ComponentOptions` / `LiveIndexLastWriteUtc`）+ `IFullTextIndexSupplyCompositionFactory`。**为什么用工厂**：R4 要求 `Enabled=false` 时「不解析 scope、**不构造协调器组合**、不 touch 索引根」—— 协调器 / staged builder / 清点 / 租约的实例化被推迟到真正进入供给路径之后（默认关闭时永不发生）。 |
| `Hosting/LuceneFullTextIndexSupplyCompositionFactory.cs` | 生产装配：`FileSystemSupplyInventory` + `StagedFullTextIndexBuilder`（live 引擎 = **查询侧同一实例**，否则 reader 失效打空）+ `FileSupplyLease` + `FullTextIndexSupplyCoordinator`；**配置流入组件**（R2）：`DefaultBudgetBytes = FullTextIndexSupplyOptions.MaxIndexBytes`、`MinRebuildInterval = MinRebuildInterval` —— 宿主侧「1 GiB」仍只有 `FullTextIndexSupplyOptions.DefaultMaxIndexBytes` 一处真源，组件常量退化为未接线时的兜底。**per-scope 新鲜度**（R3）= `IFullTextIndexRootedEngine.ResolveIndexDirectory(scope)` 指向的 **live 索引目录** mtime（组件内的映射单一真源；`SupplyIndexDirectoryLayout` 仍是 internal，宿主**不复刻**哈希规则）。 |
| `Services/IndexPrebuildService.cs` | 写路径改为「提交 `SupplyScopeRequest` → 轮询 `GetStatusAsync` 到终态」（`StartupDelay` / `StatusPollInterval`(默认 250ms) / `BuildWaitTimeout`(默认 **30min 上限**) 可注入；**超时只停止观测、不取消 job、不重试**）；`Busy` / `Rejected`(含 OverBudget) / `Failed`(含 RolledBack / 切换失败，原因原文照登) / 超时**逐类如实记录**（带 jobId）；**每个 scope 最多提交一次**。引擎只剩**只读**用途（`HasIndex`）。 |
| `Hosting/IndexPrebuildFreshness.cs` | 注释口径修正：新鲜度输入从「索引根 mtime」改为**该 scope 自己的 live 索引目录 mtime** —— U4-7 登记的「粗粒度代理」留白就此关闭（旧口径下建出任一 scope 就会把其余 scope 集体误判为新鲜）。 |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 新增 `AddSingleton<IFullTextIndexSupplyCompositionFactory>(…)`：取 `FullTextIndexOptions` + 断言引擎实现 `IFullTextIndexRootedEngine`（staged 切换需要「语料根 → 索引目录」映射与 reader 缓存失效两个接缝），staging 引擎工厂 = `new LuceneSearchEngine(stagingOptions)`。 |

测试（`../Tests/PuddingHost.Tests/Hosting/`，宿主 **154 用例**；S5 新增 8 条）：
`S5IndexSupplyHostWiringTests`（A1 默认关闭零 I/O・工厂/协调器/引擎 0 调用・索引根不建；A2 **真实 Lucene** 端到端可查询 + live 引擎**零直写**；A3 OverBudget 如实记录且 live 逐字节不变、旧索引仍可查；A4 **真实跨进程文件租约** Busy ⇒ 只提交一次、记录 owner/PID、继续下一 scope；A5 per-scope 新鲜度只提交陈旧者（A 新鲜只跳 A）；A6 配置流入组件；轮询超时有界）、
`S5FullTextIndexSupplyHostCompositionTests`（组合根：`system.json` → 组件策略 + `IFullTextIndexRootedEngine` 接缝成立，全程零索引写入）、
`S5SupplyTestDoubles.cs`（夹具与替身）、`IndexPrebuildServiceTests`（U4-7 三条按 S5 语义适配）。
证据：`temp/S5-REPORT.md`、`temp/s5-evidence/`。

⚠️ **边界**：CLI 与其它工程一律未改（组件零改动）；**重启后的运行态验证由父级执行**（本刀禁止重启任何进程）。
