# Chat 暗色主题修复施工方案

## 目标

修复 `http://localhost/admin/chat` 的暗色模式失效和配色异常问题。

本次施工不只是改几个颜色，而是把 Chat 从亮色 token 直连迁移到主题语义 token，确保 light/dark/system 三种模式稳定工作。

## 当前问题评估

### 1. 主题开关本身存在

`Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx` 已实现：

- `pudding_admin_theme_mode` 持久化；
- `ThemeModeContext`；
- `data-pudding-theme='light|dark'`；
- AntD `defaultAlgorithm | darkAlgorithm`；
- `ThemeToggleAction`。

所以问题不是“没有暗色模式”，而是“暗色 token 没有贯穿 Chat 页面”。

### 2. Chat 样式仍大量绑定亮色变量

`Source/PuddingPlatformAdmin/src/pages/chat/styles.ts` 中大量使用：

```css
var(--warm-beige)
var(--soft-white)
var(--earth-brown)
color-mix(in srgb, var(--soft-white) ...)
color-mix(in srgb, var(--earth-brown) ...)
```

这些变量在 `:root` 是亮色纸张语义，暗色模式下没有完整重绑定，因此出现浅背景、暗组件、紫色按钮、棕色文字混在一起的问题。

### 3. CSS token 与 AntD token 不一致

当前 `ThemeProviderContainer` 暗色 AntD token 偏深蓝：

```ts
colorBgLayout: '#0b1020'
colorBgContainer: '#172033'
colorPrimary: '#a78bfa'
```

但 Chat 的设计语言是暖纸张、低饱和、克制工具感。如果 CSS 侧继续使用暖米白，而 AntD 侧切到深蓝，会形成两套主题。

## 施工原则

1. 主题状态只从 `ThemeProviderContainer` 来。
2. Chat 样式只读 `--pudding-chat-*` 语义 token。
3. 暗色模式使用暖暗色，不使用大面积纯黑或深蓝。
4. 先兼容旧变量止血，再迁移 Chat 语义 token。
5. 每阶段都保留 light 模式回归检查。

## 阶段一：补齐全局 token

修改文件：

```text
Source/PuddingPlatformAdmin/src/global.style.ts
```

### 1.1 在 `:root` 增加 Chat light token

```css
:root {
  --pudding-chat-bg: #f5f0e8;
  --pudding-chat-sidebar-bg: rgba(250, 250, 247, 0.72);
  --pudding-chat-header-bg: rgba(250, 250, 247, 0.78);
  --pudding-chat-surface: #fafaf7;
  --pudding-chat-surface-muted: #f2eee7;
  --pudding-chat-border: rgba(92, 74, 58, 0.10);
  --pudding-chat-border-strong: rgba(92, 74, 58, 0.18);
  --pudding-chat-text: #1a1a2e;
  --pudding-chat-text-muted: #5c4a3a;
  --pudding-chat-text-subtle: rgba(92, 74, 58, 0.62);
  --pudding-chat-accent: #7c3aed;
  --pudding-chat-accent-soft: rgba(124, 58, 237, 0.10);
  --pudding-chat-success: #4f7f58;
  --pudding-chat-warning: #b7791f;
  --pudding-chat-danger: #b42318;
  --pudding-chat-shadow: 0 1px 6px rgba(0, 0, 0, 0.04);
}
```

### 1.2 在 dark 下增加 Chat dark token

```css
[data-pudding-theme='dark'] {
  --pudding-chat-bg: #11100d;
  --pudding-chat-sidebar-bg: rgba(24, 22, 18, 0.94);
  --pudding-chat-header-bg: rgba(24, 22, 18, 0.90);
  --pudding-chat-surface: #1c1a16;
  --pudding-chat-surface-muted: #26231d;
  --pudding-chat-border: rgba(224, 211, 190, 0.12);
  --pudding-chat-border-strong: rgba(224, 211, 190, 0.22);
  --pudding-chat-text: #f4efe7;
  --pudding-chat-text-muted: #d2c5b5;
  --pudding-chat-text-subtle: #a99c8d;
  --pudding-chat-accent: #a78bfa;
  --pudding-chat-accent-soft: rgba(167, 139, 250, 0.14);
  --pudding-chat-success: #86efac;
  --pudding-chat-warning: #facc15;
  --pudding-chat-danger: #fca5a5;
  --pudding-chat-shadow: 0 8px 28px rgba(0, 0, 0, 0.32);
}
```

### 1.3 增加旧变量暗色兼容层

这是迁移期止血，不作为长期接口。

```css
[data-pudding-theme='dark'] {
  --warm-beige: var(--pudding-chat-bg);
  --soft-white: var(--pudding-chat-surface);
  --earth-brown: var(--pudding-chat-text-muted);
  --text-primary: var(--pudding-chat-text);
  --text-secondary: var(--pudding-chat-text-muted);
  --accent-purple: var(--pudding-chat-accent);
}
```

## 阶段二：同步 AntD ConfigProvider token

修改文件：

```text
Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx
```

目标是让 AntD 和 CSS 主题语义一致。

建议将当前深蓝暗色 token 改为暖暗色：

```ts
token: {
  colorPrimary: isDark ? '#a78bfa' : '#7c3aed',
  colorBgLayout: isDark ? '#11100d' : '#f5f0e8',
  colorBgContainer: isDark ? '#1c1a16' : '#fafaf7',
  colorFillSecondary: isDark ? 'rgba(224, 211, 190, 0.08)' : 'rgba(92, 74, 58, 0.06)',
  colorFillTertiary: isDark ? 'rgba(224, 211, 190, 0.06)' : 'rgba(92, 74, 58, 0.04)',
  colorBorder: isDark ? 'rgba(224, 211, 190, 0.12)' : 'rgba(92, 74, 58, 0.12)',
  colorBorderSecondary: isDark ? 'rgba(224, 211, 190, 0.08)' : 'rgba(92, 74, 58, 0.08)',
  colorText: isDark ? '#f4efe7' : '#1a1a2e',
  colorTextSecondary: isDark ? '#d2c5b5' : '#5c4a3a',
  colorTextTertiary: isDark ? '#a99c8d' : 'rgba(92, 74, 58, 0.62)',
}
```

## 阶段三：迁移 Chat 样式

修改文件：

```text
Source/PuddingPlatformAdmin/src/pages/chat/styles.ts
```

按区域替换，不建议一次大面积机械替换。

### 3.1 布局层

| 当前 | 替换为 |
| --- | --- |
| `background: var(--warm-beige)` | `background: var(--pudding-chat-bg)` |
| 侧栏 `var(--soft-white)` 混合 | `var(--pudding-chat-sidebar-bg)` |
| 顶栏 `var(--soft-white)` 混合 | `var(--pudding-chat-header-bg)` |
| `var(--earth-brown)` 边框混合 | `var(--pudding-chat-border)` |

重点样式：

```ts
layout
sidebar
sidebarHeader
sidebarSearch
mainArea
header
chatBody
inputPanel
statusBar
```

### 3.2 文本层

| 当前 | 替换为 |
| --- | --- |
| `var(--text-primary)` | `var(--pudding-chat-text)` |
| `var(--earth-brown)` 普通文字 | `var(--pudding-chat-text-muted)` |
| `opacity: 0.5/0.6/0.7` 表达层级 | 优先使用 `--pudding-chat-text-subtle` |

不要依赖 `opacity` 让文字变浅。暗色模式下透明文本叠在暗背景上容易低对比。

### 3.3 气泡与消息区

| 组件 | 亮/暗语义 |
| --- | --- |
| `userBubble` | `background: var(--pudding-chat-accent-soft)`，边框 `color-mix(... --pudding-chat-accent ...)` |
| `agentBubble` | `background: transparent` 或 `var(--pudding-chat-surface)`，边框 `--pudding-chat-border` |
| `assistantAnswer` | `background: var(--pudding-chat-surface)` |
| `reasoningPanel` | `background: color-mix(in srgb, var(--pudding-chat-surface) 82%, transparent)` |
| `stepCard` | `background: var(--pudding-chat-surface-muted)` |

### 3.4 输入区

输入区是暗色模式最容易出错的位置，需要单独验收：

- 输入面板背景必须是暗色 surface；
- placeholder 使用 `--pudding-chat-text-subtle`；
- 用户输入使用 `--pudding-chat-text`；
- 发送按钮 disabled 不得低到不可见；
- focus ring 使用 `--pudding-chat-accent-soft`。

### 3.5 浮层与菜单

同步检查：

```text
CommandPalette
ContextMenu
SubAgentIndicator drawer/modal
TokenStatsIndicator
ProviderBalanceIndicator
```

所有浮层背景统一使用 `--pudding-chat-surface` 或 `--pudding-chat-surface-muted`。

## 阶段四：清理内联颜色

搜索命令：

```powershell
rg "#[0-9a-fA-F]{3,8}|rgba\\(|rgb\\(|var\\(--earth-brown\\)|var\\(--soft-white\\)|var\\(--warm-beige\\)" Source/PuddingPlatformAdmin/src/pages/chat -n
```

处理规则：

- 状态色可以保留明确语义，如 success/warning/danger，但应优先映射到 token；
- 大面积背景、文本、边框不得保留硬编码；
- SVG 图标内的 `stroke/fill` 也需要跟随 token。

## 阶段五：测试与验收

### 5.1 单元测试

为 `ThemeMode` 增加或补充测试：

- 默认读取 `system`；
- 点击切换为 `dark`；
- 再点击切回 `light`；
- double click 回到 `system`；
- `document.documentElement` 正确设置 `data-pudding-theme`。

### 5.2 Playwright 视觉测试

新增或补充 e2e：

```text
Source/PuddingPlatformAdmin/e2e/chat-theme.spec.ts
```

场景：

1. 打开 `/admin/chat`。
2. 截图 light。
3. 点击 `aria-label="切换主题"`。
4. 等待 `html[data-pudding-theme='dark']`。
5. 截图 dark。
6. 校验没有水平溢出。
7. 校验关键区域 computed style：
   - layout background；
   - sidebar background；
   - header background；
   - input panel background；
   - body text color。

视口：

```text
1172x1270
390x844
```

### 5.3 对比度验收

最低标准：

| 文本类型 | 对比度 |
| --- | --- |
| 正文、输入文本、按钮文字 | >= 4.5:1 |
| 时间、状态、辅助信息 | >= 3:1 |
| disabled 文本 | 可低于 3:1，但必须可辨识状态 |

## 建议施工顺序

1. 修改 `global.style.ts`，加入 `--pudding-chat-*` 和旧变量 dark 兼容层。
2. 修改 `ThemeMode/index.tsx`，同步 AntD 暖暗色 token。
3. 先迁移 `styles.ts` 的布局层和输入区。
4. 再迁移消息气泡、reasoning、tool output、step cards。
5. 最后迁移 Chat 子组件内联颜色。
6. 补测试和截图验收。

## 完成定义

- [ ] `切换主题` 后 Chat 页面真正进入暗色，不出现亮色主背景残留。
- [ ] Light 模式与当前视觉基本一致。
- [ ] Dark 模式使用暖暗色，不是深蓝主题。
- [ ] 输入区、消息区、侧栏、顶栏、浮层全部可读。
- [ ] 正文对比度满足 4.5:1。
- [ ] 桌面与移动截图均通过。
- [ ] 单测和 Playwright 主题验收通过。

