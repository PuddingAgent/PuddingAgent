# GLM 实施进度复核与看板状态修订

复核日期：2026-09-12；仓库：`E:\github\AgentNetworkPlan\PuddingAgent`；工作区：`default`。

本文件更新 9 月 11 日审计后的进展。04/05 保留当时缺陷和回执；当前状态以本文件及实时看板为准。本轮没有修改产品源码、配置或运行数据库，没有派发 Agent、部署或重启 Core。验证只使用临时测试资产和隔离输出。

## 1. 当前结论

GLM 已提交三个补修和 S01-B 首片；原来的 10 个失败探针全部转绿。但 F01 的界面闭环、S01-B 的完整请求归因/恢复合同尚未满足，不能由此宣布全部工作完成。

| 工作包 | 实现证据 | 最新判断 | 看板状态 |
|---|---|---|---|
| F01-R | `19ec137`；前端 28/28 含原独立 5 探针 | 调度器/取消/StrictMode 修复已通过；认证恢复、失败展示、手动重试仍缺真实消费者 | Backlog → Ready → **NeedsReview**，v1 → v4 |
| S01-A-R | `9537f60`；Platform 48/48 含原独立 3 探针 | 原严格幂等缺陷源码复核通过；S01-B 范围未因此完成 | Backlog → Ready → **NeedsReview**，v1 → v4；源码评价 accepted |
| T01-R | `33c1489`；Runtime 40/40 含原独立 2 探针 | 原参数合同缺陷源码复核通过；原 T01 读取/分页等扩展仍未完成 | Backlog → Ready → **NeedsReview**，v1 → v4；源码评价 accepted |
| S01-B | `15842ff` | 冻结归因与闭日失效首片已实现；完整身份落账与故障恢复需补齐 | Backlog → Ready → **NeedsReview**，v1 → v4 |
| C01 | 6 个 Runtime 文件有 WIP，新增 2 个 Composition 测试文件 | C01-A 已落盘、待独立验收；C01-B 未完成 | Backlog → Ready → **NeedsReview**，v1 → v4 |
| C02 | 未发现最终 Provider manifest 的可验收交付 | S01-B/C01 依赖尚未完成 | **Backlog** 保持；描述 v1 → v2 |
| Token 缓存命中率优化 | 总目标与六张子卡关联保留 | 没有本批生产、完整七日窗口或供应商对账结论 | **InProgress** 保持；描述 v7 → v8 |

五张卡的 NeedsReview 含义分别为“源码补修通过待结算”“仍需补修”“WIP 待验收”，描述、流转备注和评价已明确区分。它不是统一的“审核通过”状态。

### 状态流转限制

External Task API v1 的 PATCH 只更新元数据，没有 Backlog 普通流转命令，因此本次描述使用官方技能脚本与 ETag，状态通过看板网页的正常“状态流转”入口更新，每步由服务端校验版本。

当前状态机只允许 Backlog → Ready；Ready 的选项为 Deferred、Reserved、NeedsReview、Cancelled，没有 Ready → Completed；NeedsReview 只能回到 Ready。这些卡没有真实 Assignment。本次使用合法审核状态，未制造 Reserved/Assigned 历史或触发模型执行来关单。因此 **S01-A-R/T01-R 已登记源码 accepted，但生命周期尚未 Completed**。

后续若要支持这类仓库外部完成工作的正式关单，需要产品提供携带证据、版本检查、审计事件的人工结算入口；不能直接改数据库或冒充 Agent 执行。本轮没有实施该功能。六张新卡的自动调度均保持关闭；旧 P0-5/P0-6 的 Completed 历史保留。

## 2. 下一步施工说明

### F01-R：补实际界面消费者

`useTurnSurfaceStore.ts` 已在 effect 内成对创建/销毁 scheduler，adapter 已接 signal/timeout；这些旧失败不再需要返工。当前对 `getHydrationFailure`、`notifyAuthRecovered`、`retryAll` 的源码搜索仅发现 hook 声明/返回、scheduler 内部实现及测试，未发现 Chat UI 和认证事件的生产消费者。

剩余工作沿现有 hook 接线：认证恢复事件调用恢复入口；明细行区分未加载、失败和不存在；失败时提供手动重试。不要新增第二套计时器或重试状态机。集成测试必须从真实消费者触发，覆盖 401 恢复、gone 同 revision 不复活、切会话取消后补位和迟到不污染。补新 tsc 与明确新 bundle 的 smoke 证据。五个旧探针不包含这项完整 UI 验收。

### S01-B：补请求身份、持久化与故障恢复

`ITokenUsageRecorder.cs` 已新增不可变归因副本和 InvocationId/AttemptId；AgentExecution buffered/streaming/compaction 进入了冻结路径。`TokenUsageRecorder.cs` 优先消费该副本，但实际 `TokenUsageEventEntity` 尚无 invocationId/attemptId/attribution source 字段；fallback 标记留在临时对象，缺快照时仍读 session 最新快照。

1. 将 invocation/attempt、run/turn/round、workspace/session、主/子身份贯通实际调用请求、Gateway usage 与 TokenUsage 事实。冻结、fallback、unknown 的来源必须持久化且可查询；不得把 session-latest fallback 计作准确归因。C01/C02 生成 revision/contentId/manifestId 后绑定同一请求事实。
2. Provider 成功后本地写入失败，应可靠重投同一个用量事实。当前 AgentExecution 的 deferred 日志不能证明后续补录。用持久重投记录或现有 durable 机制承接，完成后去重确认；spy 验证本地 busy/commit 失败及恢复过程中模型只调用一次。
3. 当前闭日失效发生在账本提交之后，失败只写日志。增加可恢复 dirty-day/outbox 或等价原子机制，防止提交后崩溃造成日报永久陈旧；重复 source 重投也能恢复未完成的失效工作。
4. 明确存储的 UTC 日与报表北京时间窗口边界；迟到事实按 occurredAt 归属，失效相应缓存后重算。增加 A 请求尚在飞行、B 已更新 session 快照的实际接线测试，以及 Provider 内部重试/streaming/buffered/控制流覆盖。

已存在的 `UsageRequestAttributionTests` 和 `RequestAttributionWiringTests` 提供局部证据；其中 helper 等价组合不能证明最终生产请求到持久账本的所有接线。

### C01：先验收 A，再实施 B

本轮后续检查已见 DirectLlmClient、CompositionRecoveryService、CompositionSnapshot、PersistentCompositionVersionRegistry、AgentExecution 两种路径的未提交变更，以及 `CompositionCommitDecouplingTests`、`CompositionRecoverySingleFlightTests`。删除旧描述中“全部文件与原基线相同”的过期判断。

C01-A 先核对改动归属、完整 diff、必要执行提交与 best-effort telemetry 的解耦；隔离验证无 sink、sink 异常、store busy、恢复 single-flight、取消/失败可见，再提交并独立复审。本轮 116 项结果不作为此 WIP 的验收。

C01-B 继续做单调 revision、可复用 contentId 与 CAS head；A→B→A 的 revision 不倒退；重启恢复精确 canonical prefix/schema；ToolExposureRevision 与 PermissionEpoch 分离；工具曝光在轮边界切换、撤权立即生效。保留原验收条件，禁止拿旧 P0-5/P0-6 的 Completed 证明新合同已完成。

### C02 与最终缓存验收

依赖 S01-B 身份/恢复合同及 C01 提交合同后，在三个 Gateway 最终序列化边界生成实际 payload manifest，记录有序段哈希、字节与首次变化位置。现有前缀 hash 或查询索引优化不等于 manifest 已完成。

顺序：S01-B 剩余链路与 F01 接线 → C01-A 验收/C01-B → C02 离线协议验证 → 明确新构建的产品 smoke → 连续七个完整北京时间自然日统计与账单对照。原 §15.3 的 token 加权、Pro/Flash 样本规则、warm/epoch/unknown 分桶、供应商对账及成功率/工具正确性/P95 条件保持。没有新窗口不能声称 >99%。

## 3. 独立验证及边界

本轮提交基线为 `15842ffe16487d221a858e6f472a6f72d15d0cf8`，工作树含他方未提交改动。下列为当前工作树定向验证，不能等同纯提交、全仓回归或已加载产品验收。

| 组 | 结果 | 覆盖 |
|---|---|---|
| 前端 | 4 suites / 28 passed / 0 failed | 原 23 项 + `audit-f01.test.tsx` 5 项 |
| Platform | 48 passed / 0 failed | TokenUsage/LlmGatewayUsage/归因与冲突回归，含独立 3 项 |
| Runtime | 40 passed / 0 failed | SaveMemory 合同/类型/记忆测试与 RequestAttributionWiring，含独立 2 项；并非全 40 项都是记忆测试 |

合计 116 项；原 10 个独立失败探针全部转绿。独立 C# 探针类名改为 `IndependentUsageProbe12` / `IndependentMemoryProbe12`，避免与 GLM 新增同名类冲突，断言未放宽。前端 Jest 报过 open-handle 提示，最终进程退出码为 0；这不是浏览器内存/资源回收验收。

本轮未重跑 tsc/入口构建、未跑 C01-A 新测试、未做全仓回归或实际模型调用。9 月 11 日 tsc/入口构建通过仅属于当时基线。

测试资产、日志和 TRX：`.tmp-test-out/glm-board-20260912/`；输出：`.tmp-build/glm-board-20260912/`。交接用资产及结构化摘要位于本包 `audit-evidence/2026-09-12/`。将该目录内容复制到仓库根下两级临时目录后运行，不能直接在 Docs 深层路径原位使用相对测试引用。

## 4. 看板写入与回执

本次修改 7 张任务描述；T01-R 的验收第4条澄清 R 卡与原 T01 扩展范围，其余原验收条件保留。五张卡完成两个合法状态步骤，每步附备注；C02 和总卡另加最新进度评论。新增评价：F01-R needs_changes、S01-A-R accepted、T01-R accepted、S01-B needs_changes、缓存总卡 needs_changes。总卡新评价替代 9 月 11 日同一 actor 的旧评价，保留旧记录。

| 工作包 | taskId |
|---|---|
| F01-R | `9e007db123b04c5fb0f369e2cc126b6f` |
| S01-A-R | `d0ac9f710b944ee5810b48c74128e98b` |
| T01-R | `104fe9c0707f4d458fd9b5f9d96e32d3` |
| S01-B | `863193411fa2437babdb37d7f08af325` |
| C01 | `9494be33635e4717ba1271a6105aec6a` |
| C02 | `a8e5340ebb9f421bac7c8b3babb768bc` |
| 缓存总卡 | `af25d72c75634aae8579ec2c0a26ce08` |

结构化回读：[board-receipts.json](audit-evidence/2026-09-12/board-receipts.json)，含最终状态/版本、完整修订描述/验收条款、评论及评价 ID，不包含令牌或整个工作区看板。
