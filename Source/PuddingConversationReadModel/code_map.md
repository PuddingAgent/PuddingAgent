# PuddingConversationReadModel — code map

> 阶段 B 交付的**叶子组件**（组件化交付规程 S1–S4；Plan《会话首屏与渐进加载架构优化方案》§3.1 / §11.1）。
> `ProjectReference = 0`、`PackageReference = 0`，只依赖 BCL；边界由
> `Source/PuddingConversationReadModelTests/ComponentBoundaryTests` 在运行期读盘/读进程断言。
> 独立测试工程：`Source/PuddingConversationReadModelTests/`（只引用本组件）。

## 定位

把会话首屏所需的**纯规则**从 Platform/Runtime 中剥离，使归约、分页预算与游标合同可以脱离
运行中 Core 独立构建与验证：`Platform 写入适配`负责事务/持久化，`Platform 查询服务`负责 SQL/授权，
本组件只做「给定输入 → 确定性输出」。

| 能力 | 入口 | 不承担 |
|---|---|---|
| canonical 事件归约 | `Contracts/ReadModelContracts.cs` → `ConversationReadModelReducer` | 事件读取、事务、持久化 |
| 统一预算与配置校验 | `Contracts/LimitContracts.cs` → `ConversationReadModelLimits` | 配置来源、环境变量 |
| 三种游标合同 | `Contracts/CursorContracts.cs` → `CursorContract` / `PageCursorContract` | HTTP、头解析、ETag |
| 历史/过程页预算 | `Contracts/PageContracts.cs` → `HistoryPageBudgeter` / `ProcessPageBudgeter` | SQL、键集查询、DTO |
| SSE 追赶预算 | `Contracts/PageContracts.cs` → `SseCatchUpMeter` | 计时器/时钟（elapsed 由调用方传入） |
| UTF-8 有界截断 | `TextBudget.cs`（internal，`InternalsVisibleTo` 仅测试工程） | — |

公开 API 与 XML 文档即**冻结合同**（阶段 A/Lead 产出），S3 只实现 `S3:` stub 行为，不改签名/字段/文档语义。
Lead 审查追加（阶段 B 仍属 S3/S4）唯一新增公开字段：`ConversationReadModelState.SubAgentRunsOmitted`
（子代理摘要遗漏量，XML 注释已写清），其余签名/字段/文档语义不变。

## 实现文件（`internal`，S3 新增）

| 文件 | 角色 |
|---|---|
| `Internal/ReducerEngine.cs` | 归约引擎：过滤（跨会话/重放）→ 按 sequence 排序 → 连续消费；Turn/根 run/子代理/压缩状态机；活动预览有界尾部；子 run 内容隔离、子代理终态不可回退、有界子代理摘要（含 `SubAgentRunsOmitted`）；`ProcessSummary` 完整性状态与原因 |
| `Internal/LimitRules.cs` | 条数归一化（`NormalizeOpenMessageLimit` / `NormalizeDetailItemLimit`）与预算一致性校验（`Validate`，违规消息含字段名） |
| `Internal/CursorRules.cs` | 游标校验（严格按冻结枚举顺序报告第一个失败） |
| `Internal/HistoryPageBounder.cs` | 历史页：默认 20/最大 50、单条 8 KiB、整页 128 KiB（含 256 B/条 JSON 开销）、messageId 去重、不可显示跳过 |
| `Internal/ProcessPageBounder.cs` | 过程页：默认 100/最大 200、单页 256 KiB、单条 16 KiB、候选扫描上限 400；从尾部向前扫描、升序输出 |
| `Internal/SseCatchUpMeterState.cs` | 2000 条 / 2 MiB / 1s 三重预算；触顶事件不消费、停止原因冻结 |

## 关键语义（测试锁定的行为合同）

- `ProjectedThroughSequence`（C）只按 canonical sequence **连续**推进；跳号不应用、投影位置停在缺口前，
  避免 C 假装覆盖未投影事件。
- 分类计数互不静默混入：`Duplicate`（重放/重叠）、`OutOfOrder`（输入倒置/跳号）、
  `ForeignConversation`、`LateAfterTerminal`、`Ignored`（Kind=Other 或缺少 TurnId/RunId）。
- 只有真正改变摘要的 `Applied` 事件递增 `Revision`；迟到/忽略事件只消费游标，摘要与修订号不变。
- 终态不可回退：`TurnCompleted/Failed/Cancelled` 后同 Turn 的 `TurnStarted`/内容事件计 `LateAfterTerminal`；
  `TurnFailed/TurnCancelled` 强制 `HasAssistantBody=false` 且不凭事件字段伪造 `AssistantMessageId`。
- `TurnCompleted` 携带有界 `TextDelta`（终态 reply 存在性证据，≤16 KiB/入组件前 64 KiB）时可证明助手正文存在：
  置 `HasAssistantBody=true` 并在缺失时补 `AssistantMessageId`；reply **不**拼入活动预览，失败/取消终态永不伪造正文。
- 父子 run 隔离：`ParentRunId` 非空的子代理事件不修改 `CurrentRootRunId`、不会把根 Turn 置终态；
  **子 run 的正文/思考/工具事件不并入父 Turn 摘要**（不追加预览、不计数、不置 `HasAssistantBody`），
  只消费游标并计入 `IgnoredEventCount`；若指向已终态 Turn，终态优先，仍归 `LateAfterTerminal`。
- 子代理生命周期终态（Completed/Failed/Cancelled/TimedOut/Interrupted/BudgetExhausted）**不可回退**；
  已终态后又收到非终态/冲突终态事件时保留先到终态（fail-closed），只消费游标并计入 `IgnoredEventCount`。
- 子代理摘要受 `SubAgentSummaryLimit`（默认 20）限长：新 run 先淘汰 `LastSequence` 最小的终态 run；
  全为活动 run 时新 run 不入摘要；淘汰与遗漏都累计到 `ConversationReadModelState.SubAgentRunsOmitted`
  （`SubAgentRunCount` 是"进入有界摘要"的 run 行数、**不是**全量 distinct 计数；同一在册 run 的多条
  生命周期事件不重复计数）。
- `ProcessSummary` 完整性优先级（从高到低）：`baseline_incomplete`（初始化未 Ready）>
  `turn_header_missing`（缺 TurnStarted 头）> `subagent_summary_truncated`（该 Turn 有子代理 run 被
  淘汰/拒收，`SubAgentRunsOmitted>0`）> `turn_active`（Turn 进行中）> `Complete`。只有 Ready、见到
  `TurnStarted` 头且无子代理摘要缺失的终态 Turn 才是 `Complete`；终态事件同时递增该 Turn 的
  `Process.Revision`；失败/取消 Turn 同样按覆盖规则处理（完整性只描述过程计数覆盖，不代表有助手正文）。
  omitted 发生时按 Turn 归因（淘汰归被淘汰 run 的 Turn、全活动拒收归事件所属 Turn），无法精确归因的
  旧状态按「会话 omitted>0 且该 Turn `SubAgentRunCount>0`」保守降级。
- 活动预览只保留 UTF-8 有界**尾部**（默认 16 KiB），截断不切断中文/emoji 码点，截断标记粘滞。
- 历史页 `HasMore` = 因条数/字节预算被省略的可显示候选数 > 0；过程页 `NextBeforeSequence` = 已实际扫描到的
  最低 sequence（排他边界），空展示页也前进，耗尽调用方窗口时为 `null`；首个候选即被字节预算拒绝时
  `ScannedBackThroughSequence=该候选 sequence`、`NextBeforeSequence=该候选 sequence+1`，下一页可重新取回它。
- SSE 计量器停止顺序：事件数 → 字节 → 耗时；停止后调用返回 `false` 且计数不变；负值/非单调 elapsed 抛
  `ArgumentOutOfRangeException`。

## 测试（`Source/PuddingConversationReadModelTests/`，193 用例）

| 文件 | 覆盖 |
|---|---|
| `ConversationReadModelReducerTests.cs` | 初始化、按序应用、重放幂等、倒序/跳号分类、跨会话拒绝、根/子 run 隔离、终态与迟到事件（含 `TurnCompleted` reply 证明助手正文/失败无正文）、正文/思考/工具/子代理计数、UTF-8 尾部预览（中文/emoji 边界）、压缩状态、游标/修订推进 |
| `SubAgentReadModelTests.cs` | 子 run 内容/工具事件不并入父摘要、终态优先的 LateAfterTerminal、子代理生命周期终态不可回退（含冲突终态 fail-closed）、`SubAgentRuns` 限长/活动保留/淘汰最旧终态/`SubAgentRunsOmitted`/淘汰后重现不撑爆 |
| `ProcessCompletenessTests.cs` | `partial/turn_active`、`complete`（Ready+TurnStarted 头+终态）、`turn_header_missing`（无头终态）、`baseline_incomplete`（非 Ready）、`subagent_summary_truncated`（omitted>0 时终态降级/活动原因/优先级）、失败/取消 Turn 覆盖语义、`Process.Revision` 递增 |
| `ConversationReadModelLimitsTests.cs` | 冻结预算默认值、两个 Normalize 的钳制、Validate 默认通过/逐项违规报名字 |
| `CursorContractTests.cs` / `PageCursorContractTests.cs` | 三种游标 + 历史/过程游标每个失败原因、合法通过、冻结校验顺序、枚举冻结值 |
| `HistoryPageBudgeterTests.cs` | 默认/最大条数、8 KiB 预览、128 KiB 页预算（含 JSON 开销）、去重、跳过、HasMore、新→旧顺序 |
| `ProcessPageBudgeterTests.cs` | 默认/最大项数、256 KiB、16 KiB 单条、候选扫描预算、空展示页前进边界、首个候选即被字节预算拒绝时的可重取边界、升序输出 |
| `SseCatchUpMeterTests.cs` | 事件/字节/耗时触顶（触顶不消费）、停止后冻结、负值/非单调抛错、预算检查顺序 |
| `TextBudgetTests.cs` | UTF-8 前缀/后缀字节精确、中文/emoji 不切码点、非正预算与空串 |
| `ComponentBoundaryTests.cs` | **S4**：组件 csproj 零引用、源码无 `Pudding*` 外部命名空间/无 I/O·网络·EF·ASP.NET·DI·时间·环境 token（扫描自动排除 obj/bin/TestResults 与 `*.g.cs`）、程序集只引用 BCL、`InternalsVisibleTo` 只给测试工程、测试工程恰好 1 条 ProjectReference、测试进程不加载宿主/兄弟组件 |

构建/测试命令（产物只在 `temp/`）：

- `dotnet test Source\PuddingConversationReadModelTests\PuddingConversationReadModelTests.csproj --artifacts-path temp\build\readmodel-tests --nologo`
- `dotnet build Source\PuddingConversationReadModel\PuddingConversationReadModel.csproj --artifacts-path temp\build\readmodel --nologo`

## S5（已接入）

已做：`PuddingPlatform` 引用本组件、`PuddingAgentNetwork.slnx` 登记组件与测试工程、`ConversationProjector` 事务化读模型写入（C2b）、统一打开查询/身份解析/HTTP 端点与 Host DI（C3b）；Host composition 2/2（ValidateOnBuild）通过。
旧 `/conversation` 在线全量摘要路径的退役随阶段 D 前端切换执行。

## 关联

- 设计依据：`Docs/12_features/会话首屏与渐进加载架构优化方案-2026-10-18.md` §3.1、§4.1、§4.2、§5.2–§5.4、§11.1 阶段 B
- 交付规程：`Docs/10_conventions/组件化交付规程.md` S1–S5
- 参考实现：`Source/PuddingPathFiltering/` + `Source/PuddingPathFilteringTests/`（叶子组件与 S4 边界断言范式）
