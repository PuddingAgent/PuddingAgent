# goal.md 归档：deepseek-harness 对齐任务（2026-08-14）

归档时间：2026-08-14（心跳自维护，goal.md 超 16KB 读取上限，归档历史 + 精简）

## 背景

用户指令（三件事，按优先级）：
1. 【P0 先做】前端消息卡片、消息输出参考 deepseek-harness（E:\github\deepseek\deepseek-harness）——由我和蜜糖协作
2. 【P0】对齐工具设计（参数/契约/实现算法流程）
3. 【P3】软件架构"一切皆是插件"

顺序先做 1。分工：我=调研+设计方案，蜜糖=评审+前端实现（沿用惯例）。

重要背景：DeepSeek API 峰谷定价（高峰 9:00-12:00、14:00-18:00 北京时间，其余空闲时段半价，8/17 生效）→ 需把重要工作移到夜间，需完善"主动工作能力 + 定时任务能力"。

## 已摸清 deepseek-harness 前端结构（pnpm monorepo，packages/client/ 下）

- ui-conversation：会话/消息卡片（MessageItem/AssistantMarkdown/ChatView/TurnTailNodeView/ReasoningRow/StatsLine/message-chrome/turn-metrics/QueueDock）
- ui-primitives：markdown/CodeBlock/MarkdownText/MessageText/JsonBlock/StateDot 原语
- ui-tool：工具卡片 ToolCallTree/ToolRow/toolviews/models
- ui-attachment：MessageImage/ImageLightbox

关键设计：
- message-chrome.ts：formatMessageClock（当天 HH:mm/今年 clock.md/跨年 clock.ymd）+ formatRunDuration + formatLatencySeconds + formatTokensPerSecond
- MessageItem 用户气泡右对齐 + clock+copy IconActions
- 错误/提示用 StateDot(error/warning) + 摘要文案（TurnErrorItem/TurnMaxTokensItem），非纯红边
- ModelRetry 有倒计时可展开 details

## 设计文档交付

Docs/deepseek-harness-message-card-alignment-2026-08-14.md 已验收落盘（10 维度 D1-D10 + G1-G12 差距 + P0/P1/P2 分级）。
交付蜜糖评审（deliveryId e3e3123e）。

## 评审 PASS + 4 裁决

1. P1-1 共存：新增行优先不替换 MessageProcessSummary；ToolCallRow 用 processItems 按 id/timestamp 配对；subCalls 递归复用 SubAgentCardMap，无新数据契约
2. P0-3 code-bg：加 --pudding-chat-code-bg 双主题 token（浅色深灰蓝非纯黑）；AA 4.5:1 入验收
3. 数据契约：
   - P2-2 StatsLine 提级"前端派生版先做"（ConversationProcessSummary 已有 durationMs/toolCalls/thinkingSteps + item timestamp 差分派生；tok/s=usage÷流式时长；cacheHitRate session 级现成）
   - P1-2 ModelRetry：后端无重试事件契约（ChatMessageBlock 无 attempt/delay/deadline）→ 前端条件渲染；"模型重试事件契约"列我（6a8）后端 P2 评估
4. P2-4 dock 不引入（与 MessageQueueDropdown 队列徽标重叠）；D8 价值用现存形态小优化吸收（单条排队时面板头直显 preview）

三分类微调 2 处：P1-5 用户图先做/助手图后做（助手图=markdown 内联刚随混排修复定型）；P0-5 并入蜜糖 backlog。

## P0 前端全量入库（蜜糖 4 commit，本地未 push，时机自管）

- 6b13d33 批A: StateDot 4态组件 + 状态色字面量收敛 + --pudding-chat-code-bg 双主题 token
- b3ae8d7 fix: 补 --pudding-status-warning（批A遗漏）
- 4d51195 批B clean: summarizeError util(9单测) + 代码块深底/sticky banner/复制1s反馈 + MessageActions copy反馈
- 7724dde 批B接线: AgentMessageBubble 错误摘要行(StateDot+summarizeError+title全量+重试按钮合并) + 5用例

验证：jest 全绿（StateDot6/summarizeError9/MessageActions6/AgentMessageBubble25/MessageItem回归），tsc 改动文件零新增。

## ⚠️ 边界事件（重要）

工作区有第三方活跃并发改动（SubAgentActivityDock/SubAgentRun 后端+前端、MessageQueueDropdown*、api.ts 等，疑似用户本人开发）。蜜糖用 hunk 分离提交（git apply --cached）。我侧后续 commit 同样必须 hunk 分离，只裹自己 hunks，不裹用户并发改动。

## 后端 delta 清单（我 6a8，均 P2 待命）

1. 模型重试事件契约（P1-2）：DirectLlmClient 有完整重试 loop（EffectiveMaxRetries 默认 2），产出 retry summary 文本。前端文本嗅探条件渲染先做；后端结构化 attempt/delay 字段排 P2
2. StatsLine per-step（P2-2）：ProcessSummaryItem 有 Timestamp（可差分派生），但无 token 字段。前端派生版可行；tok/s 需另取 usage；per-step DurationMs 排 P2 单字段增强

## 当前态势（归档时）

- P1-3 运行态降噪 sub-8c9fd678 / P1-4 用户消息按钮/失败态 sub-e4b0c132 并行在途（蜜糖）
- P1-1 工具行最大项排其后（蜜糖）
- P2 两后端需求（我）待命不变
