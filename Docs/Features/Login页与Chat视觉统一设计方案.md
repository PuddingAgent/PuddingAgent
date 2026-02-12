# Login 页与 Chat 视觉统一设计方案

> 日期：2026-05-23  
> 页面：`/admin/user/login`  
> ADR：[36ADR-035登录页与Chat视觉统一ADR](../07架构/36ADR-035登录页与Chat视觉统一ADR.md)

---

## 1. 目标

本方案用于指导 Dev 重做浏览器评审中选中的登录页。

目标不是一次性新增完整账号体系，而是让用户进入 Pudding Runtime 的第一个页面与 `/admin/chat` 的 Quiet Runtime 语言统一，并把登录页升级为后续 Bootstrap / Register 也能复用的 Runtime Entry Shell：

1. 去掉深色玻璃拟态和强发光。
2. 复用 Chat 的暖纸张背景、低饱和边框、克制紫色强调和 8px 圆角。
3. 把登录页从“营销 Hero 卡片”改成“本地 Agent 工作台入口”。
4. 登录成功通过页面内转场进入 Chat，而不是硬跳转。
5. 保持现有认证 API、token 写入、i18n 和自动登录行为不变。
6. 页脚与首屏品牌统一为 PuddingAgent，不再出现 Ant Design Pro 默认页脚。
7. 默认使用白天模式；暗色切换只复用全局主题能力，不在登录页单独实现。

---

## 2. 当前问题

当前 `Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx` 的样式特点：

| 当前实现 | 问题 |
|----------|------|
| `#0B1020` 深色背景和径向渐变 | 与 Chat 的 `--warm-beige` 暖纸张背景断裂。 |
| `glassCard` 使用 blur、16px radius、重阴影和发光 | 与 Chat 的 8px、低阴影、工作台面板不一致。 |
| 主按钮紫色渐变、hover 发光和上浮 | 与 Chat 的实色、克制反馈不一致。 |
| 32px 大标题和 `letterSpacing: 0.03em` | 过于 Hero 化；Chat 标题系统更紧凑。 |
| 语言切换是白色半透明 | 依赖深色背景，改为浅色后不可直接复用。 |
| placeholder 承担主要表单说明 | 需要补充真实 label 或可访问名称。 |

最终效果应让登录页看起来像 Chat 的前厅，而不是另一个产品。

---

## 3. 目标视觉语言

### 3.1 设计原则

| 原则 | 落地方式 |
|------|----------|
| 安静 | 不使用大面积深色、强渐变、发光边框、背景光斑。 |
| 可靠 | 表单清晰、按钮稳定、错误状态明确，不用炫技动效。 |
| 本地工作台 | 背景像纸面工作区，控件像可长期使用的管理工具。 |
| 渐进披露 | 登录页只说明 Workspace / Agent / Skills / Chat 的关系，不展开功能教学。 |
| 与 Chat 同源 | 颜色、半径、密度、边框和 hover 行为对齐 `chat/styles.ts`。 |

### 3.2 Token 对齐

优先使用现有全局 token：

```text
--warm-beige: #f5f0e8
--soft-white: #fafaf7
--earth-brown: #5c4a3a
--text-primary: #1a1a2e
--text-secondary: var(--earth-brown)
--text-muted: #5C4A3A
--accent-purple: #7c3aed
--desaturated-green: #7a9a7e
```

建议新增或局部使用的语义变量：

```text
--pudding-login-panel: var(--soft-white)
--pudding-login-line: color-mix(in srgb, var(--earth-brown) 10%, transparent)
--pudding-login-muted: color-mix(in srgb, var(--earth-brown) 72%, transparent)
```

如果不新增变量，也可以在 `createStyles` 中直接使用 `color-mix(...)`，保持局部改造。

---

## 4. 页面结构

### 4.1 桌面布局

```text
┌─────────────────────────────────────────────────────────────┐
│                                  [主题?] [语言]              │
│                                                             │
│   PuddingAgent                                            │
│   本地 AI Agent 工作台            ┌──────────────────────┐  │
│   连接工作空间、Agent 与 Skills，  │ 登录 PuddingAgent    │  │
│   安静地理解，可靠地执行。         │ 用户名               │  │
│                                   │ [ admin            ] │  │
│   Workspace → Agent → Skills       │ 密码                 │  │
│               ↓                   │ [ pudding.dev      ] │  │
│              Chat                 │ ☑ 自动登录           │  │
│                                   │ [ 进入工作台        ] │  │
│                                   └──────────────────────┘  │
│                                                             │
│                                      PuddingAgent · GitHub   │
└─────────────────────────────────────────────────────────────┘
```

说明：

- 页面使用两列但不是营销 split hero：左侧是软件形象与轻量运行态，右侧是认证卡片。
- 左侧文案不放进卡片，避免页面变成卡片堆叠。
- 表单面板是唯一强边界容器。
- 宽屏时整体最大宽度建议 1040-1120px。

### 4.2 移动布局

```text
┌──────────────────────────────┐
│               [主题?] [语言] │
│ PuddingAgent                 │
│ 本地 AI Agent 工作台         │
│ Workspace · Agent · Skills   │
│                              │
│ ┌──────────────────────────┐ │
│ │ 登录 PuddingAgent        │ │
│ │ 用户名                   │ │
│ │ [ admin                ] │ │
│ │ 密码                     │ │
│ │ [ pudding.dev          ] │ │
│ │ ☑ 自动登录               │ │
│ │ [ 进入工作台            ] │ │
│ └──────────────────────────┘ │
└──────────────────────────────┘
```

移动端要求：

- 375px 宽度无横向滚动；
- 页面 padding 16px；
- 表单宽度 `100%`；
- 输入框和按钮高度不小于 44px；
- 页脚可保留在底部自然流，不强制 sticky。
- 页脚只展示 PuddingAgent GitHub 链接，不展示 Ant Design Pro / Powered by Ant Design。

---

## 5. 组件与样式建议

### 5.1 组件结构

保留现有 `Login` 组件，可在文件内重组 JSX。当前实现允许先在页面内落地，后续 Bootstrap / Register 接入时再抽共享组件：

```text
Login
├── Helmet
├── Lang
├── RuntimeEntryShell
│   ├── RuntimeEntryVisual
│   │   ├── brandLockup
│   │   ├── title / subtitle
│   │   ├── capabilityStrip
│   │   └── runtimeMap
│   └── AuthCardSwitcher
│       ├── LoginCard
│       ├── BootstrapCard (future)
│       └── RegisterCard (future)
└── Footer
```

本次施工可以不新增跨页面组件，但命名与结构应按 `RuntimeEntryShell` 组织，避免 Bootstrap 页改造时再次重写。

### 5.2 样式替换表

| 旧样式 | 替换方向 |
|--------|----------|
| `container` 深色渐变 | `background: var(--warm-beige)`，可加低透明纸面纹理分区。 |
| `formArea` 居中单卡 | 改为 `runtimeEntryShell`，桌面两列、移动单列。 |
| `glassCard` | 改为 `authCard`：`var(--soft-white)`、8px、细边框、轻阴影或无阴影。 |
| `brandTitle` 32px | 改为 24-28px，font-weight 650/700，letter-spacing 0。 |
| `brandSub` 蓝灰色 | 改为 `earth-brown` 低透明度，line-height 1.7。 |
| `brandDivider` 渐变线 | 删除，或替换为低透明 `earth-brown` 细线。 |
| `submitBtn` 渐变发光 | 改为实色 `--accent-purple`，8px，hover 只改变背景/透明度。 |
| `errorAlert` 暗色错误 | 改为浅底低饱和错误色，贴合暖色页面。 |
| `lang` 白色半透明 | 改为 `earth-brown` 低透明度，hover 浅棕背景。 |
| Ant Design Pro 默认页脚 | 改为 `PuddingAgent · GitHub`，链接到官方仓库。 |

### 5.2.1 主题与页脚

默认主题为 light。登录页不单独实现暗黑/白天状态：

- 如果已有全局主题能力，右上角可以展示图标型主题按钮；
- 如果没有全局主题能力，右上角只展示语言切换；
- 主题按钮必须提供 `aria-label`；
- 不使用文字胶囊式“白天/黑夜”切换控件。

页脚统一为：

```tsx
<a
  href="https://github.com/PuddingAgent/PuddingAgent"
  target="_blank"
  rel="noreferrer"
>
  PuddingAgent · GitHub
</a>
```

### 5.3 推荐关键样式

```ts
container: {
  minHeight: '100vh',
  display: 'flex',
  flexDirection: 'column',
  background: 'var(--warm-beige)',
  color: 'var(--text-primary)',
}

runtimeEntryShell: {
  flex: 1,
  width: '100%',
  maxWidth: 1040,
  margin: '0 auto',
  padding: '72px 24px 32px',
  display: 'grid',
  gridTemplateColumns: 'minmax(0, 1fr) 400px',
  gap: 48,
  alignItems: 'center',
}

authCard: {
  width: '100%',
  padding: '32px 34px',
  borderRadius: 8,
  background: 'color-mix(in srgb, var(--soft-white) 94%, transparent)',
  border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
  boxShadow: '0 1px 6px rgba(0,0,0,0.04)',
}
```

移动端覆盖：

```ts
'@media (max-width: 767px)': {
  runtimeEntryShell: {
    gridTemplateColumns: '1fr',
    gap: 24,
    padding: '64px 16px 24px',
  },
  authCard: {
    padding: '24px 20px',
  },
}
```

---

## 6. 表单规范

### 6.1 字段

保持字段名不变：

```text
username
password
autoLogin
```

默认值保持：

```ts
initialValues={{ autoLogin: true }}
```

### 6.2 Label 与 placeholder

建议让 `ProFormText` 和 `ProFormText.Password` 显示 label：

```tsx
<ProFormText
  name="username"
  label="用户名"
  placeholder="admin"
/>

<ProFormText.Password
  name="password"
  label="密码"
  placeholder="pudding.dev"
/>
```

原因：UI/UX 检索结果明确指出 React 表单不应只依赖 placeholder 作为 label。这样也能改善无障碍体验。

### 6.3 主按钮文案

建议中文默认值从 `进入` 调整为：

```text
进入工作台
```

如果要控制变更范围，也可以保留现有 i18n key `pages.login.submit`，只把 `defaultMessage` 改为 `进入工作台`。

---

## 7. 轻量运行态视觉

左侧上下文建议包含品牌、简短定位和运行态拓扑：

```text
Pudding Runtime
本地 AI Agent 工作台
连接工作空间、Agent 与 Skills，安静地理解，可靠地执行。

Workspace → Agent → Skills
            ↓
           Chat
```

设计要求：

- `Pudding Runtime` 24-28px；
- 副标题 15-16px；
- 描述 14px，最大宽度 420px；
- `Workspace / Agent / Skills / Chat` 使用低饱和标签、细线和小状态点；
- 可以使用 CSS animation 表达“流动到 Chat”，但动画必须低频、低对比、可关闭；
- 不展示功能卡片、不展示统计数字；
- 不在登录页加入 Agent 选择或 Skill 列表。

### 7.1 卡片状态与转场

认证区按状态组织：

```text
bootstrap -> login -> entering-chat -> chat
register  -> login
```

当前施工范围：

- `login`：完整实现。
- `entering-chat`：登录成功后设置短暂状态，按钮文案变为“正在进入 Chat…”，页面整体轻微淡出 / 位移后进入 Chat。
- `bootstrap`：系统未初始化时应复用同一壳层展示初始化卡片；当前已有 Bootstrap 路由，后续迁入。
- `register`：当前仅保留禁用态或未来入口，不暴露可点击假流程。

---

## 8. 错误、加载与焦点状态

| 状态 | 设计 |
|------|------|
| 默认 | 输入框浅底、细边框、label 清晰。 |
| hover | 边框略加深，不改变布局。 |
| focus | `box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-purple) 18%, transparent)`。 |
| submit loading | 使用 ProForm / AntD 默认 loading，不自定义发光。 |
| entering-chat | 右侧卡片淡出，左侧运行态轻移到 Chat，随后 route replace。 |
| 错误 | Alert 使用浅红底、细红边、文字清晰，放在 panel header 与表单之间。 |
| reduced motion | 关闭 page enter、按钮 shimmer、translate 动效。 |

不要保留旧的：

- submit hover `translateY(-1px)`；
- 按钮 shimmer；
- 紫色发光阴影；
- 卡片 `fadeIn 600ms`。

---

## 9. 实施步骤

1. 修改 `Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx` 的 `useStyles`：
   - 删除深色背景、glass card、发光按钮；
   - 新增 `loginShell`、`loginIntro`、`loginPanel`、`capabilityStrip` 等样式；
   - 调整 `lang`、`errorAlert` 和 form 控件样式。
2. 重组 JSX：
   - `formArea` 改为 `loginShell`；
   - 左侧放品牌与上下文；
   - 右侧放登录面板；
   - 保留 `Helmet`、`Lang`、`Footer`、`ProForm`、`LoginMessage`。
3. 表单补 label：
   - 用户名 label；
   - 密码 label；
   - 自动登录保留 checkbox 文案。
4. 登录成功转场：
   - 成功后设置 `entryTransition = 'entering-chat'`；
   - 等待 150-300ms；
   - `history.replace(normalizedRedirect || '/chat')`；
   - redirect 中出现 `/admin/...` 时先归一化为 Umi 内部路径。
5. 保持认证逻辑原样：
   - 不改 `handleSubmit`；
   - 不改 token key；
   - 不改登录 API。
6. 更新登录页测试：
   - `Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx`；
   - 删除旧玻璃拟态快照；
   - 覆盖 `runtime-entry-shell`、`runtime-entry-visual`、`auth-card-login` 和 `entering-chat`。
7. 浏览器验收：
   - `http://localhost:5000/admin/user/login`；
   - 登录成功跳转 `/admin/chat`；
   - 错误密码展示错误；
   - 375px / 768px / 1440px 截图检查。

---

## 10. 验收清单

- [ ] 登录页不再使用深蓝黑背景、玻璃拟态、强发光或紫色渐变按钮。
- [ ] 首屏与 Chat 使用同一暖色背景、细边框、8px 半径和克制紫色强调。
- [ ] 登录表单在 375px 宽度下无溢出。
- [ ] 用户名、密码、自动登录、提交按钮键盘可达。
- [ ] 用户名和密码有 label，不只依赖 placeholder。
- [ ] 错误提示可见且不改变表单布局稳定性。
- [ ] 登录成功、失败、redirect、autoLogin 行为不变。
- [ ] 登录成功先进入 `entering-chat` 状态，再进入 Chat。
- [ ] `prefers-reduced-motion` 下没有持续动画。
- [ ] 登录页单测更新并通过。

---

## 11. 非目标

本次不做：

- 真正开放注册、找回密码、SSO；
- 登录页选择 Workspace / Agent；
- 展示完整 Skill 列表；
- 本次直接迁移 Bootstrap 初始化流程；
- 修改 Auth API 或 token 存储方式；
- 抽象全局 Runtime Entry 组件。

这些都可以后续基于同一视觉语言单独设计。Bootstrap / Register 的关键约束是复用同一个 Runtime Entry Shell，而不是再做独立页面。
