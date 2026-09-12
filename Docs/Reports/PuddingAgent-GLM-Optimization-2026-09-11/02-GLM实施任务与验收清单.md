# GLM 实施任务与验收清单

日期：2026-09-11  
这是原施工任务分解。完整原理与约束见同目录 01-代码审阅与优化设计.md；当前复审状态以下方说明及 04/05 文档为准。  
先从 F01、S01、T01、A01 的小范围变更开始；C01/C02 冻结合同后，开始缓存观测。
> 2026-09-11 独立审计：既有 91 项回归通过，新增 10 个边界探针失败。F01/S01-A/T01 needs_changes，优先实施 F01-R/S01-A-R/T01-R；A01 删除与构建层面通过。S01-B → C01 → C02 继续按依赖推进，尚未生产验收。详见 04 独立审计与 05 看板回执。

## 1. 可直接作为 GLM 初始指令的文本

> 请基于当前工作树和本目录两份设计文件实施 PuddingAgent 优化。开始前读取 Agents.md、code_map.md、相关权威 ADR，核对 review-baseline.json 的 commit/dirty/文件 hash。
>
> 本计划来自代码审阅，并未做生产部署或当前缓存/内存测量。不要将历史报告中的数字当作当前指标，不要把已经存在的修复再实现一遍。
>
> 一次选择一个可验证工作包，先复现关键失败交错，再做最小可完成修改。必须保存无关 dirty 文件与运行数据。完成单项后报告实际改动、测试结果、产物状态和未解决项，再推进依赖允许的下一项。
>
> 不要把“partial 拆文件”“新增接口”或“测试名字包含 bounded”当作架构优化完成；必须证明真实行为、状态所有权或工作量改善。
>
> 模型路由、必要任务、工具语义、当前用户输入、typed 图片、工具结果原文和历史压缩覆盖不应因命中率优化而丢失。授权检查不能为冻结前缀让步。
>
> 本文授权范围仅是实施规划的描述；生产停机、重启、数据修改和真实模型验收按用户对你当前任务的实际授权执行。不要从本报告推定可以清空 D:/data 或读取 LLM Secret。

## 2. 任务卡

新增类、表、接口名称均为建议名；先检查仓库已有实现，能扩展则扩展，不新建同义系统。

### F01 — 真正有界的明细水合

- 修改入口：Source/PuddingPlatformAdmin/src/pages/chat/hooks/useTurnSurfaceStore.ts。
- 建议新增：DetailHydrationScheduler，放 chat/client 或 chat/runtime，与 React hook 分离。
- 相关测试：hooks/__tests__/turnSurfaceStore.hydration.test.ts；projections/turnSurfaceStore.test.ts。
- 先复现：保持前两个请求 pending，更新 conversationView；观察是否启动第三、第四个请求。
- 实施：总 active slots；稳定请求 key；selection generation；AbortController；所有回调 owner 校验；可见项 unregister；401/403/404/临时失败分类。
- 完成证明：持续重渲染、快速 A→B→A、未完成请求交错时实际并发峰值 ≤2；旧 callback 不改新状态；所有有效项最终完成；认证失效后请求停止。
- 交付边界：先保持当前调用接口和 UI。不要顺带重写整页状态管理。

### S01-A — usage 并发幂等与原子月聚合

- 修改入口：Source/PuddingPlatform/Services/TokenUsageRecorder.cs、LlmGatewayUsageRecorder.cs、Data/PlatformDbContext.cs。
- 先复现：两个独立 context 同时读取同一聚合，写入两个不同 source；让读取阶段通过 barrier 对齐。
- 实施：明确唯一 source；单事务 conditional insert + atomic aggregate upsert；捕获精确重复冲突；同 ID 异内容拒绝；保留金额精度。
- 测试：Source/PuddingPlatformTests/Services 下新增 recorder 并发集成测试，使用临时 SQLite 文件，不以 InMemory provider 代替 SQLite。
- 完成证明：50 并发与重复请求的 events、sum、requestCount、cost 完全一致；失败事务不加聚合。

### S01-B — 请求级 attribution 与迟到数据

- 修改入口：TokenUsageRecorder.cs、Source/PuddingRuntime/Services/DirectLlmClient.cs、现有 LlmInvocation 合同/实现、ContextUsageSnapshotStore。
- 建议合同：invocationId、attemptId、immutable RequestUsageAttribution，复用既有 trace/run/tool ID。
- 实施：不再从 session 最新调试缓存归因旧请求；usage/local commit 的恢复独立于 Provider 重试；日聚合对迟到补录失效。
- 禁止：以“相同 Token 数、相同内容 hash、相邻时间”代替 invocation 身份去重；不同请求可以返回完全相同 usage。
- 测试：A 延迟落 attribution，B 已准备请求；A 仍保留 A 的 shape；次日补录昨日 usage 可重算；Provider 成功而本地提交失败不再发模型请求。
- 前置：S01-A。

### C01-A — Composition 与 telemetry 解耦

- 修改入口：DirectLlmClient.RecordCompositionSnapshotAsync、PersistentCompositionVersionRegistry、CompositionRecoveryService。
- 先复现：不注入 telemetry sink，验证当前是否仍发生 composition store append。
- 实施：必要执行状态独立服务；遥测只消费结果；恢复 single-flight、可取消；写入失败有明确状态。
- 测试：无 telemetry、telemetry 异常、store busy、外部取消、并发恢复。
- 禁止：将所有异常 catch 后继续使用空工具集合；将 critical state 混入 best-effort sink。

### C01-B — revision、content identity 和授权 epoch

- 修改入口：Source/PuddingCore/Runtime/CompositionContracts.cs；CompositionSnapshot.cs；SqliteCompositionStore.cs；ToolExposurePlanner.cs。
- 先复现：同 permission fingerprint 的 A→B→A 观察；验证返回版本及 restart 后的 latest。
- 实施：long 单调 revision 与可复用 contentId 分离；CAS head；ToolBindings 引用准确 schema 版本；PermissionEpoch 与 ExposureRevision 分开；轮边界提交。
- 测试：A/B/A、回滚配置、工具按需发现、撤销权限、旧工具定义缺失、两个提交者撞 revision、重启和 hot reuse。
- 删除/修改：原有“版本可倒回复用”“失败必须纯内存降级”等与新合同冲突的注释/测试。
- 前置：C01-A；S01 身份合同。

### C02 — 最终请求 manifest 和缓存报表

- 修改入口：Source/PuddingCore/Core/OpenAiLlmGateway.cs、ResponsesLlmGateway.cs、AnthropicMessagesLlmGateway.cs；现有 prefix/usage 合同；TestScripts/deepseek-cache-hitrate.py。
- 实施：各 Adapter 在最终序列化处共享发送形状与 manifest；逐段 hash/bytes；比较完整 history；明确模型可见字段与传输字段；同 attempt 关联 usage。
- 先做离线测试：仅尾部追加；第 20 条历史改一个字符；增加第二条 system；工具属性/required/schema 变化；图片 artifact 版本变化；协议切换。首变位置应准确，末尾 append 不被误记为首部重建。
- 报表：hit/(hit+miss)、不一致数据/unknown 数据覆盖、账单对账、模型/来源/主子代理/控制面/首请求/warm/epoch 分桶；不平均每请求百分比。
- 完成证明：可关联率和真实可解释率分别报告；七日门禁未满不写通过；不硬编码历史账本覆盖率。
- 前置：S01-B、C01。

### R01-A — byte cursor 增量读取

- 修改入口：Source/PuddingPlatform/Services/FileSubAgentRunStore.cs 的 ProjectPendingConversationEventsCoreAsync 和 cursor helpers。
- 实施：byte offset + file generation + last event；完整 UTF-8 行；每批字节/事件/时间预算；提交目标事件后推进 cursor；减小 per-run gate 持锁时间。
- 测试：Source/PuddingPlatformTests/Services/FileSubAgentRunStoreTests.cs 扩展，并新增读取量断言。
- 必测：100 MiB 文件只追加 10 行；半行、截断、同长度替换、sharing violation；投影提交后 cursor 写失败。
- 禁止：把行号换成 byteOffset 字段但仍 ReadAllLines；一出异常就跳过事件。

### R01-B — 有界发现与待投影 Run 索引

- 修改入口：ReplayPendingConversationEventsAsync、sub_agent_runs 的既有查询及调用 worker。
- 实施：dirty signal + durable 进度 + DB keyset discovery；孤儿恢复扫描有分片位置；maxRuns/maxBytes/maxElapsed 约束发现阶段。
- 测试：10 万目录下单 tick 有界；重启、索引遗漏、文件已追加但 dirty 标记前崩溃，最终全部投影。
- 禁止：先递归全目录 ToArray 再 Take，或把 FileSystemWatcher 当唯一恢复机制。
- 前置：R01-A；文件/DB 跨资源恢复协议明确。

### F02-A — 一致投影版本

- 修改入口：Source/PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs；相关 AgentConversation DTO、Controller；前端 chat/client/types.ts、chatClientStore.ts。
- 实施：区分 headSequence/projectionCheckpoint/viewRevision；服务端快照内容和 checkpoint 一致；客户端按 viewRevision 判等。
- 必测：同 cursor 同消息数，正文/状态已变；终态事件先于消息投影；读取中途新事件写入。旧 snapshot 不可推进 cursor 后永久跳过内容。
- 禁止：仅增加 DateTime.Now 字段强迫每次响应变化，这会破坏缓存且不解决一致性。

### F02-B — 有界投影与增量刷新

- 修改入口：现有 Conversation projector、GetConversationAsync、chat/index.tsx、agentChatApi.ts。
- 实施：TurnProcessSummary 增量聚合；active output 避免反复从第一条 delta 拼接；详情分页；沿用 Snapshot+Watch；无变化小响应。
- 必测：长回合 10 万 events 刷新读量受窗口约束；所有摘要计数仍准确；断流恢复等价。
- 前置：F02-A。

### T01 — 记忆工具闭合合同

- 修改入口：Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs、MemoryToolArgs.cs；现有 ManageMemory/检索工具。
- 最小施工：switch action 全覆盖；未知值 fail；业务错误 Success=false；取消传播；schema 与运行分支一致。
- 后续：现有工具增加 chapterId 精确读取和分页；检索返回稳定 ID；写入返回 version/hash；重要记忆入口消除重复。
- 必测：action 拼错且 content 非空时依然零写入；失败统计不是成功；upsert→read→search→read 一致。
- 禁止：为了兼容错误 action 猜测为 upsert；让通用只读查询隐含发起模型写入。

### A01 — 组合根和文档入口

- 修改入口：Source/PuddingAgent/Services/PuddingServiceCollectionExtensions.Runtime.cs / Connectors.cs 与 Host 对应文件；PuddingAgent.csproj；Docs/架构.md、Docs/README.md、code_map.md。
- 实施前证明：Program 启动 Host；实际 Compile 当前旧副本存在；核查任何外部类型引用。
- 实施：删除确认为无效的副本，保留 Host 唯一组合；更新文档 current/legacy 和状态说明。
- 必测：Compile 项、Host 两种入口 DI、工具自动发现依赖、Desktop 不引用 Host；启动失败仍能进入 Desktop 设置。
- 禁止：删除 Host 正在使用的注册或把 Core 业务移进 WPF。

### R02 — 缓存和 keyed lock 生命周期

- 修改入口：AgentSessionManager、CompositionVersionRegistry、PersistentCompositionVersionRegistry、ContextUsageSnapshotStore、ChatExecutionWorker、FileSubAgentRunStore。
- 实施：明确 resource owner、pin、字节预算、TTL、session generation；可恢复后淘汰 warm state；正确的引用计数 keyed lock。
- 必测：1 万短 session 结束后项数回落；复开恢复；取消 waiter 与 eviction 的竞争；同 key 不出现两个锁 owner。
- 禁止：Release 后直接从字典删锁/Dispose，或未确保 Composition durable 就清 loaded tools。
- 前置：C01；与 R01 协商 projector locks 所有权。

### C03 — 快速估算与独立诊断

- 修改入口：Source/PuddingCore/Platform/LlmOptions.cs、LlmRequestBudgetGuard.cs、统一 RequestShape 的缓存接口。
- 实施：稳定段估算缓存；conversation-unit 累计预算；一次选裁剪窗口；最终 hard guard 复核；entropy 仅按需要采样。
- 必测：同样输入/裁剪结果/current Turn/tool groups；计算次数与分配；异常 calibration 不能误套下一请求。
- 禁止：仅提高压缩阈值掩盖高开销；降低安全余量换取表面输入空间。

### A02 — 共用 Agent Loop

- 修改入口：AgentExecutionService.cs、AgentExecutionService.Buffered.cs、AgentExecutionService.Streaming.cs 和现有 AgentLoop 模块。
- 拆成四次交付：request preparation → budget/tool processing → terminal → loop。
- 每批保留入口合同，先构造 buffered/streaming 等价场景记录。只有通过前一批才继续。
- 完成证明：同 fake Provider 脚本对应同工具次数、transcript、usage 和 terminal；差异仅限允许的呈现时序。
- 禁止：新增 RuntimeServicesBag、ServiceLocator 或 20 个模式 flag 代替职责拆分。

### F03 — Chat 单状态源与细粒度订阅

- 修改入口：useChatState.ts、useAgentChatClient.ts、TurnSurfaceStore、useSessionEventProjection.ts、useSessionEventBuffers.ts、MessageList/MessageRow。
- 实施：owner 表先落地；normalization 和 turn selector；session generation 统一；清理证实未使用的 useConversation/旧 fallback。
- 必测：末尾 delta 只重算相关回合；完整 replay 等价；草稿/焦点/滚动/详情披露不回退；真实 WebView2 长消息测试。
- 禁止：为了减少代码直接启用尚未接入的旧 useConversation；它不是已验收迁移捷径。
- 前置：F01、F02。

### R03 — 取消、后台监督与重试

- 修改入口：ChatExecutionWorker、DirectLlmClient；逐项确认被触及的 Host background service 和子代理后台启动。
- 实施：acquired lease 立即进入所有权 try/finally；等待锁取消也释放；capacity 配置验证；Provider retry 与本地写重试分开；半开 probe 限额。
- 必测：等待锁取消、host stopping、首包前/后错误、429、永久 4xx、本地 usage 失败；工具次数不增加。
- 禁止：把所有 Task.Run 改为 await 后堵塞 Ready；对首包后网络断开重发整个工具回合。

### S02 — 状态解释与结算

- 修改入口：TaskExecutionTracker.cs、TaskExecutionRepairCoordinator.cs、LegacyTaskExecutionProbe.cs、TaskCompletionSettlementService.cs、Availability/read model。
- 实施：共享 evidence snapshot 与纯判定；所有修改事务内重读 fence；统一 reason DTO；复用既有 Goal/Task outbox。
- 必测：旧 attempt terminal+新 retry pending、owner/fence 更换、误作用域、缺证据、前台抢占；不得释放有效执行。
- 完成证明：source tests 和真实自动派发分开；七夜不足继续 pending。

### A03 — 边界和质量门禁

- 修改入口：领域 API client、orchestration 页面/Store、相关大类，按实际触及范围分批。
- 实施：按职责而非行数拆分；生成 DTO 的来源唯一；严格状态联合；扩展 ArchitectureGuardTests；复用 bundle budget。
- 必测：移动前后合同快照/路由/事件不变；干净隔离输出测试不依赖固定 BaseDirectory；Desktop 串行构建。
- 禁止：全仓格式化、无测量大范围升级依赖、用空转接口层代替实体职责。

## 3. 测试执行说明

不要一开始全量 build 以覆盖正在运行的 DLL。先看当前进程和代码归属，再使用仓库已有隔离构建/部署脚本。构建、测试、发布输出只放 .tmp-build、.tmp-test-out 或系统 Temp；不得放 D:/data。

以下命令是施工时的示例，不表示本轮已执行。测试名按实际新增类更新；缺少 restore 资产时在允许的构建目录完成正常 restore，不应把 --no-restore 失败当业务回归。

~~~powershell
# 仓库根目录运行；先检查和保留当前 dirty 状态
git status --short

# 仓库现有、只用内存 SQLite 的日报测试
python -B TestScripts/test_deepseek_cache_hitrate.py

# 仅求值入口项目实际编译项，不构建
dotnet msbuild Source/PuddingAgent/PuddingAgent.csproj -nologo -getItem:Compile

# 按实际测试类替换 Filter；OutDir/Results 均在仓库临时目录
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --no-restore --nologo --filter "FullyQualifiedName~TokenUsage|FullyQualifiedName~FileSubAgentRunStore" -p:OutDir=E:/github/AgentNetworkPlan/PuddingAgent/.tmp-build/glm-platform/ --results-directory .tmp-test-out/glm-platform

dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --no-restore --nologo --filter "FullyQualifiedName~Composition|FullyQualifiedName~LlmRequestBudgetGuard" -p:OutDir=E:/github/AgentNetworkPlan/PuddingAgent/.tmp-build/glm-runtime/ --results-directory .tmp-test-out/glm-runtime

# 前端目录下运行：src 范围按卡选择
npm run jest -- --runInBand --runTestsByPath src/pages/chat/hooks/__tests__/turnSurfaceStore.hydration.test.ts
npm run tsc

# 每批结束检查本次文件 diff 与格式
git diff --check
~~~

这些命令不是一键全量执行列表。共享 OutDir 的相关构建串行，Desktop build/test/publish 尤其必须串行。为不同工作树/卡片分配各自临时输出目录，不让两个验证过程互相覆盖。

前端 build 输出使用仓库现有 PUDDING_ADMIN_OUTPUT_PATH 与 Host 的 PuddingAdminDistPath 机制，执行 check-chat-bundle-budget；不要覆盖开发 dist 后直接宣称 Desktop 已加载。

## 4. 必交证据模板

每项施工结果使用以下最小记录：

~~~text
WorkPackage:
BaseCommit / PatchIdentity:
SourceFiles:
ProblemTrigger:
BehaviorBefore:
BehaviorAfter:
PreservedContracts:
RemovedDuplicateResponsibilities:
Tests (exact command, passed/failed, artifacts):
PerformanceFixture / Baseline / Candidate:
ActualReadBytes / ConcurrentRequests / AllocatedBytes (适用项):
ProductBuildIdentity:
DeploymentStatus:
ProductionAcceptanceStatus:
RemainingRisks / NextDependency:
~~~

代码与测试完成后仍可能是 ready-for-external-deploy。七日 cache、七夜调度、外部生命周期验收各自记录起止时间，不合并成一个模糊的“已完成”。

## 5. 源码定位索引

定位以函数名为准，行号是 2026-09-11 工作树的辅助锚点。文件改动后用 symbol 搜索。

| 问题 | 文件与锚点 |
|---|---|
| 水合并发新增而非总量 | chat/hooks/useTurnSurfaceStore.ts：161 附近，targets filter → slice(0,2) |
| generation 与可见集合 | 同文件：96 起 reset；216 起 registerVisibleTurn |
| 测试未测未完成峰值 | hooks/__tests__/turnSurfaceStore.hydration.test.ts：drains more than two visible turns... |
| 聚合读后累加 | TokenUsageRecorder.cs：267 起读取 stats、274 起 +=、305 SaveChanges |
| attribution 读取 latest session 快照 | TokenUsageRecorder.cs：RecordContextLayerMetricsAsync / ResolveLayerToken |
| Composition 被 telemetry gate 控制 | DirectLlmClient.cs：1076 起 _telemetrySink 检查 |
| 未等待写穿 | PersistentCompositionVersionRegistry.cs：Observe / WriteThroughAsync |
| A/B/A 复用旧版本 | CompositionSnapshot.cs：SessionState.Observe，key → _versions |
| MAX/INSERT 与合同落差 | SqliteCompositionStore.cs：50 起 AppendAsync |
| 工具重新排序 | Tools/ToolExposurePlanner.cs：CreatePlan 的 OrderBy |
| hash 口径不一 | CompositionSnapshot.ComputeToolSpecHash / PrefixCacheSnapshotBuilder.Build |
| 全目录发现 | FileSubAgentRunStore.cs：540 起 discovery cache refresh |
| changed-file 整份读取 | 同文件：734 ReadAllLinesSharedAsync |
| 有界明细但无界 metadata/正文 | AgentConversationProjectionService.cs：GetConversationAsync，outputEvents/processMetadata |
| 最新 cursor 与视图读取分离 | 同文件：298 eventCursor |
| 视图判等不比内容版本 | chat/client/chatClientStore.ts：88 isConversationSame |
| 活跃轮询频率 | chat/index.tsx：selectedAgent 同步 effect，1200/5000 ms |
| 未释放工具集合 | AgentSessionManager.cs：225 RemoveInternal |
| 每次估算全文 gzip | Core/Platform/LlmOptions.cs：149 CaptureLlmRequest |
| 裁剪循环重复估算 | LlmRequestBudgetGuard.cs：Prepare / PrepareSoftCompaction |
| 未知 action 写入与业务错误 Ok | SaveMemoryTool.cs：52 action；135 后默认 upsert；205 catch |
| 未闭合 Action 参数 | MemoryToolArgs.cs：9 SaveMemoryArgs |
| lease 后等待锁在 try 前 | ChatExecutionWorker.cs：ProcessWithSessionLockAsync |
| 网络与本地后处理同 try | DirectLlmClient.cs：174 附近 retry try；213 usage write |
| 已修复监视异常 | ExecutionRunCoordinator.cs：MonitorAsync / ApplyMonitorOutcome |
| 实际入口和遗留副本 | PuddingAgent/Program.cs、PuddingAgent.csproj、Services 两个注册文件 |
| 文档产品入口冲突 | Docs/架构.md 开头、Docs/README.md 建议阅读顺序 |

上表省略共同前缀：chat 路径在 Source/PuddingPlatformAdmin/src/pages；Platform 服务在 Source/PuddingPlatform/Services；Runtime 服务在 Source/PuddingRuntime/Services；Core 在 Source/PuddingCore。完整路径和关键文件 hash 见 review-baseline.json；主报告各章节也列出了所属层。

## 6. 不允许混淆的完成条件

- 已扫描文件数量 ≠ 已逐行审查所有代码。
- 当前工作树存在修复 ≠ HEAD 已含修复 ≠ 生产加载修复。
- 通过现有 4 个 Python 测试 ≠ C#/前端回归通过。
- 字节 hash 一致 ≠ Provider cache 一定命中。
- Token hit rate 高 ≠ 任务有效产出高。
- 单次冷/热探针 ≠ 连续七日通过。
- Task Assigned / Message Delivered ≠ Execution Succeeded。
- 删除旧分支 ≠ 可以删除运行数据或历史证据。
