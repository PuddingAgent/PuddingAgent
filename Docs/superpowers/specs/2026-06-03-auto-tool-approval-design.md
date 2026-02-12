# Auto Tool Approval Design

> 日期：2026-06-03
> 状态：draft
> 相关代码：`Source/PuddingCore/Tools/ToolAuthorization.cs`、`Source/PuddingRuntime/Services/Tools/PuddingToolRegistry.cs`、`Source/PuddingRuntime/Services/Tools/InMemoryToolAuthorizationService.cs`

## 1. 目标

为 Pudding 的长程 Agent 增加自动审批层，使 Agent 在需要执行高风险工具时，可以提交结构化工单，由独立审批 LLM 做一次性无上下文审阅，并让执行引擎根据工单结果放行或拒绝。

自动审批层的本质不是“自动放行器”，而是一道柔性的安全围栏。当 Agent 因幻觉、错误假设、过度自信或误闯陷阱而接近高风险边界时，系统要求它停下来回答一组事实问题：为什么必须这么做、是否可能损坏系统或数据、是否有可回滚方案、是否触碰未授权区域、是否存在更低风险替代方案。工单系统因此也是一个 checklist，迫使 Agent 根据当前事实和可验证证据行动，而不是依赖记忆或惯性推断。

这道围栏在普通高风险操作上是柔性的提醒和核查机制；但在不可逆、破坏性、无备份、无回滚或目标不明的操作上必须变成严格防火墙。格式化磁盘、删除文件且不能确认是临时文件、删除数据库、修改系统关键配置且没有回滚方案、执行没有步骤方案的 destructive shell 命令，自动审批层必须直接拒绝，不能把这类操作交给 reviewer LLM 自由批准。

核心目标：

- 自动审批与人工 `/authorize` 并行，二者任一有效即可满足高风险工具的运行时授权要求。
- 自动审批不绕过 Agent 模板能力策略；工具必须先被 capability policy 暴露，自动审批只替代人工运行时授权。
- Agent 通过专用工具提交工单，工单持久化并可审计。
- 审批 LLM 必须是干净上下文的单次调用，不携带当前 Agent 对话历史，不可使用工具。
- 执行引擎必须校验实际执行的工具、命令、参数或资源是否落在工单批准范围内。
- 没有人工授权、没有有效自动审批工单时，高风险工具继续拒绝执行，并提示 Agent 使用自动审批工具申请。
- 自动审批拒绝时应给出可执行提醒，例如补充事实依据、改用只读命令、增加备份、缩小目标范围或请求人类授权。

非目标：

- 不改变 `/authorize`、`/deny`、`/revoke` 的现有语义。
- 不让自动审批批准 capability policy 未暴露的工具。
- 第一版不支持自动审批 `permanent` 授权。
- 第一版不支持对 shell 命令做宽泛正则授权；shell 默认按 exact command 或 exact normalized command hash 匹配。
- 第一版不把审批结果交给 Agent 自行解释；执行引擎是最终判定点。

## 2. 当前基线

当前高风险工具的执行链路是：

1. Chat API 解析用户发送的 `/authorize <tool> ...`。
2. `InMemoryToolAuthorizationService` 记录人工授权 grant。
3. Agent 的 tool call 进入 `ToolInvocationService`。
4. `PuddingToolExecutionService` 先检查 capability policy，再检查 runtime authorization，最后进入 sandbox 和真实工具执行。

现有系统的关键约束：

- `ToolPermissionPolicyService.RequiresRuntimeAuthorization` 将 High 权限、shell、文件写入、destructive 工具标记为运行时授权工具。
- `PuddingToolExecutionService` 在授权检查前已经确认工具对当前 Agent 可见。
- 人工授权 grant 的身份边界包含 workspace、session、agent、user、tool。
- `once` 授权会在第一次有效检查时消费。
- `permanent` 人工授权会保存到 `data/runtime/tool-authorizations.json`。

自动审批应插入到同一个运行时授权检查阶段，而不是另起一条工具执行路径。

## 3. 核心架构

新增自动审批层由四个部分组成：

1. 工单模型：记录 Agent 申请高风险工具的用途、风险声明、范围、有效期和审批结果。
2. 申请工具：`request_tool_approval`，供 Agent 提交工单。
3. 审批服务：`IAutoToolApprovalService`，负责保存工单、调用 reviewer LLM、生成审批结论。
4. 执行校验：`PuddingToolExecutionService` 在人工授权未命中时查询自动审批工单。

推荐接口边界：

```csharp
public interface IAutoToolApprovalService
{
    Task<ToolApprovalTicketResult> SubmitAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        CancellationToken ct = default);

    Task<ToolApprovalCheckResult> CheckAsync(
        ToolApprovalExecutionRequest request,
        CancellationToken ct = default);
}
```

执行授权顺序：

```text
tool exists
  -> capability policy allows tool
  -> human /authorize grant exists
      yes: allow
      no:
        -> auto approval ticket exists and matches actual call
            yes: allow
            no: deny with request_tool_approval guidance
  -> sandbox allows
  -> execute
```

这个顺序保证自动审批不会提升模板权限，也不会绕过 sandbox。

## 4. 工单数据模型

Agent 提交工单时至少需要提供：

| 字段 | 类型 | 说明 |
|------|------|------|
| `ToolId` | `string` | 需要使用的工具 ID，例如 `shell`、`file_write`、`file_patch`。 |
| `CommandName` | `string?` | 命令或动作名称。shell 建议填实际命令摘要，例如 `dotnet test`。 |
| `Purpose` | `string` | 为什么需要这个工具，必须是具体任务目的。 |
| `Necessity` | `string` | 为什么现在必须执行高风险工具，而不是继续只读分析或请求人类确认。 |
| `FactBasis` | `string[]` | 支撑本次操作的事实依据，例如刚读取的文件、测试输出、错误日志、用户原文要求。 |
| `RequestedArgumentsJson` | `string?` | 预期工具参数。shell/file 写入类建议必填。 |
| `TargetResources` | `string[]` | 涉及的路径、域名、数据库表、进程或其他资源。 |
| `AuthorizedArea` | `string[]` | Agent 认为自己被授权操作的范围，例如 workspace 相对路径、测试项目、临时目录。 |
| `OutsideAuthorizedAreaReason` | `string?` | 如果触碰授权范围外资源，说明为什么必要；为空表示不触碰。 |
| `MayDamageOrDeleteData` | `bool` | 是否可能导致数据损坏、覆盖或删除。 |
| `IsIrreversibleOperation` | `bool` | 是否不可逆或难以自动恢复，例如格式化磁盘、删除数据库、删除未确认性质的文件、修改系统关键配置。 |
| `BackupTaken` | `bool` | 是否已采取备份、可回滚措施或只读验证。 |
| `RollbackPlan` | `string?` | 出错后的回滚方式；无回滚方案时必须说明原因。 |
| `OperationContext` | `string` | 本次操作使用的上下文和环境，例如 cwd、shell、数据库环境、目标 workspace、是否为生产/测试环境。 |
| `OperationPlan` | `string?` | 操作过程摘要；不能替代逐步操作清单。 |
| `OperationSteps` | `OperationStep[]` | 详细的逐步操作清单；每一步说明命令、目标对象、目的、影响、合理性和停止条件。 |
| `TemporaryFileEvidence` | `string?` | 删除文件时证明目标是临时文件、生成物或可安全删除文件的证据。 |
| `MayExposeSecrets` | `bool` | 是否可能泄露敏感信息、密钥、token、个人数据。 |
| `UserConsentStatus` | `explicit | implied | absent | unknown` | 是否征得用户同意。 |
| `AlternativesConsidered` | `string[]` | 已考虑的低风险替代方案，例如只读检查、dry run、备份后操作、缩小范围。 |
| `RequestedScope` | `once | session | timed` | 第一版支持的有效期范围。 |
| `RequestedDuration` | `TimeSpan?` | `timed` 时必填，最大默认限制为 1 天。 |
| `RiskNotes` | `string?` | Agent 对风险和缓解措施的补充说明。 |

服务端持久化字段：

| 字段 | 类型 | 说明 |
|------|------|------|
| `TicketId` | `string` | 工单 ID，用于审计和后续匹配。 |
| `WorkspaceId` | `string` | 当前 workspace。 |
| `SessionId` | `string` | 当前 session。 |
| `AgentInstanceId` | `string` | 申请 Agent。 |
| `UserId` | `string` | 当前用户。 |
| `RequestedArgumentsHash` | `string?` | 工具参数 hash。 |
| `Status` | `pending | approved | denied | expired | consumed` | 工单状态。 |
| `DecisionReason` | `string` | 审批理由。 |
| `AllowedScope` | `once | session | timed` | 实际批准范围。 |
| `ExpiresAtUtc` | `DateTimeOffset?` | 过期时间。 |
| `MaxUses` | `int?` | `once` 为 1，session/timed 可为空。 |
| `RemainingUses` | `int?` | 剩余次数。 |
| `ReviewerModel` | `string` | 审批 LLM 模型。 |
| `CreatedAtUtc` | `DateTimeOffset` | 创建时间。 |
| `DecidedAtUtc` | `DateTimeOffset?` | 审批完成时间。 |
| `ConsumedAtUtc` | `DateTimeOffset?` | 单次工单消费时间。 |

`OperationStep` 建议结构：

| 字段 | 类型 | 说明 |
|------|------|------|
| `StepNumber` | `int` | 从 1 开始的步骤序号。 |
| `Command` | `string` | 计划执行的具体命令、工具调用或操作动作。 |
| `WorkingDirectory` | `string?` | 命令执行目录；不适用时为空。 |
| `Environment` | `string?` | 操作环境，例如 local dev、test database、production、Windows PowerShell。 |
| `TargetObject` | `string` | 此步骤直接作用的文件、目录、数据库、服务、配置项或其他对象。 |
| `Purpose` | `string` | 这一步为什么需要执行。 |
| `ExpectedEffect` | `string` | 这一步预期会产生什么影响，包括可能写入、删除、重启、锁定或网络访问。 |
| `Reasonableness` | `string` | 为什么这一步在当前事实下是合理的，为什么没有更低风险替代。 |
| `SafetyCheckBefore` | `string?` | 执行前检查，例如确认路径、确认环境不是生产、确认备份存在。 |
| `StopCondition` | `string` | 遇到什么情况必须停止，而不是继续执行后续步骤。 |
| `RollbackForStep` | `string?` | 该步骤失败或产生意外影响时如何恢复。 |

## 5. 审批规则

审批 LLM 输入必须是结构化单次请求，包含：

- 工单 JSON。
- 当前工具 descriptor 的风险元数据。
- 系统固定审批规则。
- 当前执行环境摘要：workspace、session、agent、user，但不包含聊天历史。
- checklist 审查问题：事实依据是否充分、是否必要、目标范围是否被授权、是否可回滚、是否存在更低风险替代方案。

审批 LLM 输出必须是严格 JSON：

```json
{
  "decision": "approved",
  "reason": "The command is scoped to running tests and does not modify data.",
  "allowedScope": "once",
  "allowedDurationMinutes": null,
  "requiresHumanAuthorization": false,
  "checklistFindings": [
    "Fact basis references the current test project.",
    "No destructive data operation is requested.",
    "A lower-risk read-only alternative would not execute the test."
  ]
}
```

合法 decision：

- `approved`：允许生成自动审批工单。
- `denied`：拒绝，执行引擎不能放行。
- `need_human`：需要人类通过 `/authorize` 或调整任务目标。

固定拒绝规则在调用 LLM 前后都要执行，LLM 不能覆盖这些规则：

- capability policy 未暴露的工具拒绝。
- 工具不存在拒绝。
- `RequestedScope=permanent` 拒绝。
- `FactBasis` 为空或只引用模糊记忆时拒绝。
- `Necessity` 为空或未解释为什么需要高风险工具时拒绝。
- `MayDamageOrDeleteData=true` 且 `UserConsentStatus != explicit` 拒绝。
- `IsIrreversibleOperation=true` 且缺少备份、回滚方案、操作步骤方案或明确用户授权时直接拒绝。
- 格式化磁盘、删除数据库、清空目录、批量删除文件、修改系统关键配置等操作，如果不能证明目标范围、安全边界和恢复路径，直接拒绝。
- 删除文件时无法证明目标是临时文件、生成物、备份已有或用户明确要求删除的文件，直接拒绝。
- 修改系统关键配置时缺少操作步骤、影响范围、回滚方案和验证步骤，直接拒绝。
- `OperationContext` 为空，或未说明上下文、环境、目标和对象时拒绝。
- `OperationSteps` 为空，或只写几个命令而未逐步解释每个命令的目的、影响、合理性和停止条件时拒绝。
- 触碰 `AuthorizedArea` 之外资源且 `OutsideAuthorizedAreaReason` 为空时拒绝。
- `MayDamageOrDeleteData=true` 且 `RollbackPlan` 为空时拒绝；explicit 用户同意只能满足授权条件，不能替代回滚方案、备份方案或操作步骤方案。
- `MayDamageOrDeleteData=true` 且 `OperationSteps` 不完整时拒绝。
- `MayExposeSecrets=true` 且未说明脱敏、最小化或不外传策略时拒绝。
- shell 工单未提供预期命令或参数 hash 时拒绝。
- 文件写入/patch 工单未提供目标路径时拒绝。
- `RequestedDuration` 大于 1 天时拒绝或降级为 1 天以内。

以下类别第一版一律不允许自动审批通过，只能拒绝或要求人类重新给出更窄、更可逆的操作：

- 格式化磁盘、分区、挂载点或存储卷。
- 删除数据库、drop table、truncate 表、清空生产或未知环境数据。
- 递归删除目录，尤其是 workspace 根、用户目录、系统目录、配置目录、日志归档目录。
- 删除无法证明是临时文件或构建产物的文件。
- 修改系统关键配置、启动项、权限策略、防火墙规则、密钥库、认证配置，且没有完整回滚和验证方案。
- 执行含有高破坏风险的 shell 命令，例如 `rm -rf`、`del /s /q`、`format`、`diskpart clean`、`DROP DATABASE`、`TRUNCATE` 等。

审批反馈应具有提醒性质，而不是只输出“拒绝”。例如：

- 缺少事实依据时，提示先读取相关文件、查询日志或运行只读诊断。
- 缺少回滚方案时，提示创建备份、使用 patch、保存 diff 或改为 dry run。
- 目标范围过宽时，提示缩小到具体文件、目录、命令或一次性调用。
- 存在低风险替代方案时，提示先尝试替代方案。
- 涉及未授权区域或敏感数据时，提示请求人类明确授权。

## 6. 执行匹配规则

执行引擎检查自动审批时必须对实际调用做匹配：

- `WorkspaceId`、`AgentInstanceId`、`UserId` 必须一致。
- `ToolId` 必须一致。
- `Status` 必须是 `approved`。
- 工单未过期。
- `once` 工单必须有剩余次数，命中后消费。
- `session` 工单必须匹配当前 `SessionId`。
- `timed` 工单不要求 session 一致，但仍要求 workspace、agent、user、tool 一致。

参数匹配：

- shell：默认要求 actual `ArgumentsHash` 等于工单 `RequestedArgumentsHash`。如果后续要支持参数化命令，需要单独设计命令 AST 或 allowlist DSL。
- file_write：目标路径必须在 `TargetResources` 中，且 actual 参数 hash 建议一致；不一致时拒绝。
- file_patch：目标路径必须匹配，patch 操作类型必须不超出工单声明；缺少操作摘要时拒绝。
- 网络工具：domain、method 和外传敏感数据声明必须匹配。
- 其他高风险工具：第一版按 exact arguments hash 匹配。

匹配失败的错误信息应包含：

```text
High-risk tool authorization required. No matching human authorization or auto-approval ticket was found.
Use request_tool_approval with tool_id='<tool>' and exact planned arguments, or ask the user to send /authorize <tool> 10m.
```

## 7. `request_tool_approval` 工具

新增高风险审批申请工具，但它本身必须是低风险或中风险工具，不需要运行时授权，否则会形成递归授权死锁。

工具参数：

```json
{
  "tool_id": "shell",
  "command_name": "dotnet test",
  "purpose": "Run the focused test suite after code changes.",
  "necessity": "The changed runtime authorization path needs verification before continuing.",
  "fact_basis": ["Source/PuddingRuntime/Services/Tools/PuddingToolRegistry.cs was modified", "PuddingRuntimeTests contains focused tool infrastructure tests"],
  "requested_arguments_json": "{\"command\":\"dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj\",\"shell\":\"powershell\"}",
  "target_resources": ["Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj"],
  "authorized_area": ["Source/PuddingRuntimeTests/**", "Source/PuddingRuntime/**"],
  "outside_authorized_area_reason": null,
  "may_damage_or_delete_data": false,
  "is_irreversible_operation": false,
  "backup_taken": false,
  "rollback_plan": "No source or data mutation is expected; if generated test artifacts appear they can be deleted from temp/bin/obj outputs.",
  "operation_context": "Local development workspace on Windows PowerShell, cwd=E:/github/AgentNetworkPlan/PuddingAgent, target is the runtime test project only.",
  "operation_plan": "Run one focused test command and inspect the result; do not execute any repair or cleanup command as part of this ticket.",
  "operation_steps": [
    {
      "step_number": 1,
      "command": "dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj",
      "working_directory": "E:/github/AgentNetworkPlan/PuddingAgent",
      "environment": "local dev, Windows PowerShell",
      "target_object": "Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj",
      "purpose": "Verify the runtime authorization behavior covered by the focused test project.",
      "expected_effect": "Runs tests and may create normal bin/obj test artifacts; it should not modify source files or application data.",
      "reasonableness": "The current task changed runtime authorization logic, and read-only inspection cannot prove the tests still pass.",
      "safety_check_before": "Confirm the command targets only the PuddingRuntimeTests project and does not include cleanup, delete, migration, or publish arguments.",
      "stop_condition": "Stop if the command attempts to run outside the repository, asks for credentials, or fails with an environment/destructive operation error.",
      "rollback_for_step": "No source rollback is expected; generated bin/obj artifacts can be removed if needed."
    }
  ],
  "temporary_file_evidence": null,
  "may_expose_secrets": false,
  "user_consent_status": "implied",
  "alternatives_considered": ["Read-only code inspection cannot verify test behavior"],
  "requested_scope": "once",
  "requested_duration_minutes": null,
  "risk_notes": "This is a test command and should not modify source or data files."
}
```

工具输出：

```json
{
  "ticketId": "tap_01hxyz",
  "status": "approved",
  "decisionReason": "Approved for one exact test command.",
  "allowedScope": "once",
  "expiresAtUtc": "2026-06-03T12:30:00Z"
}
```

被拒输出：

```json
{
  "ticketId": "tap_01hxyz",
  "status": "denied",
  "decisionReason": "Destructive operation requires explicit user consent.",
  "recommendedNextStep": "Ask the user for explicit consent or use /authorize file_write once."
}
```

## 8. 审计与可观测性

必须记录以下事件：

- `tool_approval.ticket_submitted`
- `tool_approval.review_started`
- `tool_approval.review_completed`
- `tool_approval.check_allowed`
- `tool_approval.check_denied`
- `tool_approval.ticket_consumed`
- `tool_approval.ticket_expired`

日志和 telemetry 维度至少包含：

- `ticket_id`
- `tool_id`
- `workspace_id`
- `session_id`
- `agent_id`
- `user_id`
- `decision`
- `scope`
- `args_hash`
- `denial_reason`

不得在日志中记录完整 shell 命令中的密钥、完整环境变量或完整工具参数。需要保留原始请求时，应写入受控工单存储并标记敏感字段。

## 9. 数据存储

第一版建议使用 Platform DB 表，而不是只用 runtime json 文件：

- 工单需要跨服务查询、审计和管理。
- 自动审批结果属于平台治理数据，不只是 runtime 临时授权。
- 后续 Admin UI 可以直接展示和筛选工单。

建议实体：`ToolApprovalTicketEntity`。

必要索引：

- `(WorkspaceId, AgentInstanceId, UserId, ToolId, Status)`
- `(TicketId)`
- `(ExpiresAtUtc)`
- `(SessionId, ToolId, Status)`

`once` 工单消费需要并发安全更新，避免两个并发工具调用同时消费同一张单次工单。

## 10. 测试策略

核心单测：

- `request_tool_approval` 对缺失字段返回可读错误。
- destructive 且无 explicit 用户同意时，在 LLM 前被拒绝。
- `permanent` 自动审批申请被拒绝。
- reviewer LLM 返回 invalid JSON 时工单为 denied 或 need_human，不抛出到 Agent loop。
- 人工 `/authorize` 命中时不查询或不要求自动审批。
- 自动审批命中时高风险工具放行。
- capability policy 未暴露工具时，即使有批准工单也拒绝。
- shell actual args hash 与工单不一致时拒绝。
- once 工单第一次放行、第二次拒绝。
- session 工单跨 session 拒绝。
- timed 工单过期后拒绝。

集成测试：

- Agent 先调用高风险工具被拒，错误提示包含 `request_tool_approval`。
- Agent 提交审批，审批通过后同一 exact tool call 成功。
- 审批拒绝后同一 tool call 仍被 403 拒绝。
- `/authorize shell once` 仍然按原链路放行，不依赖自动审批。

## 11. 后续扩展

后续可以独立扩展：

- Admin UI 展示审批工单、筛选拒绝原因、人工撤销。
- 人类可将 `need_human` 工单转换为 `/authorize` 或手动批准。
- 针对文件操作设计资源 allowlist DSL。
- 针对 shell 命令设计安全命令 AST，而不是正则匹配。
- 增加策略配置，例如不同 workspace 的自动审批上限、模型、拒绝规则。
