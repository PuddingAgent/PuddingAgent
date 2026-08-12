# 业界 Agent Chat 界面最佳实践调研与技术方案

> 调研日期: 2026-08-12 | 委托: 子代理 (workspace-task-agent)
> 目标: 对标 Harness.io AI Assistant / Cursor / Claude Code+Desktop / VS Code Copilot Chat / OpenAI Codex CLI，输出可直接指导 PuddingPlatformAdmin 实现的方案。
> 说明: 本工作区实际前端为 **React (umi) + Ant Design**（`Source/PuddingPlatformAdmin/src` 为 .tsx/.ts），非 Vue3+Element Plus；SSE 流式、虚拟滚动、上下文指示器、过程摘要等已实现。方案以 React/AntD 现状为基线给出，若确另有 Vue3 版前端，模式可平移（`@tanstack/vue-virtual`、`vueuse` 等）。

---

## SUMMARY

业界 5 个标杆（Harness AI、Cursor、Claude Code/Desktop、Copilot Chat、Codex CLI）的 Agent Chat UI 已收敛出高度一致的范式：

1. **工具调用 = 可折叠卡片**：默认一行摘要（工具名+状态+参数摘要），展开后分「输入(参数) / 输出(结果)」两区，失败用红/橙状态徽章。Claude Desktop 提供 Normal/Verbose/Summary 三档转录视图；Claude CLI `Ctrl+O` 展开全部工具明细。
2. **审批 = 内联卡片 + 分级策略，不做模态弹窗打断**：read 免批、工作区内文件写免批、敏感/配置文件需批、shell 命令需批、网络走白名单；一次审批可带「仅本次 / 总是允许 / 拒绝」三选（Claude Desktop Allow once/Always allow/Deny；Cursor approval prompt；Codex approval policy）。
3. **思维链 = 折叠块（collapsed reasoning）**：Claude extended thinking 是"点击展开的折叠块"；VS Code Focus view 每 turn 只留一行可展开行；Harness 错误分析给出「变化影响关联」面板而非逐 token 思维链。**业界趋势是不把 CoT 当聊天正文，而折叠成附属视图。**
4. **进度追踪 = 任务清单 + 检查点时间线**：Claude 有 task checklist（Ctrl+T）；Cursor/VS Code 有 checkpoint（快照、restore/redo/fork）与 queued message（排队/让位/打断三态）。
5. **流式 = SSE 单向推送 + REST 反向控制**：ChatGPT/Claude/Copilot 均以 SSE/fetch-stream 为主；Codex 内部用 WebSocket/JSONL 解耦 TUI 与引擎（`app-server --listen ws://`），但聊天场景无必要。Pudding 已用 SSE+Last-Event-ID+sequenceNum，方向正确。
6. **上下文窗口可视化 = prompt box 内进度条/环形指示器**（Claude VS Code context indicator、Desktop usage ring、Cursor context bar、Pudding ContextMemoryIndicator 已具备）。
7. **性能 = 虚拟滚动（Pudding 已用 @tanstack/react-virtual）+ 稳定 Markdown 边界增量提交（Pudding 已实现）+ 内容指纹跳过重渲染（Pudding 已实现）+ 高度提示分级（compact/normal/rich/streaming，Pudding 已实现）。**

最大差距（Pudding 缺失）：**聊天内审批流**（当前仅有后端 allowlist/audit 页面与 processPreview 里的 `approvalMissing` 失败归类，无 `approval.requested` SSE 事件与内联审批 UI）、**plan 模式**（生成计划→审阅→批准执行）、**checkpoint/队列**（消息排队/steer）、**transcript 视图分级**、**Focus view 折叠**。

---

## FINDINGS — 产品逐个拆解

### 1. Harness.io AI Assistant（AIDA / DevOps Agent）

- **形态**：不是独立聊天 App，而是**嵌在 Harness 平台 UI 内的聊天面板**，与 pipeline/服务/环境/GitOps 等管理界面同屏联动（in-harness-ui）。DevOps Agent 仅在 Harness UI 可用（无外部安装）。来源：developer.harness.io/docs/platform/harness-ai/core-capabilities/in-harness-ui/devops-agent。
- **生成式交互**：自然语言 → 生成完整 pipeline/step/stage YAML → **YAML Preview 面板**（可审查）→ 点 **Create/Accept** 落库。即"先生成后整体确认"，不做逐 token 审批。
- **错误分析（Error Analyzer）**：失败流水线上点 "Analyze Error" → Change Impact Correlation 面板：最近变更(时间戳+作者)、外部依赖状态、历史相似失败(相似度评分)、**优先级化建议(Priority/Action/Justification)**、影响面与风险等级。再点 "Help me fix the pipeline yaml" → **Pipeline Fix Summary（问题/方案/修改前后 YAML 对比）** → Accept 应用。来源同上。
- **审批流**：平台本身有原生 pipeline approval step；AI 生成含审批门禁的 pipeline；RBAC 控制谁能用 AI 与创建资源。AI 创建 secret 时**只生成对象结构，值由用户在 UI 输入**（安全边界）。
- **个性化**：Harness AI Rules（account/org/project/user 级自然语言规则）+ AI Memories（从聊天交互捕获的用户级上下文）——可视为"规则系统 + 记忆库"的前端呈现。
- **对外集成**：Harness MCP Server（11 tools / 139 resource types），供 Cursor/Windsurf/Claude Desktop/VS Code 连接——说明其 Chat UI 与工具执行层解耦、可被第三方 UI 复用。
- **模型**：Claude Opus 4.6（AWS Bedrock / Google Vertex 托管）。
- **启示**：领域对象级预览（YAML/拓扑图）+ 后置整体审批；错误分析做「关联面板+修复建议+前后对比」，而非让用户看原始日志。

### 2. Cursor

- **形态**：Chat 侧边栏（Cmd+I）+ **Agents Window**（agent-first 全屏界面，多 workspace、并行 agent、独立 diffs view、worktrees、本地↔云端切换）。来源：cursor.com/docs/agent/agents-window、cursor.com/docs/agent/tools。
- **Plan Mode**：Shift+Tab 循环进入；agent 先提问澄清→研究代码库→生成**可编辑 plan（Markdown 或聊天内）**→用户审阅/修改→"Click to build"。计划默认存 home 目录，可 "Save to workspace"。来源：cursor.com/docs/agent/plan-mode。
- **工具调用展示**：chat timeline 按请求分组；**Checkpoints**（自动快照所有被改文件，点击时间线 checkpoint 可预览并还原，hover 消息出现 Restore Checkpoint 按钮）。来源：cursor.com/docs/agent/tools。
- **消息队列**：agent 工作中 Enter 即排队（可拖拽重排，显示在活跃任务下方）；Cmd+Enter 立即发送打断（immediate messaging）。来源：cursor.com/docs/agent/tools。
- **审批（Run Modes）**：默认 **Auto-review**：allowlist 调用直接跑；可 sandbox 的 shell 命令进沙箱跑；其余交给 classifier（Claude 4.5 Haiku / GPT-5.4 Mini）；classifier 拒绝后 agent 换路径，若仍坚持则**弹审批**。另有 Allowlist（确定性白名单）与 Run Everything。来源：cursor.com/docs/agent/security/run-modes。
- **保护边界**：工作区文件读写免批、**配置文件（如 .vscode 设置）需批准**、终端命令默认需批准、网络仅白名单（GitHub、web search、direct link retrieval）、MCP 连接需批准+每个 MCP 工具单独批准+`permissions.json` 的 `mcpAllowlist`/`terminalAllowlist`/`autoRun`(自然语言指令)。来源：cursor.com/docs/agent/security、cursor.com/docs/reference/permissions。
- **沙箱**：macOS Seatbelt / Linux Landlock(+bubblewrap)；注入 `CURSOR_SANDBOX`/`CURSOR_ORIG_UID` 等 env；`.git/config`、`.vscode`、`.cursorignore` 等保护路径只读。
- **启示**：审批分级 + 沙箱 + classifier 三件套；plan 模式先行；checkpoint 时间线；消息队列。

### 3. Claude Code / Claude Desktop

- **权限模式分级**（核心范式）：`default(Manual) → acceptEdits → plan → auto → dontAsk → bypassPermissions`。CLI 用 Shift+Tab 循环，状态栏显示 `⏸ manual mode on / ⏵⏵ accept edits on / ⏵⏵ auto mode on`；VS Code/Desktop 在 prompt box 底部有模式选择器。来源：code.claude.com/docs/en/permission-modes。
  - `default`：只读免批，写/命令/网络都问；
  - `acceptEdits`：工作区内文件写+常见 fs 命令免批；
  - `plan`：只研究不改写，产出计划，批准后才编辑；
  - `auto`：全部动作由**独立 classifier 模型**审查，blocked 规则（curl|bash、敏感外发、生产部署、force push、terraform destroy 等 30+ 类），连续 block 3 次或累计 20 次回退为手动审批；`/permissions` 的 Recently denied 可 r 重试；
  - `dontAsk`：仅预批准工具；
  - `bypassPermissions`：全免（仅限隔离容器）。
- **工具调用卡片**：默认折叠为摘要（如 "Called slack 3 times"），`Ctrl+O` 打开 transcript viewer 看时间戳+模型+每次 MCP 调用明细。Desktop 提供 **Normal / Verbose / Summary 三档转录视图**（Ctrl+O 循环）。来源：code.claude.com/docs/en/interactive-mode、code.claude.com/docs/en/desktop。
- **审批 UI**：内联 permission dialog（方向键切换 tab）；Desktop 权限卡片 **Allow once / Always allow / Deny**（站点级持久化可撤销）。来源：code.claude.com/docs/en/desktop。
- **Plan 审阅**：计划完成后内联选项：**Yes, and use auto mode / Yes, manually approve edits / No, keep planning**；`Ctrl+G` 在默认编辑器打开 plan 直接改。来源：code.claude.com/docs/en/permission-modes。
- **思维链**：extended thinking 以**折叠块**出现在会话中，点击展开，`Ctrl+O` 全展开/全收起。来源：code.claude.com/docs/en/vs-code。
- **Focus view（VS Code）**：每 turn 折叠成一行（隐藏工具调用/结果/thinking），工作期间显示当前运行工具，Ctrl+Alt+F 切换。来源：code.claude.com/docs/en/vs-code。
- **上下文指示器**：prompt box 显示上下文占用；Desktop 有 usage ring（会话上下文+计划用量）。来源：code.claude.com/docs/en/vs-code、desktop。
- **子代理安全**：spawn 时审查委托任务描述、运行中逐动作审查、结束时审查完整动作历史。来源：code.claude.com/docs/en/permission-modes（Accordion: How auto mode handles subagents）。
- **任务清单**：`Ctrl+T` to-do checklist；`/tasks` 看运行中 shells/subagents。来源：code.claude.com/docs/en/interactive-mode。
- **启示**：权限模式分级是行业标准答案；内联审批优于弹窗；三档转录视图；thinking 折叠块；Focus view。

### 4. Copilot Chat（VS Code）

- **形态**：**Chat view**（code-first 侧栏，绑定当前 workspace）+ **Agents Window**（agent-first，跨项目编排）+ Inline Chat（Ctrl+I）+ Quick Chat。会话级配置：agent harness（Copilot/Claude/Codex/local）、agent role、**permission level**、模型。来源：code.visualstudio.com/docs/copilot/copilot-chat、docs/agents/overview。
- **消息队列三态**（Send 按钮变 dropdown）：**Add to Queue**（等当前回复完成）/ **Steer with Message**（当前工具执行完即让位，新消息立即处理——用于纠正方向）/ **Stop and Send**（取消当前请求直接发新消息）；pending 消息可拖拽排序。默认 `chat.requestQueuing.defaultAction=steer`。来源：code.visualstudio.com/docs/copilot/copilot-chat。
- **审批**：会话级 permission level；**敏感文件审批** `chat.tools.edits.autoApprove`（glob 模式，如 `**/.vscode/*.json:false`、`**/.env:false` → 弹出 diff 审批）；extension-host 模式下编辑先落盘为 **pending edits**（Explorer/标签页有小圆点标记），在**编辑器内嵌覆盖控件**逐条 Keep/Undo 或跳转上一条/下一条，可 hover 单条接受/拒绝；`chat.editing.autoAcceptDelay` 自动接受。来源：code.visualstudio.com/docs/agents/run/review-code-edits。
- **Checkpoints**：每请求前快照；hover 请求 → **Restore Checkpoint / Fork Conversation / Redo**；`chat.checkpoints.showFileChanges` 显示每次请求的变更文件与 ±行数。来源同上。
- **工具调用可视化**：Agent Logs（chronological event log：工具调用、LLM 请求、prompt 文件发现）+ Chat Debug view（raw system prompt、user prompt、context、tool payload）——面向调试者，非默认界面。来源：code.visualstudio.com/docs/copilot/copilot-chat。
- **通知与时间戳**：`chat.notifyWindowOnResponseReceived` / `chat.notifyWindowOnConfirmation`（off/windowNotFocused/always）；`chat.verbose` 显示请求/完成时间戳。来源同上。
- **上下文**：隐式（活动文件+选区）+ `#`-mentions（file/folder/symbol/codebase/terminalSelection）+ 图片/浏览器元素。
- **启示**：三态消息队列（queue/steer/stop）是打断交互的教科书实现；glob 式敏感文件审批；checkpoint 可 fork 会话；通知策略。

### 5. OpenAI Codex CLI

- **形态**：终端 TUI（交互式）+ `codex exec`（非交互，`--json` 输出 NDJSON 事件流，每状态变化一行）。**UI 与引擎解耦**：`codex app-server --listen ws://IP:PORT`（或 unix://），TUI 通过 `--remote ws://...` 连接——即 **WebSocket/JSONL 作为 UI↔引擎协议**；Desktop app 同引擎。来源：learn.chatgpt.com/docs/developer-commands。
- **权限组合矩阵**（sandbox × approval policy 两轴）：sandbox ∈ {read-only, workspace-write, danger-full-access}；approval ∈ {on-request, never, untrusted}。默认 **Auto 预设 = workspace-write + on-request**（读/写/跑命令免批，工作区外写与网络需批）；`--ask-for-approval untrusted` = 自动改文件但跑不可信命令前问。来源：learn.chatgpt.com/docs/agent-approvals-security。
- **启动引导**：检测 git 仓库——有版本控制→推荐 Auto；无→read-only；可用 `/permissions` 查看/切换，`/status` 看 workspace 目录。来源同上。
- **保护路径**：writable root 下 `.git`、`.agents`、`.codex` 强制只读（含 gitdir 指针解析）。
- **Auto-review（approvals_reviewer=auto_review）**：guardian policy（data exfiltration / credential probing / persistent security weakening / destructive）；风险分级 low/medium 可放行、high 需用户授权、critical 拒绝；解析失败 fail-closed。来源同上（openai/codex 仓库 policy.md）。
- **网络**：默认无网络；`network_proxy` 域名白名单（allowlist-first、deny 优先、DNS rebinding 防护、默认禁 loopback/私网）；web_search 默认 cached（防 prompt injection）。来源同上。
- **启示**：sandbox×approval 两轴矩阵、非交互 NDJSON 事件流（可被 web 前端直接消费）、auto-review 分级、启动按仓库风险自适应默认权限。

---

## FINDINGS — 七个重点主题横向对比

### A. 工具调用 UI 展示
| 产品 | 展示方式 |
|---|---|
| Claude | 折叠摘要行 → Ctrl+O/Verbose 展开；Desktop 三档视图 |
| Cursor | 时间线内卡片 + checkpoint 还原 |
| Copilot | 默认隐藏于回复，Agent Logs/Debug view 才见明细；Focus view 折叠 |
| Codex | TUI 内命令/输出流式呈现；exec --json 事件流 |
| Harness | YAML Preview 面板 + 前后对比 |
**共识**：卡片化、默认折叠、状态徽章（running/ok/failed）、输入输出分栏、展开成本低（无需离开会话）。

### B. 审批交互
| 产品 | 机制 |
|---|---|
| Claude | 内联 dialog；模式分级；Allow once/Always/Deny |
| Cursor | 内联 approval prompt + Run Modes + classifier |
| Copilot | 会话 permission level + 敏感文件 glob 审批 + pending edit 内联 Keep/Undo |
| Codex | sandbox×approval 矩阵；approval policy on-request |
| Harness | 生成后整体 Accept（YAML 预览）；RBAC |
**共识**：**内联 > 弹窗 > 独立面板**；必须提供「总是允许/仅本次/拒绝」；分级免批（读、工作区写、白名单命令）。

### C. 思维链/推理可视化
**共识**：CoT 不进入正文；**折叠块**（Claude thinking blocks）、**Focus view 单行**（Copilot/Claude VS Code）、**关联面板**（Harness Change Impact）。实时流式时只显示「正在思考…」占位或强度指示（Pudding 已有 ThinkingIntensityIndicator）。

### D. 执行进度与步骤追踪
**共识**：任务清单（Claude Ctrl+T）、turn 级状态机（thinking/tool_executing/streaming/completed——Pudding 已有同款 ChatStatus）、checkpoint 时间线（Cursor/Copilot）、子代理 dock（Pudding 已有 SubAgentActivityDock）、CI 状态条（Claude Desktop PR）。

### E. 实时流式技术方案
- 聊天主流 = **SSE/fetch-stream**（ChatGPT、Claude、Copilot 全用）；Pudding 已用 fetch reader + Last-Event-ID + sequenceNum + generation，**正确无需更换**。
- Codex 内部用 **WebSocket/JSONL** 做 TUI↔app-server 解耦——当需要「一个引擎服务多 UI（web/desktop/cli）」或双向控制时才引入 WS；聊天双向控制（审批/取消/steer）用**独立 REST 端点 + 事件总线**即可。
- 轮询仅用于回放兜底（Pudding `resolveSessionReplayPollInterval` 已有）。
- 强化点：SSE 心跳/注释保活、指数退避重连（Pudding 已有 reconnectCount）、事件游标一致性校验（已有 cursor/sequenceNum）、断线后 replay missed events（已有 `replayMissedSessionEvents`）。

### F. 上下文窗口可视化
**共识**：prompt box 旁进度条/环形 + 百分比 + 用量 K 值 + 接近饱和警告 + compaction 提示。Claude Desktop 用 usage ring；Pudding ContextMemoryIndicator 已实现环形+渐变+弹层+警告（`context.health` 事件带 usedTokens/effectiveWindowTokens/usageRatio）。**差距**：可加分段（system/prompt/tools/history）与 compaction 状态条（Pudding 已有 compaction 事件但 UI 未展示完整状态流）。

### G. 消息渲染性能优化
**共识**：虚拟滚动（Pudding 已用 @tanstack/react-virtual + 高度提示 + 内容指纹跳过重渲染，**与业界一致**）；流式时稳定 Markdown 边界增量提交（Pudding useTypewriterStreaming 已实现稳定边界 + chunked text 分组，**领先**）；代码块懒渲染/语法高亮按需；大文本折叠；骨架屏（Pudding MessageList Skeleton 已有）。

---

## RECOMMENDATIONS — Pudding 落地技术方案（按优先级）

### P0（当前架构内小步快跑，1-2 天）
1. **审批事件与内联审批卡**：
   - 后端 SSE 新增 `{type:'approval.requested', requestId, tool, arguments, rationale, scope}`、`{type:'approval.resolved', requestId, decision}`；REST `POST /api/approvals/{id}/decide`（allow-once/allow-always/deny）。（当前 api.ts:2472-2545 事件联合体无此类型）
   - 前端 `AgentMessageBubble`/`MessageProcessSummary` 内新增 `ApprovalCard`：内联三按钮 + 状态徽章；`allow-always` 写入本地 allowlist（对齐后端 tool-approval/allowlist 页：`src/pages/tool-approval/allowlist/index.tsx`）。
   - processPreview.ts 的 `ProcessFailureBreakdown.approvalMissing`（processPreview.ts:56-66）已有归类，接上事件即可闭环。
2. **transcript 视图分级（Normal/Verbose/Summary）**：复用现有 `processItems`/`TimelineItem`，在 ChatMain 加视图切换；Summary 只显示最终回复，Verbose 全展开（对齐 Claude Desktop）。
3. **上下文指示器增强**：`context.health` 已有用量/窗口/比率（api.ts:2508），把 ContextMemoryIndicator 从全局状态栏下沉到 **prompt box 内**（对齐 Claude/Cursor），并加 compaction 状态条（`context.compaction.started/completed/failed` 事件已存在，api.ts:2509-2513）。

### P1（1-2 周）
4. **权限模式分级**：Composer 增加模式选择器（Manual/acceptEdits/plan/auto/ask-everything），映射现有 backend permission 配置；模式显示在 prompt box 底部（对齐 Claude），SSE 状态事件带当前模式。
5. **Plan 模式**：`plan` 模式下 agent 输出 `{type:'plan.proposal'}` 事件 → 前端渲染可编辑计划卡（Markdown 大纲 + 文件变更列表）→ 批准按钮（approve-and-build / approve-manual / keep-planning）。
6. **三态消息队列**：现有 `useMessageInteractionQueue`（hooks/useMessageInteractionQueue.ts）已有队列/steer 雏形——补上「排队/让位/取消」三态 dropdown（对齐 Copilot Send dropdown）与 pending 消息拖拽排序。
7. **Checkpoint 时间线**：turn 前快照（后端已有 run 概念），消息 hover 显示 Restore Checkpoint / Fork（对齐 Cursor/Copilot）。
8. **Focus view**：每 turn 折叠成一行（隐藏工具/thinking），运行中显示当前工具（对齐 Claude VS Code）。

### P2（1 月内）
9. **Auto-review 分级审批**：后端 classifier（可用现有低配模型）对高风险动作预审，blocked 3 连/20 次回退手动（对齐 Claude/Cursor/Codex）；前端 Recently denied 面板可重试。
10. **Sandbox 边界可视化**：`/status` 等价物——聊天面板显示 workspace 根、保护路径、网络模式（对齐 Codex/Cursor），`CURSOR_SANDBOX` 类 env 注入。
11. **WebSocket 解耦（可选）**：若需多 UI 共用一个引擎（web/desktop/CLI），按 Codex app-server 模式引入 WS/JSONL 协议层；纯 Web 场景维持 SSE+REST。

### 不做（明确非目标）
- 不把 CoT 流式刷进正文（保持折叠块）。
- 不引入轮询做主通道（仅回放兜底）。
- 不迁移状态管理库（沿用现有组合模式）。

---

## EVIDENCE

### 外部来源（调研抓取，均为官方文档）
- Claude Code permission modes: https://code.claude.com/docs/en/permission-modes （模式表、Shift+Tab 循环、auto classifier 规则清单、plan 审阅选项）
- Claude Code interactive mode: https://code.claude.com/docs/en/interactive-mode （Ctrl+O transcript viewer、Ctrl+T task checklist、tool 摘要折叠）
- Claude Code VS Code: https://code.claude.com/docs/en/vs-code （Focus view、上下文指示器、thinking 折叠块、permission mode indicator、tab 状态点）
- Claude Desktop: https://code.claude.com/docs/en/desktop （Normal/Verbose/Summary 三档视图、Allow once/Always allow/Deny、diff stats 指示器、usage ring、会话侧栏）
- Codex CLI: https://learn.chatgpt.com/docs/developer-commands （TUI、app-server ws://、exec --json NDJSON）
- Codex approvals & security: https://learn.chatgpt.com/docs/agent-approvals-security （sandbox×approval 矩阵、Auto 预设、保护路径、auto-review、network_proxy）
- Cursor Agents window: https://cursor.com/docs/agent/agents-window （并行 agent、diffs view、worktrees）
- Cursor tools/checkpoints/queue: https://cursor.com/docs/agent/tools （checkpoint 快照还原、排队消息、Cmd+Enter 打断）
- Cursor plan mode: https://cursor.com/docs/agent/plan-mode （Shift+Tab、可编辑 plan、Click to build）
- Cursor agent security: https://cursor.com/docs/agent/security （工作区写免批、配置文件需批、终端默认需批、网络白名单、MCP 逐工具批准）
- Cursor run modes: https://cursor.com/docs/agent/security/run-modes （Auto-review/Allowlist/Run Everything、classifier、sandbox、保护路径）
- Cursor permissions.json: https://cursor.com/docs/reference/permissions （mcpAllowlist/terminalAllowlist/autoRun 自然语言指令）
- VS Code Copilot chat: https://code.visualstudio.com/docs/copilot/copilot-chat （三态 Send dropdown、pending 重排、#-mentions、checkpoints、通知、时间戳、Agent Logs/Debug view）
- VS Code agents overview: https://code.visualstudio.com/docs/agents/overview （Chat view vs Agents window、agent harness、permission level）
- VS Code review code edits: https://code.visualstudio.com/docs/agents/run/review-code-edits （pending edits 内联 Keep/Undo、Restore Checkpoint/Fork/Redo、sensitive-file glob 审批、autoAcceptDelay）
- Harness AIDA overview: https://developer.harness.io/docs/platform/harness-aida/aida-overview （AI 功能矩阵、MCP Server、AI Rules/Memories）
- Harness DevOps Agent: https://developer.harness.io/docs/platform/harness-ai/core-capabilities/in-harness-ui/devops-agent （YAML Preview→Create/Accept、Error Analyzer、Change Impact Correlation、Fix Summary、GitOps 聊天操作、secret 值人工输入）
- Harness AI chat guide: https://developer.harness.io/docs/platform/harness-aida/harness-ai-chat-guide （自然语言生成 pipeline 的 prompt 模式）

### 本仓库现状（path:line）
- SSE 事件联合体（缺 approval 类型）: `Source/PuddingPlatformAdmin/src/services/platform/api.ts:2472-2545`
- SSE 订阅（fetch reader + Last-Event-ID + sequenceNum + generation）: `api.ts:2085-2130`
- SSE 生命周期/重连/回放: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useSessionEventConnection.ts:53-120`
- Typewriter 流式（稳定 Markdown 边界 + chunk 分组）: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useTypewriterStreaming.ts:60-150`
- 虚拟滚动（@tanstack/react-virtual + 高度提示 + 指纹）: `Source/PuddingPlatformAdmin/src/pages/chat/viewport/useMessageViewportRuntime.ts:1-80`、`viewport/messageProjection.ts:33-40`
- 消息列表/加载骨架: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageList.tsx:1-30`
- 过程摘要（thinking 分组 + 工具项，默认折叠）: `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageProcessSummary.tsx:1-60`
- 过程指标/失败归类（含 approvalMissing）: `Source/PuddingPlatformAdmin/src/pages/chat/components/processPreview.ts:56-66`
- 上下文环形指示器: `Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMemoryIndicator.tsx:1-211`
- 子代理活动 dock: `Source/PuddingPlatformAdmin/src/pages/chat/components/SubAgentActivityDock.tsx:1-90`
- 会话消息类型（ProcessSummaryItem/ConversationMessageView）: `Source/PuddingPlatformAdmin/src/pages/chat/client/types.ts:1-115`
- 消息发送/交互队列（queue/steer 雏形）: `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useMessageInteractionQueue.ts`、`hooks/useMessageSend.ts`
- Composer 状态机（thinking/tool_executing/streaming/completed）: `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx:35-60`
- 后端审批配置页（allowlist/audit，无聊天内审批）: `Source/PuddingPlatformAdmin/src/pages/tool-approval/allowlist/index.tsx`、`pages/tool-approval/audit/index.tsx`

---

## RISKS

1. **技术栈认知偏差**：委托上下文称前端为 Vue3+Element Plus，实际 `PuddingPlatformAdmin` 是 **React(umi)+Ant Design**（src 下 .tsx、dist 为 umi 构建）。若存在另一 Vue3 前端，本方案需组件等价替换（如 AntD Collapse→ElCollapse、@tanstack/react-virtual→@tanstack/vue-virtual、antd-style→Element Plus 变量）；若不存在，建议以本方案为准并纠正任务书描述。
2. **审批流引入会改变后端运行语义**：当前审批失败只是 `approvalMissing` 失败归类（processPreview.ts:56-66），要新增 `approval.requested` 事件与 decide 端点，涉及 Runtime 层阻塞/恢复机制，属契约变更，需与主线程 agent 的 tool-call 等待模型对齐，防止审批卡死 run。
3. **消息队列三态可能引入并发复杂度**：Steer 语义（当前工具完成后让位）与现有 `useMessageInteractionQueue` 的已实现逻辑需仔细合并，避免与 SSE 事件投影（useSessionEventProjection）竞态。
4. **Plan 模式对模型依赖高**：生成可编辑计划需要稳定结构化输出；低配模型（DeepSeek 优先原则）可能产出不可执行计划，需 schema 校验与降级（回退普通回复）。
5. **性能回归风险**：Focus view/视图分级增加渲染分支，需在 MessageRow.memo 与 content fingerprint（useMessageViewportRuntime.ts:40-60）上补充用例，防止折叠展开引发整列重渲染。
6. **外部文档时效性**：调研基于 2026-08-12 抓取的官方文档快照；产品迭代快（如 Claude auto mode 默认化 2026-08-14 生效），实现前应复核最新文档。
7. **审批 UI 的误点安全**：Always allow 需二次确认 + 可撤销清单（对齐 Claude Desktop 站点级撤销、Cursor permissions.json 覆盖关系）。

---

## BLOCKERS

1. 无浏览器/桌面认证环境：本次调研以官方文档抓取为准（浏览器 bridge 断开），未能录制真实产品截图/交互视频；如需像素级对标需人工在 Cursor/Claude Desktop 实测。
2. 审批事件契约（`approval.requested`）与 decide 端点需要主 PuddingAgent 后端协作确认，本调研未改动任何源码。
3. 若确实存在 Vue3+Element Plus 版前端（与本仓库 React 版并存），需先确认以哪个为落地目标，再翻译本方案组件映射。

---

## 附：五产品一句话要点

- **Harness**：平台内嵌 Chat + 领域对象(YAML)预览 + 生成后整体审批 + 错误关联分析面板。
- **Cursor**：Plan Mode 先行 + checkpoint 时间线 + 消息队列(排队/打断) + Run Modes 审批分级 + 沙箱。
- **Claude**：权限模式五级(Manual/acceptEdits/plan/auto/bypass) + 内联审批 + 三档转录视图 + thinking 折叠块 + Focus view。
- **Copilot**：三态 Send(排队/让位/取消) + checkpoint(restore/redo/fork) + 敏感文件 glob 审批 + pending edit 内联 Keep/Undo。
- **Codex**：sandbox×approval 两轴矩阵 + exec --json NDJSON 事件流 + app-server(WebSocket) UI/引擎解耦 + auto-review 分级。
