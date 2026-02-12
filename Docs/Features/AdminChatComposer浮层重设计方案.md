# Admin Chat Composer 浮层重设计方案

> 日期：2026-05-23  
> 页面：`/admin/chat`  
> 对象：底部 Composer 的 `+` Popover 与运行状态详情  
> ADR：[34ADR-033AdminChatComposer浮层重设计ADR](../07架构/34ADR-033AdminChatComposer浮层重设计ADR.md)

---

## 1. 设计目标

本方案用于指导 Dev 重做截图中被选中的 Composer 浮层。

目标不是新增能力，而是把现有信息重新分层：

1. `+` 是输入动作菜单，只回答“我可以给本轮对话添加什么、调整什么”。
2. 运行状态是独立状态入口，只回答“Agent 现在是否在工作、是否需要我处理”。
3. 技术指标进入详情或开发者模式，不再堆在普通用户第一层菜单里。

最终体验应从“窄长指标抽屉”变为“清晰、可执行、可解释的小型 Composer 控制区”。

---

## 2. 当前问题

当前 `InputArea.tsx` 的 `composerMenuContent` 直接把多类内容拼在一起：

- 禁用动作：上传附件、上传图片、思考强度；
- 可用动作：导出对话；
- 运行状态标题；
- 8 个技术指标组件：Token、思考强度、Provider、Token stats、ASP/LSP、索引、潜意识 LLM、SubAgent。

从截图看，主要问题如下：

| 问题 | 表现 | 影响 |
|------|------|------|
| 入口语义不一致 | 用户点 `+` 后看到运行状态 | 预期落差，增加认知负担。 |
| 内容过密 | 180px 左右窄浮层承载大量图标和文本 | 可读性差，无法快速判断重点。 |
| 可用动作被埋没 | `导出对话` 混在禁用项和指标之间 | 真正可点击项不明显。 |
| 内部词暴露 | ASP、索引、潜意识等直接出现 | 普通用户会误以为是异常或调试信息。 |
| 触达区偏小 | 多个小型状态组件纵向堆叠 | 不适合触屏，也不利于键盘焦点管理。 |

---

## 3. 目标信息架构

### 3.1 默认 Composer

```text
┌────────────────────────────────────────────────────────────┐
│ +  麦克风   输入你的问题或任务…                  [发送]    │
│            上下文 · 记忆 · 子任务 0        就绪 / 查看状态 │
└────────────────────────────────────────────────────────────┘
```

说明：

- `+` 只打开动作菜单；
- 状态文案独立于 `+`；
- 生成中、工具调用、错误时状态行可点击打开状态详情；
- 空闲且无异常时，状态详情入口可以弱化或隐藏。
- 上下文、记忆、子代理等原本被隐藏的指示器，以低权重反馈带表达“参与情况”，而不是重新堆回菜单里。

### 3.2 `+` 动作菜单

```text
┌──────────────────────────────┐
│ 添加到本轮                    │
│  [附件] 即将开放              │
│  [图片] 即将开放              │
│                              │
│ 本轮设置                      │
│  思考强度        自动         │
│                              │
│ 会话                          │
│  导出对话                     │
└──────────────────────────────┘
```

设计要求：

- 宽度建议 260-300px；
- 分区标题弱化，动作项高度 36px 起；
- 可用项和禁用项视觉差异清楚；
- `导出对话` 可单独放在会话区，不再被指标挤压；
- 禁用项保留 `即将开放` 标签，避免用户以为功能坏了。

### 3.3 运行状态详情

```text
┌──────────────────────────────┐
│ 运行状态                      │
│ ● 正在生成回复…               │
│                              │
│ 本轮摘要                      │
│ Token    1.2k / 32k           │
│ 记忆      已参考 3 条          │
│ 工具      正在执行 1 个        │
│ 子任务    0 个运行中           │
│                              │
│ 模型服务  可用                 │
│ 后台整理  待机                 │
│ 索引      未启用               │
│                              │
│ [打开开发者详情]               │
└──────────────────────────────┘
```

设计要求：

- 面向普通用户使用自然语言；
- 默认不显示 ASP/LSP、Subconscious LLM 等内部缩写；
- 指标只展示摘要，不展示原始 payload；
- 开发者模式可以从底部入口进入完整诊断；
- 错误状态在顶部显示恢复动作。

---

## 4. 组件拆分建议

### 4.1 目标组件树

```text
InputArea
├── ComposerStatusLine
│   ├── ComposerFeedbackStrip
│   └── ComposerStatusDetailsPopover
├── ComposerActionMenuPopover
│   └── ComposerActionMenu
└── ComposerInputFrame
    ├── ComposerToolRail
    │   ├── ComposerActionMenuButton
    │   └── VoiceInputButton
    ├── Input.TextArea
    └── ComposerSubmitRail
        └── Send / Stop Button
```

### 4.2 新增组件

| 组件 | 文件建议 | 职责 |
|------|----------|------|
| `ComposerActionMenu` | `components/ComposerActionMenu.tsx` | 渲染 `+` 动作菜单，不读取运行时指标。 |
| `ComposerStatusLine` | `components/ComposerStatusLine.tsx` | 渲染自然语言主状态，可点击打开详情。 |
| `ComposerStatusDetails` | `components/ComposerStatusDetails.tsx` | 渲染运行状态摘要，统一翻译内部指标。 |
| `ComposerFeedbackStrip` | `components/ComposerFeedbackStrip.tsx` | 用轻标签 / 状态点表达上下文、记忆、索引、子代理等参与情况。 |
| `ComposerInputFrame` | 可先内联在 `InputArea.tsx` | 管理三段式输入布局、focus / typing / error 状态。 |

若希望控制改动规模，可以先只新增 `ComposerActionMenu.tsx` 与 `ComposerStatusDetails.tsx`，状态行仍留在 `InputArea.tsx` 中。

### 4.3 视图模型

建议在 `InputArea.tsx` 内部先组装一个轻量视图模型，避免每个指标组件各自决定文案：

```ts
interface ComposerRuntimeSummary {
  status: ChatStatus;
  statusLabel: string;
  token?: {
    used: number;
    limit: number;
    percentage: number;
  };
  contextService: 'available' | 'idle' | 'disabled' | 'error';
  memory: 'used' | 'idle' | 'disabled' | 'error';
  memoryCount?: number;
  index: 'available' | 'building' | 'disabled' | 'error';
  backgroundMemory: 'idle' | 'running' | 'disabled' | 'error';
  subAgentsRunning: number;
  modelService: 'available' | 'warning' | 'error';
}
```

第一阶段不要求后端新增字段，可以由现有 props 和 indicator 默认值映射生成。缺数据时显示 `未启用` 或隐藏对应行，不显示 `undefined`。

---

## 4.4 轻反馈带设计

很多指示器被隐藏，是因为旧状态栏的视觉重量过高，而不是这些信息没有价值。新的策略是：**不恢复重型图标栏，改用可扫读的低权重反馈带**。

### 反馈层级

| 层级 | 展示方式 | 用途 |
|------|----------|------|
| L1 主状态 | `正在生成回复…` | 告诉用户当前主流程。 |
| L2 轻反馈带 | `上下文 · 记忆 · 子任务 0` | 告诉用户哪些能力参与了本轮。 |
| L3 状态详情 | Popover 摘要表 | 告诉用户每项能力的结果。 |
| L4 开发者详情 | DevPanel / 原指标组件 | 排障与内部诊断。 |

### 轻反馈带样式

```text
上下文  记忆  子任务 0
```

规格：

- 高度 18-22px；
- 字号 11-12px；
- 颜色使用 `earth-brown` 45%-60% opacity；
- 激活项使用低饱和色点，不使用发光；
- 异常项只把对应标签变为低饱和红，不整条报错；
- 最多显示 4 项，超出折叠为 `更多`；
- 点击任意标签打开状态详情。

### 能力映射

| 能力 | 空闲 | 参与 | 运行中 | 异常 |
|------|------|------|--------|------|
| 上下文 | 隐藏或 `上下文` 弱态 | `上下文` | `整理上下文…` | `上下文异常` |
| 记忆 | 隐藏 | `记忆 N` | `检索记忆…` | `记忆不可用` |
| 索引 | 隐藏 | `索引` | `索引中…` | `索引异常` |
| 子代理 | `子任务 0` 可隐藏 | `子任务 N` | `子任务 N 运行中` | `子任务异常` |
| 后台整理 | 隐藏 | `后台` | `后台整理中` | `后台异常` |

设计判断：普通用户不需要知道 ASP/LSP 和 Subconscious LLM，但需要知道“上下文、记忆、子任务是否参与”。这能保留 Agent 的可解释性，同时不把页面拉回调试台。

---

## 5. 视觉规格

### 5.1 Popover 容器

| 属性 | 建议 |
|------|------|
| 宽度 | 260-300px，窄屏 `calc(100vw - 24px)` |
| 圆角 | 8px |
| 背景 | `var(--pudding-surface, #fffefa)` |
| 边框 | `var(--pudding-line, rgba(92,74,58,0.16))` |
| 阴影 | `0 8px 24px rgba(0,0,0,.08)` |
| 内边距 | 8px |
| 分区间距 | 8-10px |

### 5.2 菜单项

| 状态 | 规格 |
|------|------|
| 默认 | 高度 36px，左右 10-12px，图标 16px，文字 13px |
| hover | 背景 `earth-brown 5-6%`，不放大 |
| focus | 2px 可见焦点环或内描边 |
| disabled | opacity 0.45，保留说明标签 |
| touch | 触屏断点高度 44px |

### 5.3 状态详情行

```text
Label           Value
Token           1.2k / 32k
记忆             已参考 3 条
工具             正在执行 1 个
```

规格：

- label 使用 12px muted；
- value 使用 12-13px primary；
- 数字使用 tabular-nums；
- 错误用低饱和红，不使用大面积红底；
- 运行中只允许一个小圆点或轻微 opacity 动效。

### 5.4 输入容器

当前截图中的主要视觉问题是 focus 样式画在 `textarea` 底部，形成一条过强的紫色横线；多行时右侧发送按钮被 `textarea` 的高度和滚动挤压，像被藏到容器边缘。

新 Composer 使用一个完整输入容器承载状态，而不是让 `textarea` 自己表现所有状态：

| 状态 | 容器表现 |
|------|----------|
| resting | 暖白纸面、1px 暖棕弱边框、无阴影 |
| focus | 边框提升到 `accent-purple 18%-22%`，阴影 `0 2px 10px rgba(92,64,42,.05)` |
| typing | 底部出现短而轻的 ink-line，从输入区左侧 20%-80% 轻扫一次 |
| loading | 状态点轻微 opacity pulse，发送按钮变停止按钮 |
| error | 边框变低饱和红，状态行显示恢复动作 |

禁止：

- 整条高饱和紫线横跨输入区；
- 持续高亮边框；
- 按钮 hover / focus 导致布局位移；
- 多行输入时按钮被滚动条、文本或容器裁切遮挡。

### 5.5 输入中动效

动效命名建议：`composerInkWake`。

触发条件：

- `textarea` focus；
- 或 `inputValue.trim().length > 0`；
- 不在 `loading` / `disabled` / `error` 中。

表现：

- 容器边框 180ms 过渡；
- 输入区底部一条 24%-36% 宽的浅色 ink-line 轻微滑动或淡入；
- 动效时长 220-300ms；
- 用户连续输入时不每个字符重启大动画，只保持 subtle active 状态；
- `prefers-reduced-motion: reduce` 下禁用滑动，只保留边框变化。

CSS 方向：

```css
.composerInputFrame[data-active='true'] {
  border-color: color-mix(in srgb, var(--accent-purple) 20%, var(--earth-brown) 12%);
  box-shadow: 0 2px 10px rgba(92, 64, 42, 0.05);
}

.composerInputFrame[data-active='true']::after {
  opacity: 1;
  transform: translateX(0);
}
```

### 5.6 多行布局

改为三段式 grid，而不是当前单行 `align-items: flex-end`：

```text
┌────────────────────────────────────────────────────┐
│ [ + ][麦]  ┌──────────────────────────────┐ [发送] │
│            │ 第一行文本                    │        │
│            │ 第二行文本                    │        │
│            └──────────────────────────────┘        │
│            上下文 · 记忆 · 子任务 0          状态   │
└────────────────────────────────────────────────────┘
```

布局规格：

- `grid-template-columns: auto minmax(0, 1fr) auto`；
- 左工具区固定宽度，`align-self: end`；
- 中央输入区 `min-width: 0`，避免撑破布局；
- 右操作区固定宽度，`align-self: end`，始终可见；
- `textarea` `max-height: 132px` 或 `160px`，超出内部滚动；
- Composer 容器整体 `overflow: visible`，textarea 内部 `overflow-y: auto`；
- 发送按钮 32x32 桌面，触屏断点 40-44px；
- 输入框和按钮之间至少 8px gap。

多行状态验收：

- 2 行、5 行、10 行文本都能看到发送按钮；
- 内部滚动条只出现在文本区，不压住按钮；
- 光标、紫色 focus 反馈不穿过按钮；
- 输入容器高度增长但页面底部不出现黑色裁切或布局跳动。

---

## 6. 交互规则

### 6.1 `+` 菜单

- 点击 `+` 打开动作菜单；
- 点击可用动作后关闭菜单；
- 点击禁用动作不关闭菜单，Tooltip 显示 `即将开放`；
- Esc / 外部点击关闭；
- 关闭后焦点返回 `+` 按钮。

### 6.2 状态详情

- `thinking` / `tool_executing` / `streaming` / `error` / `completedVisible` 时显示状态行；
- 状态行可点击打开详情；
- `error` 状态详情顶部显示恢复动作：`重试`、`查看开发者详情`；
- `idle` 时可隐藏状态详情入口，只保留弱状态或不显示。

### 6.3 开发者模式

- 普通模式只看摘要；
- 开发者模式可在状态详情底部展开完整指标；
- 若项目已有 DevPanel 开关，则 `打开开发者详情` 复用该入口，不新增全局状态。

### 6.4 输入状态

- focus 进入时，Composer 容器进入 `active` 状态；
- blur 且输入为空时，Composer 回到 `resting`；
- 输入非空时，即使 blur 也保留轻微 active 纸面状态；
- loading 时禁用 typing 动效，避免与生成状态争抢；
- error 时优先显示错误边框和恢复动作；
- Enter 发送后，若输入清空，容器回到 resting。

---

## 7. Dev 施工步骤

### Phase 1：拆分动作菜单

1. 在 `InputArea.tsx` 中移除 `composerMenuContent` 对运行状态指标的直接渲染。
2. 新增 `ComposerActionMenu.tsx`，只接收：
   - `onExport`
   - `onClose`
   - 未来动作的 disabled 状态。
3. 保留 `Popover placement="topLeft"`，但给 Popover 内容增加统一 className。
4. 为 `+` 按钮补充 `aria-label="打开输入动作菜单"`。

验收：点击 `+` 后不再出现 `运行状态`、ASP、索引、潜意识等指标。

### Phase 2：新增状态详情入口

1. 将当前 `composerStatusLine` 扩展为可点击状态胶囊。
2. 新增 `ComposerStatusDetails.tsx`，消费 `tLimit`、`tUsed`、`tPct`、`cacheHitTokens`、`cacheMissTokens`、`cacheHitRate`、`status`、`sessionId` 等现有数据。
3. 使用自然语言映射内部状态。
4. 缺省或未知状态显示安全 fallback，不渲染空值。

验收：生成中点击状态行能看到状态摘要；空值不显示 `undefined/null/NaN`。

### Phase 3：技术指标降级到开发者详情

1. 普通 `ComposerStatusDetails` 只展示摘要。
2. 现有 `StatusBarTokenIndicator`、`ProviderBalanceIndicator`、`AspLspIndicator`、`IndexIndicator`、`SubconsciousLlmIndicator`、`SubAgentIndicator` 可继续复用在开发者详情或 DevPanel。
3. 如果暂时没有 DevPanel 联动，保留一个低权重链接按钮，点击后打开现有开发者模式入口。

验收：普通模式第一层不出现内部缩写；开发者仍能找到完整指标。

### Phase 4：样式与可访问性

1. 在 `styles.ts` 增加：
   - `composerPopover`
   - `composerMenuSection`
   - `composerMenuSectionTitle`
   - `composerMenuItem`
   - `composerMenuItemDisabled`
   - `composerStatusPill`
   - `composerStatusDetails`
   - `composerStatusDetailRow`
2. 移除 Popover 内联样式，避免后续维护分叉。
3. 补齐 Tooltip、`aria-label`、焦点态和移动端尺寸。
4. 加 Playwright 或手动验收截图：桌面 986x1270、移动 390x844。

验收：无文本重叠、无横向溢出、键盘可操作、点击目标达标。

### Phase 5：输入框状态与多行布局修复

1. 在 `InputArea.tsx` 中增加本地状态：
   - `isComposerFocused`
   - `isComposerActive = isComposerFocused || inputValue.trim().length > 0`
2. 将当前 `composerRow` 改为 `composerInputFrame`：
   - 左侧 `composerToolRail`
   - 中央 `composerTextareaWrap`
   - 右侧 `composerSubmitRail`
3. 将 focus 样式从 `composerTextarea &:focus boxShadow` 移到 `composerInputFrame[data-active]`。
4. 发送 / 停止按钮固定在 `composerSubmitRail`，不参与 textarea 内部滚动。
5. `textarea` 设置稳定 max height 和内部滚动，不让父容器裁切按钮。
6. 增加 `composerFeedbackStrip`，先用现有 summary 默认值展示 `上下文`、`记忆`、`子任务`，后续再接真实数据。
7. 补充 `prefers-reduced-motion`，关闭 `composerInkWake`。

验收：截图中的多行输入、紫色横线过重、发送按钮被隐藏三类问题全部消失。

---

## 8. 文案规范

### 8.1 动作菜单

| 文案 | 说明 |
|------|------|
| 添加到本轮 | 附件、图片、文件引用分区 |
| 本轮设置 | 思考强度等仅影响当前输入的设置 |
| 会话 | 导出对话等会话级动作 |
| 即将开放 | 禁用项标签 |

### 8.2 状态详情

| 内部状态 | 普通文案 |
|----------|----------|
| `idle` | `就绪` |
| `thinking` | `正在整理上下文…` |
| `tool_executing` | `正在调用工具…` |
| `streaming` | `正在生成回复…` |
| `completed` | `已完成` |
| `error` | `出错了，可重试` |
| ASP/LSP | `上下文服务` |
| Index | `索引` |
| Subconscious LLM | `后台记忆整理` |
| SubAgent | `子任务` |

禁止普通模式直接显示：

- `ASP`
- `LSP`
- `Subconscious LLM`
- `Provider balance`
- 原始 session id
- 原始 JSON / payload
- `undefined` / `null` / `NaN`

---

## 9. 验收清单

### 信息架构

- [ ] `+` 菜单只包含输入动作、输入设置、会话动作。
- [ ] 运行状态不再作为 `+` 菜单分区。
- [ ] 技术指标不在普通 `+` 菜单里出现。
- [ ] 可用动作与禁用动作视觉区分清楚。

### 可用性

- [ ] `导出对话` 可一眼识别且可点击。
- [ ] 禁用项显示 `即将开放`。
- [ ] 状态行可打开运行状态详情。
- [ ] 错误状态提供恢复动作。

### 可访问性

- [ ] `+`、语音、发送/停止按钮都有 `aria-label`。
- [ ] Popover 可 Esc 关闭。
- [ ] 焦点态可见。
- [ ] 桌面点击目标不小于 36px。
- [ ] 触屏点击目标不小于 44px。

### 视觉

- [ ] Popover 不再是窄长指标堆叠。
- [ ] 内容无重叠、无横向滚动。
- [ ] 与暖纸面、低饱和、8px 圆角体系一致。
- [ ] 常态无发光、扫描线、多点 pulse。
- [ ] 输入 focus 不再显示高饱和整条紫色横线。
- [ ] 输入中有轻微、短时、可关闭的容器动效。
- [ ] 多行输入时发送 / 停止按钮始终可见。
- [ ] 文本区滚动条不遮挡按钮和状态反馈。
- [ ] 上下文、记忆、子代理等能力有轻反馈入口。

### 回归

- [ ] 发送、停止、输入、语音按钮不受影响。
- [ ] `/` 命令面板仍可正常打开。
- [ ] 导出对话回调仍执行。
- [ ] 开发者仍能查看完整运行指标。

---

## 10. 非目标

本轮不做：

- 附件上传后端；
- 图片上传后端；
- 新思考强度 API；
- 新运行时指标 API；
- 新 SSE 事件；
- 新数据库字段；
- 消息流、Timeline 或 DevPanel 大规模改造。

---

## 11. 交付判断

重新打开 `/admin/chat` 并点击输入框左侧 `+` 后，理想结果是：

> 用户看到的是“可以对本轮输入做什么”，而不是“系统内部正在怎么运行”。

如果菜单里仍然第一层展示 ASP、索引、潜意识、Provider、Token 明细等内部指标，本次重设计未完成。
