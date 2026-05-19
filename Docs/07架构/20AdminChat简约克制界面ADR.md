# 20 Admin Chat 简约克制界面 ADR

> 状态：**accepted**  
> 作者：@lead（综合 @ui-designer / @user-agent 只读评审输入）  
> 日期：2026-05-18  
> 范围：`/admin/chat` 页面视觉与交互信息架构，不改变后端契约  
> 关联：[06PuddingAgent与客户端](06PuddingAgent与客户端.md)、[14消息管线与终端代理与前端优化ADR](14消息管线与终端代理与前端优化ADR.md)、[18上下文缓存可观测性ADR](18上下文缓存可观测性ADR.md)、[../Features/PuddingUiUxRedesign](../Features/PuddingUiUxRedesign.md)、[../Features/ChatUIRedesign](../Features/ChatUIRedesign.md)、[../Features/AdminChat简约克制设计方案](../Features/AdminChat简约克制设计方案.md)

---

## 1. 背景

`/admin/chat` 当前已具备完整的 Agent Chat 骨架：

- 左侧会话列表；
- 顶部 Workspace / Agent 上下文选择；
- 中央消息流；
- Runtime Timeline：思考、记忆检索、工具调用、生成、最终答案；
- 底部 Agent Console：工具栏、输入框、状态栏、Token / 记忆 / 子 Agent 等状态入口；
- 开发者模式面板。

这些能力方向正确，但截图和当前实现暴露出两个核心问题：

1. **视觉权重过高**：Timeline、状态栏、紫色 glow、动效、底部多图标同时抢占注意力，使页面从“静谧书斋式 AI Agent”滑向“调试仪表盘”。
2. **可信度泄漏**：思考摘要中出现 `undefined用户问...` 这类内部拼接痕迹，直接破坏用户对 Agent 的信任。

本 ADR 的目标不是重做 Chat，也不是新增后端能力，而是为下一轮界面优化确立“简约克制”的决策边界。

---

## 2. 决策驱动因素

| 驱动因素 | 说明 |
|----------|------|
| Chat 是主舞台 | 用户打开页面的首要目标是输入意图并获得可靠结果，而不是观察运行时仪表盘。 |
| Agent 可解释性仍重要 | 运行过程不能完全黑箱，但应采用渐进披露，默认不打扰。 |
| 可信度优先于炫技 | 任何 `undefined` / `null` / 原始 prompt / payload 泄漏都应视为发布阻断项。 |
| 保持现有架构 | 本轮只做前端视觉、信息层级和文案治理，不改 SSE 协议、不改数据库、不新增 API。 |
| 可长期阅读 | 页面应适合长时间对话和阅读，降低持续动画、发光、玻璃拟态和高饱和状态色。 |

---

## 3. 方案对比

### 方案 A：保持当前“运行时透明”界面

- **做法**：继续默认展示 Runtime Timeline、底部多状态图标、技术状态与动效。
- **优点**：开发者可见性强，实现成本最低。
- **缺点**：普通用户负担重；视觉焦点被过程信息抢走；不符合“简约克制”。
- **结论**：不采纳。

### 方案 B：极简黑箱聊天

- **做法**：默认只显示用户消息、Agent 答案、输入框，隐藏全部过程信息。
- **优点**：最简洁，接近 ChatGPT 默认体验。
- **缺点**：削弱 Pudding 的 Agent 差异点；工具调用、记忆检索、长任务缺少安全感。
- **结论**：不采纳。

### 方案 C：简约默认态 + 过程渐进披露（采纳）

- **做法**：默认只显示一条“当前主状态”和最终答案；Runtime Timeline、Token、工具详情、开发者信息按需展开。
- **优点**：兼顾简洁、可信和 Agent 可解释性；复用现有组件；风险低。
- **缺点**：需要梳理状态映射、文案清洗和折叠逻辑。
- **结论**：采纳。

---

## 4. 决策

### ADR-020-A：默认界面采用“Quiet Chat”而非“Agent Console”

`/admin/chat` 默认视觉层级必须收敛为三层：

1. **当前上下文**：当前 Workspace、Agent、会话状态；
2. **消息流**：用户输入与 Agent 答案是主角；
3. **输入区**：输入框、发送/停止、少量必要入口。

底部的 Console 能力保留，但默认语义不再强调“Console”。普通用户看到的是“输入区”，高级状态进入“详情”或“开发者模式”。

### ADR-020-B：Runtime Timeline 默认折叠为“单一主状态”

生成过程中，用户默认只看到一个主状态：

- `正在整理上下文…`
- `正在查找相关记忆…`
- `正在调用工具…`
- `正在生成回复…`
- `已完成`
- `出错了，可重试`

完整 Timeline 节点只在用户点击“查看过程”或启用开发者模式时展开。

完成后，过程信息自动收束为一行弱提示，例如：

- `已参考 3 条记忆`
- `已调用 1 个工具`
- `已生成回答`

### ADR-020-C：所有用户可见文本必须经过可信度清洗

以下内容不得出现在默认用户界面：

- `undefined`
- `null`
- `NaN`
- 原始 prompt 拼接片段
- 原始 JSON / payload
- 内部枚举值或未翻译字段名
- 未清洗的工具输出

若摘要生成失败，宁可不展示摘要，也不能展示半成品。

这是一条 **P0 发布阻断门禁**。

### ADR-020-D：技术指标默认隐藏，必要状态保留

默认隐藏：

- Token 详细统计；
- 上下文窗口明细；
- Provider / 模型 / 连接细节；
- 潜意识 LLM、索引、ASP/LSP 等内部状态图标；
- 完整工具输出；
- Dev raw events。

默认保留：

- 当前 Agent / Workspace 弱可见；
- 输入是否可用；
- Agent 是否正在工作；
- 停止 / 中断按钮；
- 错误与恢复动作；
- 高风险动作确认。

### ADR-020-E：视觉基线采用“暖纸面 + 低饱和强调 + 节制动效”

主视觉采用 `warm beige` / `soft white` / `earth brown` 的书斋基调。

紫色只用于低频强调：

- 用户气泡；
- 当前焦点；
- 当前运行状态的少量提示。

限制：

- 常态页面紫色可见面积不超过主要视口的 **5%**；
- 同一屏幕持续循环动画不超过 **1 个**；
- 常态卡片不使用 glow；
- Glass / blur 只保留在浮层或 sticky 输入区域，主内容卡片采用接近实色的纸面；
- 动效优先使用 opacity / color / border / shadow，不使用持续扫描线或粒子背景。

### ADR-020-F：本轮不引入新后端能力

本 ADR 不要求：

- 新 SSE 事件；
- 新数据库字段；
- 新 API；
- 新工具调用协议；
- 新消息分支/引用/编辑能力。

已有 `ChatUIRedesign.md` 中的高级能力（分支、引用、编辑历史等）可继续作为长期方向，但不进入本轮“简约克制”优化的默认范围。

---

## 5. 影响面

| 文件 / 区域 | 影响类型 | 说明 |
|-------------|----------|------|
| `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts` | 样式收敛 | 降低 glow、particle、breathing、glass、紫色高饱和状态色。 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/MessageGroup.tsx` | Timeline 渐进披露 | 默认单一主状态；清洗 thinking 摘要；完成后收束为摘要。 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/InputArea.tsx` | 输入区降噪 | 默认突出输入框与发送/停止；次级工具收进更多菜单或浮层。 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ChatMain.tsx` | 顶部上下文克制化 | 上下文选择保留但视觉弱化；开发者模式入口保持低频。 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/StatusBarTokenIndicator.tsx` | 默认隐藏细节 | Token 详情进入 tooltip / popover / dev mode，不常驻抢焦点。 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMenu.tsx` | 操作聚合 | 低频操作保持右键或更多菜单，不默认铺开。 |

---

## 6. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| 过度隐藏导致用户觉得黑箱 | 长任务中焦虑 | 保留单一主状态；提供“查看过程”。 |
| 开发者调试效率下降 | 排障成本上升 | 开发者模式继续保留完整 Timeline、Raw Events、Token 详情。 |
| 文案清洗遗漏 | 信任受损 | 将 `undefined/null/NaN` 作为测试与人工验收阻断项。 |
| 简化后功能入口不易发现 | 高级功能使用率下降 | 使用“更多”菜单和 tooltip；保留快捷键或命令面板。 |
| 视觉收敛后品牌记忆变弱 | 个性下降 | 保留浅紫用户气泡、Pudding logo、暖纸面基调。 |

---

## 7. 验收标准

### 7.1 可信度

- 用户可见界面不得出现 `undefined`、`null`、`NaN`、原始 prompt、原始 payload。
- thinking / memory / tool 摘要为空或异常时，显示安全 fallback 或隐藏。
- 错误状态必须给出恢复路径：`重试` / `继续生成` / `查看详情` / `取消`。

### 7.2 默认信息层级

- 第一屏默认只有三块主区域：顶部上下文、消息流、输入区。
- 普通对话时同时可见的主状态不超过 1 个。
- Runtime Timeline 默认折叠；完成后自动收束为 1 行弱提示。
- 底部默认外露状态/工具入口不超过 5 个。

### 7.3 视觉克制

- 常态页面无明显粒子、扫描线、大面积发光或持续呼吸动画。
- 同屏持续循环动画 ≤ 1。
- 紫色可见面积 ≤ 5%。
- 正文对比度达到 WCAG 4.5:1。
- 次级文字不低于可读阈值，避免 `opacity < 0.55` 用于关键信息。

### 7.4 可访问性

- 图标按钮必须有 tooltip 和 `aria-label`。
- 焦点态必须可见。
- 支持 `prefers-reduced-motion`：关闭非必要动画。
- 可点击区域桌面端不小于 36px，触屏场景目标 44px。

---

## 8. 回滚策略

本轮不改变数据契约与后端协议，回滚可控制在前端样式和组件逻辑层：

1. 保留旧 Timeline 渲染分支，使用 feature flag 或 dev mode 进入完整过程视图；
2. 样式 token 改动集中在 `styles.ts`，必要时可逐项恢复；
3. 默认隐藏的状态组件不删除，只调整默认展示条件；
4. 若用户反馈信息不足，可提高“查看过程”入口显著性，而不是恢复全部常驻状态。

---

## 9. 结论

采纳 **方案 C：简约默认态 + 过程渐进披露**。

下一轮 `/admin/chat` 优化的关键词不是“炫酷”，而是：

- 安静；
- 可信；
- 单一主状态；
- 答案优先；
- 技术细节按需展开。

---

## 10. 实施状态

- **P0 可信度与默认折叠**：✅ 已实施 (2026-05-18)
  - MessageGroup.tsx: sanitizeDisplayText 清洗 undefined/null/NaN，Runtime Timeline 默认折叠为单一主状态，完成后过程自动收束，Token 用量默认隐藏
  - styles.ts: mainStatusLine/mainStatusDot/completionSummary/viewProcessLink 样式
- **P1 视觉降噪**：✅ 已实施 (2026-05-18)
  - styles.ts: 关闭 10 种常态动画，答案卡 glass→paper，紫色 18%→10%，状态色降饱和，添加 prefers-reduced-motion
- **P2 输入区重构**：✅ 已实施 (2026-05-18)
  - InputArea.tsx: STATUS_LABEL 去内部词，placeholder 简约化，状态栏收敛为"状态胶囊+更多"，8 个技术指标进入 Popover
- **P3 Design Tokens 统一**：📋 待排期（prefers-reduced-motion 已在 P1 完成）
