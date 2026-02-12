# Agent 工作台 UI 施工蓝图

> 日期：2026-05-28
> 状态：施工前 UI 蓝图
> 上游设计稿：`Docs/superpowers/specs/2026-05-28-agent-workbench-interaction-design.md`
> 目标：把语音输入、语音输出、Agent 感知、视觉入口和事件时间线组织为统一工作台，而不是继续堆叠在聊天输入框周围。

## 1. 当前前端现状

当前代码里已经存在多模态能力的雏形：

| 能力 | 当前文件 | 现状判断 |
| --- | --- | --- |
| 聊天主区 | `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx` | 仍以 ChatMain 为页面中心，已接入 Agent avatar runtime event。 |
| 输入区 | `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx` | 已有键盘/语音模式切换，但整体仍是 Composer 语义。 |
| 语音输入/输出面板 | `Source/PuddingPlatformAdmin/src/pages/chat/components/VoiceConversationPanel.tsx` | 已有录音、转写草稿、发送、朗读最新回复；需要提升为工作台意图控制台的一部分。 |
| 虚拟形象 | `Source/PuddingPlatformAdmin/src/pages/chat/components/AgentAvatarRuntimeView.tsx` | 已有 sprite/static avatar 状态视图；需要成为右侧 Agent 感知栏的核心，而不是孤立 aside。 |
| 摄像头输入 | `Source/PuddingPlatformAdmin/src/pages/chat/components/CameraInputModal.tsx` | 已有 modal 型视觉输入；首期保留，但需要在工作台结构中有明确入口和状态归属。 |
| 样式 | `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts` | 已有 Pudding 风格 token 和局部 voice/camera 样式；需要补齐工作台三栏和感知栏样式。 |

结论：下一步不是从零做语音，而是把已有局部能力重新组织成 **Agent Workbench** 的统一信息架构。

## 2. 页面组件分层

目标组件树：

```text
ChatLayout
└─ AgentWorkbenchLayout
   ├─ WorkbenchLeftRail
   │  └─ SessionSidebar
   ├─ WorkbenchCenter
   │  ├─ WorkspaceNavigationHeader
   │  ├─ SessionTimeline
   │  │  └─ MessageList
   │  └─ IntentConsole
   │     ├─ ConsoleModeTabs
   │     ├─ KeyboardIntentPanel
   │     ├─ VoiceIntentPanel
   │     └─ Camera/File/Command entries
   └─ AgentPresenceRail
      ├─ AgentAvatarRuntimeView
      ├─ VoiceCaptureStatusCard
      ├─ VoicePlaybackStatusCard
      ├─ VisionStatusCard
      └─ RuntimeActivityList
```

首期可以不一次性创建所有新组件，但施工方向应按此边界拆分，避免继续把全部逻辑压在 `InputArea` 和 `ChatMain` 里。

## 3. 首期建议组件边界

### `AgentWorkbenchLayout`

职责：

- 替代“Sidebar + Main”的纯聊天布局语义。
- 管理三栏桌面布局和窄屏折叠。
- 不直接处理语音、摄像头或消息业务逻辑。

输入：

- sidebar 展开状态。
- main workbench 节点。
- presence rail 节点。

### `IntentConsole`

职责：

- 承载所有用户意图入口。
- 统一管理当前输入模式：`keyboard | voice | camera | file | command`。
- 保持输入模式之间共享草稿、上下文、禁用状态和发送策略。

首期模式：

- `keyboard`
- `voice`

后续模式：

- `camera`
- `file`
- `command`

### `VoiceIntentPanel`

由当前 `VoiceConversationPanel` 演进而来。

职责：

- 麦克风权限与采集。
- 实时/最终转写展示。
- 转写编辑与确认发送。
- 发送 voice metadata。
- 触发 voice capture projection。

不应负责：

- 页面整体状态。
- Agent 头像状态计算。
- Timeline 渲染。
- 摄像头输入。

### `AgentPresenceRail`

职责：

- 展示 Agent 的当前存在感。
- 聚合 avatar、语音输入、语音输出、视觉状态和运行活动。
- 成为用户判断“Agent 当前在做什么”的固定位置。

它是本次交互重设计最关键的新界面区域。没有右侧感知栏，语音能力仍会显得像 Composer 附属功能。

## 4. 桌面布局规格

桌面端推荐宽度：

```text
┌──────────────┬───────────────────────────────┬────────────────┐
│ 280px        │ minmax(0, 1fr)                │ 260px          │
│ Left Rail    │ Center Timeline + Console     │ Presence Rail  │
└──────────────┴───────────────────────────────┴────────────────┘
```

规则：

- 左侧会话栏沿用现有宽度体系。
- 中央区域是主阅读与主操作区。
- 右侧感知栏固定宽度约 `240-280px`，不使用大卡片堆叠。
- 底部意图控制台固定在中央区域底部。
- 感知栏与意图控制台之间状态一致：例如控制台正在录音时，右侧也显示“正在聆听”。

窄屏规则：

- `<= 1100px`：右侧感知栏折叠成顶部状态条或抽屉。
- `<= 760px`：左侧会话栏隐藏，意图控制台保留；语音面板可占据更大高度。

## 5. 视觉语言规格

遵循 `Quiet Local Intelligence`：

- 背景使用暖中性色，不使用纯白满屏。
- 面板边框使用低透明度棕灰线。
- Accent purple 只用于选中、焦点、少量状态点。
- 语音录制中不使用高饱和红色大面积提示，改用暖棕/琥珀状态。
- 状态文案短、明确、可恢复。

建议核心样式命名：

| 区域 | 样式名 |
| --- | --- |
| 工作台根布局 | `workbenchShell` |
| 中央工作区 | `workbenchCenter` |
| 右侧感知栏 | `agentPresenceRail` |
| 感知栏区块 | `presenceSection` |
| 意图控制台 | `intentConsole` |
| 模式切换 | `intentModeTabs` |
| 语音面板 | `voiceIntentPanel` |
| 语音状态条 | `voiceStateLine` |
| 运行活动列表 | `runtimeActivityList` |

现有 `composerSurface` 可以作为过渡样式，但最终命名应转向 `intentConsole`，避免产品语义继续停留在 Composer。

## 6. 意图控制台细节

### 默认键盘模式

布局：

```text
[模式：键盘 | 语音]  [上下文/工具状态]
[+] [输入你的问题或任务...] [发送/停止]
```

行为：

- 用户进入页面时可以直接输入。
- 点击语音 tab 后，输入区切换为语音面板。
- 键盘模式下的小麦克风按钮只作为模式切换捷径，不承担完整语音交互。

### 语音模式

布局：

```text
[模式：键盘 | 语音]  [麦克风状态 / Agent 状态]
┌─────────────────────────────────────────────┐
│ 语音会话                          状态：正在听 │
│ [主控制：开始/停止] [音量反馈] [00:12]          │
│ 转写草稿                                      │
│ [可编辑文本区域]                               │
│ [重录] [取消] [朗读最新回复] [发送语音内容]       │
└─────────────────────────────────────────────┘
```

首期行为：

- 默认手动确认发送。
- 录音开始时停止当前语音播放。
- 转写完成后进入待确认。
- 发送后清空草稿并回到待命。
- 朗读最新回复只朗读最新 Assistant 文本，不生成新的时间线消息。

后续行为：

- 支持静音后自动发送。
- 支持按住说话。
- 支持语音打断正在播放的 Agent 回复。

## 7. Agent 感知栏细节

### 区块结构

```text
Agent
[虚拟形象]
状态：正在听

听觉
麦克风：正在采集
转写：等待确认

声音
输出：可朗读
最近回复：[回放] [停止]

视觉
摄像头：关闭
[开启视觉输入]

当前活动
- 正在整理上下文
- 子 Agent：2 个
```

### 状态文案

| 状态 | 主文案 | 辅助文案 |
| --- | --- | --- |
| idle | 待命 | 可以输入、说话或添加上下文 |
| listening | 正在听 | 麦克风正在此浏览器中启用 |
| transcribing | 正在转写 | 转写结果会等待你确认 |
| thinking | 正在思考 | 正在整理上下文 |
| tool | 正在使用工具 | 可在时间线查看步骤 |
| speaking | 正在说话 | 可以停止或直接开始说话打断 |
| seeing | 正在看 | 摄像头画面只在授权后启用 |
| error | 需要处理 | 查看权限或服务状态 |

### Avatar 规则

- `AgentAvatarRuntimeView` 继续只负责形象渲染。
- 状态推导放在 runtime/projection 层。
- 感知栏负责把 avatar、状态文字、语音/视觉卡片排在一起。
- 隐藏虚拟形象不应隐藏 Agent 状态，状态卡片仍然保留。

## 8. 时间线细节

### 用户语音消息

显示：

```text
用户  · Voice · 10:24
帮我总结今天的任务，然后安排下一步。
```

展开详情：

- `voiceSessionId`
- `asrProvider`
- `asrModel`
- `language`
- 原始转写，如果用户编辑过

### Assistant 语音回复

显示：

```text
Agent · Text + Voice · 10:25
这是今天任务的摘要...
[朗读] [停止]
```

规则：

- 不重复生成一条“语音消息”。
- 语音是回复的输出模态之一。
- 回放控制可以出现在时间线消息操作区，同时右侧感知栏保留“最近回复”控制。

### 工具和事件

默认折叠：

```text
运行事件 · 3 步 · 已完成
```

展开后展示：

- 工具名称。
- 状态。
- 简短结果。
- 原始 payload 只放在 Dev/Details。

## 9. 状态到 UI 的映射

### Voice capture

| Runtime 状态 | 控制台 | 感知栏 | Avatar |
| --- | --- | --- | --- |
| `requesting_permission` | 等待麦克风权限 | 麦克风：等待授权 | idle |
| `recording` | 正在听 | 麦克风：正在采集 | listening |
| `transcribing` | 正在转写 | 转写：进行中 | hearing |
| `completed` | 待确认 | 转写：等待确认 | idle |
| `cancelled` | 待命 | 麦克风：关闭 | idle |
| `failed` | 显示错误 | 麦克风：不可用 | error |

### Voice playback

| Runtime 状态 | 控制台 | 感知栏 | Avatar |
| --- | --- | --- | --- |
| `synthesizing` | 朗读准备中 | 输出：生成中 | thinking |
| `playing` | 停止朗读 | 输出：正在播放 | speaking |
| `completed` | 可再次朗读 | 输出：已完成 | idle |
| `cancelled` | 可再次朗读 | 输出：已停止 | interrupted |
| `failed` | 显示错误 | 输出：不可用 | error |

## 10. 权限与错误 UX

### 麦克风未授权

文案：

```text
需要麦克风权限
Pudding 只能在你授权后通过当前浏览器听见你。
[授权麦克风] [继续使用键盘]
```

规则：

- 不自动反复弹权限。
- 用户拒绝后提供恢复说明。
- 仍可使用键盘模式。

### 浏览器不支持语音输入

文案：

```text
当前浏览器不支持语音输入
你仍可以使用键盘输入；语音输出是否可用取决于浏览器。
```

### 语音输出不可用

文案：

```text
当前浏览器无法朗读回复
文本回复不受影响。
```

### 摄像头未启用

文案：

```text
摄像头关闭
Agent 目前不会看到你的画面。
```

## 11. 可访问性要求

- 模式切换使用 `role="tablist"` 和 `role="tab"`。
- 所有图标按钮必须有 `aria-label`。
- 录音状态变化应有低干扰的 `aria-live="polite"` 文案。
- 语音主按钮在录音中必须能通过键盘停止。
- 文本区域在转写完成后可聚焦编辑。
- 错误文案不能只通过颜色表达。
- 支持 `prefers-reduced-motion`。

## 12. 首期施工验收清单

- [ ] 页面首屏可见 Agent 工作台语义，而不是只看见聊天输入框。
- [ ] 中央时间线、底部意图控制台、右侧 Agent 感知栏同时成立。
- [ ] 键盘模式可直接输入和发送。
- [ ] 语音模式是展开面板，不是单个麦克风按钮。
- [ ] 录音、停止、重录、发送语音内容行为清楚。
- [ ] 语音转写可编辑并作为用户消息发送。
- [ ] 用户语音消息带 `inputMode=voice` 元数据。
- [ ] Agent 最新回复可以朗读和停止。
- [ ] Agent 感知栏能显示听、想、说、错四类状态。
- [ ] 虚拟形象状态由 runtime/projection 驱动。
- [ ] 摄像头入口存在但不抢占首期语音体验。
- [ ] 窄屏时右侧感知栏有折叠策略。
- [ ] 权限失败可以恢复或回到键盘输入。

## 13. 推荐施工顺序

1. 抽出 `AgentPresenceRail`，先承载现有 `AgentAvatarRuntimeView` 和语音状态文字。
2. 将 `InputArea` 的产品语义改为 `IntentConsole`，保留兼容导出或薄包装。
3. 将 `VoiceConversationPanel` 命名/职责演进为 `VoiceIntentPanel`，补齐状态文案与权限 UX。
4. 在 `ChatMain` 中形成中央时间线 + 底部意图控制台 + 右侧感知栏布局。
5. 给语音消息和 Assistant 语音输出补齐时间线展示规则。
6. 加桌面/窄屏视觉 QA。
7. 最后再接摄像头和更复杂的 avatar 动画，不与首期语音施工混在一起。

## 14. 不建议的施工方式

- 不建议只在现有输入框加大麦克风按钮。
- 不建议把语音状态全部放在 `VoiceConversationPanel` 内部，右侧没有 Agent 感知。
- 不建议让虚拟形象作为漂浮装饰层。
- 不建议首期直接引入完整 WebRTC/Qwen-Omni UI，把语音输入输出和实时视频一起做。
- 不建议为语音录制使用高强度动效、强红警示或大面积渐变。

