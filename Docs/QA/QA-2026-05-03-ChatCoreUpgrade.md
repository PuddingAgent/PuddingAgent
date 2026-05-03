# QA 审阅报告：Task-UI-01 — Chat 核心体验升级

**审阅日期**: 2026-05-03  
**变更主题**: 聊天界面核心体验要素补齐（时间戳、重试、骨架屏、欢迎引导）  
**审阅者**: QA-Sonnet (Claude Sonnet 4.6)  
**开发者**: @dev (GPT-5.3-Codex)  
**结论**: **PASS_WITH_NOTES** — 无 P0/P1 阻断问题，6 条 AC 全部通过，存在 4 个 P2 改进建议

---

## 一、审阅范围

| 文件 | 路径 | 变更类型 |
|------|------|----------|
| Chat 页面 | `Source/PuddingPlatformAdmin/src/pages/chat/index.tsx` | **新建** (810 行) |

---

## 二、验收标准逐条验证

### AC1 ✅ 相对时间戳 + hover 精确时间

**实现**: 每条消息气泡下方的 `messageMeta` 区域显示 `formatRelativeTime()` 结果，外层包裹 `<Tooltip>` 展示 `YYYY-MM-DD HH:mm:ss` 精确时间。

```tsx
<Tooltip title={dayjs(msg.timestamp).format('YYYY-MM-DD HH:mm:ss')}>
  <Text className={styles.timeText}>{formatRelativeTime(msg.timestamp)}</Text>
</Tooltip>
```

- 相对时间逻辑: 刚刚 (<1min) / X分钟前 (<60min) / X小时前 (<24h) / MM-DD HH:mm (≥24h)
- 用户消息和 Agent 消息均显示时间戳 ✅
- "发送中"状态额外显示 "发送中..." 文本 ✅

### AC2 ✅ 5 分钟间隔时间分隔线

**实现**: 渲染消息列表时，检查相邻消息的时间差。

```tsx
{idx > 0 && msg.timestamp - messages[idx - 1].timestamp > 5 * 60 * 1000 && (
  <div className={styles.timeDivider}>
    <span className={styles.timeDividerText}>—— {dayjs(msg.timestamp).format('HH:mm')} ——</span>
  </div>
)}
```

- 阈值正确: `5 * 60 * 1000 = 300000ms` = 5 分钟 ✅
- 分隔线样式: CSS `::before/::after` 伪元素绘制横线，中间显示 HH:mm 时间 ✅
- 仅当 idx > 0 时检查，首条消息不会误触发 ✅

### AC3 ✅ 发送失败保留原文 + 错误状态 + 重新发送

**实现**:
1. 消息 `status` 字段支持 `'error'` 状态
2. 失败时 `catch` 块将用户消息 status 设为 `'error'`，保留 `text`
3. 气泡样式：`msg.status === 'error' && styles.errorBubble`（红色边框）
4. "重新发送" 按钮仅在 `status === 'error' && role === 'user'` 时显示
5. `handleRetry` 调用 `sendMessage(message.text, message.id)` 重试

```tsx
{msg.status === 'error' && msg.role === 'user' && (
  <Button type="link" size="small" className={styles.retryButton}
    onClick={() => handleRetry(msg)}>重新发送</Button>
)}
```

- 原文保留 ✅
- 错误视觉反馈（红色边框） ✅
- 重试按钮出现条件正确 ✅
- 重试时 loading 保护（`if (!text || loading) return`）防止并发重试 ✅

### AC4 ✅ 骨架屏（Skeleton）替代 Spin

**实现**: 3 条 `Skeleton.Button`，分别模拟用户短气泡 (30%)、Agent 长气泡 (70%)、Agent 中气泡 (50%)，仅在 `loading` 为 true 时渲染。

```tsx
{loading && (
  <div className={styles.loadingRow}>
    <div className={cx(styles.skeletonRow, styles.skeletonRight)}>
      <div className={styles.skeletonShort}>
        <Skeleton.Button active block className={styles.skeletonBubble} />
      </div>
    </div>
    <div className={cx(styles.skeletonRow, styles.skeletonLeft)}>
      <div className={styles.skeletonLong}>
        <Skeleton.Button active block className={styles.skeletonBubble} />
      </div>
    </div>
    <div className={cx(styles.skeletonRow, styles.skeletonLeft)}>
      <div className={styles.skeletonMedium}>
        <Skeleton.Button active block className={styles.skeletonBubble} />
      </div>
    </div>
  </div>
)}
```

- 3 条气泡轮廓模拟真实对话 ✅
- `active` 属性启用动画 ✅
- 无 Spin 组件 ✅
- 气泡宽度变化 (30%/70%/50%) 模拟对话自然形态 ✅

### AC5 ✅ 欢迎引导页

**实现**: 当 `!agentId && !error` 时渲染 `onboardingState` 区块。

```tsx
{!agentId && !error && (
  <div className={styles.onboardingState}>
    <img src="/admin/assets/images/logo.png" ... />
    <Title level={2}>你好，我是布丁 👋</Title>
    <Text className={styles.onboardingSubtitle}>选择一个场景和 Agent，开始对话吧</Text>
    <div className={styles.promptList}>
      {QUICK_PROMPTS.map(...)}
    </div>
  </div>
)}
```

- Logo 路径 `/admin/assets/images/logo.png` ✅（与 UmiJS `base: '/admin/'` 对齐，文件存在于 `public/assets/images/logo.png`）
- 能力简介文案 ✅
- 4 个快捷提示词（在 3-5 个范围内） ✅
- 当 Agent 已选择且有消息时，引导页自动隐藏 ✅

### AC6 ✅ 快捷提示词填入输入框

**实现**: 点击 Card 时 `setInputValue(prompt)`。

```tsx
<Card hoverable onClick={() => setInputValue(prompt)}>
  {prompt}
</Card>
```

- 填入输入框（非直接发送） ✅（任务描述 "填入输入框或直接发送" 中 "或" 表示二选一均可）
- Card 有 hoverable 效果 + 过渡动画 ✅

---

## 三、代码质量检查

### 3.1 安全性 ✅

| 检查项 | 结果 |
|--------|------|
| `dangerouslySetInnerHTML` | **未使用** — 消息文本通过 `{msg.text}` 渲染，React 自动转义 ✅ |
| XSS 向量 | **无** — 无 HTML 注入、无 innerHTML 操作 ✅ |
| API 调用 | `sendAdminChatMessage` 通过封装的 `request()` 函数，无裸 fetch ✅ |

### 3.2 调试残留 ✅

- `console.log` / `debugger`: **0 个匹配** ✅
- 注释风格: 清晰的中文分段注释，无临时调试注释 ✅

### 3.3 架构边界 ✅

| 检查项 | 结果 |
|--------|------|
| 依赖方向 | UI(pages/chat) → API Service(services/platform/api)，无逆向引用 ✅ |
| 状态管理 | `useState` + `useRef` 仅限本页，无跨组件状态泄露 ✅ |
| 路由 | 通过 `@umijs/max` 的 `history` 导航，符合 Umi 规范 ✅ |

### 3.4 样式一致性 ✅

- 使用 `antd-style` 的 `createStyles` 符合项目规范 ✅
- `token.color*` 变量适配主题切换 ✅
- 用户气泡主色 `#6366f1` 与 `defaultSettings.ts` 中 `colorPrimary` 一致 ✅
- 无内联 `style={{}}` 对象 ✅

### 3.5 异步处理 ✅

- `CancellationToken`: 通过 `active` 标志变量模拟取消（React 中标准做法） ✅
- 竞态条件: `useEffect` cleanup 函数设置 `active = false` ✅
- 错误处理: try/catch 覆盖所有 API 调用 ✅

---

## 四、边界情况覆盖

| 边界情况 | 处理方式 | 结果 |
|----------|---------|------|
| 无可用场景 | 显示 error Alert "没有可用场景" | ✅ |
| 无可用 Agent | 显示 error Alert + 欢迎引导页 | ✅ |
| Agent 已选但无消息 | 显示 "开始和 Agent 对话吧" | ✅ |
| 单条消息（无时间分隔线） | `idx > 0` 条件正确跳过 | ✅ |
| API 返回 `reply: undefined` | 降级文本 "(Agent 未返回可展示文本)" | ✅ |
| 快速连续发送 | `loading` 状态互斥锁阻止 | ✅ |
| 重试期间禁止新发送 | `loading` 状态保护 | ✅ |
| 切换场景/Agent | 重置对话 `resetConversation()` | ✅ |
| 空输入 | `handleSend` 中 trim 检查 | ✅ |

---

## 五、发现的问题

### P2-1: 缺少 `useMemo` / `useCallback` 优化 (性能)

`workspaceOptions`、`agentOptions` 在每次渲染时重新计算，`handleSend`、`handleRetry`、`handleKeyDown` 等函数每次渲染重新创建。

**建议**:
```tsx
const workspaceOptions = useMemo(() => workspaces.map(...), [workspaces]);
const agentOptions = useMemo(() => agents.map(...), [agents]);
const handleSend = useCallback(() => { ... }, [inputValue, loading, workspaceId, agentId]);
```

**影响**: 低 — 当前页面组件树不深，无大量子组件，但若未来拆分子组件会引发不必要的重渲染。

---

### P2-2: `skeletonBubble` 样式耦合 Ant Design 内部类名

```tsx
skeletonBubble: {
  '& .ant-skeleton-button': { ... }
}
```

**风险**: Ant Design 大版本升级可能变更 `.ant-skeleton-button` 类名。

**建议**: 使用 `Skeleton.Button` 的 `style` prop 直接传入，或通过 `className` 结合 antd-style 的 `cx` 控制。

---

### P2-3: 相对时间缺少「昨天」语义

`formatRelativeTime` 在 ≥24h 时直接 fallback 到 `MM-DD HH:mm`。

**建议**: 增加 `diffHours < 48` 时显示「昨天 HH:mm」的判断，提升可读性。

---

### P2-4: 快捷提示词硬编码，缺少后端对齐

```tsx
const QUICK_PROMPTS = [
  '帮我写一份工作周报',
  '解释这段代码的作用',
  ...
];
```

**风险**: 如 task 中警告所述，"快捷提示词需要定义能力边界描述，需与后端 Agent 能力对齐"。硬编码的提示词可能不适用于所有 Agent。

**建议**: 考虑从 Agent metadata 中动态获取推荐提示词（非本次任务范围，可作为后续优化）。

---

## 六、综合判定

| 维度 | 评分 | 说明 |
|------|------|------|
| 功能完整性 | ★★★★★ | 6 条 AC 全部实现 |
| 代码质量 | ★★★★☆ | 结构清晰，无调试残留，缺 useMemo/useCallback |
| 安全性 | ★★★★★ | 无 XSS、无注入、无敏感信息泄露 |
| 边界处理 | ★★★★★ | 空状态、错误状态、加载状态覆盖完整 |
| 样式一致性 | ★★★★★ | createStyles 规范使用，token 变量适配主题 |

**最终结论: PASS_WITH_NOTES**

所有 6 条验收标准均已完成，无 P0 阻断或 P1 严重问题。4 个 P2 建议可在后续迭代中优化，不阻塞合并。
