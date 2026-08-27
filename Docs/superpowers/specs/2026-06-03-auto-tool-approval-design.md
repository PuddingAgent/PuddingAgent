# 自动权限审查与危险操作防火墙统一设计

> 日期：2026-08-26（v2.1 合并修订版，原 2026-06-03 draft）
> 状态：Proposed for deployment；确定性命令防火墙已在 `master`，三层漏斗 `e716829` 尚未合入/部署
> 唯一跟踪任务：`e187a8bbd2d640bb87b96fd3cf548966`
> 已合并历史任务：`ce63f8c06ad64069b7839056e46461ed`
> 相关代码：`Source/PuddingRuntime/Services/AgentFirewall.cs`、`Source/PuddingRuntime/Tools/Platform/ToolInvocationService.cs`、`Source/PuddingCore/Tools/PuddingToolContracts.cs`（`IPuddingToolExecutionService`）、`Source/PuddingRuntime/Tools/Approval/InMemoryToolAuthorizationService.cs`、`Source/PuddingCore/Runtime/RuntimeControlService.cs`、`Source/PuddingCore/Tools/ToolApproval.cs`（`IToolApprovalService`）

---

## 0. 修订说明（2026-08-20—2026-08-26）

本版本按用户 2026-08-20 设计方向，**否决原「工单制 + reviewer 审批」架构核心**，改写为「系统侧自动审查 + 无感放行/质询」架构。关键变化：

| 维度 | v1（2026-06-03，已否决核心） | v2（2026-08-20，本版） |
|------|------------------------------|------------------------|
| 触发面 | 所有需要运行时授权的高风险工具，Agent 主动提交 `request_tool_approval` 工单 | **只拦危险命令**，普通命令零介入；全程对 Agent 无感 |
| 审查主体 | 独立 reviewer LLM 审批工单（干净上下文单次调用） | 三层漏斗：静态分级 → 事实自检 → 兜底质询（静态判死 / 单次 LLM 调用） |
| Agent 负担 | 每次高风险操作须填写 20+ 字段工单，等待审批 | 不要求任何操作提供票据；仅当 Agent 犯蠢时收到「严厉质询」反馈 |
| Audit agent | 计划引入 workspace audit agent 实例 | **明确否决**（用户 2026-08-20 指示）：不引入 agent 实例，最多一次 LLM API 调用 |
| 模型选择 | 硬编码 reviewer 模型 | **配置化**：模型路由走 `llm_resource_pool`，用户开关与覆盖项读 `<DataRoot>/config/system.json`，默认 Flash 级别，不硬编码 |
| 工单制 | 核心审查路径 | **降级**为可选的人工介入通道（保留但不作为默认审查方式） |
| 安全底线 | 不可逆/破坏性操作直接拒绝 | **保留**：不可逆/破坏性/无证据删除仍严格拒绝，不交给 LLM 自由批准 |

修订记录（蜜糖评审落实）：

| 日期 | 修订项 | 内容摘要 |
|------|--------|----------|
| 2026-08-20 | R1/R2 | 新增 §9「迁移与下线计划」（三阶段灰度 + 每阶段一句话回退路径）；§3.3 补充「Gate2 证据采集规范」（零成本投影优先、文件系统/git 探测硬性超时 ≤2s、探测失败视为证据缺失 → Gate3，绝不挂起） |
| 2026-08-26 | R3 | 将“危险命令字符串防火墙”任务 `ce63f8c06ad64069b7839056e46461ed` 合并到三层漏斗任务 `e187a8bbd2d640bb87b96fd3cf548966`，后者成为唯一跟踪入口 |
| 2026-08-26 | R4 | 用户审批降为最后手段：普通写入和可证明安全的操作由系统自动放行；只有无法自动裁决、且策略允许人工覆盖的高风险操作才显示审批 UI |
| 2026-08-26 | R5 | 风险事实改由工具描述、实际参数与系统证据推导；删除 Agent 可通过修改 `may_damage_or_delete_data` / `is_irreversible_operation` 自报字段改变裁决的能力 |
| 2026-08-26 | R6 | 固化 `save_memory` 参数级分级：`get_*` 为 L0、`upsert/set_important` 为 L1、`delete` 为 L2；禁止再把整个 `save_memory` 统一视为破坏性操作并要求工单 |

---

### 0.1 2026-08-26 当前事实与新增事故

本设计不是从零开始，当前代码处于“两段已写、尚未统一上线”的状态：

| 部分 | 当前事实 | 结论 |
|---|---|---|
| 确定性字符串防火墙 | 已在 `master`：`ToolApprovalCommandFirewall` 接入 `InMemoryToolApprovalService.CheckAsync`；危险命令秒拒，安全命令秒放，定向测试 6/6 | 已实现，但危险拒绝仍默认引导 `request_tool_approval`，需并入 v2 统一反馈 |
| 三层漏斗 | `feature/auto-approval-v2` 的 `e716829` 已实现 17 个文件、62/62 定向测试和 Runtime 构建；`ToolReview.Enabled=false` | 代码存在，但不在当前 `master`，也没有部署生效 |
| Gate2 事实自检 | `e716829` 在 `AgentFirewall` 中仍传 `Evidence = null` | 设计接口存在，真实证据投影尚未接通，不能把分支代码存在视为 Gate2 已完成 |
| 工具风险分级 | `e716829` 把 `save_memory` 整体列入 L1；当前 `master` 的工具 descriptor 又把它整体标记为 `Destructive` | 两者都缺少 `action` 级语义：upsert 应自动放行，delete 必须进入高风险审查 |

2026-08-26 现场再次出现审批低效循环：Agent 为 `save_memory action=upsert` 调用 `request_tool_approval`，因 `may_damage_or_delete_data=true` 被拒；随后尝试把同一字段改成 `false` 再申请。该行为同时暴露三个问题：

1. 普通、可回滚的业务 upsert 被粗粒度工具风险标记升级为人工审批；
2. 安全关键事实由 Agent 自报，模型可以通过改布尔值尝试绕过；
3. 拒绝反馈仍把 Agent 引向下一张工单，形成“申请—拒绝—改字段—再申请”的低效循环。

因此，v2.1 上线门禁新增：**风险事实系统推导、参数级风险分类、相同调用有界去重、用户审批最后手段**。

---

## 1. 目标

为 Pudding 的长程 Agent 提供一道**系统侧、对 Agent 无感的自动安全围栏**。系统自动审查每一次高风险工具调用：能放行就无感放行，不能放行就把「前面是悬崖」式的严厉质询直接返回给犯蠢的 LLM，让它自己纠正；整个过程不消耗 Agent 的注意力，不要求 Agent 提交票据，不引入独立审计 Agent 实例。

自动审查的本质是**透明护栏**，不是审批流程：

- **无感优先**：普通命令（读、查、低风险写入）零介入；只有 High 权限、破坏性、不可逆、删除类命令才进入审查漏斗。
- **事实驱动**：调用方上下文（最近的工具结果、git 状态、备份/回滚证据、范围声明）已经足够充分时，直接放行，不打扰。
- **兜底质询**：证据不足时，能静态判死就静态判死（确定性危险规则不调用 LLM），不能静态判死才做**一次**干净的 LLM 调用（无对话历史、不可用工具），返回 allow/deny。
- **透明可审计**：所有审查事件入日志与遥测，但不注入 Agent 工作流，不打断 Agent 节奏。
- **用户审批降权**：系统先用确定性规则、事实证据和最多一次 LLM 审查完成裁决；检查已有人工 grant 不等于向用户发起审批。只有返回 `HumanRequired` 且策略允许覆盖时，才显示一次用户审批。
- **风险事实不可由模型改写**：`mayDamageOrDeleteData`、`isIrreversible`、目标范围和实际 operation 必须从 tool descriptor、实际参数和系统证据计算。Agent 提供的同名字段最多是非权威说明，不能降低风险级别。
- **拒绝不成循环**：同一 `(sessionId, toolId, argsHash, evidenceHash)` 的重复拒绝复用稳定结果，不再次调用 reviewer；第二次无进展重试返回 `approval_loop_detected` 和可执行替代路径。

安全底线（v1 保留，不可由 LLM 覆盖）：

- 格式化磁盘、删除数据库、递归删除 workspace 根/用户目录/系统目录、删除无法证明性质的未跟踪文件、修改系统关键配置且无回滚——这类操作**系统直接拒绝**，不交给 LLM 自由批准，也不进入任何审批流程。
- 系统拒绝时返回的是**严厉质询提示词**（指出具体缺失项与可执行替代路径），让犯蠢的 LLM 自行止损并修正行为，而不是把决策权外包给另一个 LLM。

非目标：

- 不改变 `/authorize`、`/deny`、`/revoke` 的 API、scope、生命周期与撤销语义；已有 grant 作为 Gate2 的强授权证据，但不能绕过 capability/workspace/sandbox 或不可覆盖的 `StaticDeny` 安全底线。
- 不让自动审查批准 capability policy 未暴露的工具。
- 不引入独立 Audit agent 实例（用户 2026-08-20 明确否决）。
- `request_tool_approval` 工单制不再是默认审查方式，仅保留为可选人工介入通道。
- 不对 shell 命令做宽泛正则授权；危险命令识别以静态特征（命令+参数+路径）为主。

---

## 2. 当前基线（执行链路现状）

当前高风险工具的执行链路：

1. Agent 的 tool call 进入 `ToolInvocationService`（`Source/PuddingRuntime/Tools/Platform/ToolInvocationService.cs`）。
2. `ToolInvocationService` 调用 `IPuddingToolExecutionService.ExecuteAsync`（契约位于 `Source/PuddingCore/Tools/PuddingToolContracts.cs`）。
3. 执行前经过 `AgentFirewall`（`Source/PuddingRuntime/Services/AgentFirewall.cs`）的 8 道门：

```text
Gate1 ModeGate        — YOLO / EStop / Safe 执行模式
Gate2 SessionGate     — session 生命周期状态
Gate3 CapabilityGate  — 工具必须在 capability policy 内
Gate4 AuthorizationGate — 显式/隐式运行时授权（人工 /authorize + implicit approval 工单）
Gate5 SandboxGate     — sandbox 策略
Gate6 WorkspaceGate   — host 文件 workspace 边界
Gate7 ResourceGate    — 资源权限（shell / file-write / network）
Gate8 StateGate       — agent 状态（heartbeat cooldown）
```

- `InMemoryToolAuthorizationService`（`Source/PuddingRuntime/Tools/Approval/InMemoryToolAuthorizationService.cs`）记录人工授权 grant（workspace/session/agent/user/tool 身份边界）。
- `RuntimeControlService`（`Source/PuddingCore/Runtime/RuntimeControlService.cs`）提供运行时模式（YOLO/EStop/Safe）。
- v1 的 `AgentFirewall.EvaluateAuthorizationGateAsync` 在人工授权未命中时，会调用 `IToolApprovalService` 查询 implicit approval 工单（v1 核心，本版改造点）。

---

## 3. 核心架构：三层漏斗

### 3.1 总览

自动审查以**三层漏斗**形式挂在 `AgentFirewall.AuthorizationGate` 位置（替代原「人工授权未命中 → 查工单」分支）。`master` 已有的 `ToolApprovalCommandFirewall` 不再作为并行审批链，而是收编为 Gate1 的 shell/terminal 确定性分类器。普通命令在 Gate1 即无感放行，绝不进入后续层：

```text
Tool call（tool_id + arguments + 调用方上下文）
  │
  ▼
┌─────────────────────────────────────────────────────────────┐
│ Gate1 静态危险分级（同步、纯静态、零 LLM）                       │
│   L0 普通（读/查/低风险写入）        ──► 无感放行（零介入）       │
│   L1 低风险写入（workspace 内 patch 等）─► 无感放行（零介入）     │
│   L2 高风险（High 权限/破坏性/不可逆/删除类）─► 进入 Gate2        │
└─────────────────────────────────────────────────────────────┘
  │（仅 L2 继续）
  ▼
┌─────────────────────────────────────────────────────────────┐
│ Gate2 事实自检（同步、零 LLM）                                 │
│   调用方上下文已含充分事实？                                    │
│   （备份/回滚/范围声明/证据/命令结构可解析）                      │
│   是 ──► 无感放行                                              │
│   否 ──► 进入 Gate3                                            │
└─────────────────────────────────────────────────────────────┘
  │（证据不足才继续）
  ▼
┌─────────────────────────────────────────────────────────────┐
│ Gate3 兜底质询（证据不足时触发）                                │
│   (a) 静态判死：命中确定性危险规则 ──► 直接返回严厉质询提示词       │
│       （不调用 LLM，"前面是悬崖"式，附具体缺失项）                 │
│   (b) 需判断：灰色地带 ──► 单次 LLM API 调用                    │
│       （干净上下文、无对话历史、不可用工具）                       │
│       allow ──► 放行；deny ──► 质询提示词（含 reason+缺失项）     │
└─────────────────────────────────────────────────────────────┘
```

所有层级的审查事件全部入日志/遥测（透明可审计），事件载荷本身不注入 Agent 对话上下文；只有 `Challenge` 的有界质询作为本次工具结果返回给 Agent，帮助其修正调用。

漏斗只生成四种最终结果：

- `Allow`：无感执行；
- `StaticDeny`：确定性安全底线拒绝，不允许 LLM 或用户覆盖；
- `Challenge`：证据不足，向 Agent 返回一次可纠正质询，不弹用户审批；
- `HumanRequired`：只有策略明确允许人工覆盖、且自动系统无法安全裁决时产生，UI 才显示一次审批。

### 3.2 Gate1 静态危险分级

**职责**：只拦危险命令；普通命令零介入。纯静态判定（tool_id + 解析后的参数），不调用任何 LLM。

分级规则：

| 级别 | 判定依据 | 处置 |
|------|----------|------|
| **L0 普通** | 只读/查询/分析类工具与参数（`file_read`、`search_grep`、`list_dir`、`query_session_logs`、只读 git/log/status 等） | **无感放行**，零介入 |
| **L1 低风险写入** | 写入类但受既有 gate 约束且不构成破坏：workspace 内 `file_patch`/`file_write`、`git commit`、`save_memory action=upsert/set_important`（已有 WorkspaceGate/SandboxGate/工具约束防护） | **无感放行**，零介入 |
| **L2 高风险** | 命中以下任一类：<br>· 工具级 High 权限标记（`RequiresRuntimeAuthorization=true` 的破坏性/删除类工具）<br>· shell 命令含破坏性动词或标志（`rm -rf`、`del /s /q`、`format`、`diskpart clean`、`DROP DATABASE`、`TRUNCATE`、`Remove-Item -Recurse -Force` 等）<br>· 删除类操作（delete/remove/truncate/drop）<br>· 不可逆操作（格式化、覆写系统配置、修改权限/防火墙/密钥库）<br>· 目标路径指向敏感区域（workspace 根、用户目录、系统目录、生产数据） | 进入 **Gate2** |

实现要点：

- 静态分类器必须是**纯函数**（输入 tool_id + 参数 + 路径，输出 risk level），可穷举单测。
- 危险特征优先采用「命令/参数 AST 级特征」而非裸正则：先解析 shell 参数结构，再匹配危险动词/标志组合，降低误报与漏报。
- 分类结果缓存（hash(tool_id, args) → level），同参数重复调用不重复分类。
- 分类器必须先解析工具的 operation discriminator（如 `action`、HTTP method、command verb），再使用 descriptor 风险元数据兜底；工具级 `Destructive` 不能把安全 operation 永久升级为人工审批。
- `save_memory` 的冻结规则：`get_important` 为 L0，`upsert/set_important` 为 L1，`delete` 为 L2。若 `action` 缺失按 schema 默认 `upsert`，无法解析才 fail closed 到 L2。
- `request_tool_approval` 的 `may_damage_or_delete_data`、`is_irreversible_operation` 从 Agent-facing schema 删除；服务端根据实际待执行参数生成 `SystemRiskFacts`。旧字段若仍由历史客户端传入，只能提高风险，绝不能降低系统推导值。

### 3.3 Gate2 事实自检

**职责**：当调用方上下文已经包含充分事实时，直接无感放行。同步、零 LLM。

检查项（全部可基于现有上下文机械判定，不新增 Agent 负担）：

| 检查项 | 判定依据 | 示例 |
|--------|----------|------|
| **范围声明** | 目标路径在 workspace 内，或属于明确允许区域（如 `temp/`、`bin/obj` 构建产物目录、`.pudding/` 运行时缓存） | `file_patch` 目标在 `Source/PuddingRuntime/**` 内 |
| **删除证据** | 目标是 git 跟踪文件（可回滚）或已有证据证明是临时文件/构建产物 | 删除 `temp/xxx.tmp`，且最近工具结果刚创建该文件 |
| **备份/回滚** | git 工作区干净或有对应 commit/diff 已保存；删除前已备份 | 删除前 `git status` 显示无未提交改动，或 diff 已存 `temp/` |
| **命令结构** | shell 命令可解析且为低破坏组合（dry-run、只读前缀、单目标、无递归强制标志） | `git clean -n`、`dotnet build`、`Remove-Item file.txt`（单文件非递归） |
| **执行模式** | YOLO 模式（用户显式放开，语义不变，`RuntimeControlService` 模式为准） | `RuntimeExecutionMode.Yolo` |

判定逻辑：

- **全部满足（或 N/A）** → 无感放行。
- **任一关键项缺失**（如删除未跟踪文件、无备份、命令含递归强制标志且无证据） → 进入 **Gate3**。

关键原则：**不因节流丢失任务信息**——Gate2 只看「事实是否已存在」，绝不要求 Agent 停下来补材料；事实不足时才由 Gate3 用质询反馈缺失项。

#### Gate2 证据采集规范（2026-08-20 补充）

`FactEvidenceBundle` 的构造时机为 **AuthorizationGate 时点**（执行引擎在调用 `IToolReviewService.ReviewAsync` 之前装配）。「同步零 LLM」不意味着无界阻塞——检查项（如备份/回滚检查里的 `git status`）确需真实探测时必须有界、可失败，规范如下：

- **证据来源优先 = 会话内最近工具结果的零成本投影**：优先从会话上下文投影（最近工具结果摘要、已执行的 git 状态缓存、`SearchAttemptLedger`、范围声明），不发起任何新的进程/IO 探测。
- **确需文件系统/git 探测时硬性超时**：仅在会话内无可用投影、必须现场确认（如 `git status`、文件存在性）时才允许探测，且必须有硬性超时（建议 ≤2s），超时即放弃本次探测。
- **探测失败 = 证据缺失 → Gate3，绝不挂起**：超时、进程失败、结果不可解析一律视为「该项证据缺失」，不重试、不阻塞执行链，直接进入 Gate3 由质询反馈缺失项。

### 3.4 Gate3 兜底质询

**职责**：证据不足时，二选一处置。此层是唯一可能调用 LLM 的地方，且**最多一次 API 调用**。

#### (a) 静态判死（确定性危险规则，不调用 LLM）

命中以下任一条直接返回**严厉质询提示词**：

- 格式化磁盘/分区/挂载点/存储卷（`format`、`diskpart clean` 等）。
- 删除数据库 / `DROP DATABASE` / `TRUNCATE` 生产或未知环境表。
- 递归删除目录且目标是 workspace 根、用户目录、系统目录、配置目录、日志归档目录。
- 删除无法证明性质的文件（非 git 跟踪、无备份、无临时文件证据）。
- 修改系统关键配置/启动项/权限策略/防火墙规则/密钥库/认证配置且无完整回滚+验证方案。
- 含有高破坏风险的 shell 命令组合（`rm -rf`、`del /s /q`、`Remove-Item -Recurse -Force` 等指向敏感路径）。

**处置**：直接 deny，不调用 LLM，把「前面是悬崖」式质询提示词作为工具结果返回给犯蠢的 LLM。

质询提示词必须包含（可执行、可纠正，而非单纯拒绝）：

```text
⚠️ 你的操作被系统安全围栏拦截。原因：<具体危险点>

当前操作：<tool_id> <参数摘要>
缺失/违反项：
  1. <缺失项1>（例如：目标文件无法证明是临时文件或构建产物）
  2. <缺失项2>（例如：命令含递归强制标志，且无备份证据）

请这样做：
  · <替代路径1>（例如：先 file_read 读取该文件确认性质，再决定）
  · <替代路径2>（例如：git status 确认可回滚，或先将 diff/文件备份到 temp/）
  · <替代路径3>（例如：缩小范围到单文件、单目录，去掉 -Recurse/-Force 等标志）
  · 若必须执行，请向用户说明风险并请求明确授权（/authorize）。

不要盲目重试同一操作——重复触发安全围栏可能导致 session fuse。
```

#### (b) 需判断（灰色地带 → 单次 LLM API 调用）

非确定性危险但未命中静态判死规则的操作（如删除某目录但不确定是否安全、shell 命令有破坏可能但结构可解释、目标区域模糊），做**单次**干净的 LLM 审查调用：

- **干净上下文**：只包含——工具 descriptor 的风险元数据、命令/参数摘要、Gate2 预提取的证据片段摘要（最近相关工具结果、git 状态）、系统固定审查规则。**不含** Agent 对话历史、不含会话上下文。
- **不可用工具**：审查 LLM 只输出 JSON，无法调用任何工具。
- **模型**：由配置决定（见第 4 章），默认 Flash 级别。
- **超时/失败策略**：调用超时、网络错误、解析失败 → **fail-closed 默认 deny**，并返回质询提示词（注明"系统无法完成审查，已保守拒绝"）。

审查 LLM 输出严格 JSON：

```json
{
  "decision": "deny",
  "reason": "The target directory is not provably a build artifact; recursive delete without backup evidence is not acceptable.",
  "requiredEvidence": [
    "confirm the directory contains only generated output",
    "or back it up to temp/ first",
    "or shrink scope to a single file"
  ]
}
```

- `decision: "allow"` → 放行（记录 model、latency、reason 摘要入审计）。
- `decision: "deny"` → 将 `reason` + `requiredEvidence` 组装为质询提示词返回给犯蠢的 LLM。

**安全底线（Gate3 不可逾越）**：即使 LLM 返回 allow，静态判死规则与工具存在性/capability policy 检查仍然前置拒绝；LLM 只能裁决灰色地带，不能推翻确定性危险判定。执行引擎是最终判定点。

### 3.5 执行授权顺序（v2）

```text
tool exists
  -> capability policy allows tool
  -> ModeGate / SessionGate / SandboxGate / WorkspaceGate / ResourceGate / StateGate
  -> derive SystemRiskFacts from descriptor + actual arguments + bounded evidence
  -> Gate1 静态危险分级（收编 ToolApprovalCommandFirewall）
      immutable hard deny: StaticDeny（现有 grant 也不能绕过）
      L0/L1: allow（无感，零介入）
      L2:
        -> attach existing human /authorize grant as strong Gate2 evidence
           （只检查既有 grant，不产生新的用户审批）
        -> Gate2 事实自检
            充分或有效 grant 已覆盖该非硬拒风险: allow（无感）
            不足:
              -> Gate3 兜底质询
                  (a) 静态判死: StaticDeny + 严厉质询提示词
                  (b) 需判断: 单次 LLM 调用 → Allow / Challenge
                  (c) 仅策略允许人工覆盖且自动裁决不足: HumanRequired
  -> HumanRequired 才创建一次用户审批；重复同参数不重复弹窗
  -> sandbox allows
  -> execute
```

---

## 4. 审查模型配置

**模型不硬编码**。审查模型优先复用 `llm_resource_pool` 的 `review` profile。产品默认值位于程序配置；用户启用状态和覆盖项位于 `<DataRoot>/config/system.json` 的 `ToolReview` 节，不写数据库，也不把 API Key 复制到该节。默认推荐 Flash 级模型（成本低、速度快；审查任务为简单二元判断，无需强推理模型）。

建议用户配置节（`<DataRoot>/config/system.json`；模型路由由资源池解析）：

```json
{
  "ToolReview": {
    "Enabled": true,
    "Provider": "llm_resource_pool",
    "PoolKey": "review",
    "Model": "deepseek-v4-flash",
    "TimeoutSeconds": 10,
    "MaxEvidenceTokens": 800,
    "FailClosed": true,
    "StaticDenyOnly": false
  }
}
```

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `Enabled` | `bool` | 自动审查总开关；关闭时回退到「人工授权 + 静态判死」最小模式 |
| `Provider` | `string` | `llm_resource_pool`（推荐，复用现有模型资源池）或 `direct`（独立通道） |
| `PoolKey` | `string?` | 资源池中的注册键；为空则取资源池默认审查槽位 |
| `Model` | `string` | 模型标识；**必须从配置读取，禁止硬编码**；默认推荐 Flash 级 |
| `TimeoutSeconds` | `int` | 单次审查调用超时；超时按 `FailClosed` 处理 |
| `MaxEvidenceTokens` | `int` | 注入审查上下文的最大证据 token 数（有界投影，防上下文膨胀） |
| `FailClosed` | `bool` | 默认 `true`：LLM 失败时保守拒绝；`false` 仅用于降级调试 |
| `StaticDenyOnly` | `bool` | `true` 时禁用 LLM 审查（只保留 Gate1/2 + 静态判死），作为无 LLM 依赖的最小安全模式 |

配置缺失时的默认行为：

- `Enabled` 缺省为 `false`，但**静态判死规则始终生效**（安全底线不依赖配置）。
- `Model` 缺省从资源池解析 Flash 级模型；资源池不可用时回退 `StaticDenyOnly=true`。
- `PoolKey=review` 未注册时不得静默借用主 Agent profile；保持 `StaticDenyOnly=true` 并记录稳定的 `tool_review.profile_unavailable`。

---

## 5. 审计与可观测性

所有审查事件**必须入日志/遥测**（透明可审计），事件载荷本身**不注入 Agent 对话上下文**；仅 `Challenge` 将脱敏且有界的 reason/requiredEvidence 作为当前工具结果返回给 Agent，不回灌完整审查记录。

事件清单：

- `tool_review.gate1_level`（tool_id、risk_level=L0/L1/L2）
- `tool_review.gate2_fact_check`（检查项命中/缺失摘要）
- `tool_review.gate3_static_denied`（静态判死，无 LLM）
- `tool_review.gate3_llm_requested`（单次调用开始）
- `tool_review.gate3_llm_allowed` / `tool_review.gate3_llm_denied`
- `tool_review.gate3_failed_closed`（超时/异常 → deny）
- `tool_review.human_required` / `tool_review.human_resolved`
- `tool_review.approval_loop_detected`（同一拒绝无新证据重复触发）

遥测维度至少包含：

- `tool_id`、`args_hash`、`risk_level`、`gate`、`decision`、`denial_reason`
- `workspace_id`、`session_id`、`agent_id`
- `model`（仅 LLM 层）、`latency_ms`（仅 LLM 层）
- `human_prompted`、`human_wait_ms`、`args_hash`、`evidence_hash`（不记录原始敏感参数）

隐私底线（v1 保留）：

- 不记录完整 shell 命令中的密钥、完整环境变量、完整工具参数；使用 `args_hash` 与脱敏摘要。
- 质询提示词会回到 Agent 对话（这是有意为之的反馈），但提示词本身只含缺失项与替代路径，不含敏感原文。
- 审查日志与工单存储（如启用）分离：日志只存摘要，原始请求存受控存储并标记敏感字段。

---

## 6. 用户审批与 `request_tool_approval`：降级为最后手段

v1 的工单制**不再作为默认审查方式**。具体处置：

- **从核心路径移除**：`AgentFirewall.EvaluateAuthorizationGateAsync` 中「人工授权未命中 → 查询 implicit approval 工单 → 放行」分支不再作为默认逻辑；默认走 Gate1→Gate2→Gate3 漏斗。
- **保留为可选通道**：`request_tool_approval`、`IToolApprovalService`、工单存储继续保留，但不在默认工具列表和默认拒绝消息中出现，用途仅为：
  - 用户希望显式记录某类操作的批准（审计用途）；
  - 系统反复质询同一 Agent/同一操作时，用户可显式批准以解除卡点；
  - 需要人工介入的灰色地带（如生产环境特殊操作）。
- **降级方式**：通过配置 `ToolReview.Enabled=false` 或保留 `RequestToolApproval.Enabled` 开关控制是否暴露给 Agent；默认不暴露在工具列表中（或标记为管理工具）。
- **提示语调整**：v1 的拒绝消息建议 Agent「调用 request_tool_approval」；v2 默认改为质询式反馈（缺什么、怎么做），不再引导 Agent 走工单。确定性硬拒绝也不得通过工单绕过。
- **用户 UI 触发条件**：仅 `HumanRequired` 创建可见审批；`Challenge` 只回给 Agent，`StaticDeny` 只给安全替代方案，L0/L1 完全无 UI。
- **字段权威性**：人工申请中的风险布尔字段从 Agent schema 移除，由服务端 `SystemRiskFacts` 填充。Agent 的 purpose/rollback 文本只能补充证据，不能改变 operation、目标和系统风险级别。
- **循环熔断**：相同 args/evidence 的第二次申请直接复用上次结果并指出“没有新证据”；第三次不再创建工单或调用 reviewer，交由现有 session fuse 记录 `approval_loop_detected`。

现有实现保留（不删除）：`Source/PuddingRuntime/Tools/Approval/RequestToolApprovalTool.cs`、`InMemoryToolApprovalService.cs`、`LlmToolApprovalReviewer.cs`、`ToolApprovalPromptBuilder.cs`、`ToolApprovalReviewParser.cs`，但标记为 legacy/optional。

---

## 7. 被否决的方案（用户 2026-08-20 指示）

### 7.1 Audit agent 实例 —— 明确否决

v1 曾计划引入 workspace audit agent（`Source/PuddingRuntime/Tools/Approval/WorkspaceAuditAgentProvider.cs` 适配器 + `IWorkspaceAuditAgentProvider` 抽象）。**否决原因（用户指示）**：

- 引入独立 agent 实例成本高：需要会话管理、生命周期、资源占用、故障处理，收益与成本不成比例。
- 审查任务本质是「一次二元判断」，不需要完整 agent 能力。
- 更简单的方案：系统直接执行**一次 LLM API 调用**（干净上下文、无工具），或干脆静态判死。

**处置**：不再新增/启用 audit agent 实例；`WorkspaceAuditAgentProvider` 保持现状但不进入 v2 核心路径，标记为废弃候选。

### 7.2 每命令拦截 —— 否决

v1 要求所有高风险命令提交工单。**否决原因（用户指示）**：不要每个命令都拦截 agent，只拦危险的命令；目标是对 Agent 无感（系统自动审查、透明），不让 Agent 耗费时间处理"票据"。

---

## 8. 实施清单

### 8.0 合并后的当前缺口

`ce63f8c06ad64069b7839056e46461ed` 的确定性命令防火墙已经实施；后续不再单独演进。统一任务 `e187a8bbd2d640bb87b96fd3cf548966` 必须完成：

1. 将 `e716829` rebase 到当前 `master`，把 `ToolApprovalCommandFirewall` 规则和测试迁入 `StaticDangerClassifier`，删除双重分类路径。
2. 修正 `save_memory` 等多 operation 工具的参数级分级；现有 `LowRiskWriteToolIds` 不能无条件把 `action=delete` 判为 L1。
3. 接通真实 `FactEvidenceBundle`；禁止以当前 `Evidence=null` 的分支宣称 Gate2 完成。
4. 把 `ToolReview` 用户配置接到 `<DataRoot>/config/system.json`，并注册可用的 `review` profile；Secret 仍只在 LLM provider 配置中。
5. 默认拒绝消息取消 `request_tool_approval` 引导，新增 `HumanRequired` 和重复审批熔断。
6. 完成外部部署、Core 重启和新会话产品 smoke；分支测试通过不是上线完成。

### 8.1 涉及代码位置

| 位置 | 现有状态 | 改造动作 |
|------|----------|----------|
| `Source/PuddingRuntime/Services/AgentFirewall.cs`（`EvaluateAuthorizationGateAsync`） | `master` 仍走工单链；`e716829` 已接 `IToolReviewService` 但未合入 | rebase 后统一为：既有 grant 未命中 → Gate1/2/3 漏斗；用户审批只由 `HumanRequired` 产生；YOLO/ModeGate 语义不变 |
| `Source/PuddingRuntime/Services/AgentFirewall.cs`（新增） | — | 新增静态危险分级器 `StaticDangerClassifier`（纯函数）与事实自检器 `FactSelfCheckEvaluator` 的调用点 |
| `Source/PuddingCore/Tools/PuddingToolContracts.cs`（`IPuddingToolExecutionService`） | 执行契约 | 不变（执行入口 `ToolInvocationService` 无需改动） |
| `Source/PuddingRuntime/Tools/Approval/InMemoryToolAuthorizationService.cs` | 人工授权 | 保留；不再作为「查工单」的触发源，仅服务 `/authorize` |
| `Source/PuddingCore/Runtime/RuntimeControlService.cs` | 运行时模式 | 不变；YOLO 模式继续跳过审查（用户显式放开） |
| `Source/PuddingRuntime/Services/Tools/ToolReview/` | `e716829` 已有首版 | 保留 `IToolReviewService`、`ToolReviewService`、`StaticDangerClassifier`、`FactSelfCheckEvaluator`、`ChallengePromptBuilder`、`ToolReviewLlmClient`；补 operation-aware risk、SystemRiskFacts、HumanRequired 和循环熔断 |
| `Source/PuddingRuntime/Tools/Approval/ToolApprovalCommandFirewall.cs` | `master` 已实现 | 规则与测试迁入 `StaticDangerClassifier` 后删除独立分支，保持唯一分类器 |
| `Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs` | descriptor 整体为 `Destructive` | descriptor 保留上界提示，最终风险由 `action` 参数细化；upsert/set_important 自动放行，delete 进入 L2 |
| 配置 | `e716829` 只绑定启动 IConfiguration | 程序默认 + `<DataRoot>/config/system.json` 覆盖；`llm_resource_pool` 注册 `review` profile |
| `Source/PuddingRuntime/Tools/Approval/*`（工单制） | 默认暴露且风险布尔由 Agent 填写 | 降级为可选人工介入通道；默认不暴露；风险事实由服务端派生 |

新审查服务接口建议：

```csharp
public interface IToolReviewService
{
    Task<ToolReviewOutcome> ReviewAsync(ToolReviewContext ctx, CancellationToken ct = default);
}

public sealed record ToolReviewContext
{
    public required string ToolId { get; init; }
    public required string ArgumentsJson { get; init; }
    public required string WorkspaceId { get; init; }
    public required string SessionId { get; init; }
    public required string AgentInstanceId { get; init; }
    public string? UserId { get; init; }
    public string? CommandName { get; init; }
    public string[] TargetPaths { get; init; } = [];
    // 调用方上下文摘要（Gate2 事实来源）：最近工具结果摘要、git 状态、备份/回滚证据、范围声明
    public FactEvidenceBundle? Evidence { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

public sealed record ToolReviewOutcome
{
    public required ToolReviewVerdict Verdict { get; init; } // Allow | StaticDeny | Challenge | HumanRequired
    public string? ChallengePrompt { get; init; }             // Challenge 时返回给 Agent 的脱敏、有界质询
    public string? Reason { get; init; }
    public string? Model { get; init; }
    public long LatencyMs { get; init; }
}

public enum ToolReviewVerdict { Allow, StaticDeny, Challenge, HumanRequired }
```

### 8.2 测试要点

核心单测：

- L0/L1 普通命令零介入：Gate1 直接 allow，**不触发**任何 LLM 调用、无额外审计噪音之外的痕迹。
- L2 危险命令进入 Gate2；事实充分（git 跟踪 + 备份证据 + 范围声明）直接 allow。
- 静态判死：`format`、`diskpart clean`、`DROP DATABASE`、`rm -rf` workspace 根、删除未跟踪无证据文件 → 直接 deny，**不调用 LLM**。
- 灰色地带走 LLM：干净上下文断言（无对话历史、无工具可用）；`allow`/`deny` 解析正确；`deny` 的质询提示词包含 reason + requiredEvidence。
- LLM 超时/异常/invalid JSON → fail-closed deny，并返回质询提示词。
- 安全底线不可逾越：LLM 返回 allow 但操作命中静态判死规则 → 仍然拒绝。
- 模型配置化：读 `ToolReview` 配置节；配置缺失 → 默认 Flash 级；`StaticDenyOnly=true` → 不调用 LLM。
- YOLO 模式：跳过审查（语义不变）。
- 人工 `/authorize` 命中时作为 Gate2 强证据：非硬拒 L2 不调用 reviewer；命中 immutable `StaticDeny` 时仍拒绝。
- capability policy 未暴露工具：即使审查 allow 也拒绝。
- `request_tool_approval` 不再出现在默认拒绝消息中（降级后仅作为可选通道）。
- `save_memory action=upsert/set_important` 在 Normal 模式下不产生审批 UI；`action=delete` 进入 L2，无法通过伪造 `may_damage_or_delete_data=false` 降级。
- 相同参数、相同证据的拒绝重复两次时 reviewer 调用次数仍为 1，且返回 `approval_loop_detected`。
- `ToolApprovalCommandFirewall` 与 `StaticDangerClassifier` 合并后，相同 shell 命令只有一次分类、一次审计结果。

集成测试：

- Agent 执行危险命令被拒，错误提示为质询式反馈（含缺失项与替代路径），而非"请提交工单"。
- 同一操作补齐事实（先备份/先读证据/缩小范围）后重试成功——验证"无感放行"路径。
- 审查事件全部出现在遥测日志；事件载荷不注入 Agent 对话，只有脱敏、有界的 `Challenge` 作为当前工具结果返回。
- 启用工单通道（可选）后 `request_tool_approval` 仍可工作，但默认工具列表不暴露。

---

## 9. 迁移与下线计划

本设计明确否决 audit agent 实例（见 §7.1），但工作区已存在 `default.audit-agent.001`（2026-08-20 作为审批链修复落地，`tap_allow_ebb0d21` 生效中）。为避免「文档否决、线上仍在跑」的空窗与审批链中断，按以下三阶段灰度迁移，每阶段回退路径一句话即可恢复。

### 阶段⓪：合并代码与安全门禁

- 动作：以当前 `master` 为基线 rebase `e716829`；统一两个分类器；补 operation-aware 风险、真实 Gate2 证据、system.json 配置、HumanRequired 和循环熔断；跑完整定向回归。
- 回退路径：不合入不部署，当前 `master` 行为保持不变。

### 阶段①：部署 v2 但 Enabled=false（零间隙）

- 动作：完成 §8 实施清单的代码/配置改造并部署，但 `ToolReview.Enabled=false`；现有审批链（audit-agent + 工单）继续运行，审查行为与现状完全一致，零间隙。
- 回退路径：v2 未启用即现状，无需任何回退动作。

### 阶段②：启用 ToolReview 并观察

- 动作：先以 `ToolReview.Enabled=true + StaticDenyOnly=true` 启用确定性漏斗，确认普通 upsert/git/read 零审批、硬危险稳定拒绝；注册 `review` profile 后再设 `StaticDenyOnly=false` 打开灰区单次 LLM。观察 Gate3 质询率、人工弹窗率、审批等待时间和误拒率，指标稳定后进入阶段③。
- 回退路径：`ToolReview.Enabled=false` 即回退到阶段①状态，审批链随时可用。

### 阶段③：用户审批降权与旧工单默认退场

- 动作：仅 `HumanRequired` 显示用户审批；下线 `default.audit-agent.001` 实例；`request_tool_approval` 从默认工具列表移除（仅保留可选人工介入通道配置，见 §6）。
- 回退路径：audit agent 部署配置与 `request_tool_approval` 通道保留不删，需要时按原配置恢复即可。

### 阶段④：产品验收

- 动作：外部控制器重启到明确的新构建；在新会话实测 `save_memory upsert`、普通 git/read/build、灰区命令、硬危险命令、一次 HumanRequired 和重复拒绝熔断，核对遥测与用户审批次数。
- 完成标准：普通任务不再因审批失败卡住；同一拒绝不再形成工具循环；安全底线和 workspace/sandbox/capability gate 无回归。

---

## 10. 后续扩展

- **危险特征库配置化**：静态危险规则（命令/标志/路径特征）从配置或 JSON 规则文件加载，支持按 workspace 定制。
- **Agent 行为画像**：对同一 Agent 的质询频率做统计，高频犯蠢可提示降级其能力策略或要求人工介入。
- **质询反馈闭环**：记录 Agent 收到质询后的修正行为（是否补证据、是否缩小范围），用于评估漏斗效果。
- **文件资源 allowlist DSL**：针对文件操作设计细粒度路径 allowlist，减少 Gate3 的 LLM 依赖。
- **Admin UI**：展示审查事件流、质询率、LLM 命中率，支持人工撤销或显式批准。
- **审查结果缓存**：同 `(tool, args_hash, evidence_hash)` 的结果短时缓存，减少重复 LLM 调用。
