# PuddingAgent 自修复代码任务书（Flash 执行版）

日期：2026-09-14。状态：代码级施工方案；未实施、未验收。

用户已授权 PuddingAgent 实施自修复。先完成 A1，交付独立 commit、测试结果和待部署清单，再进入下一批；不要用又一篇调查报告代替代码。此文件给出已核对源码的判断逻辑，避免重复全仓调查。

## 0. 已知证据与执行边界

基线：`cfa8ea7`；审计见 [最近 24 小时工作效率评估](PuddingAgent最近24小时工作效率评估-2026-09-14.md)。

- 7 个子 Run 只有 3 个 completed；两个自动任务在 21 / 16 轮因累计输入达到 1,000,000 提前停止，配置的 600 轮并未耗尽。
- 一个 118 轮子 Run 花费 81.67 分钟、输入 13,253,899 tokens，包含审批拒绝和无进展等待；高缓存命中不代表任务有效完成。
- Gateway 加权命中率 95.1818%；后台维护只有 20.33%，贡献 35.74% 的 miss。不能删任务或停必要工具来伪造 >99%。
- 17 次确认心跳有 7 次失败；下午 6 次心跳在同一个图片路由错误上失败。当前外部控制器已在 19:45 左右启动新 Core，但尚不能据启动事实宣称原场景恢复。
- 目前 `AgentProjectionDtos`、`AgentConversationProjectionService`、Web chat 状态组件、`ResponsesLlmGatewayTests` 等有其他任务 WIP。不要修改、暂存或还原这些改动。预算相关文件若也出现新改动，先检查差异，不覆盖。

仓库：`E:\github\AgentNetworkPlan\PuddingAgent`。开始只需读取本文件、根目录指令和目标文件；`git status --short` 记录他方 WIP。不要再次全量统计 24 小时日志，不要读取 provider secret，不要复制运行数据库。临时脚本和测试日志放仓库 `temp/`，构建输出遵守仓库规定，不能写入 `D:\data`。

## 1. A1 / P0：输入容量与累计消耗分离（本次立即实施）

### 1.1 确定语义

保持字段 `MaxInputTokens` 的本轮改动范围可控，但在 WorkUnit / Runtime 契约中明确为“单次模型请求输入容量”；与模型配置的同名字段分别取最小值。累计输入仍完整计量，不作为此轴的耗尽条件。成本与输出仍累计扣减，不允许关闭它们以绕过预算。

| 量 | 语义 | 父子传递 |
|---|---|---|
| MaxInputTokens | 单次请求输入容量，0 表示该 WorkUnit 不额外设输入上限 | 保持不变，不减已用输入，不除以子任务数 |
| InputTokens | 当前执行包含已结算子执行的累计输入账本 | 累加，保留真实值 |
| PeakRoundInputTokens | 本执行实际模型请求的最大 prompt tokens | 仅作实际请求容量判断和诊断，不能把子执行累计量当成一轮 |
| MaxOutputTokens / MaxCost | 本执行剩余累计输出 / 成本预算 | 扣减，批量派生按份分配，耗尽后诚实为 0 |
| 未启用累计轴 | 无该项限制 | 派生后仍未启用，不得被统一的派生标志解释为耗尽 |

输入容量使用 `observedPromptTokens > limit` 判超限；恰好等于容量允许。输出和成本保持累计达到上限后不再启动下一轮的语义。原有模型上下文、输出预留、安全 buffer、工具总次数、时间和用户指定轮数限制继续生效。本次不要将 600 改小，也不要把所有 WorkUnit 默认强行改成 600；弹性轮次按 ADR-087 的独立任务实施。

### 1.2 修改清单与逻辑

**A. `Source/PuddingCore/Runtime/ITurnExecutor.cs` — `ExecutionUsageBudget`**

1. 更新字段和 `TurnExecutionContext.UsageBudget` 注释：容量冻结不变；可消耗预算只能递减。
2. `IsDerivedRemainder` 不能独自决定所有零值轴的语义。新增两个明确的累计轴标志，例如 `OutputLimitEnabled`、`CostLimitEnabled`，以及统一判断 `HasOutputLimit = OutputLimitEnabled || MaxOutputTokens > 0`、`HasCostLimit = CostLimitEnabled || MaxCost > 0`。根预算正值自动视为启用；派生时将真实启用状态保留下来，即使剩余归零。
3. `IsDerivedRemainder` 可以继续表达来源，但删除“派生后每个 0 都是耗尽”的判断。不要恢复 `Math.Max(1, remaining)`。

**B. `Source/PuddingRuntime/Services/AgentExecution/ExecutionUsageBudgetTracker.cs`**

1. `Record` 拆分成清楚的两个入口：`RecordInvocationUsage(usage)`、`RecordDelegatedUsage(usage)`。共用私有累计记账逻辑，避免两套成本公式。
2. 实际模型请求入口先完整累加 input/output/cache/cost，再更新本执行 `PeakRoundInputTokens`，按本次输入 / 峰值与输入容量比较。即使超限，账本也必须记录本次真实消耗。
3. 子执行入口只汇总已结算累计 usage，不更新本执行单轮峰值，不做“该累计输入是一轮”的判断；仍检查累计输出和成本。子执行已经继承同一输入容量并在自身请求边界执行检查。
4. `CreateRemainingBudget()`：输入容量直接复制；output/cost 按原饱和减法归零；上述两个启用标志从原预算的 `Has...Limit` 继承。保留已有 saturating add、防负数、cacheHit clamp 和价格缺失 fail-closed。
5. `EvaluateBeforeRound()` 对“已启用且剩余 0”的累计轴立即停止；未启用的零轴不停止。若上次真实输入已超容量，仍禁止继续正常轮次。
6. `CreateUsageSnapshot()` 保持累计输入 / 输出 / hit 口径。不要为了让预算通过把账本改成峰值。

**C. `Source/PuddingRuntime/Services/SubAgentInvocationService.cs` — `DivideUsageBudget`**

输入容量原样复制；只分割 output/cost。派生后保留两个累计轴启用标志，份额为 0 时明确不可执行。不引入新的自定义生命周期管理。

**D. `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs` — `DescribeBudgetInfeasibility`**

复用同一启用判断：input 正值低于最低输入容量才拒绝，0 表示没有额外 WorkUnit 输入限制；output/cost 仅在该轴启用时检查耗尽 / 最低可执行份额。保留模型自身硬窗口。错误文案把 `input 剩余` 改为 `input 单次容量`，不要建议靠减少父轮次“恢复”输入容量。

**E. 请求和委派接线（两种执行模式都改）**

- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs`：当前约 1181 行实际请求 Record；约 1578 行 delegatedUsage Record；约 768 行 `LlmRequestBudgetGuard.Prepare`。
- `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs`：约 856 行实际请求 Record；约 1275、1984 行 delegatedUsage Record；约 697 行 Prepare。
- 行号仅作定位，以方法 / 变量为准。两个实际请求入口用 `RecordInvocationUsage`；三个委派汇总入口用 `RecordDelegatedUsage`。不能只改流式路径。
- 检查现有成功 / 失败委派结算分支，不重复记录同一个子执行 usage；保留现有失败且有 usage 的成本记录。

**F. `Source/PuddingRuntime/Services/LlmRequestBudgetGuard.cs` — `Prepare`**

增加显式可选的 WorkUnit 输入容量参数，计算 `min(模型有效输入窗口, 正值 WorkUnit 输入容量)`；两个执行策略从当前冻结预算传入。long 转 int 必须先 clamp 到 int.MaxValue，0 / null 表示没有额外限制。复用现有受保护尾部、完整工具调用会话单元裁剪与超限异常，不另建裁剪器。不允许先付费发超大请求，再仅靠返回 usage 阻止下一轮。

这个前置检查仍是本地估算，不承诺精确预知供应商计数。真实 usage 回来后的校验是补充防线；不能把 estimate 命名成 provider 实报。

**G. 冻结计划 / 注释 / 版本**

- `Source/PuddingCore/Scheduling/TaskExecutionPlanContracts.cs`、`Source/PuddingCore/Platform/IExecutionCommandReader.cs`：同步 WorkUnit 输入容量语义。
- `Source/PuddingPlatform/Services/Scheduling/TaskExecutionPlanCompiler.cs`：删除 `MaxInputTokens = MaxCost × 1,000,000` 的等价消耗点说明。现有正值上限先保留，实际仍与模型窗口取 min；本任务不顺手重调价格或输出配额。
- 输入轴语义变化必须进入计划身份：将编译器 material 的 `planVersion` 升到 2，使 fingerprint 变化，新增测试证明版本 / 指纹与旧计划不同。先在目标链的计划读取 / 校验处检查是否有 `PlanVersion == 1` 硬编码；如有必须同步，不能静默复用旧冻结计划。新部署验收用重新生成的计划；不直接 SQL 改写历史计划和账本，不恢复旧 assignment。
- 核对 `ExecutionRunCoordinator`、`TaskGoalDispatchTransactionStore`、`GoalContinuationWorker` 的预算映射，确认字段原样传递；只修改确实需要的接线。不要扩展到 Scheduler 重构。

### 1.3 必须通过的回归用例

优先修改现有 `ExecutionUsageBudgetTrackerTests`、`SubAgentBudgetLifecycleTests`，并在现有 guard / compiler / 执行策略测试类增加真实接线用例。删除旧“输入累计耗尽是正确行为”的断言，替换为以下语义；不要删成本、权限、历史完整性的保护断言。

1. 输入容量 1,000,000，600 次实际请求各 input=80,000，output/cost 未启用：600 次不因输入轴停止；累计 input=48,000,000、峰值=80,000。纯内存测试，禁止真实调用模型 600 次。
2. 单次 input=1,000,000 允许；单次 1,000,001 拒绝；拒绝时累计量仍含这一轮。
3. 容量 100,000，实际请求 12,000 / 24,473 / 18,000 后，派生输入仍为 100,000；output/cost 正常减少。
4. 两个子任务分配同一输入容量，output/cost 份额总和不超过剩余；剩余归零不可伪装为 1。
5. 一个累计 input=1,790,000 的子结果回填，不形成 1,790,000 的假单轮峰值；累计 input / output / cache / cost 仍计入且只计一次。
6. 根 output=0/cost=0 的未启用轴，经 tracker / divide / admission 后仍可委派；已启用轴耗尽到 0 时，在下一轮和派生边界均被拒绝。
7. 仅开启 cost 且价格未知、或需要用量核验但 provider usage 缺失，仍 fail-closed；现有 cache-hit 计费测试通过。
8. 请求 guard：WorkUnit 容量小于模型窗口时生效，反之仍用模型窗口；超限且只剩受保护内容时异常返回，不发送供应商请求；long 大值和 0 不溢出。
9. Streaming / Buffered 的实际请求与委派汇总均走正确入口，失败委派不漏计 / 重计。
10. 新计划 PlanVersion=2，fingerprint 对语义版本敏感；原 Task/Assignment/WorkUnit 投递字段不丢失。

验证命令先按现有项目运行：

```powershell
dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --no-restore --nologo --filter "FullyQualifiedName~ExecutionUsageBudgetTracker|FullyQualifiedName~SubAgentBudgetLifecycle|FullyQualifiedName~LlmRequestBudgetGuard"
dotnet test Source/PuddingPlatformTests/PuddingPlatformTests.csproj --no-restore --nologo --filter "FullyQualifiedName~TaskExecutionPlanCompiler|FullyQualifiedName~GoalContinuation"
```

将新加的策略接线测试类加入相应 filter。上述测试通过后不重复全仓跑同一批测试；只有新改动或失败才重跑有关测试。不能写“600 轮测试通过”却只证明配置常量为 600。

### 1.4 A1 交付

只暂存本任务文件；检查 diff 和 staged stat，独立 commit。回复格式：commit、改动文件、测试数量 / 失败数、输入累计与峰值的反例结果、剩余风险、部署要求。看板关联既有预算语义卡，不重复建同题卡；只报告源码验证，不标生产完成。不自行关闭 / 重启承载自身的 Desktop/Core。

## 2. A2 / P0：压缩口径纠偏（下一批，先不混入 A1）

依据 [频繁压缩诊断](上下文频繁压缩诊断-2026-09-13.md)。目标不是把 65% 调大：当前 `GetHealthAsync` 把整请求 `usage.UsedTokens` 与 `MaxActiveRawTokenBudget=131072` 比较，系统提示和 schema 也触发“原文超限”。

文件入口：`ContextCompactionService.GetHealthAsync`、`ContextCompactionService.TokenEstimation.cs`、`ContextWindowManager.TryAutoCompactAsync`、`CompactionCoordinator`、`ContextUsageSnapshotStore.RecordProviderUsage`（`Source/PuddingCore/Platform/LlmOptions.cs`）。

拆成两个独立提交：

1. **口径修正**：抽出一次构建的候选窗口统计，原文 cap 只用实际可压缩原文 token；从 canonical transcript 同步后按稳定 MessageId 去重，排除 `compact_summary`、已 `CompactedBy` 原文以及受保护尾部。选择窗口与统计复用同一个结果，不能另开“全表求和”或只看过期 DB。完整请求预算仍用于请求发送安全检查；ProviderPromptTokens、LocalEstimate、SafetyUpperBound 保持不同字段 / 标签，不能将 max 后的估算当作供应商实报。
2. **无收益抑制**：Coordinator 记录候选窗口 fingerprint / generation 与本次结果。对同一窗口的 no-op / 摘要增长不再反复请求摘要，新增原文或明确手动压缩再评估；保留硬窗口保护。canonical completed 必须携带 outcome=applied / skipped_no_gain / skipped_no_candidate，失败独立记录，不能把跳过计作写入成功。不修改另一个任务正在施工的 Web chat 文件。

必验反例：整请求 204,942、候选原文 17,439、raw cap 131,072，不因 raw cap 升为 Critical；候选原文超 cap 才触发该理由；同窗口无收益不重复调用摘要；新候选使抑制失效；只剩 summary 是 no-op；当前轮原文同步、tool-call/result 配对、回滚与历史覆盖既有回归不退化。真实 24h 频次验收必须按活跃请求数 / 新增原文 token 归一化，不能把下午任务失败导致压缩变少称为优化。

## 3. A3 / P1：停止无效等待，但保留长程执行能力

先修共享执行 / 工具进度层，不往主代理提示词堆“不要重复”。追踪同一 operation/job 的进度标识（状态、输出游标、退出码、目标后置条件），工具参数字符串不同不代表新进展。审批 pending 应持久等待授权事件；denied 明确停止该操作；不得改参数重试绕过审批。

对已退出的命令先核验目标后置条件；重复无产物标记 `execution_stalled`。对仍运行且有新输出 / 状态推进的长任务，保持等待；对无新输出的长计算不能仅凭输出静默杀进程，需使用其声明的 deadline / 无进展策略。复用既有 cancellation、进度、审批和事件机制。本次先把“同一个 job 两次 600 秒等待”和“审批已拒绝仍重试”做成确定性回归；不要以缩小全局 MaxRounds / Timeout 修复。

在修复前，执行本任务的 Agent 应采用此操作纪律：测试启动后等待同一个 job，核对 exit code 和测试摘要；相同错误第二次出现就定位具体源码或阻断条件，不创建参数变体去撞同一错误。不允许为完成测试删除审计或权限检查。

## 4. A4 / P0：Blocked 恢复必须保持结算不变量（单独设计后实施）

原 `task-bound-goal-settlement-deadend-20260914.md` 中“NeedsReview 且不释放 assignment”不能直接实施。当前 `GoalSettlementStore.ApplyDecisionAsync` 的 task-bound 终态释放逻辑明确维护唯一 active `(conversation, agent)` 执行槽；`binding.AssignmentId` 留作历史引用并不是仍有活跃执行权。

正确方向：保留已失败尝试与账本，通过既有管理命令 / durable outbox 创建新的 fenced attempt；分类 transient、awaiting_review、budget_exhausted、policy_denied，不把它们都无限重试。容量语义修复不能自动补充已耗尽成本；必须核验新的执行计划、剩余授权、退避、次数与去重键。恢复动作需 CAS 验证当前任务版本且不复用旧 assignment。两次并发恢复只产生一次新尝试，重启不重复投递；人工 review 不应自动变为任务 completed。

本次 A1 不修改 verifier、settlement、repair coordinator 或数据库。A4 待明确恢复策略与状态转移测试后开工，避免扩大第一批风险。

## 5. 自改进与验收闭环

每一批都必须产出 `缺陷反例 → 源码修复 → 自动回归 → 独立提交 → 外部部署 → 新会话功能验收` 的对应证据。Pudding 内部只能交付 `ready-for-external-deploy`，新构建功能 smoke 和外部生命周期验收分开记录。

不要每轮心跳复述整份 goal.md；仅保存本批 checkpoint（commit、测试结果、下一步、阻断原因），长日志引用制品。心跳不能推进时应记录具体事件 / 依赖并等待，不用新模型调用重复报告“仍受阻”。本任务的交付回复不要宣称达到缓存 >99%、全天稳定运行或完成所有自循环改进；这些需要后续按同一任务负载验证。
