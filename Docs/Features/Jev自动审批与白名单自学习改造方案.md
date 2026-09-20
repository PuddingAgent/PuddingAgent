# Jev 自动审批与白名单自学习 —— 改造方案（v1，2026-09-20）

> 目标读者：实现该改造的子代理。本文自包含，含全部 file:line 锚点、接口契约、安全不变量与验收标准。

## 0. 用户需求（原话要点）

1. Jev 速度毫秒级、擅长决策判断 ⇒ 用于**权限自动审批**，**取代原来的审批审计员**。
2. Jev 直接审计 Agent 输出的指令（**工具调用 + Shell 指令**）与背景信息，判断是否放行。
3. 通过 Jev 判断该指令**是否适合加入白名单**，避免每次工具/命令请求都调用 Jev。

## 1. 现状（侦察所得，均为 file:line 实证）

### 1.1 拦截点（唯一执行入口）
`Source/PuddingRuntime/Services/AgentFirewall.cs:169-242` —— **Gate 4（Authorization）**：
- `:181` `if (_policySvc is null || !_policySvc.RequiresRuntimeAuthorization(descriptor)) return Allow;`
- `:194` `var authorization = await _authzSvc.CheckAsync(authzCtx, descriptor, ct);`（会话 grant 通道）
- `:201` `var approval = await _approvalSvc.CheckAsync(new ToolApprovalExecutionRequest{ … WorkingDirectory = ctx.WorkingDirectory }, descriptor, ct);`（票据 + 审计通道）
- `:222-231` 保留 typed 结果：`if (approval.Disposition is { } disposition && disposition != Denied) return FirewallDecision.Deny(approval.Message, FirewallGate.Authorization, disposition, approval.ReasonCode);`

调用方：`Source/PuddingRuntime/Tools/Platform/PuddingToolRegistry.cs:649`（`_firewall.EvaluateAsync(firewallCtx, ct)`）；
后台投递链另有一处 `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs:422-425`。
退出码映射：`PuddingToolRegistry.cs:653-661`（Authorization 门 + DeferredDependency ⇒ **exitCode 428** 且 `ToolResultStatuses.DependencyWait`，其余 403）。

### 1.2 决策契约
`Source/PuddingCore/Tools/ToolApproval.cs`：
- `:4-15` `enum ToolApprovalDecision { Approved, Denied, NeedHuman, DeferredDependency }`
  —— `DeferredDependency` 为 **ADR-091 §4.4** 新增：**依赖不可用，不得折叠成 NeedHuman/Denied**。
- `:177-194` `record ToolApprovalReviewResult { Decision, DecisionReason, AllowedScope, AllowedDuration, RequiresHumanAuthorization, ChecklistFindings, MissingRequirements, AllowlistProposals, RecommendedFix, ReviewerModel, ReasonCode }`
- `:195-201` `record ToolApprovalAllowlistProposal { ToolId?, Command?, ArgumentsJson?, Reason? }` ← **白名单自学习的现成落点**
- `:366-373` `interface IToolApprovalReviewer { Task<ToolApprovalReviewResult> ReviewAsync(ToolApprovalTicketRequest request, ToolApprovalIdentity identity, ToolDescriptor descriptor, CancellationToken ct = default); }`
- `:376-388` `interface IToolApprovalService { SubmitAsync(...); CheckAsync(ToolApprovalExecutionRequest, ToolDescriptor, ct); }`
- `:355-362` `interface IToolApprovalAuditStore { SaveAsync(ToolApprovalAuditEvent, ct); ListAsync(ct); }`
- `:202-232` `record ToolApprovalTicketRecord { TicketId, Identity, ToolId, Request, **ArgumentsHash**, Scope, Status, DecisionReason, CreatedAtUtc, DecidedAtUtc?, ExpiresAtUtc?, RemainingUses?, ConsumedAtUtc? }`

### 1.3 现有实现
- `Source/PuddingRuntime/Tools/Approval/InMemoryToolApprovalService.cs:14`；`CheckAsync:337`、`SubmitAsync:92`。
- 评审器：`Source/PuddingRuntime/Tools/Approval/LlmToolApprovalReviewer.cs:12-31`（`ReviewAsync:21-30`：
  `ToolApprovalPromptBuilder.Build` → `_client.ReviewAsync` → `ToolApprovalReviewParser.Parse`）。
- LLM 客户端接缝：`LlmToolApprovalReviewer.cs:34-42` `IToolApprovalLlmClient` →
  `InvocationToolApprovalLlmClient`（`:208-422`，ctor `:221-233`，`ReviewAsync:235-325`，
  私有 `InvokeWithDeadlineAsync:331-384`）：**单次隔离 LLM + 30s 自身 deadline + 全链 fail-closed 到 DeferredDependency**。
- **工单 / 白名单 / 审计不是数据库表**：`<RuntimeRoot>/tool-approval/{tickets,allowlist}.json`
  与 `audit-events.jsonl`。

### 1.4 Jev 侧现成能力（本次改造的**最小侵入接缝**）
- `Source/PuddingCore/Abstractions/IJevDecisionService.cs`：`DecideAsync(JevDecisionRequest, ct) → JevDecisionResult`；
  `JevQuestion { Name, Type, Instructions?, ChoiceCriteria?, ScoreCriteria?, NoulCriteria? }`；
  `JevQuestionType { Choice, Score, Noul }`；`JevDecisionException` + 稳定错误码 `JevDecisionCodes`。
- 实现：`Source/PuddingRuntime/Services/JevDecisionService.cs`（`HttpClientName = "Jev"`）。
- 选项解析：`Source/PuddingRuntime/Services/JevDecisionOptionsProvider.cs`（**资源池优先**，池 `jev.apiKey` 已生效）。
- **现状：`IJevDecisionService` 已注册但无任何生产调用点** ⇒ 本次是它的第一个消费者。

## 2. 安全不变量（来自行业实证，必须由测试锁死）

调研结论（Claude Code / Codex CLI / OpenAI Agents SDK / GuardFall / Microsoft AGT ADR）：
业界把「模型推理直接做执行期 allow/deny」视为**反模式**，正确形态是
**确定性规则/沙箱为地基 + 小模型分类器作第二道闸门 + 人工为最终兜底**，
且 **deny 是固定求值序中最优先且不可被 allow 挖洞**。
GuardFall 实测：11 个开源 Agent 中 10 个可被 5 类 shell 重写绕过（`r''m`、`$IFS`、命令替换、`base64|sh`、破坏性 argv flag）。

因此本方案**不把 Jev 当作唯一授权者**，并锁死以下不变量：

| ID | 不变量 | 验证方式 |
|---|---|---|
| **I1** | **deny 优先**：命中确定性危险模式时**绝不调用 Jev**，直接 `Denied` | fake Jev 断言**调用次数 = 0** |
| **I2** | Jev 不可用/超时/无 answers/解析失败 ⇒ **`DeferredDependency`**（fail-closed），不得折叠为 Approved/NeedHuman | 注入异常/空 answers，断言 Decision |
| **I3** | 白名单提案**只允许精确匹配**：`Command` 含 shell 元字符或通配符时**不得**产生提案 | 参数化用例（`; & \| > < $ \` ( ) { } * ? ~`、换行、`$IFS`）断言 `AllowlistProposals` 为空 |
| **I4** | **低置信不自动放行**：白名单 noul 概率 < 阈值（默认 0.90）或 decision ≠ approve ⇒ 不产生提案 | 边界用例 |
| **I5** | 白名单规则必须带 **provenance**（来源工单 / Jev 模型 / 概率 / 理由 / 时间）且可撤销 | 断言落库字段 |
| **I6** | 白名单命中时**不再调用 Jev**（缓存键 = `ToolId` + `ArgumentsHash` + `WorkingDirectory` + `Shell`） | fake Jev 断言调用次数 = 0 |

## 3. 设计

### 3.1 新增：`JevToolApprovalReviewer : IToolApprovalReviewer`
位置：`Source/PuddingRuntime/Tools/Approval/JevToolApprovalReviewer.cs`

依赖（全部可选参数 + 默认 null，保证 DI 未注册时退化）：
`IJevDecisionService`、`ILogger<JevToolApprovalReviewer>`、`IOptions<ToolApprovalJevOptions>`（或 IConfiguration）。

`ReviewAsync` 流程：
1. **确定性 deny 前置检查**（I1）——命中即返回 `Denied` + `ReasonCode = "jev_skipped_deny_rule"`，**不调用 Jev**。
2. 组装 `JevDecisionRequest`：
   - `State`（JSON 对象，**只放决策必需信息**，成本按 input token 计）：
     `toolId`、descriptor 的类别/风险标记、`argumentsJson`（**截断至 8 KB**）、`workingDirectory`、`shell`、
     发起者身份（agent/session）、**命中的白名单/既有 deny 规则摘要**、最近 N 条同类审计事件摘要。
   - `Questions`（一次 round trip，四问并行、共享 state 成本）：
     | name | type | 语义 | criteria |
     |---|---|---|---|
     | `decision` | **choice** | 是否放行 | `approve` / `deny` / `need_human`（每个给清晰描述） |
     | `risk` | **score** | 风险等级 0..3 | 4 级：无害只读 / 局部可逆 / 影响共享状态 / 破坏性不可逆 |
     | `scope` | **choice** | 放行粒度 | `once` / `session` / `timed` |
     | `allowlist` | **noul** | 是否适合加入白名单 | instructions 明确要求「仅当命令可被**精确重现**且无参数歧义」才判是 |
3. 映射（**唯一映射表，实现必须照此**）：

| Jev 结果 | ToolApprovalDecision | RequiresHumanAuthorization |
|---|---|---|
| `decision=approve` 且 `risk<=1` 且 `scope!=once` 且 allowlist 概率≥阈值 | `Approved` | `false` |
| `decision=approve` 其余情形 | `Approved` | `true` |
| `decision=need_human` | `NeedHuman` | `true` |
| `decision=deny` | `Denied` | `false` |
| 缺 answers / 抛 `JevDecisionException` / 映射失败 | **`DeferredDependency`** | `true` |

4. `AllowlistProposals` 生成条件（**I3+I4 同时满足才给**）：
   `decision=approve` ∧ `allowlist` 概率 ≥ 0.90 ∧ `Command` **无 shell 元字符/通配符/换行/`$IFS`**
   ⇒ 产出**单条**提案：`ToolId` = 精确工具 id，`Command` = **规范化后的精确命令全文**，
   `ArgumentsJson` = 本次调用参数的**规范化 JSON**，`Reason` 含 Jev 模型 + 概率 + 风险等级。
   **禁止**产生前缀/通配/正则形态。
5. `ReviewerModel` = `JevDecisionResult.Model`（如 `jev-1.13.0`）；`ReasonCode` 用稳定码：
   `jev_approved` / `jev_denied` / `jev_need_human` / `jev_unavailable` / `jev_skipped_deny_rule` / `jev_invalid_response`。

### 3.2 选择与回退（"取代审批审计员"的正确落地）
- 配置开关：`ToolApproval:Reviewer` = `jev`（默认，当 Jev 已配置时）| `llm`（旧行为）。
- **不做跨模型静默回退**：Jev 不可用时返回 `DeferredDependency`（fail-closed），
  与现有 `InvocationToolApprovalLlmClient` 的 30s deadline → `DeferredDependency` 语义保持一致。
- `LlmToolApprovalReviewer` **保留**（开关可切回），不删除。

### 3.3 白名单快速通道（需求 3）
先核实 `InMemoryToolApprovalService.CheckAsync:337` 是否**在调用 reviewer 之前**查白名单：
- 若已是 ⇒ 无需改动，仅补测试（I6）。
- 若不是 ⇒ 调整为「白名单命中 ⇒ 直接 Approved，不再进 reviewer」，并保持 I1 的 deny 前置。

## 4. 验收标准

1. `dotnet build` **0 error**（含 `PuddingHost` 全链）。
2. 新增离线测试全绿，且**逐条覆盖 I1–I6**（每条一个显式命名的用例）。
3. 既有审批测试不回归（若因开关默认值变化需调整，必须在交付说明中**显式列出并解释**）。
4. 开关 `ToolApproval:Reviewer=llm` 时行为与改造前一致。
5. 文档同步：`Source/PuddingRuntime/code_map.md` 增加新类型条目；新类型全部带 XML 文档注释；
   在 `Docs/Features/` 记录开关与不变量。
6. 交付说明必须包含：`git diff --stat`、测试结果原文（通过/失败/总计）、以及**未做之事**的诚实清单。

## 5. 明确不做（防范围蔓延）

- 不改 `AgentFirewall` Gate 1-3、不改 grant 通道语义。
- 不引入数据库表（工单/白名单仍为 JSON 文件）。
- 不实现前端（`PuddingPlatformAdmin`）审批 UI。
- 不让 Jev 覆盖确定性 deny（见 I1）。
- 不实现 ADR-091 §4 中尚不存在的 `IToolAdmissionService` 草图（属设计态）。

## 6. 参考（行业实证摘要）

- Claude Code：`deny → ask → allow` 固定求值序，deny 不可被更窄 allow 挖洞；规则由 harness 而非模型强制；
  auto mode 的 classifier 只是第二道闸门，**不能覆盖 deny**。
- Codex CLI：`sandbox_mode`（能力边界，OS 强制）× `approval_policy`（何时必须问）；
  `approvals_reviewer='auto_review'` 负责审"本来就该审的动作"，判决 Approved/Denied/Aborted/Timed out，
  **不改变 sandbox 边界**。
- OpenAI Agents SDK：审批建模为「暂停 + 可序列化 RunState」，未决审批作为 interruptions 暴露，approve/reject 后 resume。
- 自动白名单的三类实证风险：静默扩大受信面；rug-pull（批准一次后目标可变脸）；「越调优越危险」
  （清单校验"执行什么"而忽略"在什么被投毒的环境里执行"）。
- 模式精确性：exact > prefix > wildcard；前缀匹配在本 shell 语义下必然有洞
  （`git push *` 匹配不到 `git -C <dir> push`）。
- fail-closed：决策依赖不可用/超时时业界取拒绝而非放行。

## 7. 实测延迟与设计含义（2026-09-20 本机实测）

### 7.1 测量方法
两个独立探针：`temp/jev-latency-probe.ps1`（真实调用）与 `temp/jev-latency-control.ps1`（对照）。
脚本**刻意写成纯 ASCII** —— Windows PowerShell 5.1 会把无 BOM 的 .ps1 按 ANSI/GBK 解码，
含中文会破坏引号配对导致 `ParserError: UnexpectedToken`（本机已踩过）。

- 探针 A：`POST https://jevtypesafeai.com/api/v1/decide`，1 个 `noul` 提问、state 极短，6 次。
- 探针 B（对照，**同主机同 TLS 但不触发推理**）：
  (a) `GET /docs` 静态页；(b) `POST /api/v1/decide` 带非法/缺失鉴权 → 401。

### 7.2 结果
| 测量 | min | median | max |
|---|---|---|---|
| 真实决策调用（1 问，286 input tokens） | 1068 ms | **1095 ms** | 1695 ms |
| 对照 a：`GET /docs`（无推理） | 364 ms | 421 ms | 1359 ms（首次含 TLS 冷启动） |
| 对照 b：`POST /decide` → 401（无推理） | 501 ms | **511 ms** | 547 ms |

6/6 成功，`model=jev-1.13.0`，每次 `input_tokens=286`、`cost_usd=0.000121`。

### 7.3 结论（严格区分证据与推断）
- **证据**：从本机到该托管端点，**端到端中位数 ≈ 1.10 s**，**不是毫秒级**。
  注：探针每次调用都新建连接，未复用连接池。
- **推断（未单独测量）**：模型推理增量 ≈ 1095 − 511 ≈ **580 ms**；其余约 0.5 s 是同主机 HTTP/TLS 开销。
- **推断（未单独测量）**：生产路径 `JevDecisionService` 经 `IHttpClientFactory` 复用连接、TLS 握手被摊薄，
  故单次出厂延迟**预计 ≈ 600–800 ms**。**此为推断**，须在部署后用「连接复用版」探针复测确认。

### 7.4 对设计的强制含义
1. **白名单缓存（I6）由「优化」升级为「必需」**：若审批路径每次调用 Jev，
   将给**每个被放行的工具调用**增加约 0.6–1.1 s 延迟；连续工具链（10 次调用）即 +6–11 s，不可接受。
   用户「用白名单避免每次调用」的判断因此**更加成立**，尽管其「毫秒级」前提与实测不符。
2. **审批调用必须设短超时**：建议 `ToolApproval:Jev:TimeoutMs = 3000`；超时 ⇒ `DeferredDependency`（fail-closed），
   **不得**沿用现有 LLM 通道的 30 s 上限。
3. **成本可计数但需归因**：单问 286 input tokens ≈ $0.000121；四问 + 完整 state（工具参数 + 背景）
   预计 1.5–3 K tokens ≈ **$0.0006–0.0013 / 次决策** ⇒ 应把 `JevUsage` 与 `ReviewerModel`
   写入审批审计事件，便于按 workspace / 工具维度归因。
