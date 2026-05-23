# RuntimeEntryShell 统一入口设计方案

> 日期：2026-05-23  
> ADR：[39ADR-038RuntimeEntryShell统一入口体验ADR](../07架构/39ADR-038RuntimeEntryShell统一入口体验ADR.md)  
> 页面：`/admin/bootstrap`、`/admin/user/login`、未来注册入口  
> 目标：将首次初始化、登录、后续注册统一为同一个 Pudding Runtime 入口体验，右侧卡片通过转场切换，登录成功通过动画进入 Chat。

---

## 1. 当前问题

### Bootstrap 页

当前文件：

```text
Source/PuddingPlatformAdmin/src/pages/bootstrap/index.tsx
```

主要问题：

| 当前实现 | 问题 |
|----------|------|
| 深蓝黑背景和径向光效 | 与 Chat / Login 的 Quiet Runtime 暖色系统割裂。 |
| 居中玻璃卡片 | 像独立启动页，不像产品工作台入口。 |
| 强紫色渐变按钮和发光 | 视觉噪音高，与 Chat 克制操作面板不一致。 |
| placeholder 作为主要字段说明 | 无障碍弱，表单扫描性差。 |
| 成功后 `window.location.href = '/admin/chat'` | 硬跳转，破坏 SPA 转场连续性。 |
| 样式写在页面内 | 与 Login 重复，后续 Register 会继续复制。 |
| 页脚仍是 Ant Design Pro / Powered by Ant Design | 与 PuddingAgent 产品身份冲突。 |
| 缺少产品形象 | 左侧没有 PuddingAgent 的品牌、运行时拓扑和产品记忆点。 |
| “显示名称”字段 | 首次初始化不需要，增加认知负担。 |
| “重置”按钮 | 语义不明，用户不知道会清空表单还是重置系统。 |
| 初始化过程引导不足 | 用户不知道当前是在创建身份、设置安全凭证还是进入 Runtime。 |
| 缺少明确主题策略 | 默认应为白天模式；暗色能力要跟随全局主题，不能入口页自造一套。 |

### Login 页

当前文件：

```text
Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx
```

Login 已经有以下可复用基础：

- `runtime-entry-shell`；
- `runtime-entry-visual`；
- `auth-card-login`；
- 暖色背景；
- 左侧 Workspace / Agent / Skills / Chat 拓扑；
- `entering-chat` 状态；
- `history.replace(normalizedRedirect)`。

问题是这些能力仍写在 Login 页面内，Bootstrap 没有复用。

---

## 2. 目标体验

桌面端：

```text
┌──────────────────────────────────────────────────────────────┐
│                                            [主题?] [语言]     │
│                                                              │
│  PuddingAgent                   ┌─────────────────────────┐  │
│  Local AI Agent Workspace       │  初始化 PuddingAgent    │  │
│  Workspace / Agent / Skills     │  1 身份  2 凭证  3 进入 │  │
│  and Chat run as one runtime.   │                         │  │
│                                 │  登录用户名             │  │
│  Workspace ─ Agent ─ Skills     │  [ admin              ] │  │
│        │                        │  邮箱地址               │  │
│       Chat                      │  [ email              ] │  │
│                                 │  密码                   │  │
│                                 │  [ password           ] │  │
│                                 │  确认密码               │  │
│                                 │  [ confirm            ] │  │
│                                 │  [ 创建管理员账号      ] │  │
│                                 └─────────────────────────┘  │
│                             PuddingAgent · GitHub            │
└──────────────────────────────────────────────────────────────┘
```

初始化成功、有 token：

```text
BootstrapCard -> bootstrap-success -> entering-chat -> /chat
```

初始化成功、无 token：

```text
BootstrapCard -> bootstrap-success -> LoginCard
```

登录成功：

```text
LoginCard -> entering-chat -> /chat
```

注册未开放：

```text
LoginCard -> RegisterDisabledCard (optional) -> LoginCard
```

移动端：

```text
Pudding Runtime
本地 AI Agent 工作台
Workspace · Agent · Skills · Chat

┌──────────────────────────┐
│ 当前状态卡片             │
│ 表单                     │
└──────────────────────────┘
```

---

## 3. 组件设计

### 3.1 文件结构

建议新增：

```text
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/
  index.tsx
  styles.ts
  types.ts
  cards/
    BootstrapCard.tsx
    LoginCard.tsx
    RegisterDisabledCard.tsx
```

页面改为薄路由：

```text
Source/PuddingPlatformAdmin/src/pages/bootstrap/index.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx
```

### 3.2 类型

```ts
export type EntryCardMode =
  | 'bootstrap'
  | 'bootstrap-success'
  | 'login'
  | 'register-disabled'
  | 'register'
  | 'entering-chat';

export type RuntimeEntryShellProps = {
  mode: EntryCardMode;
  title?: string;
  subtitle?: string;
  children: React.ReactNode;
  showLanguage?: boolean;
  showThemeToggle?: boolean;
  footer?: React.ReactNode;
};
```

### 3.3 RuntimeEntryShell

职责：

- 渲染统一页面背景；
- 渲染语言切换；
- 渲染左侧 Runtime visual；
- 渲染右侧 card stage；
- 根据 `mode` 设置 `data-transition`；
- 处理 reduced motion 样式；
- 不直接调用 API。

DOM 结构：

```tsx
<div className={styles.container}>
  <Lang />
  <main
    className={cx(styles.shell, isEnteringChat && styles.shellEntering)}
    data-testid="runtime-entry-shell"
    data-transition={mode}
  >
    <RuntimeEntryVisual />
    <section
      className={styles.cardStage}
      data-testid="runtime-entry-card-stage"
    >
      {children}
    </section>
  </main>
  <Footer />
</div>
```

主题策略：

- 默认主题是 light；
- 如果项目已有全局主题状态，`showThemeToggle` 可展示图标型 Sun / Moon 按钮；
- 如果项目没有全局主题能力，`showThemeToggle` 必须为 `false`；
- 不允许入口页单独维护一份暗黑/白天状态；
- 主题按钮必须是 icon button，并提供 `aria-label`，不使用文字胶囊按钮。

### 3.4 RuntimeEntryVisual

显示内容：

- `PuddingAgent`；
- `Local AI Agent Workspace` / `本地 AI Agent 工作台`；
- 简短说明；
- `Workspace / Agent / Skills / Chat` 状态标签；
- 桌面端低频 runtime map；
- 移动端隐藏 runtime map。

建议沿用 Login 当前节点：

```ts
const runtimeNodes = [
  { key: 'workspace', label: 'Workspace', x: 16, y: 58 },
  { key: 'agent', label: 'Agent', x: 52, y: 24 },
  { key: 'skills', label: 'Skills', x: 78, y: 60 },
  { key: 'chat', label: 'Chat', x: 50, y: 82 },
];
```

视觉约束：

- 不用 SVG hero 插画；
- 不用渐变 orb；
- 不用深色玻璃；
- 不用 Ant Design Pro 品牌；
- CSS 动画只做小状态点流动；
- reduced motion 下隐藏 flow signal。

产品形象必须是产品相关视觉，不是纯背景装饰。推荐组合：

```text
PuddingAgent wordmark
Runtime topology map
Agent handoff signal
Workspace / Skills / Chat state chips
```

不要求本轮创建完整品牌 VI，但必须让用户在第一屏明确知道这是 PuddingAgent，而不是 Ant Design Pro 模板页。

---

## 4. 卡片设计

### 4.1 BootstrapCard

职责：

- 创建首个管理员账号；
- 展示密码强度；
- 成功后调用上层 `onSuccess(token?)`。

字段：

| 字段 | label | 说明 |
|------|-------|------|
| `userId` | 登录用户名 | 必填，至少 3 个字符。 |
| `email` | 邮箱地址 | 必填，email 格式。 |
| `password` | 密码 | 必填，至少 8 位，含大小写和数字。 |
| `confirmPassword` | 确认密码 | 必须与密码一致。 |

不展示 `displayName`。显示名称不是首次初始化的必要信息，后续应在账号资料或用户管理中维护。

卡片文案：

```text
INITIALIZE
初始化 PuddingAgent
创建第一个管理员账号，完成本地 Runtime 封存。
```

初始化引导：

```text
身份
创建管理员登录身份

安全凭证
设置密码并确认本地访问凭证

进入 Runtime
完成初始化后进入 Chat 工作台
```

交互状态：

| 状态 | UI |
|------|----|
| 初始 | 高亮“身份”。 |
| 用户聚焦密码或确认密码 | 高亮“安全凭证”。 |
| 提交中 | 高亮“进入 Runtime”，主按钮 loading，显示 sealing 状态。 |
| 成功有 token | 卡片进入 `entering-chat`。 |
| 成功无 token | 卡片进入 `bootstrap-success` 后切到 Login。 |
| 失败 | 保持当前 card，错误显示在对应区域。 |

不展示“重置”按钮。该按钮在初始化语境中语义不清，容易被理解为重置 Runtime 或系统数据。如果后续确实需要清空表单，只能使用“清空表单”作为次要文字按钮，并通过确认设计评审后再加入。

成功处理：

```ts
const handleBootstrapSuccess = async (token?: string) => {
  if (token) {
    localStorage.setItem('pudding_token', token);
    setMode('entering-chat');
    window.setTimeout(() => history.replace('/chat'), ENTRY_TRANSITION_MS);
    return;
  }

  setMode('bootstrap-success');
  window.setTimeout(() => setMode('login'), ENTRY_TRANSITION_MS);
};
```

### 4.2 LoginCard

从当前 Login 页面迁出：

- `login({ ...values, type: 'account' })` 不变；
- token key `pudding_token` 不变；
- 成功后 `fetchUserInfo()` 不变；
- 成功后 `setMode('entering-chat')`；
- 240ms 后 `history.replace(normalizedRedirect || '/chat')`。

卡片文案：

```text
AUTHENTICATION
登录 PuddingAgent
继续进入 Chat，接管当前工作空间中的 Agent 与 Skills。
```

### 4.3 RegisterDisabledCard

当前只作为未来入口，不开放实际注册。

行为：

- 从 Login card 点击“注册功能后续开放”时可不切换；
- 如果产品希望展示说明，切到 disabled card；
- disabled card 只能返回 Login；
- 不出现可填写但无法提交的假表单。

文案：

```text
REGISTRATION
注册暂未开放
当前版本通过初始化管理员账号和受控用户管理进入 Runtime。
```

---

## 5. 转场规范

### 5.1 时间

```ts
export const ENTRY_TRANSITION_MS = 240;
```

允许范围：180-300ms。

### 5.2 动画

卡片进入：

```css
opacity: 1;
transform: translateY(0) rotateY(0);
```

卡片离开：

```css
opacity: 0;
transform: rotateY(-7deg) translateY(-4px);
```

进入 Chat：

```css
shell opacity: 0;
shell transform: translateY(-6px) scale(0.985);
```

reduced motion：

```css
@media (prefers-reduced-motion: reduce) {
  transition: none;
  animation: none;
}
```

业务逻辑不得依赖 `transitionend`，统一用短 timer 或直接进入下一状态。

### 5.3 路由归一化

保留 Login 当前逻辑：

```ts
const normalizeRouteTarget = (target: string | null): string => {
  if (!target) return '/chat';

  const url = new URL(target, window.location.origin);
  const path = url.pathname.startsWith('/admin/')
    ? url.pathname.slice('/admin'.length)
    : url.pathname;
  return `${path || '/chat'}${url.search}${url.hash}`;
};
```

---

## 6. 样式迁移

### 6.1 从 Bootstrap 删除

删除或替换：

- `container` 中的 `#0B1020`；
- radial-gradient dark background；
- `glassCard`；
- 16px radius；
- blur/backdropFilter；
- 紫色渐变按钮；
- hover 发光和上浮；
- `window.location.href` 硬跳转。
- Ant Design Pro 默认页脚；
- `displayName` 初始化字段；
- “重置”按钮。

### 6.1.1 默认浅色主题

入口页默认使用 light。建议 token：

```text
background: #F8F5EE / var(--warm-beige)
surface: #FFFFFF / var(--soft-white)
text: #2A2118
muted: #756A5D
border: rgba(84, 64, 42, 0.16)
accent: #7C5CFF
```

暗色主题只在全局主题能力存在时跟随实现。没有全局主题能力时，本轮不加切换按钮。

### 6.2 共享样式

从 Login 页面抽出：

- `container`；
- `shell` / `shellEntering`；
- `visual`；
- `brandMark`；
- `runtimeMap`；
- `panelWrap` / `panel` / `panelEntering`；
- `submitBtn`；
- `errorAlert`；
- `lang`。

命名改为通用：

| Login 当前名 | 共享组件名 |
|--------------|------------|
| `shell` | `entryShell` |
| `visual` | `entryVisual` |
| `panelWrap` | `cardStage` |
| `panel` | `entryCard` |
| `panelEntering` | `entryCardLeaving` |
| `submitBtn` | `primaryAction` |

### 6.3 Footer 品牌

删除当前默认页脚：

```text
Ant Design Pro
Ant Design
Powered by Ant Design
```

替换为：

```tsx
<footer data-testid="runtime-entry-footer">
  <a
    href="https://github.com/PuddingAgent/PuddingAgent"
    target="_blank"
    rel="noreferrer"
  >
    PuddingAgent · GitHub
  </a>
</footer>
```

页脚应低对比、轻量展示，不应抢占入口操作注意力。

---

## 7. 页面改造

### 7.1 Bootstrap 页面

目标形态：

```tsx
const BootstrapPage: React.FC = () => {
  const [mode, setMode] = useState<EntryCardMode>('bootstrap');

  return (
    <RuntimeEntryShell mode={mode}>
      <BootstrapCard
        leaving={mode === 'bootstrap-success' || mode === 'entering-chat'}
        onSuccess={async ({ token }) => {
          if (token) {
            localStorage.setItem('pudding_token', token);
            setMode('entering-chat');
            window.setTimeout(() => history.replace('/chat'), ENTRY_TRANSITION_MS);
            return;
          }
          setMode('bootstrap-success');
          window.setTimeout(() => setMode('login'), ENTRY_TRANSITION_MS);
        }}
      />
      {mode === 'login' && <LoginCard />}
    </RuntimeEntryShell>
  );
};
```

实际实现可只渲染当前 card，避免两个表单同时存在。

### 7.2 Login 页面

目标形态：

```tsx
const LoginPage: React.FC = () => {
  const [mode, setMode] = useState<EntryCardMode>('login');

  return (
    <RuntimeEntryShell mode={mode}>
      <LoginCard
        onEnteringChat={() => setMode('entering-chat')}
        onNavigateToChat={navigateToChat}
      />
    </RuntimeEntryShell>
  );
};
```

---

## 8. app.tsx 路由守卫影响

现有逻辑保留：

- 未初始化 -> `/bootstrap`；
- 已初始化未登录 -> `/user/login`；
- 已登录 -> 正常进入业务页。

需要确认：

1. Bootstrap 成功且拿到 token 后，`initialState.currentUser` 能在进入 Chat 前刷新；
2. Bootstrap 成功但无 token 时，不能被 guard 再推回 bootstrap；
3. Login redirect 仍支持 `/admin/...` 外部路径归一化。

UserLayout 或全局布局如果仍注入 Ant Design Pro footer，入口路由必须覆盖或禁用它。Bootstrap/Login 不得同时出现 RuntimeEntryShell footer 和 Ant Design Pro footer。

---

## 9. 测试计划

### 9.1 RuntimeEntryShell 单测

文件：

```text
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/RuntimeEntryShell.test.tsx
```

断言：

- 渲染 `runtime-entry-shell`；
- 渲染 `runtime-entry-visual`；
- 渲染 `runtime-entry-card-stage`；
- 渲染 `runtime-entry-footer`，链接指向 `https://github.com/PuddingAgent/PuddingAgent`；
- `mode='entering-chat'` 时 `data-transition='entering-chat'`；
- 默认不渲染局部主题切换；已有全局主题注入时才渲染 `theme-mode-toggle`；
- reduced motion 样式不影响 DOM。

### 9.2 Bootstrap 单测

文件：

```text
Source/PuddingPlatformAdmin/src/pages/bootstrap/bootstrap.test.tsx
```

覆盖：

- 初始渲染 `auth-card-bootstrap`；
- 不渲染“显示名称”字段；
- 不渲染“重置”按钮；
- 渲染初始化步骤引导；
- 密码强度提示；
- 密码不一致展示错误；
- API 成功有 token：写入 token，进入 `entering-chat`，调用 `history.replace('/chat')`；
- API 成功无 token：进入 `bootstrap-success` 后切换 Login card；
- API 失败保留 bootstrap card。

### 9.3 Login 单测

更新：

```text
Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx
```

覆盖：

- 使用共享 `RuntimeEntryShell`；
- 登录成功进入 `entering-chat`；
- redirect `/admin/workspace` 归一化为 `/workspace`；
- 登录失败显示错误但不离开 card。

### 9.4 浏览器验收

视口：

```text
375x812
768x1024
1309x1270
1440x900
```

路径：

```text
http://localhost:5000/admin/bootstrap
http://localhost:5000/admin/user/login
http://localhost:5000/admin/chat
```

检查：

- 无深色玻璃拟态；
- 无 Ant Design Pro 页脚；
- 默认浅色；
- 第一屏能识别 PuddingAgent 产品身份；
- 无水平滚动；
- 卡片文字不溢出；
- 输入目标高度 >= 44px；
- 初始化和登录转场可见但不拖沓；
- reduced motion 下流程仍完成。

---

## 10. 非目标

本次不做：

- 真正开放注册；
- 改登录 API；
- 改 bootstrap API；
- 改用户权限模型；
- 改 Chat 页面布局；
- 把 `/bootstrap` 和 `/user/login` 合并成一个路由；
- 引入新动画库。

---

## 11. Dev 施工顺序

1. 清理入口路由中的 Ant Design Pro footer，确认 Bootstrap/Login 由 RuntimeEntryShell 接管页脚。
2. 新增 `RuntimeEntryShell` 组件和浅色样式 token。
3. 新增或迁出 `RuntimeEntryVisual`，加入 PuddingAgent 产品身份和 runtime topology。
4. 从 Login 页面迁出右侧 card stage 和通用转场样式。
5. 把 Login 页面改成薄路由 + `LoginCard`。
6. 把 Bootstrap 页面改成薄路由 + `BootstrapCard`。
7. 从 Bootstrap 表单删除 `displayName`，删除“重置”按钮。
8. 给 BootstrapCard 增加初始化步骤引导和提交状态。
9. 替换 Bootstrap 的 `window.location.href` 为 `history.replace` + entry mode。
10. 按全局主题能力决定是否展示 `theme-mode-toggle`，默认 light。
11. 补 `RegisterDisabledCard` 或保持 Login 内 disabled 文案。
12. 更新 i18n 文案。
13. 补单测。
14. 跑 `npm run jest -- src/pages/user/login/login.test.tsx src/pages/bootstrap/bootstrap.test.tsx --runInBand`。
15. 跑浏览器截图验收。

---

## 12. 验收清单

- [ ] `/admin/bootstrap` 和 `/admin/user/login` 都有 `runtime-entry-shell`。
- [ ] `/admin/bootstrap` 不再出现深蓝背景和玻璃卡片。
- [ ] 默认是白天模式。
- [ ] 没有全局主题能力时，不出现假的暗黑/白天切换。
- [ ] 有全局主题能力时，主题切换按钮是图标型并有 `aria-label`。
- [ ] 第一屏出现 PuddingAgent 产品身份和产品形象。
- [ ] 页脚只出现 PuddingAgent GitHub 链接。
- [ ] 页脚不出现 Ant Design Pro / Ant Design / Powered by Ant Design。
- [ ] Bootstrap 表单不出现显示名称。
- [ ] Bootstrap card 不出现“重置”按钮。
- [ ] Bootstrap card 有初始化步骤引导。
- [ ] Bootstrap 初始化成功不使用 `window.location.href`。
- [ ] 有 token 时初始化后动画进入 Chat。
- [ ] 无 token 时初始化后动画切到 Login card。
- [ ] Login 成功进入 `entering-chat` 后再 `history.replace`。
- [ ] Register 未开放时没有假注册表单。
- [ ] 移动端无溢出。
- [ ] reduced motion 下无持续动画。
- [ ] 单测和浏览器验收通过。
