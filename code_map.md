## 2026-09-24 U3-B3：删除/重命名真清除 + 按文件增量（检索正确性）

**第 0 步是取证，不是改码**。实测基线（改动前、可复现）：① 变更管线级 —— 文件已索引 → `Deleted` 批次 → 查询**仍返回该文件的符号**（`Assert.IsEmpty 失败。大小 0 的预期集合。实际：1`）；② 索引器级（真 Roslyn + 真 store）—— 文件从编译中消失后跑**全量重索引**，该文件仍残留 2 条符号行（`T:Probe.Alpha`、`M:Probe.Alpha.Value`）与对应的 `CodeFiles` 行 ⇒ **全量重索引不做 sweep**（源码证据：`RoslynCSharpIndexer.cs` 的 `IndexWorkspaceCoreAsync` 只遍历编译中的语法树，没有“本轮未见”清理），sweep/校准归 U3-C。

**交付**：① `ICodeIndexStore.RemoveFilesAsync`（**一个批次一个事务**：文件记录 + 其全部符号 + 其关系/引用；**幂等**，未知路径安全 no-op 并返回真正删除的文件数）；② 新端口 `ICodeIndexFileUpdater.IndexFileAsync`（**故意不放进 `ICodeIndexer`** —— 给全量端口加成员会破坏**每一个**实现者，实测直接弄坏了禁写路径 `Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs:290` 的 `CountingCodeIndexer`；`ICodeIndexer.cs` 已回退回 HEAD，blob hash 相等）；Roslyn / Python / TypeScript 三个索引器实现该能力，不能处理则返回 `Failed`；③ `CodeIndexMaintenanceService` **按文件施用**：`PathsToRemove` 真删、`PathsToReindex` 逐文件索引、重命名 = 旧清新增、消失的变更路径按删除处理；目录变更 / 索引器拒绝 / 无该能力 ⇒ **升级为 scope 级重索引**（不丢变更）；批次施用异常 ⇒ 标 `NeedsReconcile(batch_application_failed)` + 错误日志；状态新增 `RemovedFileCount` / `IncrementallyIndexedFileCount` / `ScopeEscalationCount`。

**检索正确性（唯一硬判据）**：文件已索引 → 被删 → 走变更管线 → **查询不再返回其符号**（`CodeIndexRemovalCorrectnessTests`，改动前红、现在绿）。

**门禁**：`PuddingCodeIndexTests` **66/66**（58 → 66：+5 管线施用 + 3 存储清除）、`PuddingCodeIntelligenceTests` **93/93**（89 → 93）、`PuddingAgent -c Release` **0 个错误 / 14 个警告（exit 0，日志内 `error CS` 真实命中 0）**；`Tests/PuddingHost.Tests` **123/124**：唯一失败项 `Enqueue_Is_Pumped_By_The_Host_Driver_And_Reaches_The_Indexer` 经实测是**既有时序竞态**（它等 `indexer.CallCount>=1`——在索引器调用**内部**被观测到——随后立即断言 `CommittedVersion>=1`，而该水位在 `CodeIndexScheduler.ProcessJobAsync` 的 `finally` 里、索引器返回**之后**才写；干净 HEAD 树（`git archive` + 补 `external/` 后）6 次运行 **2 红**，本工作树 9 次运行 3 红，失败率同级）。

**边界**：`PuddingCodeIndex` 的 `ProjectReference` 仍为空、无 Roslyn/MSBuild 引用（带阳性对照的反向检索：4 个禁用名仅命中 2 处**注释**）；**未改 Host/DI**（无新增依赖注册：维护服务从已注册的 `ICodeIndexer` 取 `ICodeIndexFileUpdater` 能力）；未新增 NuGet。

**变异取红（两组，各三份原始输出）**：A 让按文件清除变 no-op ⇒ **6 红 / 60 绿 / 66**（含硬判据用例）；B 只删文件记录不删符号 ⇒ **4 红 / 62 绿 / 66**（含硬判据用例）；复原后 `git hash-object` 与变更前**逐位相同**（`bccc3a3b0f0ae14867af8599c6ce85a28520a581`）且复跑 **66/66 exit 0**；`MUTATION` 残留 0。

**诚实留白**：`ReconcileRequired` 永不自动清除（U3-C）；目录删除/重命名下的**子树**陈旧条目不在本刀（U3-C manifest 校准）；`PuddingFullTextIndex`（Lucene）是与 code index **无数据交叠**的第二份索引（文件内容检索，自建 `stalePaths` 增量清理，`LuceneSearchEngine.BuildIndexAsync`），本刀**不接**；Roslyn `IndexFileAsync` 每次调用重新打开 MSBuild 工作区（未做复用），且**未做**真 `.csproj` 端到端实测；Python/TypeScript 的按文件实现沿用既有逐文件抽取脚本，未在测试环境实测。

## 2026-09-23 U3-B2a：索引维护接入宿主生命周期（P0「入队无人泵」收口）

U3-B1（`a378a9d8`）删掉 `CodeIndexScheduler` 的自驱动 worker 后，生产受理点 `code_index_register_project`（`Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeProjectManagementTools.cs`）的 `Enqueue` 就没有任何东西去泵 —— 入队即静默滞留。本刀让「入队 → 泵 → 索引器」在宿主运行时真正闭合。

**组件侧（逻辑留组件内）**：`CodeIndexMaintenanceService.ProcessDueBatchesAsync` 改为**完整驱动步** —— 除消费到期批次外，**每步都无条件泵一次** `ICodeIndexSchedulerDriver.ProcessPendingAsync`。为什么必须无条件：调度器队列的投喂方不止变更管线，注册工具是直接 `Enqueue` 的、背后没有批次；只在 `HandleBatchAsync` 里泵会让这类请求永久饿死（那就是 P0 本身，只是换了个位置）。`ICodeIndexMaintenance` 契约补齐 `EnsureScope(workspaceId, scopeId, rootPath)`（原先只在实现类上），使宿主可以**只通过端口**挂载 scope。

**装配侧**：`PuddingCodeIntelligence/DependencyInjection.cs` 新增三项注册 —— `ICodeIndexSchedulerDriver`（从 `ICodeIndexScheduler` 的**同一实例**派生：两个调度器＝同一索引两个写者；契约不满足时 fail-closed 抛出可诊断异常）、`ICodeIndexWatcherFactory`（显式传 `ILoggerFactory` 派生的 logger，非泛型 `ILogger` 未注册会静默为 null）、`ICodeIndexMaintenance` → `CodeIndexMaintenanceService`。`PuddingHost` 新增 `Hosting/CodeIndexMaintenanceHostedService`（**唯一生命周期驱动**，本身**没有任何循环/队列/调度器调用**）：`StartAsync` 非阻塞启动组件驱动，并把「已注册 scope」挂上变更源（附着在启动路径之外，失败只记日志，无 scope 时安全 no-op）；`StopAsync` 有界（外层 10s 上限 + 组件自身边界），不抛异常逃逸、不丢已入队请求。注册点：`PuddingServiceCollectionExtensions.Platform.cs`，紧邻 `AddPuddingCodeIntelligence()`。

**门禁（含一处顺带修复）**：`Tests/PuddingHost.Tests` **在本刀之前根本编译不过** —— `Storage/StorageMaintenanceServiceTests.cs:357` 与 `StorageManagementAdministrationTests.cs:530` 仍 `using PuddingCodeIntelligence.Contracts;`，而 `ICodeIndexScheduler` 已随组件拆分迁到 `PuddingCodeIndex.Contracts`（拆分片遗留回归，两个 1 行 using 即修，各 +1/−0）。修复后实测：`PuddingHost.Tests` **124/124**（其中 `Hosting` 命名空间 58/58、`PuddingApplicationHostCompositionTests` 2/2、新增 `CodeIndexMaintenanceHostCompositionTests` 3/3）；`PuddingCodeIndexTests` **58/58**；`PuddingCodeIntelligenceTests` **89/89**（两者与拆分后基线一致）；`PuddingAgent -c Release --no-incremental` **已成功生成 / 0 个错误 / 175 个警告**。新增 `Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs` 断言：驱动可解析＋泵端口与调度器同实例＋驱动已注册且持有同一实例＋无 scope 时附着安全 no-op＋有界停止；`Enqueue → 泵 → ICodeIndexer`（替身计数）闭合；已注册 scope 被挂上变更源。

**变异取红（两组，各三份原始输出）**：A 移除 `ICodeIndexMaintenance` 注册 ⇒ Build 期 `ValidateOnBuild` 失败（`Unable to resolve service for type 'PuddingCodeIndex.Contracts.ICodeIndexMaintenance' while attempting to activate 'PuddingHost.Hosting.CodeIndexMaintenanceHostedService'`）**3 红 / 0 绿**；B 移除驱动器 `AddHostedService` 注册 ⇒ `Assert.Single() Failure: The collection was empty` ＋ 2×`Sequence contains no elements` **3 红 / 0 绿**。复原后 `git hash-object` 与变更前**逐位相同**（`69fd5faf4d41b011c2b4dbb7f423a5e8e86bb71b`／`beaa5eefa33a9ef05092a505b74e01148ac3e8fa`），`MUTATION` 零残留。

**诚实留白**：scope 附着的 workspace id 取自 `<DataRoot>/workspaces/` 下的目录名（平台既有约定，见 `PuddingDataPaths.WorkspaceRoot` 与默认 agent manifest 的 `workspaceId: default`）—— 仓库内**没有**跨 workspace 列举 scope 的契约，故未新增此类 API；持久待办账本（U3-B2b）、按文件增量索引（U3-B3）、`PathsToRemove` 真删除语义、`NeedsReconcile` 自动清除均**不在本刀**。未部署、未提交（改动留在工作区由父代理验收）。

## 2026-09-23 组件化交付规程 S2/S3 首次兑现（PuddingCodeIndexTests）

新建 `Source/PuddingCodeIndexTests/`：索引组件的独立测试工程，**只引用** `PuddingCodeIndex`（`ProjectReference` 恰好 1 条）。从 `PuddingCodeIntelligenceTests` 迁入 `Services/CodeIndex/` 8 文件 + `Storage/SqliteCodeIndexStoreTests.cs`（**55 用例，零丢弃**：守恒等式 `144 = 89 + 55`），另加 3 条机器可验的边界断言（`ComponentBoundaryTests`：探测器自检 / 进程未加载 Roslyn·MSBuild·上层程序集 / `*.deps.json` 依赖闭包）。`PuddingCodeIndex` 的 `InternalsVisibleTo` 由 `PuddingCodeIntelligenceTests` 改为 `PuddingCodeIndexTests`（实测移除后上层测试仍全绿），**未对上层开放任何反向可见性**。门禁：新工程 58/58 exit 0、`PuddingCodeIntelligenceTests` 89/89 exit 0、`PuddingAgent -c Release` exit 0；变异取红 2 组（加 `PuddingCodeIntelligence` 引用 ⇒ 2 条边界断言红、分别列出 5/7 个禁用程序集；`Compile Remove` 一个测试文件 ⇒ 通过数 58→51 而 exit 仍 0），复原后 `git hash-object` 逐位相同（`5505b031…`）、`MUTATION` 大小写敏感 0 残留。见 `Source/PuddingCodeIndexTests/code_map.md`、`Docs/Conventions/组件化交付规程.md` §4/§4.1。

## 2026-09-21 code_map 索引补齐（PuddingTaskRecall.Cli）

按 `code-map-incremental-update` 技能做索引完整性审计：`Source/` 下 19 个生产项目均已有 L2 索引，唯一缺口是生产 CLI `Source/PuddingTaskRecall.Cli/`（有 `.csproj`、无 `code_map.md`，且未登记进顶层目录表）。已按模板补 `Source/PuddingTaskRecall.Cli/code_map.md`（入口 & 配置 / 核心功能 / 结果模型 / 测试）并在 L1 顶层目录表补链接。8 个 `*Tests`/Benchmarks 项目仍无索引（L1 无测试项目区，未擅自新增）。

## 2026-09-21 出站 HTTP User-Agent 统一标识

新增 `PuddingCode.Configuration.PuddingUserAgent`（`PuddingAgent/1.0`）作为出口 UA 单一事实来源：组合根 13 个命名 HttpClient 在各自 `AddHttpClient` 配置中逐点写入 UA（Connectors 6／Platform 6／Runtime 1）；`FlurlWebClient` 为全部搜索/抓取工具出站请求兜底（调用方显式 UA 优先）；`ControllerLlmProxyService` 三处裸 `new HttpClient()` 改经 `CreateHttpClient()`；`GitHubSearchTool` 硬编码改为引用常量。全局兜底方案（`ConfigureHttpClientDefaults` + `DelegatingHandler`）经 A/B 对照实测会使 `PuddingWebApiTests` 产生 48 项回归（失败 6→54，孤立运行亦失败），已回退为逐点注册并在组合根留注释警示。验证：`PuddingHost` 构建 0 error；`PuddingWebApiTests` 172 项 6 失败（均为既有已定性项）／166 通过；`PuddingRuntimeTests` 定向 31/31。提交 `a452eb76`。

## 2026-09-21 Desktop 空闲 CPU 与日志呈现

`IdleDetector` 将 ReArm 的调度续行与空闲状态日志标志分离，同一活动窗口只打印一次；IdleDetector/Heartbeat 18项回归通过，保留周期回调。

`WebView2PresentationGate` 在隐藏/最小化/卸载时解绑 SDK `PART_image.Source`，恢复同一图像，覆盖 Workbench/Agent Browser，不暂停浏览器执行。`RuntimeCenterView` 可见性门控 timer；`RuntimeCenterViewModel.RefreshTransient` 仅通知变化字段；`CoreProcessLogBuffer.GetTail` 缓存未变化文本；Shell/托盘忽略瞬时通知与重复提示。247 项测试通过，真实运行中心 CPU 2.014%→0.098%，设置页1.709%→采样0%；可见工作台仍有绘制成本。见[证据与部署记录](Docs/Reports/Desktop空闲CPU与日志展示修复-2026-09-21.md)。

## 2026-09-21 工具重复参数键故障隔离

`HarnessToolCompatibilityAdapter.GetArgumentValidationError` 按JSON对象递归检查重复属性，规范化保留歧义原文；`ToolInvocationService`/`PuddingToolExecutionService` 拒绝执行并返回 `tool_arguments_duplicate_key`，避免延迟JsonObject物化异常击穿Turn。`HarnessToolCompatibilityAdapterTests` 覆盖5种重复键、合法对象及失败后续行；连同分类器接线/健康/Jev单元回归60项通过。源码验证与Core重新加载分别记录。

部署补充：`1941a2e` 已通过 Desktop 第二次部署，Core PID42796/08:53:26 Ready，托管清单与实际前端匹配；Codex MCP恢复Available/7工具，蜜糖飞书通道因原Agent禁用保持告警，分类器未裁决时unknown。见[部署诊断与验收边界](Docs/Reports/Core重启与夜间代码部署诊断-2026-09-21.md)。

## 2026-09-21 夜间效率问题登记与优化合同

[第一性原理与代码级方案](Docs/Features/夜间效率问题登记与第一性原理优化方案-2026-09-21.md)：N01–N14覆盖测试门禁、RSI评测、执行身份、失败止损、价格对账、记忆/压缩、恢复片段、降噪余项、部署证据、信号来源、Goodput、最终请求与终端等待。优先补充现有任务，保留历史和执行状态；具体TaskId及写后核对见方案末尾登记表。登记不是实施或产品验收。

## 2026-09-21 夜间效率与RSI评估（只读取证）

[评估报告](Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.md)与[聚合数据](Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.metrics.json)：窗口09-20 22:00至09-21 08:09，2410条Gateway用量，DeepSeek缓存98.0698%、GLM93.8195%；DeepSeek静态计价为账单单价两倍，精确窗口按账单价重估¥15.93但非供应商结算。82提交有实质产出，心跳/私有goal.md不等于平台Goal；降噪summary已部署生效，后半夜classifier未加载。RSI优先补suite门禁、执行身份、同目标失败episode与恢复片段评测；本轮无产品/配置/运行数据修改。

## 2026-09-20 计划语义版本与重规划修订分离

`TaskPlanRunEntity.PlanVersion` 是固定编译语义；新增 `PlanRevision` 调度计数，`GoalSettlementStore.TryReplanBoundPlan` 仅增修订号，`GoalQueriesController`/熔断事件分别输出两者。`TaskPlanningSchemaBootstrapper` 幂等补列，`ExecutionCommandReader` 继续严格拒绝旧版/未知语义。`TestScripts/repair-task-plan-semantic-version.py` 是有编译绑定与连续事件证明的停机恢复工具，保留状态/成本/历史。源码 `08b3cbb` 已于21:21经Desktop部署，Core PID36380/Ready；原受影响任务第26轮真实工具调用通过。见[修复与验收记录](Docs/Reports/计划语义版本与重规划修订分离修复-2026-09-20.md)；完整 Task 侧合同重规划仍待实现。

## 2026-09-20 全量任务看板整理与规划实施分工（设计登记）

[施工方案](Docs/Features/任务规划与实施分工及存量看板施工方案-2026-09-20.md)与[逐卡台账](Docs/Reports/任务看板全量整理台账-2026-09-20.md)：全量盘点271条历史/活动记录，合并重复、归档测试卡、保留已完成历史，为有效卡补充代码入口和分层验收。Task侧拟新增ImplementationBrief校验与阶段路由，规划sol/困难决策astra/实施低成本路由均受授权和实际调用门禁。`GoalSettlementStore.TryReplanBoundPlan` 的 `PlanVersion++` 是版本循环直接落点；PlanVersion语义与PlanRevision修订必须分离。这里只登记方案，不代表新增机制已实现或部署。

## 2026-09-19 图片预处理与错误恢复

`VisionRequestPolicy`/`VisualInputRequestBudget`：默认 600 图，统一统计历史/附件/工具输出并分配尺寸和字节预算。`VisualRequestBodyBudget`：DeepSeek 最终 JSON 48 MiB 检查与有界重建。`IVisualArtifactPreprocessor`→`VisualArtifactResolverBridge`→`VisionArtifactStorageService.ResolveForRequestAsync`：保留原图的压缩/缩放缓存。`VisionTextContinuation`：纯文本续聊仅投影历史图片引用；Streaming/Buffered 共享恢复语义，视觉错误不触发 API 熔断。见[实施记录](Docs/Reports/图片请求官方限制与预处理恢复修复-2026-09-19.md)与 ADR-077 §3.2、ADR-088 补充。源码 `adc09ff` 已于 15:08 经 Desktop 受控部署重启，Core PID1608/Ready，托管产物哈希一致；真实模型图片 smoke 待验收。

## 2026-09-17 Goal模式简化设计（待实现）

[权威方案](Docs/Features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md)与[ADR-092第二版](Docs/07架构/106ADR-092目标驱动执行与分层验证闭环ADR.md)：Goal自有持久表、单一状态机/决策入口、回合与检查分离；删除Goal步骤推进/两级Verifier，Task可选适配并统一终态入口。Agent自身goal.md独立，不作Goal运行依赖。本轮仅设计，S1–S4代码落点、迁移和验收见方案。

## 2026-09-17 Goal多轮效率与缓存评估

只读账本核实G92-0 Goal仅以build/test合同完成，Task仍NeedsReview、G92-1 Blocked；15轮270次主调用52.43M输入，缓存98.8814%，业务目标未达成。当天DeepSeek96.7819%、GLM96.0982%；优先补目标合同、事件等待及认领恢复。见[评估报告](Docs/Reports/Goal多轮效率与缓存命中评估-2026-09-17.md)。

## 2026-09-17 DeepSeek缓存首批优化已部署

`ToolExposurePlanner`按首次发现的文件/编辑/代码/终端/Git只读能力稳定成组曝光；`ContextPipeline`偏好使用L9尾部完整快照，支持去重、清空及冷恢复。`UserPreferenceService`只读工作区专用偏好Book，读取失败不伪装空集。92项Runtime+1项宿主回归通过，Core已部署重启。见[实施与验收](Docs/Reports/DeepSeek缓存首批优化与部署-2026-09-17.md)；正式缓存99%验收未完成。

## 2026-09-17 隔夜缓存与参考实现诊断

2550次Gateway usage与归因唯一内容关联：54次工具稳定追加；后16次摘要复用99.29%，checkpoint后首轮18.40%。比较Harness动态快照/摘要重放与Reasonix稳定工具入口/版本上下文，给出Composition、网关、ToolExposurePlanner、压缩和后台的文件级方案。见[完整报告](Docs/Reports/隔夜缓存命中评估与Harness-Reasonix优化方案-2026-09-17.md)及ADR-084；当前仅诊断设计。

## 2026-09-17 GoalResume 宿主生命周期修复

`GoalResumeService`：单例 epoch 台账 + 每次调用 async scope 的 GoalRunStore/IGoalCommandService；产品 Runtime 组合根注册 GoalResumeTool。`GoalResumeServiceTests` 使用严格 DI 生命周期验证，`PuddingApplicationHostCompositionTests` 验证真实 DesktopChild 注册。见 [报告](Docs/Reports/GoalResume依赖生命周期导致Core崩溃修复-2026-09-17.md)。

## 2026-09-16 模型输出上限权威来源

`PuddingFileLlmConfigService` 从资源池模型读取 `MaxOutputTokens`；`AgentRuntimeProfileResolver`、`AgentLLMConfigResolver` 不再按 Agent/角色收紧。Agent 编辑、模板 DTO/manifest/profile 已移除 `maxReplyTokens`。DeepSeek 模板参数及本机验收见 [报告](Docs/Reports/模型输出上限归一与资源池核对-2026-09-16.md)。

## 2026-09-16 缓存命中诊断与修复方案

只读核对Gateway、归因、Composition及分层hash：47次工具变化为稳定追加，用户偏好重写system，四次摘要复用异常尚缺最终请求差异。修复顺序为任务能力包、偏好版本快照、C02最终请求证据、冷启动/后台输入治理。见[完整诊断与验收边界](Docs/Reports/缓存命中诊断与修复方案-2026-09-16.md)，ADR-084及长程自治设计已同步待实施项。

## 2026-09-16 Goal 状态格式与聊天页崩溃

`GoalCommandsController.ToDto` 统一 snake_case 状态；`GoalBanner` 对未知状态安全显示并限制动作。补齐七种 API 状态与额度耗尽/未知值 UI 回归。见[修复与运行验证](Docs/Reports/Goal状态格式与聊天页崩溃修复-2026-09-16.md)。

## 2026-09-16 Desktop/Core 同时退出与独立启动

新增 `TestScripts/start-pudding-desktop-independent.ps1`，使用既有 Explorer 窗口代理启动，核对 Desktop 父进程并拒绝单实例转发。07:28 退出与 Codex 更新重合，缺少新崩溃栈；已恢复并改正开发工具进程归属。见[证据与边界](Docs/Reports/Desktop与Core退出恢复及独立启动-2026-09-16.md)。

## 2026-09-15 Goal 检查器启动依赖修复

PuddingHost 将 DefaultTerminalCommandPolicy 同一 singleton 映射到 ITerminalCommandPolicy 与 ITerminalCommandAdmission，消除 GoalCheckRunner/GoalSettlementWorker 启动验证失败。产品组合根测试覆盖解析及准入拦截。见[运行中心熔断修复](Docs/Reports/Goal检查器依赖注册与Core启动修复-2026-09-15.md)。

## 2026-09-15 产品视觉上下文注册修复

PuddingHost 产品组合根显式注册共享 `FrozenVisionContextAccessor`，让 Agent Streaming 的逐次 MoveNext 快照绑定实际到达 DirectLlmClient；TurnExecutorAdapter 保留 Runtime 的 errorCode。见[故障与验证](Docs/Reports/产品视觉上下文注册修复-2026-09-15.md)。

## 2026-09-15 主代理缓存前缀修复

工具可见顺序在 SessionManager 中跨 dispatch 保留，并从既有 Composition 恢复；稳定规则留在 system，可变工具/技能目录在 User tail 按最新完整正文去重追加。保留权限、压缩及即时发现，65 项定向测试通过；线上 99% 待实际账本验收。见[诊断与修复](Docs/Reports/主代理缓存前缀修复-2026-09-15.md)。

## 2026-09-15 Image Reader 预处理

Image Reader 支持 metadata/read/prepare、detail、缩略图、原图多区域裁剪、旋转、灰度、降噪和编码；统一模型边界准备聊天/历史图片，保留原图。VisionHelper 配置与路由合同已移除。见[能力合同](Docs/Features/ImageReader原生阅读与预处理-2026-09-15.md)及[验证记录](Docs/Reports/ImageReader预处理实施记录-2026-09-15.md)。

## 2026-09-15 原生视觉流式上下文与 Image Reader

每次 LLM MoveNext 重新绑定冻结视觉快照，避免 SSE yield 后 AsyncLocal 丢失；Image Reader 只返回 typed 图片给调用模型，移除 helper 调用与设置入口。见[诊断与验证](Docs/Reports/原生视觉流式上下文与ImageReader修复-2026-09-15.md)。

## 2026-09-15 长消息卡片阅读优化

`ExpandableMessageContent` 为正文/静态过程提供按高度展开预览；`TurnContentStream` 就地收拢较早交错前缀，`ActivityGroup` 初次展示最近6项。保留canonical顺序与完整复制/TTS，手动行为组展开优先。阈值见ADR-079对应实施方案§15，验证与发布见[优化记录](Docs/Reports/长消息卡片阅读优化-2026-09-15.md)。

## 2026-09-20 心跳持久调度与低频登记

`AgentWakeQueue` 原子保存每实例 `state/heartbeat-wake.json`，重启恢复绝对到期时间；`HeartbeatOrchestrator` 全量登记/每分钟目录核对/发送前准入复核，忙碌短延期、成功后接续周期。`HeartbeatPreference.Enabled` 只控制心跳，sleep/agent_status 同步尊重。见[实施与验收](Docs/Reports/心跳持久调度与登记开销修复-2026-09-20.md)。52 项定向测试通过；已部署 Core PID32060/Ready，实机重启后绝对到期时间保持不变，自然心跳执行待到期验收。

## 2026-09-14 心跳失败状态与部署修复

见 [诊断与验证](Docs/Reports/心跳连续失败与状态投影修复-2026-09-14.md)：历史图片进入旧 Responses 文本路由导致 6 轮心跳连续失败；部署当前图片路由处理，ConversationMessageView.TurnOutcome 从 canonical 终态恢复无回复状态，MessageList/MessageRow 显示心跳失败原因；不改持久化结构。

## 2026-09-13 Desktop 启动恢复验证

见 [启动恢复记录](Docs/Reports/Desktop启动恢复验证-2026-09-13.md)：重新启动当前 Desktop 后 Core 约 30 秒进入 Ready，健康检查 HTTP 200；原 60 秒超时未复现，未修改产品代码，根因待失败周期诊断确认。

# PuddingAgent CodeMAP

## 2026-09-15 G92-1 下一行动（源码接线未完成）

见[定向检查与执行指令](Docs/Reports/Goal-G92-1运行链路下一步指令-2026-09-15.md)：4d5980f 已有 G92-0/1 合同与测试代码，尚未运行测试；下一步优先生产 capsule 事实填充、最终 Goal 结算消费 typed disposition、同条件多检查归并、真实 CheckRunner、完成提议与独立 Goal。六项源码提交不等于验收完成；外部指令与 canonical 回执单列。

## 2026-09-15 Goal 目标驱动与真实校验（ADR-092，Proposed）

见[施工方案](Docs/Features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md)、[ADR-092](Docs/07架构/106ADR-092目标驱动执行与分层验证闭环ADR.md)与[任务修订记录](Docs/Reports/Goal持续执行方案与任务修订-2026-09-15.md)。现状入口：GoalVerificationContracts、ConservativeGoalIterationVerifier、GoalSettlementStore/Worker、GoalContinuationWorker、TaskAgentCommandService、WorkUnitAwaitHandleEntity、AgentSleepTool。复用既有 outbox，补真实检查与逐项条件，修正 Turn 正常结束即单元完成、Task 先完成的循环依赖及恢复性 Goal Failed。四张既有任务重写范围；仅设计/派发，未实施或部署。

## 2026-09-15 ADR-091 A91-0 独立复审（needs_changes）

见[复审与下一轮指令](Docs/Reports/ADR-091-A91-0-Review-2026-09-15.md)：精确提交dd575cc定向131/131通过，943270d+6c34a24为123通过/16失败，另10项审计契约用例全部复现。已认可resolver解耦；待修隐式Check/Firewall的Dependency→Denied映射、生产fake开关、JSON语义、ReasonCode/wire传播、测试DI和真实deadline/cancel。A91-0已登记needs_changes并继续推进；未修改运行权限或部署Core，A91-1硬边界/原子许可仍待实施。

## 2026-09-15 自动审计后续设计（ADR-091，Proposed）

见[代码级设计](Docs/Features/自动审计与执行准入闭环设计-2026-09-15.md)、[ADR-091](Docs/07架构/105ADR-091自动审计与执行准入闭环ADR.md)和[阶段卡记录](Docs/Reports/自动审计方案与派发记录-2026-09-15.md)。现状入口：AgentFirewall、Tools/Approval/LlmToolApprovalReviewer、InMemoryToolApprovalService/AuthorizationService、PuddingToolRegistry.cs 内 PuddingToolExecutionService，以及 Web autoReviewClassifier/useAutoReviewClassifier/ChatMain。方案统一执行准入、增量行为审计与外部验证的修复循环；本轮仅文档和任务派发，未修改/部署这些产品代码。

## 2026-09-15 历史压缩状态显示修复（46679d7，已发布静态资源）

见[复查报告](Docs/Reports/压缩频繁复查与历史状态显示修复-2026-09-15.md)：新Core启动后的本轮观察无新compaction事件；useCompaction历史完成使用occurredAt绝对时间，未知不冒充刚刚，终态不被迟到started重新激活，reset清除旧toast。14项前端回归通过，隔离构建并发布148个静态文件；/admin/chat已返回umi.42511e7c.js且hash核对一致，Core PID24908未重启。A2后台无收益抑制仍执行中，不能将UI修复当作长期频次或99%验收。

## 2026-09-15 百万上下文压力压缩（572c394，已部署）

见[修复报告](Docs/Reports/百万上下文频繁压缩修复-2026-09-15.md)：ContextCompactionDefaults共享80%阈值；ContextCompactionService删除128K绝对cap、以CoverageManifest代际时间排除旧usage并优先当前请求；LlmOptions中的ContextUsageSnapshotStore区分实报与估算。141项定向回归通过，Core PID24908、只读context_health真实工具成功，UsedTokens与ProviderTotalTokens一致。按当前1M/384K配置约49.2万输入触发；A2无收益抑制、M01有界索引及长期/99%验收仍待完成。

## 2026-09-14 缓存99优化（部分修复，目标未达）

见 [实测报告](Docs/Reports/缓存99优化与实测-2026-09-14.md)：4680cc5新增HistoryPrefixReconciler及ChatMessage本地SourceContentHash，在ContextWindowManager核对热历史后保留原消息/追加canonical尾部；c0641c1让TurnId和MessageId共同排除当前入站（含空TurnId）；8edab4f移除AgentContextEnvelopeRenderer的JSON缩进。92项Runtime+1项Core回归通过，PID34120已加载。最终冷恢复97.5490%、热续行98.9814%、合计98.2766%；现场仍走既有richer_in_memory_history分支，新增对齐分支的线上命中未验收。C99 v11仍InProgress/C01 v7仍NeedsReview，下一步C02最终请求变化诊断与冷恢复协议差异，不能宣称99%完成。

## 2026-09-14 日志全文召回按需化（已部署）

见 [实施与产品验证](Docs/Reports/日志全文召回按需化与查询边界修复-2026-09-14.md)：fa309ba删除AgentLogRecallService、Runtime/Host注册和ContextPipeline私人日志回退层；95a0cd3由`RawSessionLogService.Fts.cs`负责显式日期/会话范围、短证据与诊断，`FullTextSearchScope`在Lucene TopK前过滤，`QuerySessionLogsTool`标记历史讨论未核实。Core PID26160真实两次调用通过，组装489ms；M01 v5/C03 v3记录部分完成，首轮摘要、通用Memory augmentation、分层快照和归档缺口仍待实施。历史记录与当前轮压缩保护保留。

## 2026-09-14 Memory快照与历史溯源定位纠偏

权威补充 `Docs/Features/Memory快照索引与历史溯源设计-2026-09-14.md` 与ADR-085：Memory是Agent主动维护的当前结论及项目/概念/场景多级索引，正文唯一存放在外部文件/目录或Book/Page；聊天与向量命中只是候选证据，历史按需查看前后文及后续修订。本节记录22:51设计基线，当时仍有自动私人日志召回、首轮整摘要和MemoryLibraryTool少量结果隐式探索；23:12已完成私人日志召回删除及显式FTS收敛，以上方实施记录为准，其余仍待完成。M02升P0/v2先实现最小快照写读，M01/v4再取消默认自动历史注入，C03/v2完善受约束向量/溯源；归档卡bfe2286/v2不再作为默认Memory前置。设计与四张看板已同步，未宣称产品完成。

## 2026-09-14 首轮上下文准备性能修复

`Docs/Reports/首轮上下文准备性能修复-2026-09-14.md`：AgentRunProjectionService 改用有界事件头查询（00bd0af）；ContextPipeline/ContextWindowManager 增加阶段计时（ddc25a2）；LuceneSearchEngine 使用稳定 SHA-256 目录，Host 从 PuddingDataPaths 注入索引路径，AgentLogRecall 每次增量刷新（8ddd0d3）。新 Core PID29400 跨进程复用索引，日志召回12.8秒→243毫秒、上下文13.7秒→683毫秒；相同两工具任务 canonical completed。用户同期恢复LM Studio，因此原87.6秒全量变化不能只归因代码。M01输入减负/99%/长程仍未验收；新增看板bfe2286047da49ec969c11717c46a81c修复归档身份与DataRoot。

## 2026-09-14 默认助手停滞与输入预算接管修复

`Docs/Reports/默认助手停滞修复与预算接管-2026-09-14.md`：Codex 直接修复 FileSearchTool 内置扫描的时间/条目预算与取消（4ce9d6f）、MessageRouter 类型化目标拒绝与 ConversationReplyProjectionWorker 逐项结算（8d2ad3b）、ExecutionUsageBudgetTracker / SubAgentInvocationService / LlmRequestBudgetGuard 的单请求容量和父子累计账本（990673e，PlanVersion=2）、ExecutionRunCoordinator 恢复前检查 pending cancel 并由 SqliteExecutionJournal 原子终态（a18702b）。204 项定向测试通过；新 Core PID25584，旧指令直接取消，真实两次 file_search 后 canonical succeeded。根目录仍可能明确部分覆盖；A2–A4、首轮上下文性能、task-bound 长程与99%缓存仍待验收。

## 2026-09-14 Flash 自修复代码任务书

`Docs/Reports/PuddingAgent自修复代码任务书-2026-09-14.md`：A1 优先分离 WorkUnit 单请求输入容量与累计输入账本，修正父子预算、委派 usage 汇总、零值轴和请求 guard，附 10 组反例及明确交付条件。后续 A2 压缩口径/无收益抑制、A3 无效等待、A4 新 fenced attempt 恢复；指出旧“失败后保留 assignment”建议与活跃执行槽不变量冲突。A1 已由 Codex 接管完成源码与部署验证，其他批次及新计划长程验收仍未完成；不要重复派发旧 A1 指令。

## 2026-09-14 最近 24 小时效率评估

`Docs/Reports/PuddingAgent最近24小时工作效率评估-2026-09-14.md` 与 `PuddingAgent效率指标-2026-09-14.json`：窗口 9/13 19:55–9/14 19:55，627 次模型调用、95.1818% 加权命中；17 次确认心跳 10 成功/7 失败，7 个子 Run 仅 3 completed。通过 canonical 工具回执核对 7 个自有提交（2 源码/5 文档），无新增 task.completed。重点为视觉故障持续失败、累计输入预算截断、审批/探针停滞与交接摘要占轮；仅评估，未改产品或看板。

## 2026-09-13 统一检索与渐进展开设计

U0 独立审阅：`Docs/Reports/ADR-089-U0审阅与返工意见-2026-09-13.md`。`bf6f91e/1f687d7/fe49137/ff79f3b` 落地 RetrievalContracts/RetrievalMatcher 与 SearchGrepTool 改造；Core 16、Runtime 32 项定向通过，补充复现暴露旧索引文本、正则超时丢失/整体预算、读取错误/取消误报 no_match、max_results 合并上限四项问题。U0 评价 needs_changes，S3 与 U1–U5 待交付；不能将已提交片段等同产品完成。

后台维护扩展（详细设计 §10）：U3 `8bddf9017b2d40049f8ea88823c2078a` 增补 FileSystemWatcher→有界 Channel→持久维护账本→后台单 writer，覆盖 in-flight 二次修改、增量提交、失效/删除、完整扫描后 sweep 与退休 generation GC；复用 CodeIndexScheduler/SqliteCodeIndexStore/LuceneSearchEngine，离线/读取异常不触发批量删除。总任务与 U5 同步扩展，仍仅设计。

看板已登记总任务 `b74e561e5299479e9afdd5123f26490d` 与 U0–U5 六张阶段卡（default；P1；自动派发关闭），U0 审阅时为 Ready、其余为 Backlog；完整 Task ID 与依赖映射见下述详细设计 §9，整体源码/部署/产品验收仍待完成。

`Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md`、ADR-089（Proposed）：以 `workspace_search` 聚合 Everything 文件路径、代码符号和内容检索，`workspace_open` 统一读取/Outline/Summary/关系/Map/Status；复用 CodeQueryService、IFileOutlinerRegistry、FileChunkService、Lucene 与托管搜索。按用户范围仅借鉴 tgrep 思路，不引入程序/服务或新 trigram 引擎；U3 为现有查询计划与变更失效整合。施工入口为 FileSearchTool、SearchGrepTool、CodeQueryTools、CodeSummaryTool、SearchAttemptLedger、SmartWorkflow 与工具/模板权限投影。U0 部分源码已落地并待返工，统一入口及后台维护尚未交付，未完成性能/产品验收。

## 2026-09-13 自动压缩频次诊断

`Docs/Reports/上下文频繁压缩诊断-2026-09-13.md`：默认助手 9/12 至 9/13 07:41 共 40 次自动压缩流程、39 次写入；同日复查截至 09:44 的诊断日志累计 45 条 Auto、44 次写入（新增部分未重核 canonical）。raw cap 实际使用整请求估算，Provider 快照采用 max 并混淆实报标签。入口为 `ContextCompactionService.GetHealthAsync`、`ContextUsageSnapshotStore.RecordProviderUsage`、`ContextWindowManager.TryAutoCompactAsync`、`CompactionCoordinator`、Web `useCompaction`；本轮只有诊断/方案，未实施产品变更。

## 2026-09-12 VS Code Desktop 启动入口

`.vscode/launch.json`：Desktop普通/`--background`两种coreclr配置、DesktopHome与仓库环境选项；`.vscode/tasks.json`的`pudding-build-desktop`构建真实Desktop项目并使用标准`bin/Debug/net10.0-windows10.0.17763.0/`输出。DataRoot/Core路径/源码调试模式仍属于desktop.json，不是Desktop CLI参数；说明见 `Docs/07架构/92ADR-078Desktop调试模式源码启动与反向代理ADR.md` §4。

## 2026-09-12 MSBuild 非法历史输出路径修复

`Docs/Reports/MSBuild非法输出路径缓存修复-2026-09-12.md`：PuddingCodeIntelligence/PuddingFullTextIndex 的 `obj/Debug/net10.0/*.csproj.FileListAbsolute.txt` 残留150条含引号的历史输出路径，触发Visual Studio MSB3541。已备份并定点移除，两个项目经VS MSBuild实际构建通过；无需改业务代码或SDK targets。复发诊断与OutDir正确传参见 `How-Debuge.md`。

## 2026-09-12 原生视觉与截图优化入口

`Docs/Features/原生视觉与统一取图截图优化设计-2026-09-12.md`、ADR-088、`Docs/Reports/原生视觉优化看板修订-2026-09-12.md`：当前主力模型文件配置缺 `vision`；Reader 内 Responses/helper 分叉；Resolver 的 Data URI→Planner 解码上传；旧 384 图片估计；WebView2/RemoteBrowserPage ScreenshotAsync Unsupported。按 V5–V10 收敛能力/预算、Reader、Provider 流式传输，补齐 Web/Desktop 采集与共享主子代理图片轨迹。ADR-077 V0–V3 已有实现，本次仅新增设计/看板；V4 真实新构建验收待完成。

## 2026-09-12 子代理弹性与双向交互修订

`Docs/Features/子代理弹性预算与双向交互设计-2026-09-12.md`、ADR-087、`Docs/Reports/子代理弹性交互看板修订-2026-09-12.md`：600改为可选实例，任意合法正整数预算；系统管理生命周期；query_sub_agents共享快照、send_message主子双向/插嘴、ask_question持久等待120秒与StopRun；Web轨迹空白先修canonical事件/回放，检查器改善发现/状态/行为/操作。复用Message Fabric/AwaitHandle/Steering/Cancellation/SubAgent投影，不建平行生命周期系统；本轮仅设计/看板。


## 2026-09-12 下一阶段设计：缓存99、Memory长程自治与600轮纠偏

权威方案：`Docs/Features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md`；ADR-084/085/086；交付包与13张看板映射：`Docs/Reports/PuddingAgent-Next-Phase-2026-09-12/README.md`。重点入口为SubAgentManager/TaskExecutionPlanCompiler预算二次截断、ContextPipeline/AgentMemorySummaryContextBuilder首轮装配、SubconsciousRecallPipeline检索与后台miss、C01/C02最终请求、ToolInvocationService与legacy执行分支。预算纠偏（N00，commit f096bc5）已实施：子代理/WorkUnit 支持任意合法正整数轮次（8/32/600/1200/10000…），未填由系统 profile 决定，600 仅为 profile 默认示例；已删除 32/40/120 下压与强抬 600 双向补丁。缓存99/Memory/30日等其余新目标仍待实施/验收。


## 2026-09-12 抖音与 WebView2 续建入口

`Docs/Reports/DouyinCreatorTools-WebView2复用与看板方案-2026-09-12.md`：外部仓库 e35dbe2 源码调研及 DY-00/01/02 验收、只读适配、可靠回复拆分。现有实现入口为 `Source/PuddingBrowser.AgentTools`、`Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs`、`Source/PuddingDesktop/Browser/BrowserBridgeCommandDispatcher.cs`、`Source/PuddingBrowser.WebView2/WebView2DomClient.cs`。七工具已存在，Evaluate/CDP 等仍 Unsupported；本轮仅文档/看板更新，Douyin 业务尚待实施。

> 顶层快速索引 | 2026-09-12 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 2026-09-12 GLM 进度复核与看板同步

后续运行审计入口：`Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md`及同目录`02-任务看板登记与实施顺序.md`。8心跳/12子Run/898请求；gateway与归因投影分开、token加权命中95.48%。主要源码定位：`MessageDeliveryDispatcher` recovery→`AgentInvocationDispatchFactory`的msg会话回退；`AgentExecutionService.Buffered`结构化文本工具路径与native路径审计不一致（F01 41工具仅2归档）；`TerminalProcessManager`输出/退出并发；`SubconsciousJobQueue`两表重复schedule_skip。方案为父级身份+canonical接续、HeartbeatOutcome/WorkUnit、统一工具审计、错误家族熔断与自修复/外部部署证据闭环。窗口外`c89920f`已提交C01-A、`b0cfa3a`已接Authorization入口；以下旧“WIP”是前轮时点，当前转为待独立验收与明确新构建验证。

当前入口：`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/06-实施进度复核与看板状态修订-2026-09-12.md`。`19ec137/9537f60/33c1489/15842ff` 已提交；原10探针转绿，前端28、Platform48、Runtime40定向通过。F01 hook恢复/失败/重试无真实消费者，S01-B invocation/attempt/provenance持久化和本地故障恢复待补；C01-A有未提交WIP待验收，C01-B/C02仍待完成。已修订7张描述、5张状态为NeedsReview；S01-A-R/T01-R源码accepted，不等于Completed或产品验收。C02保持Backlog，总卡InProgress；回执在同包`audit-evidence/2026-09-12/`。

## 2026-09-11 GLM 批次1独立审计

历史结论见 `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/04-批次1独立审计与下一步.md`；当时任务 ID/回执见同目录 `05-后续任务与看板回执.md`。9月11日既有91项通过、TypeScript/入口构建通过，新增10个边界探针失败；这些探针已在9月12日转绿，最新剩余工作以06为准。A01删除与编译层面通过，未做本批生产验收。

## 2026-09-11 GLM 优化批次1实现入口（原交付记录）

`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/` 的 15 个工作包中，批次1已有以下实现；完成状态以以上独立审计为准：

- **F01 明细水合**：新增 `Source/PuddingPlatformAdmin/src/pages/chat/runtime/detailHydrationScheduler.ts`（页面级 capacity=2 总并发、稳定请求 key、generation/owner token、AbortController、僵尸占位、401/404/transient 分类与有界退避）；`useTurnSurfaceStore` 改为订阅衔接，`registerVisibleTurn` 配对 `unregisterVisibleTurn`（引用计数），MessageRow→MessageList→ChatMain→ChatLayout→index 全链路透传 `onTurnInvisible`；MessageRow 视口观察不再首次相交即 disconnect。测试：`detailHydrationScheduler.test.ts` 7/7、`turnSurfaceStore.hydration.test.ts` 7/7（含 8 可见×20 重渲染并发≤2、A→B→A、迟到回调、离视口剪枝、401 停止、unmount）、`MessageRow.focus.test.tsx` 9/9。
- **S01-A usage 并发**：`TokenUsageRecorder.RecordCoreAsync` 改为 BEGIN IMMEDIATE 单写事务（明细幂等读 + 月度聚合读改写同事务，decimal 语义不变；同 source 异 payload 抛冲突，best-effort 路径吞掉），新增 `Services/UsageWriteConflict.cs` 唯一索引兜底；`LlmGatewayUsageRecorder` SaveChanges 捕获唯一冲突按幂等成功。顺手收尾了 dirty 树遗留的 `occurredAtUtc` 半成品重构（该文件此前无法编译）。测试：`TokenUsageRecorderConcurrencyTests` 5/5（50 并发对齐、重复 source 计一次、冲突拒绝、best-effort 跳过），usage 相关回归 39/39。
- **T01 记忆工具**：`SaveMemoryTool` important 分支身份改由 `ToolExecutionContext.AgentInstanceId` 派生（原 root 读取的 agent_instance_id 不可达），upsert 增加 preference 必 key / fact 必 content 前置校验（空 content 由旧“警告后照写”改为 fail-closed 零写入，`MemoryToolsTests` 对应用例同步更新）。测试：`SaveMemoryToolContractTests` + MemoryToolsTests 共 27/27。
- **A01 组合根**：删除 `Source/PuddingAgent/Services/` 两个旧服务注册副本（msbuild Compile 由 3 项减至 Program.cs 1 项，入口构建 0 错误）；`Docs/架构.md` 开头标注 Desktop + Core 当前形态、单进程 P2P 叙述移为历史背景；PuddingHost.Tests 组合守卫通过，Desktop 不引用 Host。

以上为首批历史记录。批次2+最新状态以同目录06为准：S01-B首片已提交、C01-A WIP待验收，其余未验收工作仍沿01/02设计推进。

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `Docs/Reports/GLM前端首批交互审计与下一步-2026-09-11.md` | GLM 首批 Chat 交互独立审计；草稿、重复 Steering、排队目标、图片门禁、受理恢复、停止与队列回执共 7 个契约反例；needs_changes，附看板所有权与产品验收门禁 |
| `Docs/Reports/前端交互体验优化建议-2026-09-11.md` | 当前前端交互评估；发送/排队/停止一致性、可信反馈、阅读与草稿连续性、导航/交付物衔接和视觉规则；第一批（鼠标排队/补充当前任务/独立停止+服务端取消接线/失败保留草稿/操作回执/键盘可达）已于 2026-09-11 实施，第二批及以后仍为 Proposed |
| `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/01-代码审阅与优化设计.md` | 原始15包设计和hash基线；同目录06为最新进度复核与看板状态，03–05保留各阶段历史；先补F01/S01-B，验收C01-A后推进C01-B/C02 |
| `README.md` / `README_zh-CN.md` | 中英文产品与目标架构入口；Windows Desktop/Core 产品边界、Plugin/Function/Hook/Event/Projection 五类合同、Agent FSM、函数图编排、前端思想、现状缺口与路线 |
| `Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md` | 工作区 TODO 台账、Agent 认领/拒绝/回报、durable 自动派发与定时消息、可信 idle、心跳 0、峰谷 WorkAdmissionFence，以及 Hook 触发的临时质询子代理、GoalRun 有界循环、manifest/Admin 模型路由、防无限循环熔断和公共 Plugin/Function/Event/Projection 映射 |
| `Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md` | `/goal` 完整专项设计；统一 Web/Desktop/Connector 命令、持久 GoalRun、事件驱动 continuation、256 个外层 Goal Iteration、证据 Verifier、用户抢占、重启停用、自动压缩和 Task-bound Goal；明确不依赖 Heartbeat |
| `Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md` | 低峰自动执行施工图；2026-08-29 已由 Desktop 构建/加载新程序集并完成真实自动派发 smoke：同步后代 Token 预算传播、Task-bound Failed 释放和 stale Message target 淘汰已验证；warm cache 97.31%（DeepSeek 98.78%）仍低于 >99%，事件驱动 intent、Goal 成本/后代工具归因、AwaitHandle/checkpoint、动态模型反馈与 7 夜验收仍未完成 |
| `Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md` | 2026-08-31 夜间调度复盘的代码级收口方案；把有效调度冻结为 Intent→Decision/Outcome→Assignment/Reservation→TaskGoalBinding/GoalRun→ExecutionRun→Verifier/Task terminal，并细化 legacy false-busy 修复、事件 Intent 可靠结算、staged mode、scan-run 持久化、Blocked 恢复 UI、Runtime 预算与无人工 smoke 的实施顺序和验收门禁 |
| `Docs/deepseek-reference-architecture-master-plan-2026-08-14.md` | 本次会话的 deepseek-harness/pi 参考架构总蓝图；以“一切业务能力皆插件”为第一原则，覆盖 Model/Tool/Skill/Session/Agent Loop/Sandbox/Storage/Schedule/UI、统一运行事实、文件级改造矩阵、任务图与 T00-T16 施工步骤 |
| `Docs/07架构/67ADR-066*.md` | Browser 能力与 Douyin 分层决策 |
| `Docs/07架构/68*.md` | WebView2 自动化分阶段实施规格 |
| `Docs/07架构/69*.md` | Desktop 浏览器工作区/运行中心/存储 |
| `Docs/07架构/70–73*.md` | Phase 2A-1 Bridge 与双标签工作区（✅） |
| `Docs/07架构/74*.md` | Phase 2A-2 Remote Browser + Agent Tools（✅） |
| `Docs/07架构/75–76*.md` | Phase 2A-3 Snapshot/Locator/Interact/Wait（✅） |
| `Docs/07架构/77–79*.md` | Phase 2A-3B/C DeepSeek 验收与闭环 |
| `Docs/07架构/80ADR-069*.md` | MOA 子代理设计委员会编排核心；Phase 1–3 计划编译、纯状态机与运行时适配 |
| `Docs/07架构/81ADR-070*.md` | 通用 Agent 编排图；V2 组件/多模态端口、SQLite 事实、Graph/Run 发现、Revision/Layout 双 CAS、replay-to-live SSE，以及 React Flow 节点/端口/Edge/Graph Input 编辑器 |
| `Docs/07架构/82ADR-071*.md` | 通用 Agent 编排平台完整目标设计；JSON 图、Revision/Layout/Deployment/Run 事实边界、Agent/Tool/Graph 统一 Function、不可变图生成流程、有界循环、多模态、Agent 工具与 MOA 统一 |
| `Docs/07架构/83*.md` | 后端执行内核与 Control Plane 施工图；契约、SQLite、API、状态转换、Function Runtime/Invoker、Typed Hook Pipeline、Parent/Child Run、Outbox、Scheduler、Trigger 与权限 |
| `Docs/07架构/84*.md` | Admin 蓝图编辑器和组件系统施工图；Node/Edge/Input/Trigger、Revision/Deployment/Run、多模态 UX、Pudding 视觉语言、原因优先状态、Function Catalog、插件 Presentation 与系统构成检查器 |
| `Docs/07架构/85*.md` | 分期交付、测试、安全、性能、Desktop 部署、浏览器 smoke、恢复与验收证据图册 |
| `Docs/07架构/86ADR-072*.md` | 工作区 TODO 第一阶段任务领域 ADR；覆盖五列 Board、Task Failed/Reopen、Task Ledger、手工/Auto 派发、受限 Cron/Message Event、Agent Availability、Task executionWindow 与 provider/model 价格时段 Resolver；完整 Auto 受 Goal 前置约束，不新增 `work-policy.json` |
| `Docs/07架构/87ADR-073*.md` | 当前产品施工总表与冲突裁决基线；列出 30 项产品任务、17 项 T00–T16 平台底座任务及专项 Phase 去重映射，覆盖目标、优先级、工作量、难度、依赖、设计位置和里程碑 |
| `Docs/07架构/89ADR-074*.md` | Goal 专项架构决策；冻结外层 GoalRun/内层 Agent Loop 双层预算、256 accepted Iteration、durable outbox、证据验证、Task-bound Goal、Availability 与低峰派发；2026-08-26 G2/G3 和 Task-bound authoritative 源码链已落但默认关闭，真实低峰/完整 Verifier/Admin/进程外门禁未通过，ADR 仍为 Proposed |
| `Docs/07架构/90ADR-075*.md` / `Docs/Features/第三方任务看板AccessToken与外部API详细设计方案.md` | 第三方任务看板开发合同；冻结 hashed opaque Access Token、ASP.NET Core 独立 scheme + scope/workspace Policy、外部 API v1、ETag/幂等、追加式 TaskEvaluation 与 Admin Access Token 管理器；P1（Token 后端）+ P3（Admin UI）+ P2 基本功能已实现：`pdt_v1_` opaque Token 摘要存储、PuddingExternalAccessToken scheme、Admin 管理 API/UI、last-used 合并写、External Task API v1（list/get/create/patch/comments/evaluations/commands + ETag/428/412 + 简化幂等）共 65 项后端测试；SSE Watch/RateLimiter/OpenAPI 与 P4（部署收口）未实现，External API 默认关闭 |
| `Docs/07架构/96ADR-082*.md` / `Docs/Features/Pudding外部工作空间Agent消息API设计与使用说明.md` | External API v1 的 Workspace/Agent/消息扩展；新增 `workspaces.read`、`agents.read`、`messages.send`，安全目录投影、`canonical_turn` Message Fabric ingress、幂等 `202 + Location` 和 Token-owned execution receipt；明确 Delivery accepted 不等于 Agent terminal。External/Token 7/7 + Dispatcher 2/2 聚焦测试与 Desktop Loopback 真实模型 smoke 已通过；非 Loopback HTTPS、RateLimiter/OpenAPI/P4 运维收口待完成 |
| `Docs/07架构/97ADR-083*.md` / `Docs/Features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md` | Agent 系统预制模板 v2 目标设计；目录包、完整 Creation Snapshot、版本/内容哈希/许可来源、显式导入升级与 drift 保护，Workspace 创建时选择模板即原子填充全部六组配置；重写通用助手并新增原创文本的 `deepseek-whalechan` 社区角色预制，既有 Agent 不被模板更新反向覆盖。当前仅设计完成，未实施或产品验收 |
| `Docs/07架构/91ADR-076*.md` / `Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md` | 遥测/Debug 存储治理设计 + 首轮实现（Phase 0–3 已落地：语义目录/快照估算/单 writer 协调器/语义 API/Web /storage 页面；Phase 4 生产验收待做）；上下文日聚合复用既有 `context_layer_daily_rollups`、retention 索引收编目录所有权、旧 /databases 端点与 Desktop 旧页面捆绑退役、appsettings Retention 节已迁移 system.json |
| `Docs/07架构/92ADR-077*.md` | 原生视觉基础：typed parts、Workspace Artifact、Files API、多轮恢复；V0–V3 已有实现，V4 真实新构建验收待做。后续 ADR-088 收敛 Reader/能力/图片预算/流式传输并补齐 Web/Desktop 截图，自动 helper 移除及显式通用子代理第二意见仍待实施 |
| `Docs/Features/Chat图片消息回放与前端旧Bundle缓存修复方案.md` | 2026-08-26 Chat 图片占位事故的可施工修复方案；冻结 Agent-first `contentParts` 透传、typed parts 优先兼容、localhost 旧 Service Worker 清理、入口/哈希资源缓存合同、build identity 和两段式产品验收；关联 P1 Task `ceba781342aa4353901654d1897092cb`，尚未实施 |
| `Docs/Features/子代理活动轨迹实时回放与运行检查器修复方案.md` | 2026-08-26 子代理检查器空时间线事故的证据化施工方案；活动 Run 继续零 archive 轮询，改由 Conversation SSE + active-subagent gap replay + 可对账状态水位恢复；修正有界工具详情导致的聚合少计、增加轨迹同步降级与 build identity 门禁；关联 P1 Task `791d062fa6ea44f18bfe5027a37696d0`，尚未实施 |
| `Docs/Reports/Core与DesktopCPU占用现场采样-2026-09-19.md` | CPU 只读采样：Core 均值 0.169%，Desktop 均值 2.727%；Desktop 热点线程映射 WPF 图形模块，Core 截图峰值未复现，待高占用窗口原生/托管联合采样 |
| `Docs/Reports/小型布局子代理耗时与进度失真诊断-2026-09-19.md` | 26 分 31 秒布局 Run 的只读诊断：83 次模型调用、6 次 Jest 启动、宽松委派预算；531 条 canonical 事件已落库但截图仍为启动/零指标；区分执行低效与活动投影缺失，未修改产品代码 |
| `Docs/07架构/tool-infrastructure-layering.md` | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | 对照 deepseek-harness 的消息、推理和工具调用 UI 目标架构；定义 TurnStatus、Reasoning/Tool/Delegation 行、toolCallId 投影、分期与验收矩阵 |
| `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md` | 聊天前端「行为链 + 质感」升级：harness 质感纪律与 Hermes/业界 12 原则调研、四档灰阶 token、交错时间线（路径 A/B 统一 ViewModel）、五类 presentation renderer 设计与实施记录 |
| `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` | Flash 代码级施工合同：canonical sequence、TextBlock ⇄ ActivityGroup、会话级唯一最新披露 owner、完整 reasoning、工具详情懒加载、柔和收起/卸载、逐文件任务卡、测试命令和双阶段验收 |
| `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md` | 冻结 AgentTurnCard 单一有序内容流与唯一正文源；当前最新 Agent 回合的最后行为组持续展开，最终正文不关闭，新行为/新回合转移 owner 并柔和收起旧组 |
| `Docs/07架构/94ADR-080任务看板分层读取子任务与命令化拖拽ADR.md` / `Docs/Features/任务看板状态机子任务渐进披露与高性能拖拽优化设计方案.md` | 任务看板下一阶段 Proposed 设计：Ready 证据化直达 Completed、单层独立状态子任务、普通 List/工具仅 id+title、Index/Card/Detail 三层投影、评论/备注分型、命令化拖拽、global-cursor Watch 修复与 10k 任务性能门禁；尚未实现或验收 |
| `Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md` / `Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md` | 模型后训练 Harness 适配；canonical 工具保持唯一，`rg/exec_command/write_stdin/apply_patch/pwsh` 在统一门禁前归一化，WSL 作为显式 Unix 通道，搜索 no-match 与真实失败分离，完整普通文本五段报告同轮收口；`BuiltInAgentTemplates` 单一权威，Low 投影保留读取/搜索/`search_tools`；动态定义在下一 LLM round 单调生效，连续 8 次 discovery-only 触发 `tool_discovery_stalled`；Token 归因使用 RuntimeExecutionIdentity；聚合报表和部署 smoke 待完成 |
| `Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md` / `Docs/superpowers/specs/2026-06-06-runtime-steering-queue-design.md` | current-Turn Steering + 无人值守队列合同：普通消息立即进 canonical Turn；队列只含未认领 delivery/Turn，认领后由消息卡与轨迹接管；Agent/heartbeat delivery 受理为 canonical Turn；独立 `⚡` 复用 Steering admission。源码已实施，产品进程重启/smoke 待做 |
| `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md` | 对照 deepseek-harness 的工具定义与执行协议；规划 canonical output、端到端 callId、结构化错误、管线、并发、spill、可回放 presentation 与 DeepSeek Code Mode |
| `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` | 对照 deepseek-harness 与 pi 的统一目标架构与 2026-08-15 复评；定义 Plugin/Function/Hook/Event/Projection、Agent Transition+Effect FSM、Function Graph、Composition Snapshot、前端解释层、底座缺口与分期路线 |
| `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md` | 7 日 Token 构成、工具结果重放、搜索失败和 ZIP 稀疏度基线；2026-08-28 登记 95.92% 事故基线、Harness 对齐的 warm-prefix checkpoint、prefix-v2 历史锚点、LLM purpose 分桶、审批控制面降耗，以及 DeepSeek 连续 7 日严格 >99% 验收 |
| `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md` | 聊天页主代理余额徽标 + 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器（DeepSeek `/user/balance` 首个落地，`/v1` 剥离修复）+ 前端 `providerBilling.ts` 展示适配器；新服务商扩展步骤、5min 低频轮询/手动刷新、apiKey 不进日志约束 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `Directory.Build.props` | 全项目通用 .NET 构建属性；SDK 默认项统一排除项目内 `temp/**`、`tmp/**`，避免测试/构建输出递归自复制并触发 Windows 超长路径评估失败 |
| `dev-up.py` | 本地开发监督器；Codex MCP 子进程必须通过 5100 TCP readiness 后才启动 Backend，避免 MCP workspace reconciliation 启动竞态 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs · Console/DesktopChild 薄壳) | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线；压缩与冷水合共享 canonical ChatMessages 增量同步门禁 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · 回环鉴权控制面（Core/前端制品加载、重启、诊断）· Core 点火事务部署/程序集哈希验收 · Browser 工作区 · 调试模式与运行中心 · 客户端精灵源素材 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingHost/` | 🔑 组合根 · 全网卡 HTTP/本机控制地址 · Browser Bridge · 飞书连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingCore/` | 🔑 抽象与契约 · 接口 · 模型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · API（含认证/当前用户投影）· EF Core · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter · FTS5 · 潜意识 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 网关适配 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 Browser Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约 | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议（8 .cs） | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 代码索引/分析 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 20 工具（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端 · Chat 虚拟视口/渐进消息/状态缓存 · Agent 编排布局编辑器 · 管理壳异步隔离 · 主代理服务商余额徽标（DeepSeek 首个，多服务商计费展示适配器） · 已移除 Phaser/2D Studio · 生产 dist 经 PuddingHostContent.props 部署到 Core `wwwroot/admin`（dev 输出分流 dist-dev，防 MSBuild 增量清理破坏部署，见 How-Debuge §6.12） | [code_map](Source/PuddingPlatformAdmin/code_map.md) |
| `Source/PuddingTaskRecall.Cli/` | 历史脏数据一次性诊断/修复 CLI（默认 dry-run；`--apply` 才写库，写前备份 + 单事务回滚） | [code_map](Source/PuddingTaskRecall.Cli/code_map.md) |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → dispatch 冻结授权 catalog/schema；search_tools 结果只在下一 LLM round 单调增加 visible definitions
  → 连续 8 次 discovery-only（查询换词也同族）→ tool_discovery_stalled，禁止高缓存命中零 Goodput 空转
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)

Agent Loop → LlmInvocationService → DirectLlmClient
  → model.protocol=openai → OpenAiLlmGateway (/chat/completions)
  → model.protocol=responses → ResponsesLlmGateway (/responses；DeepSeek reasoning_text + incomplete/length 终态兼容)
  → model.protocol=anthropic → AnthropicMessagesLlmGateway (/messages)
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议
  → Provider usage → ILlmGatewayUsageRecorder → llm_gateway_usage_events
    → StatsApiController（月度/趋势本地计费口径）
    → TokenUsageDailyAggregateService / ContextLayerDailyRollupService
      （闭日 UTC 聚合缓存 llm_usage_daily_aggregates / context_layer_daily_rollups +
        stats_daily_cache_days 完成标记；当天实时计算，Rebuild 后按月失效）
    → TokenUsageEvents 继续只承担会话/角色/上下文归因

TaskAutoDispatchWorker（5min bounded authoritative；MaxStartsPerScan=2）
  → AgentAvailabilityProjectionStore（每轮先刷新全部 Agent；即使候选为 0 也输出 idle/busy/unknown）
  → BacklogRefinementEvaluator/Store（显式 autoDispatchEnabled；结构化准入后 CAS Backlog→Ready）
  → TaskAutoDispatchEvaluator（Ready/Deferred；结构化类型/能力/模型路由 + Availability/Window）
  → ProviderModelExecutionWindowResolver（llm.providers.json 版本化价格窗口；未知 fail closed）
  → TaskExecutionPlanCompiler（结构化 Task → 版本化 WorkUnit DAG + budget/scope/dependency SHA-256）
  → TaskExecutionTracker（active Binding；Task→Plan/WorkUnit→Assignment→Reservation→Goal→Iteration→Execution→Outbox 五态跟踪）
  → GoalContinuationWorker / ConversationAcceptanceStore（canonical plan/node/fingerprint + reservation 二次围栏；首个 WorkUnit 原子 Running）
  → ExecutionCommandReader / ExecutionRunCoordinator（执行前重读 Command→Goal→Binding→Plan/Node；Agent 与 WorkUnit rounds/tools/duration 取更严格值）
  → 每 Agent 每轮最多一个、全局最多两个；任何 promotion/start/repair 都必须经唯一 CAS/fencing 写入者

Admin ChatMain 余额徽标 → useProviderBalance (5min 轮询/手动刷新)
  → GET /api/llm/providers/{id}/balance → LlmProviderApiController.GetBalance
    → LlmProviderFileService.GetBalanceAsync（解析 apiKey：ApiKey/${ENV}/{{vault}}/ApiKeyRef）
      → ILlmBalanceProvider 注册表 CanHandle 分发（DeepSeek: {baseUrl 去 /v1}/user/balance）
      → 未注册适配器 → IsAvailable=false「暂不支持」DTO（前端隐藏/显示 —）

ContextPipeline → Tool layer mandatory delegation policy
  → 首次工具调用前必须判定 Direct / Delegated
  → 复杂任务前三次工具调用内必须进入匹配 smart_* 或 spawn_sub_agent
  → SmartWorkflowToolBase 将历史 question/what/query 仅在执行边界归一为 task
  → smart_explore 统一替代已退役的 smart_search / smart_query_session_log

Terminal 长命令能耗协议（2026-08-22）
  → terminal_wait 阻塞语义：等到任务退出或输出超过预览上限才返回，wait_seconds 0-600 默认 60
  → 工具描述/NextAction/ToolLoopInstruction/Smart 提示词统一引导"一次阻塞等待"，禁止 1-2 秒式轮询
  → 动机：旧"出现新输出即返回"语义在全库产生 6,040 个纯轮询轮 ≈ 8.26 亿 tokens（16.3%）

上下文注入冗余治理（2026-08-22，指令层曾占每次调用 67%）
  → 工具描述单语化：41 文件去除英文复述（-13K 字符）；使用教学入 skill 文档，schema 只留必要说明
  → search_tools 装载收紧：默认 3/上限 8（原 8/20），阻止长会话工具集棘轮到 50+（主会话曾 34.6K schema tokens/轮）
  → L1-TOOLS 索引补延迟工具名清单（仅 id 无 schema），Agent 不再盲搜 search_tools
  → L2-SKILLS 索引行压缩：skillId + 首句摘要(≤100字) + tags≤4/keywords≤6，去掉 Name/版本/path；
    主 Agent 57 技能的索引从 29K 字符/轮显著缩减，全文仍由 agent_skill 渐进加载
  → 前缀稳定性结论：分层排序已正确（稳→动）；L9-INBOUND/L6-AGENT-LOG-RECALL 变化属尾部动态层，
    缓存损伤被限制在其自身与 <1K 尾巴，无需整改

缓存命中率冲刺（2026-08-25，基线 8/22-24 DeepSeek 96.783% → 目标 >99%，设计方案 §1.2）
  → 有界冷启动重组：ContextCompactionOptions.MaxHydrationTokenBudget(49152,0=禁用) 钳制重水合
    预算（DB/JSONL 双路径）；摘要链优先占预算，JSONL 胜出时补拼（原会静默丢摘要）
  → 滚动摘要链：压缩候选纳入旧代 compact_summary，新摘要统一标记 CompactedBy；
    CompactionCoverageFilter 改全部 manifest 并集（修多代 JSONL 复活缺口）
  → 转录连续性：每次压缩和 memory DB 冷水合前从 platform ChatMessages 按稳定 MessageId + durable platform Id
    高水位 after-Id 升序分页（256 条）镜像当前 session 到 memory Messages，不全扫会话/既有 ID；同步失败时水合 fail-closed，
    当前 turn/message 从历史水合排除，pre-projection live 历史不被 DB 覆盖；压缩候选包含当前围栏 Turn 或最后一个未围栏 user 时，
    在摘要和 DB 写入前以 current_turn_in_compaction_scope fail-closed；自动压缩后只合并完整 hash 围栏的当前 live Turn
    （禁止 active.Count==0 门禁）；summary-only（含超大旧摘要）/无可压缩原文直接 no-op，
    result/diagnostics compacted count 均为 0；补读排除 CompactedBy!=null 原文（阻止失忆/套娃）
  → 绝对窗口 proactive 压缩：MaxActiveRawTokenBudget(131072) 与 0.65 比例 OR 触发
    （大窗口模型旧阈值 16 万 token 才压缩）；Streaming 路径补齐轮内软压缩
  → 前缀字节稳定：ToolLoopInstruction 可见集清单（原全注册表）；ContextAssemblyService
    首组装透传 LoadedToolIds/Capability（灭 turn1→turn2 必变）；L0-AGENTS-ROSTER session 冻结
  → 归因卫生：vision-helper:/subconscious: sessionId 命名空间；image_reader 委派稳定 system 前缀
    + (artifact,prompt) 观察缓存；ConversationProjector usage 指纹查重（灭双计 NULL 桶）；
    session_rehydrated 显式归因；会话默认驻留 1h→4h
  → 验收：TestScripts/deepseek-cache-e2e.py 保持任务/模型/工具不变执行双轮真实 DeepSeek 探针，TestScripts/deepseek-cache-hitrate.py 生成日报；连续 7 完整自然日 >99%（§15.3）

自动权限审查（唯一任务 e187a8bbd2d640bb87b96fd3cf548966；ce63f8c0 已合并）
  → ToolApprovalCommandFirewall：引号/括号感知解析 PowerShell/POSIX pipeline、正则 pipe、2>&1、变量赋值；
    已知只读/构建/测试命令秒放，危险命令秒拒，绝对输出/调用运算符/子表达式等未知形态继续 LLM 审批；
    provider usage 以 purpose=approval 独立计费归因
  → feature/auto-approval-v2 的 e716829 已实现 Gate1 静态分级 / Gate2 事实自检 / Gate3 单次 LLM，62/62 测试通过，但尚未合入/部署且 AgentFirewall 仍传 Evidence=null
  → 参数级风险：save_memory get=L0、upsert/set_important=L1、delete=L2；风险事实由 descriptor+实际参数+系统证据派生，Agent 不能用 may_damage_or_delete_data=false 降级
  → 用户审批降为最后手段：L0/L1 无感放行，StaticDeny 不可覆盖，Challenge 只反馈 Agent，仅 HumanRequired 弹一次审批；相同 args/evidence 重复拒绝触发 approval_loop_detected
  → 配置由程序默认 + <DataRoot>/config/system.json ToolReview 覆盖，review profile 走 llm_resource_pool；分阶段部署/启用/下线旧 audit-agent 与默认工单入口
  → 权威设计：Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md

工具模型倾向适配（2026-08-22，实测子代理调用链驱动）
  → shell 输出去 ANSI：pwsh 注入 $PSStyle.OutputRendering='PlainText' + NO_COLOR=1 + 输出侧正则剥离兜底
  → 探查命令返回值教育：Get-ChildItem/Select-String/Get-Content 等成功输出尾附专用工具提示（94.8% shell 曾是探查类）
  → Codex 补丁格式自动转译：UnifiedDiffParser 识别 *** Begin Patch 并转 unified diff（内容匹配定位，行号占位安全）
  → file_read 护栏窗口 120→400 行（小文件与大文件双路径），减少同文件翻页重读（实测同文件重读 8 次）

ContextPipeline → stable system prefix + volatile User tail
  → 当前消息、日期、召回与 inbound context 不再插入 system prompt
  → AgentExecutionService 用 `[CURRENT USER TURN input_sha256=…]` 围住本轮文本与 typed ContentParts；若预算裁剪/投影后围栏缺失，Buffered/Streaming 在 provider 调用前 fail-closed
  → AgentExecutionService → ToolResultContextPolicy（模型历史最多 8 KiB；原始完整结果写入工作区 `.pudding/context-tool-results`，不做模型输入脱敏）
  → search_tools 已发现 schema 在 live session 内保持加载，避免跨 dispatch 重复收缩/扩张

用户 Turn → TurnExecutorAdapter → AgentExecutionAdmissionCoordinator（foreground）
  → 抢占同 workspace/agent 的 Message Fabric 后台执行（含 subagent_result）
  → MessageDeliveryDispatcher 取消旧执行并把 exact delivery 立即 defer 回队列
  → foreground demand 存续期间 recovery/idle drain 不领取后台 delivery
  → MessageFabricStore 依据 wake event deliveryId 精确 claim，避免旧队首抢在用户事件前执行

Agent `send_message` → MessageDeliveryPolicy → Message Fabric 反风暴消费
  → 默认 `inform/report_result` 为 `notify`：不创建 Turn、不调用模型；`ask/request_review/delegate` 才为 `execute`
  → `agent_reply` 永远被动，ConversationReplyProjectionWorker 最多投影一次，切断 A→B→A 自动回声
  → MessageDeliveryDispatcher 按 workspace/Agent 跨 room 原子领取最多 20 条 notify，ConversationNotificationStore 逐条原子写 ChatMessage + message.created 后 ACK
  → 合并 claim 不合并内容/因果链/UI 卡片；execute 仍一条 delivery 对应一个 canonical Turn
  → `message_deliveries.handling_mode` + bootstrap/migration 回填历史普通 inform/report_result/agent_reply

P1-2 召回同源去重（压缩摘要/原文/recall 片段 ≤1 次注入）
  → SessionChunkIndexer（写侧）回查 Messages 补齐 CanonicalContentHash/ContextGeneration 冗余列
  → MemoryLibrary 第 5 路 LEFT JOIN Messages 取 hash/generation/CompactedBy，默认过滤 covered chunk
  → RecalledMemory/SearchHit 透传 SourceMessageId + CanonicalContentHash
  → SubconsciousRecallPipeline 注入前经 CompactionCoverageFilter 过滤 covered + 同轮 hash 去重
  → ContextPipeline assembler 兜底去重（双保险）

P1-3 Reasoning 紧凑归档（v2 sidecar + ThinkingJson 不回流）
  → ReasoningCompactCodec（PuddingCore）：{v:2,text,chunks:[{o,t}],hash} UTF-8 字节偏移 + delta 时间戳 + SHA-256，旧格式兼容、hash fail-open
  → MessageDeliveryDispatcher 写侧：thinking 帧累积 → ReasoningCompactCodec.Encode 落 v2（T2）
  → MessageApiController / AgentConversationProjectionService 读侧：codec 双格式解码（T3）
  → JSONL/Compaction 路径断言：ThinkingJson 不进模型 prompt / compact 输入（T5）
  → E2E：写侧 v2 → 读侧解码 → UI DTO 逐字节还原 + hash 校验（T6）

Plugin configuration → Plugin Resolver → PluginActivation
  → capability registry（Tool/LLM/Prompt/Context/Connector/Job/Presentation）
  → Typed Hook（Guard/Transform/Around，同步有界干预）
  → state commit + transactional outbox → durable DomainEventLog
  → per-consumer checkpoint/retry/dead-letter → UI projection / Heartbeat / Subconscious / Self-learning
  → Session/Run/Turn/LLM/Tool/SubAgent/Message/Compaction/Heartbeat/Job/Learning 使用统一状态机与提交后事件

spawn_sub_agent → SubAgentInvocationService → SubAgentManager
  → model 参数必须是 providerId/modelId 完整路由；裸 modelId 多 provider 注册时 FileLlmResolver 报
    "exists under multiple providers"（2026-08-24 起 list_llm_providers 内置工具输出实时路由表与
    ambiguous_model_ids 歧义清单，不含 apiKey/baseUrl，已入 CoreToolIds 常驻可见；应急快照
    memory/llm-providers-cheatsheet.md 转兜底）
    → `runtime.execution.json` 提供系统 profile（默认 600/2400/24h，非强制统一值）
  → 内部契约 SubAgentSpawnRequest 已含 `int? MaxRounds` 等请求级预算字段（N00 已实施）
  → 父代理工具 schema 面向可选 `max_rounds` 的暴露随 ADR-087 后续批次（SA-MSG/SA-ASK）落地
  → AgentExecutionService 在启动、剩余 80%/50% 与预算耗尽时注入预算通知
  → 正常轮次/时间耗尽后提供 20 轮、最多 30 分钟的收尾宽限，终态为可续跑 `budget_exhausted`
  → `resume_sub_agent_id` 复用 SubSessionId/上下文、创建新 runId 并重置系统计数器
  → run archive 固化实际预算与 `subagent.budget.notice`
  → 子代理轮内 warm-prefix checkpoint（2026-08-28）：估算达 0.65×有效输入上限时以原样
    system/tools/history + 固定尾部指令生成摘要，只有有效且缩小的 checkpoint 才原子替换旧区间；
    失败保留完整 history、每 dispatch 最多尝试一次；写 subagent.context.compacted 事件，
    LlmRequestBudgetGuard 硬悬崖保留为最后防线
  → FileSubAgentRunStore 归档并发协议（ADR-060 §3.11）：读写同一 per-run gate、读方 FileShare.ReadWrite、
    JSONL 追加 sharing violation 退避重试；重试耗尽丢弃事件写 archive-degraded.json 降级，不杀死运行
  → FirewallContext.WorkingDirectory 从 ToolExecutionContext 冻结；防火墙 WorkspaceGate、审批目标解析
    与文件工具统一委派执行根（worktree），不回退进程级静态 workspace root
  → ContextPipeline 以 ConfigurationAgentInstanceId 读取持久 Skill/人格/记忆，缺失 Skill 索引不写盘
  → SubAgentTransientDirectoryGcService 只隔离终态/孤儿的精确空脚手架，运行归档与有状态目录不进入 GC

Runtime 跨层服务 → Core contracts → Platform implementations
  → SubAgentTool → ISubAgentPool → SubAgentPool
  → AgentDiagnosticsTool → ITokenUsageEventRepository → TokenUsageEventRepository
  → FileReadTool/FilePatchTool → Runtime-owned FileChunkService

PuddingHost 产品组合根 → Runtime tool assembly scan
  → 每个自动发现的 IPuddingTool 都参与 ValidateOnBuild
  → 新工具的构造依赖必须同步注册到 PuddingHost 的 Runtime 扩展
  → AgentExecutionAdmissionCoordinator 必须在 Runtime 与 PuddingHost 两个组合根都注册为 Singleton，供前台 Turn 与 MessageDeliveryDispatcher 共享准入状态
  → PuddingApplicationHostCompositionTests 用 DesktopChild 入口防止“构建成功、Core 启动即退出”

Desktop → Core Ready 契约（2026-08-28 增加冷升级启动租约）
  → Core 初始化期间每 5s 发 PUDDING_DESKTOP_STARTING（协议/PID/单调序号）；Desktop 以 startupTimeoutSeconds 作为静默超时，合法租约允许 10 倍且最高 10 分钟的有界冷升级窗口
  → Core 在全部 hosted service StartAsync 返回后才发 PUDDING_DESKTOP_READY；租约不能替代 Ready、PID 校验或 /health/ready
  → ConnectorHostLifecycleService 本地注册保持同步，StartAllAsync 后台执行（ApplicationStopping 绑定）
  → FeishuWebSocket 端点发现/WS 握手各 15s 上限；飞书不可达只 Faulted 单个连接器，不阻塞 Ready

当前视觉链路（2026-09-12复核：ADR-077 V0–V3 已有实现；ADR-088收敛待实施）：typed image content part（`ContentPart{type=image, artifactId, detail}`）
  → ConversationAcceptanceStore 同事务写 `ChatMessages.ContentPartsJson`（v1 信封，Content 为文本拼接投影）
  → ExecutionRunCoordinator 读 canonical parts + 冻结 AgentExecutionSnapshot（CapabilityTags/Protocol/VisionPolicy；VisionHelperRoute 已随 ADR-077 V2 去外挂化移除）
  → 主模型带 vision：ChatMessage.ContentParts 原生进入请求；文本模型只收 `artifact://` 占位并显式调用 image_reader
  → 已删除 VisualArtifactObservationService 自动预观察旁路（服务+注册+旧测试）
  → LlmVisualInputPlanner fail-closed；已有inline/Files两种路径，旧产品策略单图2MB转Files、inline聚合40MiB、默认8张和384估计由V5/V7纠偏，不作为当前各Provider通用限制
  → Responses：user `input_image`（detail original→high）；`function_call_output.output` 支持 [input_text, input_image] 数组
  → ChatCompletions/Anthropic 遇图片工具结果抛 vision_tool_output_not_supported
  → Image Reader（image_reader）：path 唯一必填（http(s) URL / 宿主绝对路径 / artifact://），Low 权限 ReadOnly|RequiresNetwork（2026-08-28 裁定：纯只读无写/删路径，免审直通）
    → 只有 native 一条路径（ADR-077 V2 去外挂化，2026-09）：ToolExecutionResult.ToolContentParts 图片部件回交调用模型，零辅助 LLM invocation；无 delegate/helper 回退
    → 调用模型无视觉能力 ⇒ fail closed `vision_model_capability_mismatch`（工具输出明示 No helper model is used）；非 responses 协议 ⇒ `vision_tool_output_not_supported`；工具 schema 不提供 mode 参数
  → image_reader source resolver：URL 有界下载（每跳 SSRF/DNS 重校验、禁内网）、本地只读、内容哈希稳定 vision-* Artifact
  → DB 水合经 MessageEntity.AttachmentsJson 恢复图片 part；Snapshot 工厂冻结能力，单一判定来源
  → V3 Files已实现上传、持久remote ref与过期恢复；V4当前真实模型smoke与进程外验收待做。V7进一步收敛流式读取、多Provider传输、全请求预算和引用生命周期

Desktop Storage → CoreStorageManagementClient
  → GET/POST /api/admin/storage/databases（Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceService
    → 平台库页面/行/重复索引 + 代码索引作用域明细
    → PreviewId（10 分钟）→ 白名单批量删除 → checkpoint/VACUUM → 重扫
    → session_event_log / conversation_events / ChatMessages / memory 永不进入清理目标

PuddingHost → RetentionPruningService（platform.db 自动保留调度壳，ADR-076 收编）
  → 策略读 <DataRoot>/config/system.json storageManagement（StorageRetentionPolicyService，CAS + fail closed）
  → 执行全部委托 StorageMaintenanceCoordinator（唯一在线维护 writer：双优先级队列 + DataRoot maintenance.lock）
    → StorageCleanupExecutor 小批执行器（100 行/批、250ms 让步、busy 退避、rowid cursor 续行）
    → conversation_events 证据先 RetentionArchiveWriter 归档再删；在线 VACUUM 已全线移除
  → 遥测/上下文原始行自动清理默认关闭（聚合未实现 fail-safe）；Debug 字段/运行活动/日志默认开启
  → 旧 /api/admin/storage/databases 三端点保留双通道鉴权，Execute 内部经协调器，Desktop 旧页面无感

ADR-076 存储管理（Core + Web /storage，2026-08-24 首轮实现 Phase 0–3）
  → StorageDataClassCatalog 语义目录（9 类型 + evidence.conversation-events，物理白名单 + 保护清单）
  → StorageInventorySampler 有界采样（50–100ms slice、LIMIT 300 样本、索引探测 min/max、目录分片）
    → StorageInventorySnapshotStore 原子合并快照 + history.jsonl（每小时一点、90 天趋势）
    → POST inventory/refresh 立即 202、重复请求合并；GET overview 只读缓存
  → StorageMaintenanceJobStore durable 作业（maintenance/storage/jobs/<id>/job.json + events.jsonl，90 天轮转）
  → StorageAdminController 语义 API（overview/data-classes/refresh/history/policy(CAS)/preview/job/confirm/cancel，Admin JWT）
  → Admin /storage 页面：总览（文件+可复用页+分类估算）、SVG 占比圆环+趋势堆叠图、分类报表、
    可清理选择器（Evidence 只读展示）、受保护区、策略 Drawer、Preview 确认 Modal、作业列表（轮询+取消+确认）
  → 消费循环修复：writer Complete 后 WaitToReadAsync 同步 false 自旋会挂死宿主 StopAsync（测试抓出）

DesignRequest + ExpertGroupDefinition → DesignCouncilPlanCompiler
  → 上下文审计 → 调研 → 独立提案 → 交叉批判 → 主席综合 → 独立终审
  → 输出 Draft + RequiresExplicitActivation
  ├→ 当前 MOA 运行：DesignCouncilRunStateMachine → ISubAgentOrchestrationRunStore
  │  → DesignCouncilRuntimeService（精确 provider/model，无 fallback）
  │  → ISubAgentInvocationService（复用 sub-session/run archive/deadline）
  └→ 通用化迁移：DesignCouncilOrchestrationGraphAdapter
     → pudding.agent-orchestration/v2（component/trigger + typed multimodal port + control/data edge）
     → AgentOrchestrationGraphCompiler（组件冻结、端口/schema/route/reference/DAG 校验，不执行）
     → SqliteAgentOrchestrationStore
       （revision CAS → run/node projection → atomic claim/fence → append-only event replay）
     → AgentOrchestrationApiController
       （graph/run discovery + catalog/revision/run/events → AgentOrchestrationEventFollower → replay-to-live SSE）
     → AgentOrchestrationLayoutApiController
       （GraphLayout read + Admin CAS write；不可变 Revision/Node 先只读校验，与 executable revision/run facts 隔离）
     → AgentOrchestrationManagementApiController
       （Admin Graph create + Head-CAS delete；任意 Run 历史都会阻止删除）
     → AgentOrchestrationHttpHookApiController
       （Admin debug POST + 显式 immutable revision；从不解析 Graph Head）
       → AgentOrchestrationHttpHookService
         （sourceEventId 幂等 + payload binding → durable Run Inputs → Create/Activate）
     → AgentOrchestrationRunCommandApiController
       （Admin 顶部“运行” + 显式 immutable revision + typed inputs → ManualRunService → Create/Activate）
     → AgentOrchestrationWorkerService
       （SubAgent → SubAgent → image-generate → image-preview；按端口 outputs_json 传递文本/Artifact、lease 续租、后继 Ready/Skipped 与 Run 终态同事务推进）
     → Admin /orchestration
       （紧凑 Graph/Run 控制条 + 顶部运行 → 全宽画布 → 悬浮工作台 → SubAgent 模型/模板/角色设置与文本输出 → 图片生成/展示组件自有预览 → Revision/Layout CAS）

Chat 插嘴模式（当前 Turn steering）
  → useMessageInteractionQueue：busy 时 Enter 仍立即提交 canonical Turn API，受理后由 chat_execution_commands + ChatExecutionWorker 持久排队；不创建 React local_pending
  → Composer 独立 ⚡ 设计增量：active Turn + 非空纯文本时直达同一 Steering admission，不写 pendingSendQueue、不创建普通 delivery/第二 Turn；202 后 compare-and-clear，409/失败保留草稿且不自动排队
  → 第一批交互一致性落地（2026-09-11，Docs/Reports/前端交互体验优化建议-2026-09-11.md §0）：
    鼠标发送按钮不再挪用作停止——运行中有草稿=「加入队列」（与 Enter 同链）、⚡菜单=「补充给当前任务」（与 Ctrl/Cmd+Enter 同链）、
    独立「停止当前执行」按钮=本地 abort + requestActiveTurnCancel（新增 cancelConversationTurn 封装，接线既有 ADR-059
    POST .../turns/{turnId}/cancel；已结束/未受理按竞态静默）；useMessageSend 失败恢复草稿（restoreDraft 端口，空输入框才回填）、
    busy 提交 202 受理后「已加入队列」回执；状态胶囊/余额徽标补键盘激活
  → MessageQueueProjectionService：默认只读投影 queued/retrying deliveries + pending commands；claimed/running 只在诊断查询出现
  → MessageQueueDropdown：内容宽度胶囊摘要；详情向上悬浮限高，明确“认领后转入会话轨迹”
  → POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering + X-Workspace-Id
  → ConversationTurnsController → CreateSteeringHandler（Running + Workspace/Agent 围栏）
  → SessionSteeringService → session_steering_messages durable queue（不可变 target_turn_id；source_queue_item_id 重试幂等；旧库由 bootstrapper 原地升级）
  → AgentExecutionService：每次 LLM 前只 drain 当前 Turn；最终回复后的 late safe boundary 命中则继续同一个 Turn
  → steering.injected + agent.steering.inject 形成消费证据；不取消正在运行的工具/模型请求
  → 独立按钮方案：Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md（Proposed；P1 Task ed88185f1d3b4e16a70e9b9ea0f0e040）

Chat first paint → AgentConversationProjectionService
  → 过滤 transport duplicate 占位、按 pudding-message envelope message_id 折叠历史重复入站
  → system/heartbeat envelope 正文投影为 context.text，不把协议 JSON 显示在聊天气泡
  → 最近 20 条可见消息 + active run 最近 64 条过程明细/全量摘要
  → TurnSurfaceStore（2026-08-24 行为链重构）：canonical turnId + 别名归并
    （messageId/runId/commandClientId），完成 turn 经 per-message 明细接口懒水合
    （text/thinking/tool/delegation 同一事件流，eventId 幂等去重），
    终态/刷新后轨迹从投影重建；AgentConversationProjectionService 明细端点
    补 text 事件 + delegation 三重排除修复（canonical 事件名/kind 映射/run 过滤）
  → 验收二轮修复（2026-08-24）：委派节点按 subAgentId upsert（重复 spawn 不再
    留下永久 running）；DTO 透传 canonical sequence/turnId/runId + activeRun 快照
    补正文事件（运行态即可交错文本段）；懒水合有界化（MessageRow 通过消息滚动
    容器 IntersectionObserver 在 600px 预取区注册可见 turn、并发窗口 ≤2 且槽位
    完成后持续排空可见队列；组件挂载不再批量水合全部历史）；正常流保留真实行高，
    删除会在动态 Agent 行上积累过期 remembered size 的 content-visibility 占位；
    虚拟化按消息内容 + 已水合 canonical 行为链 render weight 即时开启；
    AgentTurnCard 大卡片外壳（暖色表面/1px 边界/14px 圆角，终态不重挂载）；
    工具行 aria-label 带状态 + 成功终态卡「已完成」标记
  → canonical event → ExecutionFlowProjectionIndex（2026-08-27）
    （eventId 先去重；同帧按 Turn 合并；只重投影 dirty Turn；快照结构共享；
      终态释放原始事件/eventId；session switch 硬 reset；collector 保留 typed payload）
  → MessageList → messageProjection（保持已组装消息顺序，未匹配 active run 留在当前流末端）→ MessageViewportRuntime（虚拟化、锚点、贴底）
  → ChatMessageStyleProvider（消息树共享一次聚合样式注册）
  → MessageRow（稳定块直接渲染 + 语义 memo；接收本 Turn 具体 Projection，其他 Turn revision 不失效；不再经过单条 MessageStream 兼容重建）
  → AgentMessageBubble → TurnContentStream（AgentTurnCard 内容块流，2026-08-25）
     （per-turn canonical 投影 nodes 按 sequence 形成 TextBlock ⇄ ActivityGroup 交错流；
       正文永久可见且只渲染一次，answerMarkdown 不再以字符串关系切回第二正文气泡；
       最新尾部组始终展开、历史组折叠并卸载成员 DOM，组内长详情默认单行折叠；
       单 Turn 默认只挂载最新 40 个内容块、单 ActivityGroup 最新 24 个行为节点，
       较早轨迹按 40/24 项渐进揭示，避免单个长 Turn 绕过消息级虚拟化撑爆 DOM；
       路径 A processItems adapter 只作无 canonical 正文节点时的旧记录回退；
       ProcessSummaryItem 必须透传服务端 sequence，缺失即 fail closed，不用下标伪造；
       已封闭 TextBlock/ActivityGroup 语义 memo，append 只更新尾段/状态变化组；
       TurnOutputChunker 非 delta 事件先 flush 正文/思考缓冲，轮次边界进 canonical sequence；
       终态 reply 分叉以服务端为准不再拼接（重复输出修复）；
       流式中同回复单卡：activeRun↔本地 turn 合并移除「本地正文为空」门槛
       （commandClientId 已是同一发送的强约束），hasProjectedUserTurn 增加
       turnId 锚点、合并保留本地 clientMessageId（2026-08-24，生成中多卡修复）；
       ReasoningDisclosureRow 多段 + 段时长 chip、ToolCallRow 耗时/exit 折叠行、
       TurnStatsLine 终态计量、PresentationRegistry 五类 renderer：terminal/diff/read/search/web）
  → 主消息运行监视区（首 Token 前也保留主代理“查看过程”：当前阶段 + 推理摘要 + 工具操作 + 有界子代理委派状态；不展开子代理内部过程）
  → subAgentReducer（事件/快照统一投影；状态接口携带 canonical runId 并可重建漏收 created/started 的运行；budget_exhausted 终态单调；原样展示有界的实际 reasoning_preview）
  → SubAgentActivityDock（实时 reducer + 终态 run 归档一次性回放；活动 run 零归档轮询=ADR-060 §3.11；归档降级时展示 archive-degraded 提示；刷新后按 canonical runId 恢复子代理任务/推理/工具/轮次/耗时/输出；Agent-first 路由回退 mainSessionId 保证图标可见）
  → 展开过程摘要时才构建 rounds / trace chips
  → MessageItem 先渲染纯文本，异步加载 Markdown/KaTeX 增强块
  → 任务看板、Checkpoint、历史搜索、开发面板、子代理检查器、右键菜单、
    会话诊断 Drawer 与摄像头输入仅在首次使用时加载；工具栏 hover/focus 可预取
  → 余额、Goal、会话推断等辅助请求在首帧后的 idle window 启动，
    不阻塞消息投影、当前 Agent 与实时事件；旧 WebView2 以短 timer 有界退化
  → 常驻埋点使用 perfEventRuntime；完整诊断模块仅在 perf/debug 模式加载
  → npm run build 强制 Chat bundle budget：同步脚本 ≤1536 KiB、Chat 路由 ≤480 KiB，
    并阻断任务看板/Checkpoint/ContextMenu 回流首载共同块
```

Task auto execution settlement (2026-08-29 hardening)
  → GoalSettlementStore: terminal completeness scans the full Turn window; EvidenceRefs retain the newest bounded 128 events
  → settlement atomically projects RunId/elapsed/LLM rounds/tool calls/input-output tokens into GoalIteration + Goal totals
    (root Turn canonical usage + recursive descendant TokenUsageEvents inside the exact Turn window)
  → blocked Task-bound attempt atomically releases Binding/Assignment/Reservation, keeps Task Blocked/NeedsReview and terminals the attempt Goal as Failed audit history
  → dispatch atomically retires legacy detached Blocked Task Goals for the same Agent/conversation (including a prior Task) before creating a fresh fenced Goal, preventing UX_goal_runs_active from masquerading as task_goal_lost_race
  → five-minute tracker/repair heals historical blocked_binding_still_active ownership before the next dispatch
  → legacy delivered-without-execution assignments are fail-closed to Blocked and released after the stall threshold
  → scan order is tracking/repair → availability rebuild → candidate evaluation → dispatch in the same interval
  → task dispatch outbox revalidates Task/Assignment before send and again at atomic bind; stale/terminal conflicts dead-letter, transient send/bind failures stop at MaxAttempts, shutdown cancellation stays lease-recoverable
  → WorkUnit remaining input/output/cost budgets propagate through tool and sub-agent boundaries; synchronous delegated usage returns to the parent ledger
  → AgentOutputTruncationPolicy: one action-forcing recovery for length/incomplete, then explicit failure
  → ConversationProjector: direct usage dedup uses SQL-stable fingerprint candidates + bounded in-memory time fence

Task scheduler + Goal user control plane (2026-08-31 source-ready)
  → Admin Task board exposes explicit auto-dispatch opt-in and structured task routing facts
  → SchedulerDrawer consumes server status/policy/actions/evaluate: pause/resume, immediate scan/repair, revision-CAS hot policy
  → TaskAutoDispatchScanRunner is the shared periodic/manual reconciliation order; dynamic policy reaches worker, event bridge, coordinator and starter
  → GoalBanner covers start/pause/resume/stop/new without deleting durable Goal history
  → Goal continuation transport keeps new Unicode readable while escaping envelope delimiters; Admin projections decode legacy JSON escapes only for server-marked goal_continuation messages
  → authoritative design: Docs/Features/任务调度器与Goal用户控制面设计.md; external Desktop/Core deploy and product smoke remain separate gates

Task scheduler effective-dispatch closure (2026-09-01 proposed)
  → effective dispatch requires Intent → durable task-scoped Decision/Outcome → fenced Assignment/Reservation → TaskGoalBinding/GoalRun → canonical ExecutionRun → verified Task settlement
  → legacy execution claims must join execution_runs terminal state; terminal/orphaned claims release ownership through Serializable deterministic repair instead of remaining Healthy forever
  → event Coordinator completes each Intent only after its triggering Task has a durable stable outcome; single/bounded modes share the same Bridge/Coordinator/Starter gates
  → durable scan-run summaries preserve empty/failed scans across restart; Blocked recovery UI uses diagnostics + preview + per-task ETag commands
  → code-level implementation and task slicing: Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md

## 测试项目

| 项目 | 覆盖 |
|------|------|
| `Tests/PuddingCoreTests/` | 工具契约、LLM 网关、MessageFabric |
| `Tests/PuddingRuntimeTests/` | Agent Loop、上下文管线、语音/图片 |
| `Tests/PuddingPlatformTests/` | 渠道配置、Artifact 存储、图片生成 |
| `Tests/PuddingMemoryEngineTests/` | Library/Book/Chapter、FTS5、Skill 去重 |
| `Tests/PuddingMemoryEngineBenchmarks/` | BenchmarkDotNet |
| `Tests/PuddingCodeIntelligenceTests/` | 代码索引 |
| `Source/PuddingCodeIndexTests/` | **索引组件（`PuddingCodeIndex`）独立测试工程**：变更管线/调度/维护/存储 + 边界断言（58 用例） |
| `Tests/PuddingCodexServiceTests/` | Codex MCP Service |
| `Tests/PuddingFullTextIndexTests/` | 全文索引 |
| `Tests/PuddingWebApiTests/` | Web API |
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client、Debug 调试模式（路由/反向代理集成/SSE/WS 中继/前端监督器/源码构建器/前端构建部署） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |

## 2026-09-05 效率与代码审计入口

持续恢复入口：`Docs/Reports/PuddingAgent持续优化执行台账-2026-09-05.md`（既有 30 分钟 heartbeat）。第四轮：`StorageInventorySampler` 的两个索引端点/2000-entry 惰性目录预算；`TokenUsageRecorder` 每当前层只读上一条 hash，`PlatformDbContext` / `TokenUsageSchemaBootstrapper` 同步覆盖索引。定向19/19、扩展96/96；第四轮已部署，同任务56.898s/预热13.253s。`Docs/Reports/PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md` 区分模型流与本地记账、GC heap与Private；后续Private仍回升，不能宣布整体内存优化通过。

第三轮部署与产品证据：`Docs/Reports/PuddingAgent第三轮部署与产品验收-2026-09-05.md`。Desktop 主管不变，新 Core/前端已部署，hash/Ready/单次只读功能通过；161.749 秒流空档、预热内存与夜间吞吐仍待收口。`TestScripts/invoke-pudding-desktop-deployment.ps1` 提供停机备份/预构建部署/全量前端核验；`test-pudding-deployment-gates.ps1` 覆盖历史 PID 停机判定（7/7）；`measure-pudding-process-baseline.ps1` 记录进程树 CPU/Private/WS，前后负载不一致不得当作 A/B 收益。

第二轮前端与发布：`Docs/Reports/PuddingAgent第二轮前端修复与发布验证-2026-09-05.md`、`Docs/Reports/pudding-agent-round2-build-2026-09-05.json`。`PuddingAdminShell/EntityCard/PageHeader/StatusBadge/Toolbar` 使用 createStyles；`PerfTab` 使用实际诊断合同；SSE reconnectCount 状态穿透 ChatLayout，移除 ChatMain 500ms 轮询；`PuddingHostContent.props` 的 `PuddingAdminDistPath` 与前端 `PUDDING_ADMIN_OUTPUT_PATH` 支持隔离打包。源码检查和发布核验通过，未部署。

首轮实现与验证：`Docs/Reports/PuddingAgent首轮修复与验证-2026-09-05.md`。新增 `Source/PuddingPlatform/Services/Scheduling/LegacyTaskExecutionProbe.cs`，由 Tracker/Repair 与完成结算共用精确 Command→latest Run 解析；`ExecutionRunCoordinatorMonitorTests.cs` 覆盖监视异常取消；`TestScripts/test_deepseek_cache_hitrate.py` 覆盖完整北京时间日→UTC 边界。产品部署与性能对照仍待验收。

`Docs/Reports/PuddingAgent效率与代码审计-2026-09-05.md`：TaskExecutionTracker legacy claim → canonical terminal、FilePatchTool schema/缺字段语义、useSessionEventConnection 鉴权重试、SubAgentConversationProjectionWorker/FileSubAgentRunStore 增量回放边界，以及 ExecutionRunCoordinator monitor fault。报告附七日指标与验证结果；这些是诊断发现，尚未标记为修复或产品验收完成。

## 2026-09-18 FastRouter 本机资源池配置

`D:\data\config\llm.providers.json` 新增 `fastrouter` 下的 `gpt-6` / `gpt-6-astra`，Responses 协议，1050000 上下文 / 128000 输出。仅运行时配置变更，待 Core 重启加载；映射和验证边界见 [配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 兼容性实测

当前 Key 分组的 GPT-6 两个 ID 返回 model_not_found；gpt-5.6-sol 的 Responses 流式工具调用与工具回传成功，Chat Completions 亦成功。入口为 ResponsesLlmGateway 及其内嵌 ResponsesStreamParser；详见[诊断证据与调用路径](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。未修改产品或替换运行时模型。

## 2026-09-18 FastRouter GPT-6 Astra 复测通过

服务商更新配置后，`gpt-6-astra` 已通过 Responses SSE 工具调用及结果回传，`gpt-6` 仍404；当前应选择完整ID gpt-6-astra。原有资源池协议不变，未重启或执行产品内会话验收。见[复测记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 最终模型清单

按用户要求，本机 fastrouter 资源池仅保留已实测成功的 gpt-6-astra，删除不可用的 gpt-6。备份与校验见[配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-18 FastRouter 新增 GPT-5.6 Sol

本机 fastrouter 资源池现含 gpt-6-astra、gpt-5.6-sol，均使用responses；Sol按官方文档登记1050000上下文/128000输出。备份、参数来源和加载边界见[配置记录](Docs/Reports/FastRouter资源池配置-2026-09-18.md)。

## 2026-09-19 历史图片8张上限诊断

VisionRequestPolicy默认8、VisionCapabilityContract上限钳制、PuddingFileConfigLoader加载拒绝及VisualInputRequestBudget跨消息累计共同导致第9份图片失败。13项现有合同测试通过，未修复/部署；历史理由、精确Turn及纠偏方向见[诊断](Docs/Reports/历史图片累计触发8图上限诊断-2026-09-19.md)。

### 2026-09-20 压缩活性与界面重设计
- 权威设计：`Docs/Features/上下文压缩运行状态与界面设计.md`。
- `ContextCompactionService.GetActiveCompaction` / `SessionEventsController.GetCompactionStatus`：精确 ID + 开始时间的轻量活性快照；started 移到摘要输入准入后；取消补写终态。
- `useCompaction`：按 ID 投影，10 秒确认、30 秒动画许可、重放不切会话；`CompactionCard` 独立状态区；`ContextUsageRing` 总窗口占比、来源和采样时间；`IntentConsole` 压缩结束刷新并防旧响应覆盖。
- 部署验收：实现 `aab7f5c`，2026-09-20 17:25 Core PID 23280 Ready，264 文件 manifest 匹配；`umi.83c11143.js` HTTP 哈希匹配。111 项定向回归通过，真实默认助手页面无伪压缩动画；自然压缩执行未被人工触发。
