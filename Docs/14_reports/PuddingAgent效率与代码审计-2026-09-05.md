# PuddingAgent 效率、调度、工具与代码审计

> 2026-09-05 修正：本次历史快照的 SQL 预筛选把带 `T/+08:00` 的字符串与数据库 UTC 空格分隔时间直接比较，漏掉目标窗口开头 8 小时。下文七日用量数字保留为原始审计证据，不再作为完整七日验收口径。修复脚本按显式 UTC 半开区间重查后，DeepSeek 为 1,105 请求、76,456,907 输入、6,231,627 miss，命中率 91.849%，有记录 6/7 日。详见 `PuddingAgent首轮修复与验证-2026-09-05.md`；差异不是优化收益。

审计日期：2026-09-05，Asia/Shanghai；实时取样约 07:16–07:29。七日统计窗口为 **2026-08-29 00:00 ≤ t < 2026-09-05 00:00**，当天数据另列。本次是评估与审计，未实施产品修复、重启、派发、任务状态变更或令牌变更。

结论：**有效吞吐未达标，自动调度被旧执行占用阻塞；缓存目标未达成；已知根因的小任务仍消耗大量探索与恢复轮次。Chat 的鉴权重试和 Core 的历史归档扫描存在可以直接定位的实现问题。源码、工作区测试、运行中程序集和看板完成状态没有形成一致的验收闭环。**

但不能把昨晚所有空闲归因于 Agent 不愿工作：实际外部指令在 21:16 将其限制为只读诊断。本报告将授权限制、缺少可派任务、平台阻塞和 Agent 行动效率分开评价。

## 1. 证据与口径

- 任务：External Task API v1 全量读取，105 张卡；凭据 doctor 正常。
- 运行：`D:\data\databases\pudding_platform.db`，SQLite `mode=ro`；核对 Task、Assignment、Binding、Goal、Turn、ExecutionRun、调度决策与两类 Token 账本。
- 用量总量：`llm_gateway_usage_events`，按服务商返回的 usage 记录加权，命中率为 `Σhit / Σ(hit+miss)`。本窗口 3,223 条、source 无重复、每条 input=hit+miss。**本地账本尚未与供应商账单独立对账；本报告不把估价 TotalCost 当实际扣费。**
- 归因：`TokenUsageEvents`，不与网关账本相加。其 2,945 条 attribution source 无重复，但覆盖面与网关不同。
- 工具：44 份可读子代理 `tools.jsonl`，2,308 次调用；再核对具体 Run 的事件、预算、模型路由和主会话工具事件。
- 代码：HEAD `731d5e0`，重点审计调度、执行、文件工具、Chat 和最近两项修复；不是对所有历史提交逐行背书。七日有 37 个仓库提交，含归并和文档，不能据共享 author 字段全部归因给 PuddingAgent，更不能等同完成 37 个任务。
- 当前程序集：Core PID 32444、Desktop PID 20284，Core 的父进程确为 Desktop；`PuddingCore.dll` / `PuddingPlatform.dll` 的 ProductVersion 都是 `1.0.0+01558a8…`。新测试产物标识为 `731d5e0…`，SHA-256 与部署目录不同。
- 页面：读取已打开的 Edge Chat DOM、日志与截图。它与 Desktop WebView2 是不同进程；该页面的结果不直接当成 Desktop 性能测试。

机器可读快照：[metrics JSON](pudding-agent-efficiency-2026-09-05.metrics.json)。只读查询脚本、反序列化复现与 TRX 位于系统 Temp；不包含凭据。

## 2. 指标完成情况

| 维度 | 本次实测 | 判断 |
|---|---|---|
| 七日看板新增 Completed | 2，约 0.29 张/日；1 张 README smoke、1 张完成结算实现卡 | 吞吐低，且不能直接作为产品验收数 |
| 自动 Task-bound Goal | 10 次尝试、10 次 Failed；其中 1 次 evidence_incomplete、9 次 iteration_failed | 没有成功自动验收闭环 |
| 默认助手调度 | 无活跃 Run，却被旧任务占用；731 条 preferred_busy（07:18 快照） | 异常 |
| DeepSeek 缓存 | 998 请求，69.284M 输入，5.864M miss，**91.54%** | 未达到 >99% |
| BigModel 缓存 | 2,225 请求，204.454M 输入，15.137M miss，**92.60%** | 作为独立模型基线，不与 DeepSeek 混验收 |
| 总输入 | 273.738M；全服务商加权命中 92.33% | 高命中仍需与交付量合看 |
| 子代理记录 | 47：completed 20、budget_exhausted 20、failed 6、interrupted 1 | 42.55% 记录耗尽预算；completed 不等于任务验收 |
| 子代理轮次 | 均值 38.43、P95 60；平均工具 49.11 | 宽松均值/P95 门槛表面满足，quick 门槛仍明显不满足 |
| 工具显式失败 | 148/2,308，**6.41%** | 未包括“成功返回但无产出” |
| Core 短采样 | WS 1,522 MiB、Private 1,408.4 MiB；15 秒平均 CPU 2.89%（8 核归一） | 占用值得优化；未复现截图的持续 18.1% |
| Desktop 短采样 | WS 207.3 MiB、Private 306.0 MiB；CPU 0.81% | 与截图时刻不同，不作为泄漏趋势 |

七日中有记录的 DeepSeek 全部为 v4-flash：08-29 **95.24%**、08-31 **88.38%**、09-01 **82.24%**、09-02 **87.25%**、09-03 **93.51%**、09-04 **90.62%**。08-30 无记录，v4-pro 全窗口无记录，均不能填成 100% 或宣称通过连续七日验收。09-05 00:00–07:18 另有 BigModel 21 请求、1.391M 输入。

依据现有缓存卡 `af25d72c75634aae8579ec2c0a26ce08`，应继续维持 InProgress。DeepSeek attribution miss 中，`tool_spec_changed` 为 724,249，`session_rehydrated` 为 472,343，`system_prompt_changed` 为 292,455，未知原因 3,794,369。**未知占 71.82%**；有 reason label 也不等于已实现 firstChangedSegment/offset/hash 的完整归因，95% 可解释性目标尚无证据达成。

## 3. 夜间窗口与任务看板

### 3.1 首要阻塞的完整链路

```text
3bd2a4b0 InProgress
  → active Assignment 21242d21…（未释放）
  → Binding 指向 Run f32f8a17…
  → 该 Run 实际 succeeded，已有 completed_at / terminal_sequence=604677
  → Tracker 因存在 executionId/sessionId 仍返回 Healthy: legacy_execution_claimed
  → 默认助手 active_task_owned
  → 77883a50 Ready + auto_dispatch=true → preferred_busy → 推迟 5 分钟
```

07:20 的实际扫描日志为：`idle=2 busy=1 candidates=1 eligible=0 started=0 tracked=1 healthy=1 repaired=0`。09-03、09-04 的 00:00–08:00 各有 96 条 preferred_busy，等于两个完整夜间观察区间持续每五分钟被同一阻塞拒绝。

另两位 Agent 虽 Idle，但目标卡显式指定默认助手且不允许 fallback；不能因此简单判为“调度器没有利用空闲 Agent”。修正所有权是第一步，是否开放其他 Agent 接任务是独立路由决定。

### 3.2 库存并非全部可执行

当前分布：Backlog 47、Ready 2、NeedsReview 2、InProgress 2、Blocked 11、Completed 36、Cancelled 3、Archived 2。

全部 47 张 Backlog 都未启用 auto-dispatch；全板只有 8 张 opt-in，其中 Ready 仅 1 张，其余大多已 Blocked/Completed。日志 `backlog=0` 表示当前可参与自动 refinement 的集合为空，并不是看板真的没有 Backlog。零失败卡也不能说明可靠：失败的自动尝试主要沉淀为 Blocked 卡和 Failed Goal。

两张七日完成卡：`c3f08eec…` 是 README smoke；`4ed930e7…` 是 Task 完成事实/释放实现，其关联旧自动 Goal 仍是失败历史，后来由 Agent 更新为 Completed。最近一条独立 task_evaluation 停留在 08-22。**缺少 evaluation 不必然使完成无效，但本次不能把这两张卡算作两个独立通过的自动产品验收。**

P0 `38da54f1…`、P1 `6495cee8…` 仍 Backlog、auto=false；Chat umbrella `ff110685…` 仍 Blocked，原因为 assignment_execution_missing。需要修复既有任务链，而不是继续创建同义的大卡。

### 3.3 sleep 与受限窗口

七日 attribution 含 59 次 sleep。主会话 canonical 事件 seq 635067、639522、690135、694100 记录了 `min_idle_seconds=1800 / max_idle_seconds=3600`：委派后用半小时到一小时心跳跟踪。该工具是**登记下一次心跳偏好并立即返回**，不能把参数相加作为实际睡眠小时数；问题在于依赖低频心跳接回工作，而未形成可靠的 child terminal → parent continuation。

09-05 00:00–07:18 没有新自动 Task-bound Goal、没有看板完成，只有 3 个完成的主会话 Turn 和 21 次 BigModel usage。Turn 结束时间并集约 8.08 分钟，仅为调用轨迹观察量，不能当准确有效利用率：未纳入全部后台工作、可派库存和运行时停机分母。

此外，已读取真实入站指令：09-04 19:51 只读计划；20:42 仅授权 P0-S1；21:06 回滚该轮未验收变更；21:16 再次要求只读，禁止代码/终端/子代理。**21:16 之后不施工符合当时边界。**控制器未重新提供可执行任务，也是窗口没有工程产出的原因之一。

00:00–08:00 只是本报告统一比较区间。实际配置 DeepSeek 谷窗为 00–09、12–14、18–24；BigModel 为 00–14、18–24（有效期至 09-30）。这里只确认本地调度配置，不对外部实际收费规则作推断。

## 4. 工具效率与行动轨迹

A/B 两项已知根因修复的网关账本合计 **231 次模型请求、22.713M 输入、0.226M 输出**，即约 22.939M 总 Token；它与 Agent 报告的 22.77M 不同，后者更接近 attribution 口径，不能混用。

| 轨迹 | 事实 | 可避免的开销 |
|---|---|---|
| A：sort_order fixture | 首段 60 轮/69 工具；续段再 60 轮/60 工具，后者显式工具失败为 0，仍 budget_exhausted | 已给定根因后仍广泛读/搜；错误修改 SQL 映射；继续投入预算不能保证补丁正确 |
| A 续段模型 | 从 DeepSeek 转成 BigModel；该模型段 8.249M 输入，缓存 **98.25%** | 接近 99% 的缓存和零显式工具失败，仍不能证明有效交付 |
| B：窗口 Unknown | 首段 60 轮/88 工具、12 个显式失败；后段 23 轮/25 工具、1 个失败 | shell 超时、终端审批/参数/路径反复修正；“12 个失败”不能全说成同一种 30 秒超时 |
| 父级修补文件 | 页面轨迹出现参数名混用、断言消失、缩进修复和重复终端 quoting；canonical 后续 file_patch 仍用 oldText/newText，被拒绝 | 工具描述与实际 schema 冲突，成功码缺乏目标后置条件 |

44 份子 Run 归档中：`file_patch` 31/132 失败（23.48%）；`apply_patch` 14/18 失败（77.78%，与 file_patch 分开统计）；`shell` 46/287 失败（16.03%）；`search_tools` 102 次。`request_tool_approval` 78 次，其调用时长相加约 80.5 分钟，包含等待并可能跨并发运行，不是单个用户等待时长。

因此行动轨迹**明显存在可删除的步骤，不能视为最优**。但没有对照实验，不能宣称某种新方案达到数学意义上的最优。已知根因任务应采用可测试的短路径：读取精确坐标 → 一次原子补丁 → 单次隔离测试 → 读取统计/产物后置条件 → 交付。模型应只处理异常差异，而不是反复学习执行 shell 的方式。

现有 quick 卡要求 5 轮内有效 diff、10 轮/15 工具/500k 输入内终结。本次明显不满足。归档声明的实际预算却是 `max_rounds=40` 加 `budget_grace_rounds=20`，并有 86400 秒 deadline。任务书中的 quick 与运行时预算并未对齐；`ExecutionUsageBudgetTracker` 目前只覆盖可选 Token/cost 预算，没有通用 round/tool/failure-family 门禁。

## 5. 代码审计发现

### F1 · P1：旧 execution claim 被永久判 Healthy，阻塞自动吞吐

位置：`Source/PuddingPlatform/Services/Scheduling/TaskExecutionTracker.cs:238–242`（近期来源 `2d8d02d`）。有 executionId、sessionId 或 delivery claim 任一项即返回 Healthy，没有查实际 Run 的终态、租约和进展。真实数据库与扫描日志已复现。

修复方向：按 lineage 关联 canonical Run；分别处理活跃、终态待结算、终态未释放、孤儿 claim。在 Serializable 事务中重验 Task/Assignment/Reservation 再释放，Task 进入需要处理的状态，不因 Run succeeded 自动伪装任务成功。已有 Scheduler 生命周期方案 §3/§4 写出了这一要求，但代码尚未闭环。

### F2 · P1：file_patch schema/示例冲突；缺失替换文本退化成删除

位置：`Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs:17,291,1235`。工具示例写 `op/oldText/newText`，实际模型是 `Type` 和 `old_text/new_text`；嵌套对象不由基类顶层归一化修复。replace 只检查 OldText，`NewText ?? ""` 把缺字段变成空替换。

本轮用当前编译出的真实 `FilePatchOperation` 类型验证：`{"old_text":"KEEP","newText":"REPLACED"}` 得到 OldText=KEEP、NewText=null、替换 fallback=""；`new_text` 才正确。未对用户文件执行此复现。

修复方向：从唯一 schema 生成示例；替换文本必须显式存在，缺字段/未知字段在写入前拒绝；显式空字符串删除与缺失字段区分；任何失败不能产生部分写入。补“照抄工具自己的示例”及缺字段不改文件测试。

### F3 · P1：401 引发固定 1.2 秒 SSE/回放重试

位置：`Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts:139–154,296–314`。仅 404/410 走终止分支，其余状态一律重连；重连先 replay 再重开 SSE，无指数退避、无鉴权终止。

07:20 浏览器日志两轮相隔约 1.21 秒，均为 replay 401 → SSE stop/start → SSE 401。页面同时报消息队列 401。修复方向：401/403 进入明确的重新鉴权/权限状态，统一停止 SSE、replay 和关联轮询；网络/5xx/429 使用有上限退避与单飞恢复，鉴权恢复后从正确 cursor 续传。不要用延长 toast 时间解决请求循环。

### F4 · P1：空闲时持续全目录扫描和历史事件整文件读取

位置：`Source/PuddingPlatform/Services/SubAgentConversationProjectionWorker.cs:15,47` 与 `FileSubAgentRunStore.cs:522–537,699–706`。

worker 每 2 秒请求处理 100 Run，但先递归枚举并排序整个 WorkspacesRoot 的 events.jsonl，再应用 maxRuns。每个 Run 即使 cursor 已到末尾，仍先 ReadAllLines 整个文件才发现无需投影。数据库索引对应的现存归档共有 1,166 份 events.jsonl，合计 340,539,969 字节（约 324.8 MiB）。线程快照实际命中该路径。

修复方向：持久化 dirty-run/outbox，完成且已追平的 Run 退出热队列；按 byte offset 流式读新增部分，设置字节/时间预算和低频有界恢复扫描。maxRuns 必须约束发现阶段，不能只限制扫描后的处理阶段。该证据证明有不必要的 IO/字符串分配；尚不能量化其占全部 CPU/Private memory 的比例，也未证明内存泄漏。

### F5 · P1：提交中的 fixture 仍失败，生产迁移修复没有覆盖测试种子

`8603746` 提交的 `TaskRecallAuditEngineTests` 自建表声明 `sort_order INTEGER NOT NULL`，但 HEAD 的 InsertTask 漏掉该列。`594321d` 只修生产 bootstrapper，无法修此 fixture。直接取 HEAD 的 DDL 和 INSERT，在独立内存 SQLite 已复现 `NOT NULL constraint failed: workspace_tasks.sort_order`。

工作区测试文件当前有 **既存未提交修复**（13 行增加/9 行删除），相关 3 例在本轮测试中通过。不能把它与 HEAD 混为一谈。此前外部控制器 19:51 的“HEAD=731d5e0、134/134”过度概括了工作区证据，应更正为“当时工作区包含补丁的验证结果”；干净提交验收仍待补齐。

### F6 · P1：监视任务异常被转换成 Continue

位置：`Source/PuddingPlatform/Services/AgentChat/ExecutionRunCoordinator.cs:550–570`。Monitor 已独立于主执行循环启动，不应继续假设“长 LLM 直接阻塞续租”。但 Monitor 内串联控制消息、续租、watchdog，非取消异常可使它 fault；GetMonitorOutcomeAsync 的 catch-all 返回 Continue，抹去监视失效。

七日窗口有 17 个 lease_lost Run，最大 attempt=10，且这 17 条 completed_at 均为空。监视 fault 被吞是已验证的代码风险；**未在本轮通过异常注入逐一证明它解释全部历史 lease_lost**。应区分正常取消与故障，保证监视失败有 canonical 终态和受限恢复；不要再并行添加另一套续租循环掩盖原路径。

### 附加测量/测试缺口

- `TaskSchedulerIntentOutcomeStoreTests.cs:121` 从 AppContext.BaseDirectory 向上找仓库；允许输出到系统 Temp 的验证会失败。这是测试夹具可移植性问题。
- `TestScripts/deepseek-cache-hitrate.py:47` 用 `datetime('now','localtime', -(days-1))` 与 UTC 文本比较，不是明确的七个完整北京时间自然日，且没有固定上界。本报告使用显式起止时间，避免直接复用旧日报当验收证明。
- `SqliteExecutionLeaseStore` 将 Run 设为 lease_lost 时不写 completed_at；不能把空完成时间统一解释成“运行至今”，否则夜间利用率会被虚假拉满。

## 6. Chat 体验与资源占用的判断边界

当前页面已启用消息虚拟化：`data-virtualized=true`，一次 DOM 样本约 1,933 元素、3 个 toolcall-row、1 个 turn-content-stream。没有证据支持“完全没有虚拟化”或“这次挂载了数百工具行”。页面有长卡片、正文/表格拥挤、很宽视口上的不均衡留白，以及重复错误提示；视觉改善需要实际长消息、代码、失败与委派场景的截图验收，不能只靠单测。

本轮没有测得首屏/交互 P95、Long Tasks、GC 堆类型分布或长时内存斜率，也没有对 Desktop WebView2 作进程树完整测量。截图中的系统 CPU 97% 和内存 83% 不能全归因 Pudding；截图明确显示 Core 占用远高于 Desktop。当前 Debug 构建、logging=Debug，多页面请求和后台投影均可能增加负担。

优先处理 F3/F4，再用同一会话、同一构建、明确 idle/stream/replay 三场景采样。优先指标是“交互延迟、每秒分配、GC 后保留量、无新事件时读写量”，不能为降内存盲目强制 GC 或清理运行数据。

## 7. 本轮验证

- Platform 五套件并集：**133 passed / 1 failed / 0 skipped / 134 total**。唯一失败是上述 Temp 输出目录找不到仓库根的源码接线测试，不能说成业务回归，也不能报告 134/134。源码中的 Host 接线实际存在；A 类 3 例在现有 dirty 工作区通过。
- HEAD fixture：独立 in-memory SQLite 复现 NOT NULL 失败。
- FilePatch：真实类型反序列化复现 newText 丢失；正确 new_text 对照通过。
- Chat 四个定向测试文件：**40/40 passed**（增量投影、内容块、MessageRow memo、会话选择）。Jest 有未及时退出的异步句柄警告；这些用例没有覆盖本次发现的 401 无限重连。
- `dotnet-stack report --process-id 32444` 成功；只是线程瞬时快照，不是 CPU 占比火焰图或 heap dump。
- 测试输出全部在系统 Temp；未向 D:\data 写测试产物。TRX：`C:\Users\huany\AppData\Local\Temp\pudding-audit-20260905-tests\audit.trx`。

## 8. 建议后续五轮：以减少等待和重复工作为主

| 轮次 | 范围与交付 | 验收 |
|---|---|---|
| 1：执行正确性和验收基线 | F1 终态所有权释放、F6 监视 fault 显式终结；核清/补齐 A fixture 的提交边界；不再增加重复续租机制 | 定向故障注入；旧 terminal/orphan claim 在一个恢复 tick 内释放；活跃长任务不误杀；干净提交与测试证据一致 |
| 2：工具合同与硬预算 | F2 示例/schema 一致、缺字段拒绝写盘；固化一次 shell profile 和 long-command handle；将 quick 的 round/tool/token 与失败族限制下沉 Runtime | 5 轮内有效 diff、≤10 轮/15 工具/500k 输入；两次同族失败/无后置条件变化停机 checkpoint；A/B fixture 对照回放 |
| 3：部署并恢复夜间可执行库存 | 外部 Desktop 控制器部署明确 build；逐张整理已有卡的 scope/AC/dependency/auto opt-in；在实际窗口验证 authoritative-single | 无 Heartbeat 时连续完成 10 个安全任务，Task→Goal→Iteration→Run→验收闭环；有可执行库存且空闲时事件决策 P95≤30s；重启恢复一次 |
| 4：Chat 和 Core 性能 | F3 停止鉴权重试风暴；F4 dirty-run/增量字节读取；在现有布局上收敛卡宽、表格、错误/等待状态 | 401 后请求停止；无新归档事件不整文件反复读；相同长会话对比首屏/交互/CPU/分配/保留内存，视觉截图验收 |
| 5：缓存与持续产出指标 | 固定 RequestShape/PrefixEpoch、稳定 tool schema；补 firstChangedSegment/offset/hash；每模型分开报有效交付成本 | ≥95% miss 可解释；连续七个完整日按既有 >99% 合同验收，缺数据不填满；同时守住成功率与延迟 |

先以现有 P0/P1/Scheduler/缓存/Chat 卡作为归属；这张报告本身不改变其状态，也未派发新施工。所有权、工具、性能问题修复前，提高并发或缩短 sleep 可能只增加失败和资源争用。

后续看板应分列：**代码已提交、干净构建已验证、已部署 build、产品已验收**。夜间报表同时给出：可派库存、授权暂停时长、Agent 可用时长、自动 starts、独立 accepted、Token/accepted task、重复失败占比和等待原因。这样才能区分遵守只读边界、没有任务、调度卡死和真正的执行低效。
