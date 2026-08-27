# Chat 独立“⚡ 插嘴”按钮与当前 Turn 即时 Steering 设计方案

> 日期：2026-08-26
> 状态：Proposed，仅完成设计，尚未修改或部署产品代码
> 任务看板：`ed88185f1d3b4e16a70e9b9ea0f0e040`（P1 / Backlog）
> 上位设计：`Docs/superpowers/specs/2026-06-06-runtime-steering-queue-design.md`、`Docs/07架构/60ADR-059Conversation执行内核与可靠命令链路ADR.md`

## 1. 用户问题与目标

当前 Chat Composer 在 Agent 运行中把普通 `Enter`/发送操作解释为“排队”，并把主发送按钮切换成“停止生成”。虽然现有实现已支持 `Ctrl/Cmd+Enter` 直接 Steering，也允许把本地待发项转换为 Steering，但这两个入口都不够显眼：用户必须记住快捷键，或先让消息进入待发队列后再点击队列项的闪电动作。

本方案增加一个独立的 **“⚡ 插嘴”** 按钮。用户在 Agent 正在执行时输入文本并点击它，消息直接投递到当前正在运行的 Turn，**不进入普通待发消息队列、不创建第二个 Turn，也不等待当前回复完整结束后再作为下一条消息发送**。

“直接”指绕过普通消息队列并绑定当前 Turn，不代表强制取消正在执行的工具或已经发出的模型请求。Steering 在当前步骤结束后的下一个安全 LLM 边界注入；现有 late-safe-boundary 仍负责处理接近终态时已被受理的 Steering。

## 2. 当前事实与设计增量

| 层级 | 当前事实 | 本方案增量 |
|---|---|---|
| Runtime | 已有 `SessionSteeringService`、`session_steering_messages` 和逐 LLM 边界 drain | 不新增第二套 Runtime 队列 |
| HTTP | 已有 `POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering`，仅接受 `Running` Turn | 独立按钮复用该唯一入口 |
| Hook | `useMessageInteractionQueue.submitSteeringInteraction` 已能解析 active Turn、提交 Steering、处理 409，并供快捷键和本地队列转换使用 | 增加 Composer 专用包装、可用性状态、草稿安全和单次提交锁 |
| 状态上抛 | `useChatState` 当前没有解构或返回 `submitSteeringInteraction` | 将 Composer Steering 能力沿既有页面属性链显式上抛 |
| Composer | `IntentConsole` 运行中只显示停止按钮；提示词告知 `Ctrl/Cmd+Enter`，没有独立闪电按钮 | 在停止/发送按钮左侧增加 34×34 的 `⚡` 直达按钮 |
| 队列 UI | `MessageQueueDropdown` 展示普通待发、后端投递及队列项转换后的 Steering | Composer 直达 Steering 不计入普通待发数量，不进入该面板 |

当前工作区已有未提交的 Steering 代码，不等于当前浏览器已加载新构建，也不等于产品验收完成。本任务实施时必须先保护这些已有改动，不能覆盖或重写无关文件。

## 3. 冻结的交互规则

### 3.1 Composer 布局

空闲状态保持现状，不增加常驻噪音：

```text
[自动 ▾] [权限 ▾] [设置] [麦克风] [发送]
```

存在可 Steering 的 active Turn 时：

```text
[自动 ▾] [权限 ▾] [设置] [麦克风] [⚡] [停止]
                                      ↑
                           插嘴当前 Agent
```

- 按钮位于主发送/停止按钮左侧，使用 `⚡` 字符作为可见内容，不使用难以理解的抽象图标。
- 尺寸与现有 Composer 工具按钮一致（34×34），不增高 Composer。
- Tooltip：`插嘴当前 Agent：在当前步骤结束后的下一次模型请求前生效`。
- `aria-label="插嘴当前 Agent"`，支持键盘聚焦；原有 `Ctrl/Cmd+Enter` 快捷键继续保留。
- 运行结束后按钮立即消失；若 UI 仍处于 loading 但 canonical active Turn 无法确定，则显示禁用态而不是猜测 Turn。

### 3.2 可用性矩阵

| 条件 | 按钮状态 | 点击结果 |
|---|---|---|
| active Turn + 非空纯文本 | 可用 | 直接提交 Steering |
| active Turn + 空白文本 | 禁用 | Tooltip 提示先输入内容 |
| 没有 active Turn | 隐藏；短暂状态不同步时可禁用 | 不发请求、不自动转普通发送 |
| 正在提交 Steering | loading + 禁用 | 双击不会产生第二次请求 |
| 存在待发送图片 | 禁用 | 图片和文本均保留；提示“图片暂不支持插嘴，请使用普通发送/排队” |
| Turn 已终态，API 返回 409 | 恢复可编辑草稿 | 不进入队列，不创建新 Turn |
| 网络/服务失败 | 草稿保持不变 | 显示错误和重试入口，不静默回退到队列 |

### 3.3 草稿与失败合同

独立按钮必须遵守“先受理、后清空”：

1. 点击时冻结 `submittedText` 和稳定的 client operation id。
2. 调用 Steering API；请求在途时按钮禁用。
3. 收到确定的 `202 Accepted` 后，仅当输入框仍是本次提交的原草稿时清空；若用户已输入新内容，不覆盖新草稿。
4. 收到 `409`、超时、网络错误或取消时，原文本留在输入框；若用户已继续输入，不能用旧文本覆盖新文本。
5. 失败后绝不调用 `submitInteraction`、`enqueueInteraction` 或普通 `sendMessage` 兜底，因为用户明确选择的是“插嘴”，不是“稍后发送”。

### 3.4 队列边界

“不进入任务/消息队列”冻结为以下可测试合同：

- 不写 `pendingSendQueue`；
- 不创建普通 backend message delivery；
- 不调用创建新 Conversation Turn 的普通发送链；
- 不增加 `MessageQueueDropdown` 的“待发”计数；
- 仍允许服务端写入 durable `session_steering_messages`，因为它是当前 Turn 的可靠 Steering mailbox，不是下一 Turn 的普通消息队列；
- 队列项主动转换为 Steering 的既有入口保持不变，仍可在队列面板内展示其转换状态；Composer 直达 Steering 使用独立的轻量 receipt，不混入普通队列投影。

## 4. 状态与数据流

### 4.1 前端状态

Hook 对外暴露一个稳定、最小的 Composer 合同：

```text
canSteerCurrentTurn: boolean
composerSteeringStatus: idle | submitting | accepted | failed
submitComposerSteering(text): Promise<boolean>
```

- `canSteerCurrentTurn` 与真正提交时使用同一个 active-Turn resolver，不能只看 `loading`。
- `submitting` 对按钮进行单飞锁；同一 client operation id 只允许一个 admission 请求在途。
- `accepted/failed` 仅驱动按钮附近的短暂 receipt/Tooltip，不加入普通 interaction queue。
- `steering.injected` 到达后可把 receipt 从“已受理”更新为“已注入第 N 轮”，随后自动淡出；事件事实仍留在 canonical diagnostics 中。

### 4.2 调用链

```text
IntentConsole ⚡
  -> onSteerCurrentDraft(text)
  -> ChatMain -> ChatLayout -> chat/index 属性链
  -> useChatState.submitComposerSteering
  -> useMessageInteractionQueue.submitSteeringInteraction
  -> createChatSteeringMessage(workspaceId, conversationId, activeTurnId)
  -> POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering
  -> CreateSteeringHandler（Running + Workspace/Agent fence）
  -> SessionSteeringService（durable、target_turn_id 不可变）
  -> AgentExecutionService 下一安全 LLM 边界 drain
  -> steering.injected / agent.steering.inject
  -> Composer receipt 更新；不产生普通队列项或第二个 Turn
```

### 4.3 幂等与竞态

- 每次按钮点击生成稳定的 client operation id；同一次模糊网络重试复用该 id。
- 可沿用现有 `sourceQueueItemId` 作为服务端幂等键，但命名不得被 UI 当作“已经进入队列”的证据；后续清理时可将协议字段统一命名为 `sourceClientOperationId`，本任务不为命名迁移新建兼容双写层。
- UI 双击、React 重渲染、SSE 重放均不得重复创建 Steering。
- active Turn 在点击与 API admission 之间终态化时，以服务端 `409` 为权威；客户端不把消息自动改投下一 Turn。
- 切换 workspace/session/agent 时取消本地在途展示，但不能假装撤回已经获得 `202` 的 durable Steering。

## 5. 文件级实施计划

| 文件 | 计划改动 |
|---|---|
| `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useMessageInteractionQueue.ts` | 抽取共享 active-Turn resolver；增加 `submitComposerSteering`、`canSteerCurrentTurn`、单飞状态和 compare-and-clear 草稿合同；Composer 直达项不合并进 `visibleInteractionQueue` |
| `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts` | 解构并返回 Composer Steering 合同；当前源码只返回队列项转换入口 |
| `Source/PuddingPlatformAdmin/src/pages/chat/index.tsx` | 将 `chat.submitComposerSteering`、可用性和提交状态传入 `ChatLayout` |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatLayout.tsx` | 增加显式 props 并透传，不在布局层复制状态机 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx` | 继续透传给 `IntentConsole`；不在消息区创建第二个发送入口 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/IntentConsole.tsx` | 在发送/停止按钮左侧渲染 `⚡`；实现 Tooltip、ARIA、禁用矩阵和点击调用，不直接访问 API |
| `Source/PuddingPlatformAdmin/src/pages/chat/styles/composer.styles.ts` | 新增与现有 34×34 按钮同尺寸的 steering 样式、提交中状态、深浅色主题和窄屏规则 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageQueueDropdown.tsx` | 仅在当前投影仍把 Composer direct receipt 计入队列时做分离；不得改回大面积常驻队列 UI |
| 对应 `.test.ts(x)` | 覆盖按钮可见性、直达路径、草稿安全、无队列副作用、双击幂等、图片禁用、409 与响应式/可访问性 |

后端 API、SQLite schema 和 Runtime drain 预期不需要新增实现；若实施时发现必须改后端，先回到本设计补充失败证据和合同，不得在 UI 任务中顺手创建第二条 Steering 路径。

## 6. 测试与验收

### 6.1 Hook/组件测试

1. active Turn + 文本时显示并启用 `⚡`；空闲时不显示。
2. 点击只调用一次 `submitComposerSteering`，不调用 `onSend`、`enqueueInteraction` 或普通 `sendMessage`。
3. `202` 后仅清除未被用户修改的原草稿；提交期间的新输入保留。
4. `409`、网络失败和取消后原草稿可继续编辑，普通队列长度不变。
5. 快速双击或连续渲染只有一个 admission 请求。
6. 有待发送图片时按钮禁用，图片和文本都不丢失。
7. `Ctrl/Cmd+Enter` 继续走相同 Composer Steering 合同；普通 `Enter` 的排队行为不回归。
8. Composer direct Steering 不增加 `MessageQueueDropdown` 的待发/处理中计数。
9. `aria-label`、Tooltip、Tab 焦点和窄屏布局可用。

### 6.2 集成与产品 smoke

1. 运行中的 Agent 执行一个跨多轮/工具任务；输入“先停止当前方向，改为检查 X”并点击 `⚡`。
2. 网络证据显示唯一 Steering POST，未创建新 Turn、未创建普通 message delivery。
3. 当前工具调用不被强杀；下一次 LLM 请求的上下文含该 Steering。
4. UI 收到 `steering.injected`，Agent 后续行为实际遵循新指令。
5. 在 Turn 即将结束时点击，验证 late safe boundary；若 admission 已晚返回 409，草稿仍在且没有自动排队。
6. 外部控制器部署明确的新前端 bundle，并在页面显示 build identity 后再做 smoke；源码测试通过不能视为浏览器已生效。

建议定向命令（实施阶段执行，本轮设计不运行）：

```powershell
Set-Location Source\PuddingPlatformAdmin
npm test -- --runInBand --runTestsByPath src/pages/chat/hooks/useMessageInteractionQueue.test.ts src/pages/chat/components/IntentConsole.test.tsx src/pages/chat/components/ChatLayout.test.tsx src/pages/chat/components/ChatMain.test.tsx
npm run build
```

## 7. 可观测性

沿用现有 Steering 事件，并补充/区分入口维度：

- `chat.steering.submit`：`origin=composer_button|keyboard|queue_conversion`、turnId、messageChars；
- `chat.steering.submitted`：steeringId、requestLatencyMs、origin；
- `chat.steering.submitFailed`：稳定错误类别、HTTP status、origin，不记录正文；
- `steering.created` / `steering.injected`：canonical 服务端受理与消费事实；
- 指标：按钮点击数、202/409/失败率、admission→injected 延迟、重复提交拦截数、错误回退到普通队列次数（目标必须为 0）。

## 8. 非目标与完成边界

- 不取消正在运行的工具/模型请求，不引入抢占式强杀。
- 不让 Steering 创建第二个 Turn。
- 不给图片 Steering 做隐式降级或静默丢图。
- 不新增 Web/Desktop 各自状态机；所有客户端仍调用同一个服务端 admission。
- 不删除普通排队、队列项转换、`Ctrl/Cmd+Enter` 或停止功能。
- 本文 `Proposed` 只表示设计和任务登记完成；只有源码实现、定向测试、构建、明确部署和产品内 smoke 全部通过后，任务才可进入完成态。
