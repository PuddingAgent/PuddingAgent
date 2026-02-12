# Dev (代码助手) — 目标状态

## 状态
- **状态**: active
- **阶段**: implement
- **最后更新**: 2026-06-20 03:30 UTC

## 已完成里程碑
- ✅ P0: 记忆隔离 — MemoryRecallService + ContextPipeline agentInstanceId 过滤
- ✅ P0: 滑动窗口熔断 — RuntimeControlService 60s窗口 + 邻近预警 + /resume恢复
- ✅ P0: AgentStatusTool 竣工 — 新建 AgentStatusTool.cs + AgentWakeQueue 扩展
- ✅ P0: Code Map V0.2 Step 1 (code_outline 友好提示)

## 正在推进
- 🔄 P0: 自动压缩优化 — 80%上下文窗口阈值触发，ContextWindowManager 接入
- 🔄 P0: 缓存命中率优化 — 38%→≥60%，CacheDiagnosticsService 接入
- ✅ P0: 心跳自检机制 — 已列为每次心跳标准动作

## 待推进
- ⏳ Code Map V0.2 Step 2 — code_summary 符号摘要工具（方案就绪）
- ⏳ Jieba 分词器修复 — Resources 目录缺失，阻塞记忆搜索
- ⏳ 缓存键 Canonicalization — Prefix + Canonicalization + Embedding

## 阻塞项
- JiebaSegmenter: NuGet 旧式包 Resources 目录未复制到输出目录
- file_write 对 E:\github\... 源码头像路径需跨盘复制

## 决策日志
- 2026-06-20 03:30: 心跳自检 — goal.md 重建，工具全可用，Jieba 仍阻塞
- 2026-06-30: 记忆系统 v2 需求已完成 R1-R9 文档化，核心原则固定为“框架潜意识 LLM + 硬编码管道自动维护，不依赖 Agent 显意识提示词”；本次实现侧先修订 P0/R9，`manage_memory delete_book` 从归档改为真删除，并以回归测试验证 `list_books` 不再返回已删除 Book。
- 2026-06-30: 记忆系统 v2 设计方案已扩写为 Step 0-11，逐步列出每步目标、设计方案、约束条件和验收标准；除 R9 已实现外，R1-R8 仍按设计待拆分实现。
- 2026-06-30: R1 写入前检索回环推进第一阶段 — `save_memory` / `UpsertExperienceAsync` 写入前先限定当前 workspace 的 Library，exact Book 命中优先，全局 FTS 候选必须过滤到当前 workspace，避免跨 workspace 写入污染；下一步继续做 Chapter 级重复检测和取代语义。
- 2026-06-30: R1 写入前检索回环推进第二阶段 — `UpsertExperienceAsync` 在目标 Book 内追加 Chapter 前按 title/content/agentInstanceId 精确匹配已有 Chapter，重复写入复用原 `chapterId`，避免同内容无限追加；后续不使用内容 hash 判断语义一致性，改由潜意识/记忆 LLM 在候选边界内判断复用、追加或取代。
- 2026-06-30: R1/R2 写入前检索回环推进第三阶段 — Exact 未命中时，`UpsertExperienceAsync` 可把当前 Book 内同 Agent 作用域候选交给记忆 LLM 语义判定；LLM 可返回 `reuse_existing`、`supersede_existing` 或 `append_new`，非法 JSON、低置信度、非法 chapterId 都退回 append；内容 hash 不进入语义判重路径。
- 2026-06-30: R2 取代语义 Chapter 版本链阶段 — `supersede_existing` 通过校验后创建新的 active Chapter，旧 Chapter 标记为 `superseded` 并写入 `SupersededByChapterId/SupersededAt`；默认列表、FTS、向量和 scoped 检索过滤旧版本，`GetChapterAsync` 保留精确审计；`manage_memory list_chapters` 与 `grep_memory` 支持显式 `include_history=true` 返回历史版本并标注取代链。后续补 Fact 层取代元数据和循环保护。
- 2026-06-30: R1 并发同名 Book 防重阶段 — `Books` 建立同一 `LibraryId + Title + active` 唯一索引，`CreateBookAsync` 先查 active Book，唯一冲突后重读并返回已存在 Book；新增并发回归测试验证 8 路同名创建只留下一个 active Book。该约束只解决 Book 标题级幂等，不使用内容 hash 判断 Chapter/Fact 语义一致性。
- 2026-06-30: R4 Hook System v2 设计完成 — 新增 `Docs/superpowers/specs/2026-06-30-hook-system-v2-design.md`，确定 Hook v2 采用强类型 `IHookPublisher` + 现有 `IInternalEventBus/IPriorityEventQueue/EventDispatcher` 持久事件管道；内部 Hook 承担可靠框架管道，外部 Hook 第一阶段只读异步不可阻断。首个实现目标为 `session.compressed`，只发布事件并转持久 Job，不直接调用潜意识 LLM。
- 2026-06-30: R4 Hook System v2 第一实现完成 — 新增 `IHookPublisher` / `HookPublisher` / `session.compressed` payload 与 schema，`ContextCompactionService` 压缩成功后发布 Hook，`SessionCompressedMemoryMaintenanceHook` 订阅事件并转交既有 `ConsolidationJob` channel；该阶段不直接调用潜意识 LLM，不写 MemoryLibrary，后续接持久 `SubconsciousJobs`。
- 2026-06-30: R4 持久 `SubconsciousJobs` 第一实现完成 — 新增 durable 潜意识任务队列实体、schema、初始化 SQL、`ISubconsciousJobQueue` 与 `SubconsciousJobQueue`；`session.compressed` Hook 现在创建 `memory.consolidate_session` 持久任务，`SubconsciousWorkerService` 优先 lease 持久任务并标记 completed/retry/dead_letter，旧 `ConsolidationJob` channel 保留为兼容 fallback。幂等键只基于来源操作，不使用内容 hash 作为语义一致性判断。
- 2026-06-30: R4/R11 潜意识任务 Trace 证据层推进 — `SubconsciousJobQueue` 在 enqueue/lease/complete/retry/dead_letter 转换时写 `RuntimeActivity`，metadata 仅保存 job/source/status/lease/retry 等结构化定位字段，不保存完整任务内容；下一步补 Metrics/Insights 聚合和诊断查询入口。
- 2026-07-01: R4/R11 潜意识任务 Metrics/诊断第一阶段完成 — `SubconsciousJobQueue` 在 enqueue/lease/complete/retry/dead_letter 转换时写 `TelemetryMetric category=memory` 指标；`query_metrics.py subconscious-jobs` 可按 `job_type + source_hook_name` 聚合完成率、重试率、死信率和最后错误。下一步推进 legacy duplicate-learning 开关、idle/workspace 限流和真正的潜意识 LLM plan 执行。
- 2026-07-01: R4 legacy duplicate-learning 开关完成 — 新增 `SubconsciousOptions`，`SubconsciousConsolidationHook` 旧 agent-loop producer 默认不再注册，`AgentExecutionService` legacy fallback enqueue 默认关闭；仅显式配置 `Subconscious:EnableLegacyConsolidationHook=true` 或 `Subconscious:EnableLegacyAgentExecutionFallback=true` 时恢复兼容路径，避免 durable `SubconsciousJobs` 之外的隐式重复学习。下一步推进 idle/workspace 限流。
- 2026-07-01: Memory v2 切换为前置基础设施规划优先 — 新增 `Docs/superpowers/specs/2026-07-01-memory-v2-foundation-prerequisites.md`，将后续推进拆为 F0-F10 基础设施层；在该规划评审前不继续实现代码，下一推荐里程碑为 F3 Worker Scheduling & Resource Control，且语义一致性继续明确不使用内容 hash。
- 2026-07-01: F3 Worker Scheduling & Resource Control 设计展开 — 在 Memory v2 前置基础设施规划中补充 Milestone A 详细设计，明确采用 idle detector + workspace limiter + budget gate，而不是 pending job 立即执行；本阶段只解决调度、并发、预算和诊断，不调用潜意识 LLM、不生成 plan、不写 MemoryLibrary。
- 2026-07-01: F3 Worker Scheduling & Resource Control 实施计划完成 — 新增 `Docs/superpowers/plans/2026-07-01-memory-v2-f3-worker-scheduling-plan.md`，按 TDD 拆分调度契约、队列过滤/统计、`SubconsciousJobScheduler`、Worker 接入、DI、诊断脚本和文档验收；下一步若进入代码阶段，应按该计划逐项执行，仍不得混入潜意识 LLM plan 或 MemoryLibrary 写入。
- 2026-07-01: F3 Worker Scheduling & Resource Control 第一实现完成 — durable `SubconsciousJobs` 现在经 `SubconsciousJobScheduler` lease，支持 enabled、idle cooldown、dry-run、global/workspace/session limiter、queue stats、workspace rolling lease count、每 workspace job-count 预算门禁和 `schedule_skip` 诊断聚合；该阶段仍不调用潜意识 LLM、不生成 plan、不写 MemoryLibrary。真实 token/cost 预算等 F4/F5 有潜意识 LLM 执行指标后再接。
- 2026-07-01: F4 Subconscious Plan Protocol 第一实现完成 — 新增 `MemoryMaintenancePlan` schema、`MemoryMaintenancePlanValidator` 和 F4 schema/fixtures 文档，validator 可拒绝非法 JSON、跨 workspace 引用、低置信度操作和候选集外引用；该阶段仍不调用潜意识 LLM、不执行 plan、不写 MemoryLibrary。下一步补潜意识 LLM plan 生成、plan dry-run/持久化和 validator 结果可观测。
- 2026-07-01: F4 Subconscious Plan Generation dry-run 第一实现完成 — 新增 `SubconsciousPlanGenerationService`，潜意识 LLM 只输出 dry-run `MemoryMaintenancePlan`，框架立即执行 validator 并记录 `RuntimeActivity` / `TelemetryMetric` 成功失败结果；Runtime DI 已注册服务。该阶段仍不执行 plan、不持久化 plan result、不写 MemoryLibrary。下一步补 plan persistence/job result envelope 和低置信度降级策略。
- 2026-07-01: F4 Job result envelope 第一实现完成 — 新增 `SubconsciousJobResultEnvelope`、`SubconsciousJobs.ResultJson` 和 `ISubconsciousJobQueue.RecordResultAsync/GetResultAsync`；dry-run plan 结果可转为 accepted/rejected envelope 并由当前 lease owner 持久化。该阶段仍不完成 Job、不执行 plan、不保存原始 LLM 全文、不写 MemoryLibrary。下一步补低置信度/非法 plan 降级策略。
- 2026-07-01: F4 低置信度/非法 plan 降级策略第一实现完成 — `SubconsciousJobResultEnvelope` 增加 `decision/nextAction`；合法 plan 进入 `accept_for_execution/enqueue_for_execution`，低置信度进入无人值守 `quarantined/defer_for_recheck/complete_quarantined`，非法 JSON 进入 `retry_later/retry_job`，结构/边界错误进入 `reject_complete/complete_rejected`。该阶段仍不完成 Job、不执行 plan、不写 MemoryLibrary。下一步进入 F5 Memory Write Coordinator 设计。
- 2026-07-01: F5 Memory Write Coordinator 设计完成 — 新增 `Docs/superpowers/specs/2026-07-01-memory-v2-f5-write-coordinator-design.md`，确定所有写入入口统一转换为 `MemoryWriteCommand` 后进入 `MemoryWriteCoordinator`；F4 plan 只能经 mapper 进入 F5，`delete` 只映射为 `delete_requested` 并默认 review，不允许潜意识自动真删除。第一轮实现建议只做 DTO、validator、dry-run、F4 mapper 和 audit envelope，仍不接真正的 `MemoryLibrary` 写入。下一步写 F5 实施计划。
- 2026-07-01: F5 Memory Write Coordinator 实施计划完成 — 新增 `Docs/superpowers/plans/2026-07-01-memory-v2-f5-write-coordinator-plan.md`，按 TDD 拆分 DTO/validator、Runtime coordinator dry-run、F4 operation mapper、F4 accepted plan 到 F5 dry-run 证明、observability 和文档验证；计划明确第一轮不迁移 `save_memory`，不接真实 `MemoryLibrary` 写入。下一步按计划执行 F5a/F5b。
- 2026-07-01: F5 Memory Write Coordinator dry-run 第一实现完成 — 新增 `MemoryWriteCommand`、`MemoryWriteCommandValidator`、`MemoryWriteResultEnvelope`、`MemoryWriteCoordinator` dry-run、F4 operation mapper 和 coordinator Trace/Metrics；合法 F4 plan 可转换成 F5 dry-run 结果，非法 command 被拒绝并记录错误。该阶段仍不接真实 `MemoryLibrary` 写入，不迁移 `save_memory`。下一步选择 F5c 边界：先写回 Job result、先迁移 convenience wrapper，或先补 execute 最小实现。
- 2026-07-01: F5 dry-run Job result 衔接完成 — `SubconsciousJobResultEnvelope` 增加 `MemoryWriteResults`，`SubconsciousPlanGenerationResult.ToJobResultEnvelope(...)` 可接收 F5 dry-run result，`SubconsciousJobs.ResultJson` 可持久化并读回该审计结果；该阶段仍不完成 Job、不执行 plan、不写 `MemoryLibrary`。下一步在 Worker 串接 dry-run result、execute wrapper 或最小 execute 实现之间选择。
- 2026-07-02: F5 Worker durable dry-run 串接完成 — `SubconsciousWorkerService` durable path 在 F4/F5 依赖存在时生成潜意识 dry-run plan，将 accepted operations 映射为 F5 dry-run command，调用 `MemoryWriteCoordinator` 并通过 `RecordResultAsync` 写入 Job result envelope；该路径不调用旧 orchestrator、不 complete job、不写 `MemoryLibrary`。下一步在 coordinator execute wrapper 或 append/reuse/supersede 最小 execute 实现之间选择。
- 2026-07-02: F5 explicit append execute 第一实现完成 — `MemoryWriteCoordinator` 在显式 `Mode=execute` 且 `Intent=append_new` 时通过 `IMemoryLibrary` 创建 Library/Book/Chapter，返回 actual Book/Chapter ID，并记录 `memory_write.execute`；潜意识 Worker 仍固定 dry-run，`save_memory` 尚未迁移。下一步在 `MemoryLibraryConvenience.UpsertExperienceAsync` wrapper 迁移或 reuse/supersede 最小 execute 之间选择。
- 2026-07-02: 潜意识调试闭环第一实现完成 — 新增独立 `/api/debug/subconscious/*` 调试 API 组，可通过 `Subconscious:DebugApiEnabled=false` 整体关闭；API 支持 runtime start/stop/status，语义为暂停/恢复潜意识 worker 租约与处理而非停止进程；新增 `Tools/Diagnostics/subconscious_debug.py` 和独立 `logs/diagnostics/subconscious/YYYY-MM-DD*.jsonl` 轮转日志，日志只写状态、ID、计数和配置快照，不保存原始记忆内容或完整 LLM 输出。
- 2026-07-02: 潜意识记忆归属隔离 fix 完成 — 新增 `SubconsciousMemoryScope`，把“框架潜意识调用者身份”和“目标记忆归属边界”分离；F4 plan generation 必须携带 workspace/agent/session/library scope，LLM invocation 使用目标 scope 记录调用归属，validator 拒绝跨 workspace/agent/session/library 的 plan source，F5 `subconscious_plan` 写入源必须带 `agentId` 并透传 `memoryLibraryId`。该 fix 解决的是记忆污染隔离，不改变潜意识仍为 dry-run 的执行边界。
- 2026-07-02: 潜意识 LLM 脚本/API 触发与行为评估完成 — `/api/debug/subconscious/trigger` 可投递 debug-only durable `memory.consolidate_session` Job，`/api/debug/subconscious/jobs/{jobId}/result` 可读取结构化 result envelope，`subconscious_debug.py trigger --wait` 可一键触发并等待结果。实测 job `2fe8ecb5f39d4b29a2fb2d8c793302ff` 进入 F4/F5 dry-run，返回 accepted plan + `memoryWriteResults[0].status=dry_run`，队列最终 `completed`，且对应 `SourceSessionId=subconscious-api-eval-20260702-222900` 的 `Chapters/MemoryPreferences` 写入数均为 0。
- 2026-07-02: 潜意识受限后台 Agent 模型修订完成 — 明确潜意识不是普通工具型 Agent，而是只读会话记录/压缩摘要/已有记忆/候选集、只输出结构化 `MemoryMaintenancePlan`、不接触物理电脑环境、不调用普通工具/终端/文件系统/网络/API、无人值守运行的后台 Agent；低置信度不再进入人审语义，统一改为 `quarantined/defer_for_recheck/complete_quarantined`，潜意识 delete 进入 `autonomous_delete_not_allowed`。
