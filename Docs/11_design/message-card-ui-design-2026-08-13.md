# PuddingAgent 消息卡片 UI 设计方案

> 日期：2026-08-13 ｜ 类型：前端设计调研 + 现状差距分析 + 可落地改造方案
> 范围：`Source\PuddingPlatformAdmin\src\pages\chat\` 消息卡片/气泡体系（只读分析，不写代码）
> 兼容约束：方案须与蜜糖正在实现的「队列徽标系统」（fresh/waiting/retrying/三终态徽标 + 错误红原文改摘要+tooltip + 计数语义）共存，避免视觉打架。

---

## 1. 业界设计特征提炼

### 1.0 总览对照表

| 维度 | Slack | Discord | 飞书 | 钉钉 | Telegram | Linear | ChatGPT/Claude/Cursor |
|---|---|---|---|---|---|---|---|
| 布局范式 | 行式（无气泡） | 行式（桌面）/气泡（移动） | 结构化卡片 | 气泡式 | 气泡式 | 行式/极简 | AI 对话式（用户气泡、AI 内容块） |
| 头像 | 36–40px 圆形；紧凑模式隐藏 | 分组合并时隐藏；2min 窗口 | person 组件分级（extra_small→large） | 24–56px 圆/方可配 | 大头像 | 小尺寸/姓名优先 | 24–32px 圆角 |
| 时间戳 | 可隐藏；hover 完整时间 | 默认隐藏，hover 显示 | 卡片内时间组件 | 默认隐藏，滑动查看 | 气泡下方小字；hover 完整时间 | 次要文字 | 相对时间 + hover 完整时间 |
| 气泡样式 | 无气泡（留白分隔） | 桌面无气泡 | 卡片 605px 上限、12px 内边距 | 蓝/绿 vs 灰，圆角矩形 | 蓝 vs 灰；圆角可调（默认 16px+）；尾部指向发送者 | 细边框卡片 | 用户填充色圆角；AI 无边框内容块 |
| 代码块 | 深色底 + 高亮 + 复制 | 深色底 + 语言标签 + hover 复制 | 富文本组件 | 普通渲染 | 主题化代码块 | 等宽字体 | 语言标签 + 复制按钮 + 高亮（Shiki） |
| 引用/回复 | 线程 + 引用回复 | 内联回复高亮 | 消息回复组件 | 引用回复 | 引用回复 | 评论线程 | 引用/上下文标注 |
| 状态指示 | 发送中/失败提示 | 发送失败红标 | 卡片状态语义色 | 已读回执（眼睛图标/圆点） | ✓/✓✓ 送达与已读 | 加载态 | 思考三点脉冲、流式光标、Stop、错误重试 |
| 操作按钮 | hover 显示（表情/线程） | hover 显示（回复/表情/更多） | 卡片 action 区 | 长按菜单 | 长按/hover 菜单 | 图标 hover | hover 显示（复制/重生成/反馈） |
| 密集可读性 | 紧凑/清晰两档密度；连续合并 | 分组圆角收敛 + 2min 合并 | 空间代替分隔线 | 分组合并 | 分组圆角收敛 | 极高信息密度 | 会话级分组、宽行距 |

### 1.1 Slack — 行式消息的密度标杆

- **布局**：消息呈「行式」而非气泡，强调扫描效率；提供 **清晰（spacious）/紧凑（compact）两档密度**，紧凑档隐藏头像、压缩行距（slack.com help: 213893898）。
- **头像与发送者**：清晰模式头像（约 36–40px 圆形）+ 发送者名 + 时间同行置顶；连续同人消息合并后仅首条显示头像与名称。
- **时间戳**：可全局配置显示/隐藏，支持 24 小时制；hover 展示完整日期时间。
- **代码/引用**：mrkdwn 格式化；代码块深色底 + 语法高亮；引用回复以缩进 + 链接形式呈现；附件卡片左侧色条 + 结构化字段（docs.slack.dev legacy attachments）。
- **状态**：发送中短暂提示，失败红底提示可重发；typing 指示器。
- **操作**：hover 弹出表情/线程/分享/更多，图标按钮组位置稳定。
- **可读性**：信息密度优先，行距与留白按档位切换；最大内容宽度约 820px 内居中。

### 1.2 Discord — 分组与代码块渲染的标杆

- **布局**：桌面端行式消息（无气泡），移动端气泡化；支持 **紧凑/舒适密度**。
- **头像分组**：同一用户连续消息合并，仅首条显示头像；**时间窗口约 2 分钟**，超时即使同人也会重新显示头像与时间（Discord 消息分组规则）。
- **时间戳**：默认不显示，**hover 行时显示绝对时间**；动态时间戳语法 `<t:UNIX:R>` 相对/本地化实时更新。
- **代码块**：深色底 + **语言标签** + hover 右上角复制按钮；行内代码反色底。
- **嵌入卡片**：左侧 4px 彩色条 + 圆角 4px + 标题/描述/字段结构化排版（design-md 中 `.embed` 规范：`max-width 520px`）。
- **回复**：内联回复显示「目标消息摘要 + 跳转高亮」，点击可定位原文。
- **操作**：hover 显示表情回应/回复/更多（…）按钮；右键菜单承载复制/编辑/删除/引用。
- **状态**：发送失败红底警示；typing 三点动画。

### 1.3 飞书 — 结构化消息卡片规范

- **卡片约束**：PC 端卡片宽自适应、**最宽 605px / 最窄 302px**，内容与边框间距 12px，组件间距 16px（open.feishu.cn 消息卡片设计规范 2023-08-25）。
- **组件化**：标题区（可选，16px Medium）+ 内容区必选，12 种组件自由堆砌；「像积木一样」拼装。
- **彩色标题语义**：green=完成/成功、orange=警告、red=错误/异常、grey=失效——**状态色即语义锚点**。
- **层级**：气泡卡片用 `border-card` 描边 + `shadow-4-down` 投影建立空间层级；**用空间代替分隔线**，减少分割线噪音。
- **人员组件**：头像 extra_small→large 分级，支持胶囊（capsule）样式。
- **交互**：卡片支持按钮 action、表单、二次确认；审批类卡片强调「主操作最右、按钮 ≤3 个」。

### 1.4 钉钉 — 已读回执与降噪的标杆

- **已读/未读**：消息右下角**小眼睛图标（已读）/ 小圆点（未读）**，点击展开已读人员列表；「收到」按钮在 @所有人 后聚合计数，避免「收到」刷屏（优设网/钉钉设计团队）。
- **时间降噪**：**默认隐藏时间**，左滑手势查看精确收发时间——减少重复信息对阅读的干扰。
- **头像**：open-avatar 组件 24–56px 圆形/方形可配（open.dingtalk.com）。
- **气泡**：己方蓝色/绿色右对齐、对方灰色左对齐；圆角矩形；消息背景可全局定制。
- **状态**：DING 消息强提醒 + 阅读追踪详情页（已读/未读分区、未打开/已打开未读两类细分）。

### 1.5 Telegram — 气泡式布局的移动标杆

- **气泡**：发送蓝色（`#40A7E3`）、接收灰/白；**圆角可在设置中调节**（默认较大，16px 级），气泡「尾部」指向发送者，识别性强。
- **分组收敛**：连续消息同侧气泡**组内小圆角、组外大圆角**（一侧直角收敛），形成自然的消息「组」。
- **状态**：**✓ 已送达 / ✓✓ 已读**（蓝色对勾），群播消息显示浏览量。
- **时间戳**：气泡底部小字显示相对/短时间，**hover 或长按显示完整时间**。
- **主题**：全局主题自定义（聊天背景/气泡颜色/圆角强度）。

### 1.6 Linear — 极简工具美学的参照系

- **画布**：近黑 `#08090a`，信息层级靠 **luminance 分层**（边框 `rgba(255,255,255,0.05–0.08)`），而非颜色堆叠。
- **字体**：Inter Variable，**510 字重**（regular 与 semibold 之间的「强调但不吵」），紧凑负字距。
- **强调色**：单一品牌紫 `#5e6ad2` 只用于交互与激活态。
- **排版**：基准字号 14px，行高紧凑；评论/消息以行式呈现，头像小、姓名与时间次级显示。
- **借鉴点**：克制动效、单强调色、细边框层级——对 PuddingAgent 现有「紫棕混搭」体系有收敛价值。

### 1.7 ChatGPT / Claude / Cursor — AI 对话界面的当代标准

- **流式输出**：**200ms 内首 token** 可见；用**光标动画（▋/▌）**而非转圈 loading；流式中提供 **Stop 按钮**（setproduct.com AI chat anatomy）。
- **布局**：用户消息右对齐填充色气泡（圆角 12px 级）；AI 回复左对齐、**无边框内容块**（宽度受限居中，最大约 768px），弱化「气泡」强调「内容」。
- **操作**：AI 回复下方 hover 显示 **复制 / 重新生成 / 编辑 / 反馈（👍👎）**；用户消息可编辑，编辑后分支重新生成。
- **思考/工具**：思考中三点脉冲；工具调用显示工具名 + 进度；错误状态带 **重试按钮**。
- **代码块**：语言标签 + 复制按钮 + 语法高亮（Shiki），代码块自成卡片式背景。
- **状态诚实性**：明确区分 thinking / streaming / tool-call / error / done，不同阶段给不同视觉反馈。

---

## 2. PuddingAgent 现状分析（逐组件）

> 源码根：`Source\PuddingPlatformAdmin\src\pages\chat\`
> 风格基线：React + antd + antd-style（createStyles）；全局 token 见 `styles\global.style.ts`（`--pudding-chat-*` 系列 + `--earth-brown` / `--accent-purple` / `--soft-white` 语义色）。

### 2.1 AgentAvatar.tsx（components\AgentAvatar.tsx，56 行）

- **视觉特征**：32px 圆形；图片头像失败自动回退 emoji/首字母（`AgentAvatar.tsx:19-23`）；`agentAvatarWrapper` 32×32 圆形、`fontSize 16`、`marginTop 18`（`agent.styles.ts:75-84`）；`grouped` 时用 `visibility:hidden` 占位保持对齐（`AgentAvatar.tsx:17`，`agent.styles.ts:96-101`）。
- **可读性**：单 agent 场景够用；多 agent 混排时无「分组头像/堆叠头像」区分；`marginTop 18` 是为与名称行对齐的硬编码，换行场景易错位。
- **差距**：头像 32px 属业界中等偏小；无在线/运行状态角标（业界 Discord/钉钉常见绿点）。

### 2.2 UserMessageBubble.tsx（components\UserMessageBubble.tsx，155 行）

- **视觉特征**：右对齐；`userMetaRow` 在气泡上方（时间 + modality 徽标 + 用户名，`UserMessageBubble.tsx:81-96`）；气泡 `userBubbleNew`：圆角 10/右下角 4、1.5px 紫边框（accent 28%）、紫 8% 浅底、`padding 10px 16px`、`fontSize 14`、`lineHeight 1.6`（`user.styles.ts:64-97`）；32px Antd Avatar 在气泡右侧（`UserMessageBubble.tsx:137-152`）。
- **状态指示**：发送中仅 `opacity 0.7` + 小字「发送中...」（`UserMessageBubble.tsx:126-128`，`user.styles.ts:169-174`）——视觉较弱，无图标。
- **可读性**：meta 行与气泡分离（时间/名字在气泡上方），与 Telegram「时间在气泡内底部」不同；时间在最左、名字在最右，扫描路径曲折。
- **差距**：无失败态红标（用户消息失败仅 opacity）；无「重试」入口；vision 图片 2 列网格 280px 上限偏小。

### 2.3 AgentMessageBubble.tsx（components\AgentMessageBubble.tsx，801 行）

- **视觉特征**（核心气泡 `agentBubbleNew`，`agent.styles.ts:124-178`）：
  - 圆角 10 / 左上角 5（非对称「指向头像」）；1px 边框（紫 9% + 棕 5% 混色）；**borderLeft 2px 紫色**（22% 混色）——「左侧强调条」风格，业界 Discord embed 同款思路。
  - `padding 12px 16px`、`fontSize 14`、`lineHeight 1.7`；阴影 `0 3px 12px` 轻微浮起；hover 上浮 1px + 轻微放大（`transform translateY(-1px) scale(1.006)`）。
  - **流式态**：紫色发光（`agentBubbleStreaming`）+ 渐变流光 overlay（`agentActiveOutputSurface` 的 `::after` 动画，`agent.styles.ts:346-373`）+ 打字机 InkBloom 光标（`markdown.styles.ts` inkCursor）。
  - **停滞警告**：15s 无增量 → 琥珀色慢脉冲边框（`agentBubbleWarning`，`agent.styles.ts:282-286`，逻辑 `AgentMessageBubble.tsx:566-580`）。
  - **错误态**：红色边框 + `glitchShake` 抖动动画（`agentBubbleError`，`agent.styles.ts:279-281`）。
  - **完成反馈**：回答落定时右下角粒子爆发（`COMPLETION_PARTICLE_OFFSETS`，`AgentMessageBubble.tsx:73-82`）。
- **过程可视化**：气泡上方可叠加 `CurrentActivityPanel`（当前工具/子代理活动）、`ReasoningPreview`（思维链）、`WaitingBubble`（无事件等待）三种运行态面板（`AgentMessageBubble.tsx:593-654`）；正文下方有可展开的 `MessageProcessSummary` 时间线。
- **操作**：hover 显示 `MessageActions`（复制/重生成/固定/TTS/删除）。
- **可读性问题**：
  1. 同一气泡周围最多可出现 **5 种视觉容器**（run monitor 面板、气泡、过程摘要、token 行、操作按钮），信息密度高但**层级区分弱**（都是边框+浅底）。
  2. 错误用「整气泡红边 + 抖动」，**无错误摘要文字**——用户看不到「错在哪」，与队列徽标系统的「摘要+tooltip」不一致。
  3. 完成粒子、流光、typewriter、悬浮阴影、入场动画叠加，动效偏多，`prefers-reduced-motion` 虽有降级但默认视觉「热闹」。
- **差距**：无时间戳 hover 完整时间；错误无摘要文案；等待呈现三套并存（WaitingBubble / CurrentActivityPanel / ReasoningPreview）。

### 2.4 MessageRow.tsx（components\MessageRow.tsx，379 行）

- **职责**：按 role 路由 user/agent/heartbeat（`MessageRow.tsx:266-377`）；支持 focusView 单行折叠（`FocusViewRow`）。
- **分组**：`groupedWithPrevious` 仅来自数据层布尔值，无时间窗口判定；分组行仅加 `messageRowGrouped`（`marginTop -4`，`message.styles.ts:63-65`）+ 头像隐藏 + 气泡左上角改 8px（`agentBubbleGrouped`，`agent.styles.ts:183-187`）。
- **差距**：与 Discord「2 分钟窗口 + 圆角收敛」相比，Pudding 无「组内组外圆角/间距」分层，连续消息仍像独立气泡。

### 2.5 MessageActions.tsx（components\MessageActions.tsx，202 行）

- **视觉特征**：icon 按钮组（24×24，`messageActionBtn`），hover 时背景浅棕 + 全亮（`message.styles.ts:196-210`）；容器 `messageActionsNew` 绝对定位于气泡左下 `bottom -26`（`message.styles.ts:159-175`），`visible` 时 `opacity 0.6`。
- **交互**：`visible` 由 React 状态控制（`AgentMessageBubble.tsx:631-633` onMouseEnter/Leave），非纯 CSS；`opacity 0.6` 的「可见但半透明」设计意图不明（悬停中应 1.0）。
- **可读性问题**：按钮组定位在气泡外底部，长气泡/贴边场景可能溢出或被滚动容器裁切；仅 icon + Tooltip，无文字标签；无 `:focus-visible` 键盘显影规则（`focusViewRowHeader` 有，messageActionBtn 无）。
- **差距**：业界（ChatGPT/Claude）操作按钮在气泡内或紧随下方、hover 全亮、键盘可达。

### 2.6 WaitingBubble.tsx（components\WaitingBubble.tsx，83 行）

- **视觉特征**：等待气泡含 3 点弹跳动画 + agent 名 + 已等待计时 + 阶段文案（4 档：请求/等待/深入分析/复杂推理，按 3s/10s/30s 阈值）+ 进度 track（已接收任务 → 等待首个可见事件）+ 解释性 hint（`WaitingBubble.tsx:38-83`）。
- **可读性问题**：单条等待气泡承载 5 类信息（点、标题、计时、track、hint），对普通用户信息过载；`waitingHint` 文案是面向开发者的（「子代理活动会显示在右侧托盘坞…」）。
- **差距**：业界等待态通常只呈现「三点脉冲 + 简短状态」（Discord typing / ChatGPT thinking）；Pudding 更接近「开发者控制台」而非聊天产品。

### 2.7 ApprovalCard.tsx（components\ApprovalCard.tsx，290 行）

- **视觉特征**：**内联 style 对象**（`cardContainerStyle` 等，`ApprovalCard.tsx:42-101`），非 createStyles 体系；左侧 3px 风险色条（low/medium/high/critical 四档色，`ApprovalCard.tsx:15-23`）；Tag 风险标签 + 状态标签；参数 `pre` 块（`maxHeight 160` 滚动、11px 等宽，`ApprovalCard.tsx:79-86`）；pending 态含理由输入框 + 3 个按钮（允许一次/始终允许/拒绝，主操作不固定最右，与飞书「主操作最右」规范相悖）。
- **一致性**：卡片样式与消息气泡体系（createStyles）分离，`--pudding-chat-*` token 与 `color-mix` 混用，圆角 10 与气泡 10 一致但边框/阴影独立定义。
- **差距**：四档风险色与队列徽标系统（琥珀警示/错误红）需要统一语义映射，避免「橙 vs 红」双套含义。

### 2.8 MessageList.tsx（components\MessageList.tsx，1235 行）

- **视觉特征**：虚拟滚动列表（`buildVirtualMessageItems` + `useMessageViewportRuntime`）；focusView 工具栏在列表顶部（`MessageList.tsx:1003-1010`）；空状态（ready/error/no-agent 三态）、加载骨架、错误 Alert（带复制诊断按钮）、底部滚动控制（回到底部 + 贴底跟随，`MessageList.tsx:1153-1199`）。
- **可读性**：`messageRow` padding 8px、行距紧凑（`message.styles.ts:47-56`）；**未见日期分隔线使用**——`timeDivider` 样式已定义（`message.styles.ts:26-34`）但当前列表无日期分组渲染；`contentVisibility:auto` + `containIntrinsicSize 120px` 虚拟化优化良好。
- **差距**：无日期分隔（今天/昨天/MM-DD）；无「新消息」插入锚点；滚动控制按钮 40×40 偏大、贴底/回底两个按钮并存略冗余。

### 2.9 MessageItem.tsx（components\MessageItem.tsx，187 行）

- **视觉特征**：Markdown 渲染容器 `markdownBody`；流式时「稳定段落 + live 尾段 + inkCursor」分段渲染（`MessageItem.tsx:147-159`）；流式结束用 FLIP transition 平滑高度突变（`MessageItem.tsx:54-88`）。
- **差距**：本身是渲染器，视觉问题主要在 MarkdownBlock 的代码块/表格上（见下）。

### 2.10 MarkdownBlock.tsx（components\MarkdownBlock.tsx，257 行）

- **代码块**：`CodeBlock` 用 Prism 高亮 + **右上角始终显示的复制按钮**（`MarkdownBlock.tsx:177-196`）；`codeBlockWrap` 圆角 8、背景为 misty-blue 30% 浅底（`markdown.styles.ts:52-64`）；**无语言标签、无行号、复制按钮不随 hover 显隐**。
- **表格**：`markdownTableScroll` 横向滚动 + 边框表（`markdown.styles.ts:36-50`）。
- **其他**：行内代码浅蓝底（`inlineCode`，`markdown.styles.ts:45-51`）；vision artifact 图片内联渲染（`MarkdownBlock.tsx:209-221`）；KaTeX/GFM/rehypeRaw 全开。
- **差距**：语言标签缺失是「AI 对话产品」最直观的差距点；代码块背景为浅色系，与业界「代码块加深色、正文保持浅色」对比度策略相反。

### 2.11 styles 体系（agent/message/user/process/markdown.styles.ts + global.style.ts）

- **token**：浅色模式 `--pudding-chat-surface:#fafaf7`、`--pudding-chat-text:#1a1a2e`、`--pudding-chat-accent:#7c3aed`（紫）；深色模式对应 `#1c1a16` / `#f4efe7` / `#a78bfa`（`global.style.ts:43-51, 341-349`）；`--earth-brown`（棕）承载名称/时间/次要文字。
- **圆角体系**：气泡 10 + 单角 5/4（指向发送者）；卡片 8–10；标签 999（胶囊）；码块 8。**半径档位未收敛为 token**。
- **阴影体系**：气泡 `0 3px 12px`、hover `0 6px 18px`；ApprovalCard 独立 `0 2px 8px`；卡片与气泡阴影强度不一。**未收敛为 token**。
- **色彩语义**：紫色=强调/运行、棕色=次要/名称、琥珀=停滞/警告、红=错误、绿=成功——与队列徽标系统（amber retrying / red 终态错误）基本同族，但**没有统一「状态色阶」文档**，散落各文件。

---

## 3. 差距清单

| # | 差距点 | 现状（引用） | 业界参照 | 影响 |
|---|---|---|---|---|
| G1 | 时间戳无 hover 完整时间 | `formatTime` 相对时间（`chatStateUtils.ts:172-176`），无 title/tooltip | Slack/Discord/Telegram/ChatGPT | 低（但易改） |
| G2 | 无日期分隔线 | `timeDivider` 已定义未启用（`message.styles.ts:26-34`） | 微信/钉钉/Telegram 日期分组 | 中：长会话定位困难 |
| G3 | 操作按钮半透明 + 定位溢出风险 | `messageActionsVisible` opacity 0.6（`message.styles.ts:176-179`）、绝对定位 bottom -26（`message.styles.ts:163`） | 业界 hover 全亮、组内定位 | 中：可点击性/可见性 |
| G4 | 操作按钮无键盘焦点显影 | `messageActionBtn` 无 focus-visible 规则（`message.styles.ts:196-210`） | Linear/ChatGPT 键盘可达 | 中：无障碍 |
| G5 | 代码块无语言标签、浅色底、复制按钮常显 | `CodeBlock`（`MarkdownBlock.tsx:177-196`）、`codeBlockWrap` 浅底（`markdown.styles.ts:52-64`） | Discord/ChatGPT/Claude | 高：最直观差距 |
| G6 | 错误无摘要文字 | 整气泡红边+抖动（`agentBubbleError`，`agent.styles.ts:279-281`） | ChatGPT 错误+重试文案 | 高：用户不知错因 |
| G7 | 错误呈现与队列徽标双轨 | 气泡错误=红边；队列错误=琥珀摘要+tooltip（`MessageQueueDropdown.tsx` getQueueStatusLabel/summarizeQueueError） | 单一语义色阶 | 高：视觉打架风险 |
| G8 | 连续消息无时间窗口分组 | `groupedWithPrevious` 仅数据布尔（`MessageRow.tsx`），无 2min 规则 | Discord 分组 | 中：密集会话可读性 |
| G9 | 等待态信息过载 | `WaitingBubble` 5 类信息 + 开发者 hint（`WaitingBubble.tsx:38-83`） | typing 三点 + 短文案 | 中：新手困惑 |
| G10 | 圆角/阴影 token 未收敛 | 各 styles 文件硬编码 10/8/5/4 与多档阴影 | Linear/飞书 token 化 | 中：一致性 |
| G11 | ApprovalCard 脱离 createStyles 体系 | 内联 style 对象（`ApprovalCard.tsx:42-101`） | 统一体系 | 低：维护性 |
| G12 | 用户消息发送中/失败指示弱 | 仅 opacity+文字（`UserMessageBubble.tsx:126-128`） | 图标+状态 | 中 |
| G13 | 输入区/队列与消息流视觉割裂 | 队列徽标在 composer 区域（`MessageQueueDropdown.tsx`） | 统一状态语言 | 中 |
| G14 | 多 agent 头像无区分度 | 32px 单头像，无堆叠/角标（`AgentAvatar.tsx`） | Discord 分组、钉钉角标 | 低（多 agent 场景） |

---

## 4. 可落地方案（P0 / P1 / P2）

> 原则：①优先做「低风险、高感知」的改动；②所有新样式收敛到 token（圆角/阴影/状态色阶）；③错误/等待/队列的状态语义统一为单一色阶，与蜜糖队列徽标系统同族；④动效全部保留 `prefers-reduced-motion` 降级。

### 4.0 状态色阶总表（全系统统一，含队列徽标兼容）

| 语义 | 色值（浅色模式建议） | 用法 | 兼容队列徽标 |
|---|---|---|---|
| 运行/进行中 | `--accent-purple`（#7c3aed 浅 / #a78bfa 深） | 流式边框/流光、活动面板、fresh 徽标 | fresh 徽标同色 |
| 等待/排队 | 琥珀 #d97706 / #b36b1e | 停滞警告、等待文案、waiting 徽标 | waiting/retrying 徽标同族（#b36b1e 已用于 retrying） |
| 成功/终态 | #22c55e | 过程摘要成功项、终态成功徽标 | 终态成功徽标同色 |
| 错误/失败 | #ef4444 | 气泡错误、终态失败徽标、重试按钮 | 终态失败徽标同色；**错误一律「摘要 + tooltip 全量」** |
| 中性/次要 | `--pudding-chat-text-muted` | 时间戳、meta、禁用态 | 计数分隔符 |

**关键规则**：错误信息（气泡级、队列级、审批级）统一采用 **「摘要文本（≤80 字符）+ title/tooltip 全量原文」** 模式（对齐 `MessageQueueDropdown.tsx` 的 `summarizeQueueError`），禁止整段红色 JSON 原文上屏；retrying 一律琥珀警示色而非红色（避免与终态失败混淆）。

---

### 4.1 P0 — 立即可做（低风险、高感知、纯样式为主）

#### P0-1 时间戳升级：hover 完整时间 + 日期分隔线
- **组件**：`chatStateUtils.ts` `formatTime`、`MessageList.tsx`、`message.styles.ts`
- **做法**：
  1. `formatTime` 保持相对时间展示（刚刚/X分钟前/MM-DD HH:mm），但由渲染处给元素加 `title={dayjs(ts).format('YYYY-MM-DD HH:mm:ss')}`（agent：`agentNameRow` 内 `agentTimeText`；user：`userMetaRow` 内 `userTimeText`）。改动最小：在 `AgentMessageBubble.tsx` 与 `UserMessageBubble.tsx` 的 time span 上补 `title` 即可。
  2. 启用 `timeDivider`（`message.styles.ts:26-34` 已有）：在 `buildVirtualMessageItems` 的投影结果中按「跨天」插入 divider 项（今天/昨天/MM-DD），渲染为居中分隔线。不动虚拟滚动核心，只在 items 数组插入 `kind:'divider'` 项（仿 loader 项的处理）。
- **验收**：hover 任意消息时间显示完整时间；跨天消息流出现居中日期分隔线。

#### P0-2 操作按钮可见性修复
- **组件**：`message.styles.ts`（`messageActionsNew` / `messageActionsVisible` / `messageActionBtn`）
- **做法**：
  1. `messageActionsVisible` 的 `opacity 0.6 → 1`（hover 中应全亮）。
  2. 增加键盘可达：`.messageActionsVisible:focus-within { opacity: 1; pointer-events: auto; }`，`messageActionBtn:focus-visible { outline: 2px solid color-mix(in srgb, var(--accent-purple) 45%, transparent); outline-offset: 1px; }`（对齐 `focusViewRowHeader` 的 focus-visible 写法）。
  3. 定位防溢出：`messageActionsNew` 由 `bottom:-26` 改为 `bottom:-30` 且容器（`agentMessageContainer`）`overflow: visible` 已满足时，确认父滚动容器不裁切；或改为气泡内右下角定位（业界常见）。**建议**：保持气泡外但给 `agentMessageContainer` 补 `paddingBottom: 8`，让按钮始终有落点。
- **验收**：hover 气泡操作按钮全亮；Tab 可聚焦且焦点环可见；长气泡场景按钮不被裁切。

#### P0-3 代码块语言标签 + 复制按钮 hover 显隐
- **组件**：`MarkdownBlock.tsx` `CodeBlock`、`markdown.styles.ts` `codeBlockWrap`/`codeCopyButton`
- **做法**：
  1. 从 `className`（`language-*`）提取语言名，无语言时显示「code」；在代码块左上角渲染语言标签（11px、半透明、等宽），右上角复制按钮保持不变。
  2. 复制按钮改 hover 显隐：`.codeBlockWrap:hover .codeCopyButton, .codeCopyButton:focus-visible { opacity:1 }`，默认 `opacity:0`。
  3. 背景对比度：浅色模式代码块背景加深一档（`color-mix(in srgb, var(--pudding-chat-surface-muted) 60%, transparent)` 或引入 `--pudding-chat-code-bg` token），正文与代码块拉开层级（参照 Discord 深底代码块、正文浅底）。
- **验收**：代码块显示语言标签；hover 才见复制按钮；浅/深色模式对比度均达标。

#### P0-4 圆角/阴影/状态色 token 收敛
- **组件**：`global.style.ts`（或新增 `chatTokens` 常量）
- **做法**：新增设计 token 并全局替换：
  - `--pudding-chat-radius-sm: 6px`（小卡/输入）、`--radius-md: 10px`（气泡/卡片）、`--radius-lg: 14px`（引用/图片）；
  - `--pudding-chat-shadow-sm: 0 1px 3px rgba(0,0,0,.04)`、`--shadow-md: 0 3px 12px rgba(63,38,95,.04)`、`--shadow-hover: 0 6px 18px rgba(63,38,95,.065)`；
  - 状态色：`--pudding-status-running / waiting / success / error`（值取 4.0 总表），替换各文件中散落的 `#ef4444`/`#22c55e`/`#d97706` 字面量。
- **验收**：grep 确认各 styles 文件不再出现散落硬编码状态色/圆角；ApprovalCard 的 `cardContainerStyle` 改为引用 token。

#### P0-5 ApprovalCard 并入 createStyles 体系（顺手）
- **组件**：`ApprovalCard.tsx`
- **做法**：把 `cardContainerStyle`/`headerStyle`/`titleStyle`/`argumentsPreStyle` 等内联对象迁移到独立 `approval.styles.ts`（createStyles），并复用 P0-4 的 token；按钮主操作（允许一次）移到最右（对齐飞书规范）。
- **验收**：ApprovalCard 与消息气泡共享 token；无内联样式残留（仅剩动态风险色与状态色）。

---

### 4.2 P1 — 中优先级（需要少量组件逻辑调整）

#### P1-1 连续消息时间窗口分组（Discord 式）
- **组件**：`MessageList.tsx`（投影阶段）或 `MessageRow.tsx`（渲染阶段）
- **做法**：在 `buildVirtualMessageItems` 时计算 `groupedWithPrevious`：同一 agent（或同一 user）且 `createdAt 间隔 < 2 分钟` 且角色相同 → 分组；分组内仅首条显示头像/名称/时间；分组末条气泡保持大圆角、组内条圆角收敛（已有 `agentBubbleGrouped` 基础，补齐「首条左上 5 → 组内 8 → 末条恢复 5」的圆角序列与组间距 `-4px` 的视觉连续性）。
- **验收**：连续快速消息视觉成「组」；间隔超过 2 分钟自动拆组。

#### P1-2 消息级错误状态升级：摘要 + 重试 + tooltip
- **组件**：`AgentMessageBubble.tsx`、`process.styles.ts`、`agent.styles.ts`
- **做法**：
  1. 错误态不再只靠红边：气泡内错误时，在正文位置渲染 `错误摘要行`（11–12px、#ef4444）：提取 `status==='error'` 时的 `content` 或 `processItems` 中失败项 message，用与 `summarizeQueueError` 相同的截断规则（≤80 字符 / 提取 message 字段），`title` 放全量原文；下方保留现有 `重试` 按钮（`processRetryBtn`）并加大到 12px 可点区域。
  2. 移除 `glitchShake` 抖动（或仅保留极轻的 1 次位移），避免「故障感」噪音。
  3. 错误摘要样式与队列徽标系统的 `composerQueueError` 视觉对齐（同字重/同缩进风格），确保两处「错误」长得像「同一种错误」。
- **验收**：气泡错误显示「为什么失败」摘要 + hover 全量 + 重试；浅/深色模式可读。

#### P1-3 等待态降噪
- **组件**：`WaitingBubble.tsx`、`agent.styles.ts`
- **做法**：三级收敛——首 token 前默认只显示「三点脉冲 + `agentName 正在运行` + 已等待计时」；≥10s 显示阶段文案（模型正在深入分析…）；≥30s 才展开 progress track；**删除开发者向的 `waitingHint` 整段文案**（或折叠进 tooltip）；`ParticleDots` 保留但降低粒子透明度。
- **验收**：等待气泡 3 秒内呈现轻量态；文案在 30s 内不超过 2 行。

#### P1-4 用户消息发送/失败状态增强
- **组件**：`UserMessageBubble.tsx`、`user.styles.ts`
- **做法**：发送中在时间旁显示小转圈图标（antd `LoadingOutlined` 12px）+ 保留「发送中...」；失败态（status==='error'）在气泡下加红色小字「发送失败」+ `title` 错误信息，并可复用 `MessageActions` 的复制按钮。
- **验收**：用户消息三种状态（发送中/成功/失败）视觉可区分。

#### P1-5 状态色与队列徽标联动说明（兼容性保障）
- 队列徽标系统位于 composer 区域（`MessageQueueDropdown.tsx`），本方案不移动它；但做三件事保证不打架：
  1. 气泡内错误用 **#ef4444 摘要**，队列 retrying 用 **#b36b1e 琥珀**，终态失败用 **#ef4444**——语义映射写入 4.0 总表，前端代码注释中引用；
  2. 气泡内**不新增第三个「重试中/排队中」徽标**——消息流只表达消息级状态（streaming/error/success），队列态交给 composer 徽标；如需在气泡感知「排队」，用 `agentStatusTag` 的弱化文案（如「排队中」灰底标签）而非彩色徽标；
  3. 计数语义（排队 N · 执行 M · 终态 K）保持 composer 专属，消息流不重复计数。

---

### 4.3 P2 — 增强项（低优先级 / 需要更多设计投入）

#### P2-1 消息可编辑（对齐 ChatGPT）
- 用户消息 hover 增加「编辑」入口，进入编辑态后本地重新生成分支（数据层需支持 edit-then-resend，纯前端先做乐观更新）。

#### P2-2 多 agent 分组头像
- 同 agent 连续消息用「头像堆叠」或「缩略头像 + 名称」；`AgentAvatar` 支持 `size="sm"`（24px）与多 agent 色环区分（`agentAvatarColors` 已有 5 色，可扩）。

#### P2-3 代码块行号 + 折叠
- 超长代码块（>12 行）默认折叠为 8 行 + 「展开」按钮；行号（右侧对齐、半透明）。

#### P2-4 动效收敛
- 统一入场动画为一种（保留 `messageGlowIn` 或 `messageBounceIn` 二选一）；完成粒子仅保留于「最终回答」场景（当前已是）；hover 上浮 `scale(1.006)` 与阴影二选一，避免「又动又浮」。

#### P2-5 输入区与消息流状态语言统一
- Composer 区域在排队非空时给发送按钮加琥珀小点；消息流底部（列表末尾）在排队期间显示细进度条（与徽标计数呼应），帮助用户理解「我发的消息排队了」。

#### P2-6 日期快速跳转
- 日期分隔线支持点击弹出「日期索引」，长会话快速定位（依赖 P0-1 的 divider 项）。

---

## 5. 一页要点速览（卡片式）

```
┌────────────────────────────────────────────────────────────────┐
│  PuddingAgent 消息卡片 UI — 设计要点速览（2026-08-13）           │
├────────────────────────────────────────────────────────────────┤
│ 【业界 7 产品共识】                                            │
│  • 头像分组：连续同人消息仅首条显头像（Discord 2min 规则）      │
│  • 时间戳：相对时间 + hover 完整时间 + 日期分隔线               │
│  • 气泡：己方右/填充色，AI 左/内容块；圆角收敛指向发送者       │
│  • 代码块：语言标签 + hover 复制按钮 + 深底高亮                 │
│  • 状态：typing 三点 / 流式光标 / Stop / 错误摘要+重试          │
│  • 操作：hover 全亮、键盘可达（focus-visible）                  │
│  • 层级：空间代替分隔线、单强调色、luminance 分层（Linear）     │
├────────────────────────────────────────────────────────────────┤
│ 【Pudding 现状亮点】                                           │
│  流式打字机+光标、停滞琥珀警告、完成粒子、过程时间线、虚拟滚动   │
│  已具备；双主题 token（--pudding-chat-*）体系完整。             │
├────────────────────────────────────────────────────────────────┤
│ 【核心差距】                                                   │
│  G5 代码块无语言标签/浅底  G6 错误无摘要  G7 错误双轨           │
│  G1/G2 时间戳无hover/无日期分隔线  G3/G4 操作按钮半透明/无键盘  │
│  G8 无时间窗口分组  G9 等待态过载  G10 token 未收敛             │
├────────────────────────────────────────────────────────────────┤
│ 【落地优先级】                                                 │
│  P0（纯样式、立即做）：时间戳hover+日期线 ｜ 操作按钮全亮+键盘   │
│     ｜ 代码块语言标签+hover复制 ｜ token收敛 ｜ 审批卡并入体系   │
│  P1（少量逻辑）：2min分组 ｜ 错误摘要+重试+tooltip ｜ 等待降噪    │
│     ｜ 用户消息状态增强 ｜ 与队列徽标语义映射                    │
│  P2（增强）：消息编辑 ｜ 多agent头像 ｜ 代码块行号/折叠          │
│     ｜ 动效收敛 ｜ composer-消息流状态联动                       │
├────────────────────────────────────────────────────────────────┤
│ 【队列徽标兼容红线】                                           │
│  ① 错误一律「摘要(≤80字符)+tooltip全量」，禁止红原文上屏       │
│  ② retrying=琥珀 #b36b1e，终态失败/气泡错误=红 #ef4444         │
│  ③ 消息流不新增第三个队列徽标，队列计数留在 composer           │
│  ④ 气泡内只表达消息级状态：streaming / error / success         │
└────────────────────────────────────────────────────────────────┘
```

---

*本文档为调研+设计建议，不修改任何源码。落地时按 P0 → P1 → P2 顺序实施，每步保持 `prefers-reduced-motion` 降级与测试契约（现有 `*.test.tsx` 的 data-testid 不做破坏性改动，样式类名优先新增而非替换）。*
