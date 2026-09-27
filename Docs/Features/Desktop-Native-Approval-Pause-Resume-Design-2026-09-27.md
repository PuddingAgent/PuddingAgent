# NC-01：审批持久暂停与同一 invocation 精确恢复

- 日期：2026-09-27；状态：**设计已定，存储/调度片已实施并独立验证；Runtime 暂停信号与恢复注入未实施**。
- 依据：[原生聊天剩余任务书](../Tasks/Desktop-Native-Chat-Remaining-Tasks-2026-09-27.md) NC-01、[审批接入设计](Desktop-Native-Approval-Integration-Design-2026-09-27.md)、ADR-091、ADR-059。
- 本文不是「Run 已可暂停」的证明。未列出的部分一律视为未实现。

## 1. 目标与禁止做法

目标：可控工具真正触发 `NeedHuman` 时，**持久**保存恢复点、释放执行槽位；人工批准后从**同一 invocation** 续行，已完成工具不重跑，副作用恰好一次。

已被既有源码证伪、因此**禁止**的做法（沿用审批接入设计 §「本轮核查明确了 A2 不能采用……」）：

| 禁止做法 | 源码依据 |
|---|---|
| 工具函数内无限等待按钮 | `PuddingToolRegistry` 的 NeedHuman 分支直接返回结果，不挂起 invocation |
| 用内存 `ResumeAnchor` 当恢复点 | `ExecutionJournal` 的锚点只在 `ConcurrentDictionary`，且只含 Agent 唤醒上下文 |
| 长时间等人占满 worker | `ChatExecutionWorker` 的 running 名额默认 3，直到任务结束才释放 |
| 借 `LeaseLost` 退回 pending 重跑整轮 | `IExecutionLeaseStore.ReleaseAsync` 会把 command 退回 `pending`、Turn 退回 `accepted` |
| 只存一个 approvalId 然后重跑整轮 | 任务书 NC-01 实施要求 1 明确禁止 |

## 2. 采用的形状：镜像 `waiting_child`

仓库已有一条被验证的「非终态 park → CAS 唯一收口」先例：A01-slice-4c 的父 Turn `waiting_child`（`IExecutionJournal.ParkForChildrenAsync` / `TryFinalizeWaitingTurnAsync`，见 `ExecutionRunJournalWaitingForChildrenTests`）。审批暂停与你共享同一套语义骨架，差别只在**唤醒后要续行 Runtime**，而不是只写终态：

| 环节 | `waiting_child`（既有） | `waiting_approval`（本设计） |
|---|---|---|
| park 后 Run/Turn/Command | `waiting_child`，释放租约，不写终态 | `waiting_approval`，释放租约，不写终态 |
| 持久化的待续信息 | `metadata_json.parked_terminal`（待提交终态） | `chat_execution_commands.approval_resume_json`（恢复点，独立列） |
| 唤醒 | 最后一个子代理终态后 CAS 收口写终态 | 人工批准后 CAS 重新领取租约并**继续执行** |
| 唯一性 | `WHERE status='waiting_child'` CAS，只允许一次 | `WHERE status='waiting_approval'` CAS + 新 fencing token，只允许一次 |

复用既有执行调度，不新增第二个 worker：唤醒由**既有** `ChatExecutionWorker` 的领取循环完成（新增一条「已批准暂停」领取分支），`ExecutionRunCoordinator` 仍是唯一执行入口。

## 3. 恢复点字段与逐字段来源

`PuddingCode.Platform.ApprovalResumePoint`。`SchemaVersion` 当前为 `1`；反序列化时精确校验，未知版本拒绝恢复。

### 3.1 身份绑定（恢复前必须与行内事实逐一复核）

| 字段 | 来源 | 恢复时的复核方式 |
|---|---|---|
| `SchemaVersion` | 常量 1 | 不等于 1 直接拒绝 |
| `WorkspaceId` | `lease.WorkspaceId` | 与 `conversation_turns.workspace_id` 比对 |
| `AgentInstanceId` | `command.AgentInstanceId` | 与 `chat_execution_commands.agent_instance_id` 比对 |
| `SessionId` | `lease.ConversationId` | 与 `execution_runs.conversation_id` 比对 |
| `RunId` / `TurnId` / `CommandId` | `lease` | 与行内主键比对 |
| `InvocationId` | 待决定工具调用的 canonical `tool_call_id` | 决定与消费都用它做精确绑定 |

### 3.2 操作快照（人看到什么就批准什么）

| 字段 | 来源 |
|---|---|
| `ToolId` / `ArgumentsJson` / `ToolDefinitionJson` / `ExecutionRoot` | Runtime 解析真实资源后提供，与 `PuddingApproval.ApprovalOperation` **同一份值**；`Fingerprint()` 复用同一算法 |
| `PolicyRevision` | 冻结策略版本（`ApprovalBinding.PolicyRevision`） |

原样保存，不做同义 JSON 重排、不改写用户所见原文。参数/目录/定义/主体任一变化后旧决定不得复用（由 `ApprovalOperation.Fingerprint()` + 决定前复核共同保证）。

### 3.3 冻结预算（恢复只能缩短，不得放宽）

| 字段 | 来源 | 恢复时 |
|---|---|---|
| `DeadlineUtc` | Coordinator 冻结的 `TurnExecutionContext.ExecutionDeadlineUtc` | 已过则不得恢复执行，按超时终态收口 |
| `MaxRounds` / `MaxToolCallsTotal` | `ResolveExecutionBudget(...)` 的结果 | 原样恢复，禁止重算放大 |
| `UsedToolCalls` | 暂停前已执行工具数 | 续行后继续累加 |
| `Round` / `PendingToolIndex` | 当前轮次、当前批次内待决定工具的下标 | 决定续行的精确位置 |

### 3.4 Runtime 私有状态（`RuntimeStateJson`）

Runtime 的循环携带状态远多于 Core 需要校验的内容（会话历史、本批次已完成工具结果、工具曝光集合与顺序、曝光 epoch、连续失败/截断恢复计数、usage 预算跟踪器余量等）。这些字段的**所有权属于 Runtime**，Core 不该也无力逐字段校验。

因此契约把它们放在一个有版本、有大小上限的 `RuntimeStateJson` 里，由 Runtime 生产与消费；Core 只强制：
- 整体字符数不超过 `MaxRuntimeStateCharacters`；
- 必须是 JSON 对象（不是裸数组/标量）；
- 与 `SchemaVersion` 同步演进，未知版本拒绝恢复。

持久化位置是 `chat_execution_commands.approval_resume_json`（独立列，见 §5.1）。

**诚实边界**：`RuntimeStateJson` 的字段清单尚未冻结——它必须在 Runtime 暂停生产者落地时按实际循环变量逐项确定，并在本文补一张与 §3.3 同规格的表。在此之前不能声称「恢复所需状态已完整定义」，也不能声称跨重启续行可行。

### 3.5 明确不进入恢复点的内容

| 不保存 | 原因 |
|---|---|
| 内存 `CancellationTokenSource` / 任务对象 | 进程内对象，跨重启无意义 |
| 数据库连接 / 事务 | 由 SqliteJournal 自己的事务边界负责 |
| 已消费的许可本体 | 权威事实在 `PuddingApproval.Sqlite`；恢复点只带 `ApprovalId` 供再次校验 |
| 秘密明文（API Key 等） | `ToolInvocationService` 已在执行前 `InjectAsync`/`StripAsync`；恢复点不落注入后的密钥 |

## 4. 状态转换与权威

```
running --park--> waiting_approval --CAS(唯一)--> leased/running（同一 Turn/Run/Command，新 fencing token）
                        |
                        +-- 拒绝 / 到期 / 停止 / 角色冻结 --> 终态（Approved 永不出现，不产生许可）
                        +-- 已消费但结果未知 --> DispatchUnknown，禁止自动重发
```

- 拒绝、到期、停止、角色冻结、Core 重启都由**显式权威转换**收口，不允许「等待超时后默认继续」。
- 等待不计执行错误（NC-00 已把两种准入等待排除在工具熔断之外），但仍受明确期限约束：`DeadlineUtc` 到期由唤醒扫描按超时终态收口，不无限占用槽位。
- 同一批次先前工具的结果已进入恢复点，续行时**不重跑**。
- 已消费但派发结果未知进入 `DispatchUnknown` 并对账，不自动派发第二次（`ApprovalService.MarkDispatchUnknownAsync`）。

## 5. 实施切片与门禁

| 切片 | 内容 | 状态 |
|---|---|---|
| S1 | 契约：`ApprovalResumePoint`（版本/绑定/冻结预算/不透明 Runtime 状态 + 校验与指纹） | **已实施并独立验证** |
| S2 | 存储：`ParkForApprovalAsync` / `TryAcquireApprovalResumeAsync`（CAS park、唯一唤醒、新 fencing token、跨重启可读） | **已实施并独立验证** |
| S3 | Runtime：NeedHuman → 非终态暂停信号 + 恢复注入（重建 history/batch/exposure） | 未实施 |
| S4 | 调度：Coordinator 遇暂停信号 park；Worker 领取已批准暂停并续行 | 未实施 |
| S5 | 端到端：副作用 0/1、双击与并发不增加、同批次不重跑、跨重启恢复、DeferredDependency/硬拒绝无人工入口 | 未实施 |

S5 完成前，NC-01 不得记为完成，也不得据此关闭 NC-03 的「完整真实 Core 端到端」门禁。

### 5.1 S1/S2 已交付的语义与证据

存储位置：恢复点写入 `chat_execution_commands.approval_resume_json` **独立列**。不并入 `metadata_json`——后者 `[MaxLength(4096)]` 且语义是「附加元数据」，装不下携带会话历史的恢复点。`ExecutionRunSchemaBootstrapper` 负责给历史库幂等补列，并在表缺失时跳过而不是让 `ALTER` 失败。

`TryAcquireApprovalResumeAsync` 的五个 CAS 步骤与 §4 的状态转换一一对应；任何一步未命中即整事务回滚并返回 null。旧 run 行被标记为 `resumed`，新 run 行取得更高 `fencing_token`，因此 park 前的旧租约**不能再写任何终态**（已用测试固定）。

独立测试 14 项（`Source/PuddingPlatformTests/Services/ApprovalPauseResumeTests.cs`）+ schema 补列 4 项（`ExecutionRunSchemaBootstrapperTests`）全部通过，日志 `temp/nc01-approval-pause.log`：

| 断言 | 覆盖的完成标准 |
|---|---|
| park 后三行 `waiting_approval`、无终态事件、租约释放、pending 输出已 flush、信号发出 | 请求落盘；等待释放槽位 |
| 恢复点逐字段读回（`ToJson`/`TryParse` 同一实现） | 恢复点可持久化、可校验 |
| 身份不匹配 / 非法恢复点（非对象 Runtime 状态、未知版本、预算越界）→ 抛错且一行未改 | 禁止只存一个 approvalId |
| fence 陈旧 → park 返回 null，不覆盖新 writer | 并发/重放不误写 |
| park 拒绝终态事件 | 终态只能走 `CommitTerminalAsync` |
| 连续两次领取：第一次成功、第二次 null；Turn 回 `running`、旧 run `resumed`、新 fence 更高 | 单次批准只执行一次 |
| 恢复后旧租约 `CommitTerminalAsync` 被拒绝，事件数为 0 | 围栏有效 |
| 无暂停行 / 冻结截止时间已过 → 返回 null | 不服宽预算 |
| 重启后（新 host + 清连接池）仍能读回并完成唯一一次领取；第二次落空 | 跨重启恢复 |
| 损坏恢复点（非 JSON / 缺字段 / 版本未知）→ 拒绝且不产生第二行 run | 未知版本拒绝恢复 |
| 8 路并发领取最多一个赢家 | 并发不增加 |

**未通过本切片证明**：Runtime 仍未产生恢复点（`RuntimeStateJson` 字段清单未冻结，见 §3.4），Coordinator 与 Worker 未接线，因此「可控工具真正触发 NeedHuman → 副作用 0/1」尚不成立。到期暂停没有扫描收口（测试显式固定该行保持在 `waiting_approval`），这是已知缺口，不算已解决。

## 6. 必测故障点

请求落盘、决定落盘、通知前、消费后四个故障点各自需要持久记录与测试证据（任务书 NC-01 完成标准）。S2 覆盖「请求落盘（park）」与「唤醒 CAS 唯一性」两个点；决定落盘与通知前属于 NC-02；消费后属于 S5。
