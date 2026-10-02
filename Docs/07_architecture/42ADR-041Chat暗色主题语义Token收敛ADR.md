# ADR-041 Chat 暗色主题语义 Token 收敛

## 状态

Proposed

## 背景

当前 `http://localhost/admin/chat` 已有主题切换入口，`ThemeProviderContainer` 会：

- 从 `pudding_admin_theme_mode` 读取 `system | light | dark`；
- 设置 `html[data-pudding-theme='light|dark']`；
- 给 Ant Design `ConfigProvider` 注入 `defaultAlgorithm | darkAlgorithm`；
- 在暗色下切换 `navTheme` 到 `realDark`。

但 Chat 页面仍大量直接消费 light 语义变量，例如 `--warm-beige`、`--soft-white`、`--earth-brown`、`--accent-purple`，这些变量在 `:root` 中是亮色语义。`global.style.ts` 虽然已经定义了 `--pudding-admin-*` 的 light/dark token，但 Chat 主样式没有系统性使用它们。

因此用户点击“切换主题”后，实际结果不是完整暗色主题，而是：

- AntD 组件进入 dark algorithm；
- Chat 布局背景、侧栏、气泡、输入区仍引用亮色变量；
- 局部文字、边框、hover、图标颜色出现混用；
- 看起来像“黑夜模式失效”或“暗色配色异常”。

## 决策

采用“主题状态唯一、语义 token 分层、Chat 只消费 Chat 语义 token”的方案。

### ADR-041-A：主题状态唯一来源

保留 `ThemeProviderContainer` 作为唯一主题状态来源。

不新增页面级主题状态，不在 Chat 内部单独维护 dark/light。所有页面根据：

```text
html[data-pudding-theme='light']
html[data-pudding-theme='dark']
```

解析视觉 token。

### ADR-041-B：建立 Chat 专用语义 token

在 `global.style.ts` 中新增 `--pudding-chat-*` token，覆盖 Chat 的主要视觉面：

| Token | 用途 |
| --- | --- |
| `--pudding-chat-bg` | Chat 主工作区背景 |
| `--pudding-chat-sidebar-bg` | 会话侧栏背景 |
| `--pudding-chat-header-bg` | 顶栏背景 |
| `--pudding-chat-surface` | 气泡、输入框、普通面板 |
| `--pudding-chat-surface-muted` | hover、次级面、状态条 |
| `--pudding-chat-border` | 普通边框 |
| `--pudding-chat-border-strong` | 聚焦、活动项边框 |
| `--pudding-chat-text` | 主文本 |
| `--pudding-chat-text-muted` | 次文本 |
| `--pudding-chat-text-subtle` | 辅助文本、时间、占位 |
| `--pudding-chat-accent` | 主强调色 |
| `--pudding-chat-accent-soft` | 强调色浅背景 |
| `--pudding-chat-danger` | 错误态 |
| `--pudding-chat-success` | 成功态 |
| `--pudding-chat-shadow` | 浮层阴影 |

Chat 样式不得直接使用 `--warm-beige`、`--soft-white`、`--earth-brown` 来表达页面结构色。

### ADR-041-C：短期兼容旧 token，长期迁移语义 token

为了降低一次性改动风险，允许两阶段落地：

1. **止血兼容**：在 `[data-pudding-theme='dark']` 下重新绑定旧变量，使现有 Chat 样式不会继续使用亮色背景。
2. **语义迁移**：逐步把 `src/pages/chat/styles.ts` 和 Chat 组件内联样式迁移到 `--pudding-chat-*`。

兼容层只作为迁移期保护，不作为长期接口。

### ADR-041-D：暗色不是纯黑主题

PuddingAgent 是本地 AI Agent 工作台，暗色主题应保持“安静、可信、低刺激、长时间使用”的特征。

不采用大面积纯黑 `#000` 或高饱和霓虹风格。推荐暗色基准：

```css
--pudding-chat-bg: #11100d;
--pudding-chat-sidebar-bg: rgba(24, 22, 18, 0.92);
--pudding-chat-header-bg: rgba(24, 22, 18, 0.88);
--pudding-chat-surface: #1c1a16;
--pudding-chat-surface-muted: #26231d;
--pudding-chat-border: rgba(224, 211, 190, 0.12);
--pudding-chat-text: #f4efe7;
--pudding-chat-text-muted: #d2c5b5;
--pudding-chat-text-subtle: #a99c8d;
--pudding-chat-accent: #a78bfa;
--pudding-chat-accent-soft: rgba(167, 139, 250, 0.14);
```

这比当前 `#0b1020 / #172033` 更贴近 Chat 的暖纸张产品气质，也避免暗色模式变成另一套深蓝主题。

### ADR-041-E：AntD token 必须与 CSS token 同步

`ThemeProviderContainer` 中的 AntD token 需要与 CSS token 在语义上对齐：

- `colorBgLayout` 对齐 `--pudding-chat-bg` / `--pudding-admin-bg`；
- `colorBgContainer` 对齐 `--pudding-chat-surface` / `--pudding-admin-surface`；
- `colorText` 对齐主文本；
- `colorTextSecondary` 对齐次文本；
- `colorBorder` 对齐边框；
- `colorPrimary` 对齐 accent。

不允许 CSS 变量是暖暗色，而 AntD 仍是深蓝色。

## 后果

### 正向影响

- 主题切换从“局部变色”变成全页面一致切换；
- Chat、Login、Console 可以共享主题状态，但保留各自页面语义 token；
- 后续新增组件只消费语义 token，不需要判断 light/dark；
- 暗色模式可通过截图和对比度测试稳定验收。

### 代价

- `src/pages/chat/styles.ts` 中直接使用旧变量的地方较多，需要分批替换；
- 部分组件存在内联颜色，需要一并迁移；
- 暗色模式需要新增 Playwright 视觉回归用例。

## 验收标准

1. 点击“切换主题”后，`document.documentElement.dataset.puddingTheme` 必须在 `light/dark` 间切换。
2. Chat 主背景、侧栏、顶栏、消息气泡、输入区、按钮、hover、focus、dropdown 均进入对应主题。
3. 正文文本对比度不低于 4.5:1，辅助文本不低于 3:1。
4. 暗色模式下不得出现大面积亮色残留。
5. 亮色模式视觉不得回退。
6. 通过桌面 1172x1270、移动 390x844 双主题截图验收。

## 相关文件

- `Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx`
- `Source/PuddingPlatformAdmin/src/global.style.ts`
- `Source/PuddingPlatformAdmin/src/pages/chat/styles.ts`
- `Source/PuddingPlatformAdmin/src/pages/chat/index.tsx`
- `Source/PuddingPlatformAdmin/src/pages/chat/components/*`

