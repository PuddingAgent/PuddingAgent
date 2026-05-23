# 39 ADR-038 Runtime Entry Shell 统一入口体验

> 状态：**accepted**  
> 日期：2026-05-23  
> 范围：`/admin/bootstrap`、`/admin/user/login`、后续注册入口、首次初始化成功转场、登录成功进入 Chat 的视觉连续性  
> 关联：[36ADR-035登录页与Chat视觉统一ADR](36ADR-035登录页与Chat视觉统一ADR.md)、[37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR](37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR.md)、[../Features/RuntimeEntryShell统一入口设计方案](../Features/RuntimeEntryShell统一入口设计方案.md)、[../Features/LoginFeature](../Features/LoginFeature.md)

---

## 1. 背景

当前 `/admin/bootstrap` 首次初始化页仍使用深蓝黑背景、玻璃卡片、强紫色渐变按钮和居中 Hero 式表单。用户在浏览器评审中建议：

- 页面分成左右两部分；
- 左侧展示软件形象图或 CSS 动画；
- 右侧是悬浮认证卡片；
- 系统未初始化时显示初始化卡片；
- 初始化完成后通过旋转、淡入淡出等动效转场到 Login 卡片；
- 后续支持 Register 卡片；
- 登录成功后不要视觉硬跳转，而是通过动画转场进入 Chat。

`/admin/user/login` 已经部分采用 Runtime Entry 方向：暖色背景、左侧 Runtime Visual、右侧 Login card、`entering-chat` 状态和 SPA `history.replace`。但 Bootstrap 仍独立实现，导致首次进入产品时视觉风格与 Chat / Login 断裂。

此外，当前 Bootstrap 页面存在明确的产品体验问题：

- 深色玻璃拟态与 Chat 和整体软件设计语言冲突；
- 没有 PuddingAgent 产品形象，页面仍带有 Ant Design Pro 的默认页脚；
- 初始化流程缺少足够清晰的 UI 引导；
- 表单中存在不需要的“显示名称”字段；
- “重置”按钮语义不明，用户不知道它会重置表单、运行时还是系统状态；
- 如果提供明暗主题切换，必须默认浅色，并与全局主题一致。

本 ADR 决定抽象统一的 `RuntimeEntryShell`，让 Bootstrap、Login、Register 共享同一入口体验和转场语义。

---

## 2. 决策

### ADR-038-A：Bootstrap、Login、Register 必须共用 Runtime Entry Shell

入口页统一为：

```text
RuntimeEntryShell
├── RuntimeEntryVisual
└── AuthCardStage
    ├── BootstrapCard
    ├── LoginCard
    └── RegisterCard (future)
```

不得再让 `/bootstrap` 和 `/user/login` 各自维护一套背景、品牌、布局和视觉语言。

### ADR-038-B：入口页使用左右分栏，不再使用居中玻璃 Hero

桌面端：

- 左侧：软件形象、Pudding Runtime 品牌、Workspace / Agent / Skills / Chat 运行态拓扑、低频 CSS 动画；
- 右侧：浮动认证卡片；
- 页面整体最大宽度约 `1080-1120px`；
- 卡片宽度约 `400-440px`；
- 视觉壳层不使用深蓝黑玻璃拟态。

移动端：

- 单列布局；
- 左侧视觉收敛为品牌和简短上下文；
- 卡片宽度 `100%`；
- 首屏优先保证表单可用。

### ADR-038-C：入口页遵循 Quiet Runtime 视觉系统

入口页应与 Chat 同源：

| 维度 | 决策 |
|------|------|
| 背景 | `var(--warm-beige)`，不使用深蓝黑背景。 |
| 面板 | `var(--soft-white)` / 半透明暖白。 |
| 边框 | `earth-brown` 低透明度细线。 |
| 强调色 | `--accent-purple` 只用于主按钮、焦点、状态点。 |
| 圆角 | 常规容器 8px。 |
| 阴影 | `0 1px 6px rgba(0,0,0,0.04)` 级别。 |
| 动画 | 150-300ms opacity / translate / rotate 微转场；遵守 `prefers-reduced-motion`。 |

不得使用：

- 大面积深蓝黑背景；
- 玻璃模糊；
- 发光边框；
- 强紫色渐变按钮；
- hero-scale 营销文案；
- 长时间持续动画。

默认主题必须是浅色。暗色主题不是本轮入口页改造的前置条件；如果已有全局主题能力，则入口页可以在右上角提供图标型主题切换按钮，但必须满足：

- 默认值为 light；
- 只使用 Sun / Moon 图标或现有全局主题控件；
- 按钮具备 `aria-label`；
- 不因入口页局部实现生成第二套主题状态；
- 没有全局主题能力时，不为本页单独实现假切换。

### ADR-038-C2：入口页必须呈现 PuddingAgent 产品形象

入口页不再出现 Ant Design Pro 默认身份。产品身份统一为：

```text
PuddingAgent
https://github.com/PuddingAgent/PuddingAgent
```

左侧视觉必须体现 PuddingAgent 的产品定位，而不是纯装饰背景：

- `PuddingAgent` wordmark 或产品标识；
- 本地 AI Agent 工作台定位；
- Workspace / Agent / Skills / Chat 的运行态关系；
- 初始化过程中的状态反馈；
- 可用 CSS 动画表现 Runtime heartbeat / agent handoff，但不得使用深色玻璃和大面积发光。

页脚必须替换 Ant Design Pro 文案。允许的页脚形式：

```text
PuddingAgent · GitHub
```

其中 GitHub 链接指向 `https://github.com/PuddingAgent/PuddingAgent`。

### ADR-038-D：卡片状态显式建模

入口状态必须显式表达：

```ts
type EntryCardMode =
  | 'bootstrap'
  | 'bootstrap-success'
  | 'login'
  | 'register-disabled'
  | 'register'
  | 'entering-chat';
```

当前实现范围：

- `bootstrap`：系统未初始化时展示；
- `bootstrap-success`：初始化 API 成功后的短暂转场；
- `login`：已有用户登录；
- `register-disabled`：后端注册未开放时只展示禁用或说明，不提供假流程；
- `entering-chat`：登录成功后的短暂转场。

`register` 只作为未来状态保留，不在没有后端能力时开放。

### ADR-038-E：登录成功仍使用路由替换，但用户感知为动画进入 Chat

“不要通过跳转方式进入 Chat”解释为：不要视觉硬切换，而不是取消 SPA 路由。

正确流程：

1. 登录 API 成功；
2. 写入 token；
3. 刷新 `currentUser`；
4. 设置 `EntryCardMode = 'entering-chat'`；
5. 播放 180-300ms 转场；
6. `history.replace(normalizedRedirect || '/chat')`。

不得使用 `window.location.href = '/admin/chat'` 做硬跳转。

### ADR-038-F：初始化成功后根据 token 能力进入 Login 或 Chat

Bootstrap API 当前可能返回 token。处理规则：

| 情况 | 行为 |
|------|------|
| `POST /api/bootstrap/admin` 成功且返回 token | 写入 token，进入 `entering-chat`，再 `history.replace('/chat')`。 |
| 成功但不返回 token | 进入 `bootstrap-success`，再切到 `login` card。 |
| API 失败 | 保持 `bootstrap` card，展示错误。 |

不要在初始化成功后使用 `window.location.href`。

### ADR-038-F2：Bootstrap 表单只采集必要初始化信息

初始化管理员账号只保留必要字段：

| 字段 | 决策 |
|------|------|
| 登录用户名 | 保留。 |
| 邮箱地址 | 保留。 |
| 显示名称 | 移除。 |
| 密码 | 保留。 |
| 确认密码 | 保留。 |

不得在 Bootstrap card 中展示“显示名称”字段。后续用户资料展示名应进入用户资料或账号管理流程，不属于系统初始化路径。

Bootstrap card 不展示“重置”按钮。原因：

- “重置”在首次初始化语境下可能被理解为重置系统或运行时；
- 如果只是清空表单，收益低且容易误触；
- 如果未来需要清空表单，按钮文案必须是“清空表单”，并放在次要区域，不得使用“重置”。

### ADR-038-F3：初始化过程必须有明确 UI 引导

Bootstrap card 需要展示初始化过程，而不是只给一个表单：

```text
1. 创建管理员身份
2. 设置安全凭证
3. 进入 PuddingAgent Runtime
```

引导必须与真实状态绑定：

- 初始态高亮第 1 步；
- 用户填写密码时高亮安全凭证；
- API 提交时显示 sealing / initializing 状态；
- 成功后进入 runtime entering 状态；
- 失败时停留在当前步骤并展示错误。

步骤文案必须简短，不使用会让用户误解系统已经完成初始化的虚假完成态。

### ADR-038-G：入口壳必须可测试

组件必须提供稳定 test id：

```text
runtime-entry-shell
runtime-entry-visual
runtime-entry-card-stage
runtime-entry-product-visual
runtime-entry-footer
theme-mode-toggle
auth-card-bootstrap
auth-card-login
auth-card-register-disabled
```

测试必须覆盖：

- bootstrap 页渲染统一 shell；
- bootstrap 成功后的 card mode 变化；
- login 成功后进入 `entering-chat`；
- redirect 归一化；
- bootstrap 不出现显示名称字段；
- bootstrap 不出现不明“重置”按钮；
- footer 使用 PuddingAgent GitHub 链接；
- reduced motion 下不依赖动画完成业务。

---

## 3. 取舍

### 方案 A：只把 Bootstrap 改成浅色

优点：改动最小。  
缺点：Bootstrap 和 Login 仍重复布局、重复样式、重复转场逻辑。  
结论：不采纳。

### 方案 B：抽 `RuntimeEntryShell`，Bootstrap/Login 共用（采纳）

优点：统一入口体验，减少重复，后续 Register 能自然接入。  
缺点：需要拆组件和补测试。  
结论：采纳。

### 方案 C：所有入口都合并到一个 `/entry` 路由

优点：状态集中。  
缺点：会触碰现有 bootstrap/login 路由守卫、redirect、权限判断，风险偏大。  
结论：暂不采纳。保留 `/bootstrap` 和 `/user/login` 路由，但共享组件。

---

## 4. 影响范围

```text
Source/PuddingPlatformAdmin/src/pages/bootstrap/index.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx
Source/PuddingPlatformAdmin/src/app.tsx
Source/PuddingPlatformAdmin/src/locales/*/pages.ts
Source/PuddingPlatformAdmin/src/layouts/UserLayout.tsx
```

新增建议：

```text
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/index.tsx
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/styles.ts
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/types.ts
Source/PuddingPlatformAdmin/src/components/RuntimeEntryShell/RuntimeEntryShell.test.tsx
Source/PuddingPlatformAdmin/src/pages/bootstrap/bootstrap.test.tsx
```

---

## 5. 验收标准

1. `/admin/bootstrap` 不再出现深蓝黑玻璃拟态页面。
2. 默认主题为浅色；没有全局主题能力时不新增局部假主题切换。
3. 如果展示明暗主题按钮，按钮为图标型、具备 `aria-label`，并复用全局主题状态。
4. `/admin/bootstrap` 和 `/admin/user/login` 使用同一 `RuntimeEntryShell`。
5. 桌面端为左侧 PuddingAgent 产品形象、右侧浮动卡片；移动端无水平溢出。
6. 页脚不再出现 `Ant Design Pro`、`Ant Design`、`Powered by Ant Design`。
7. 页脚展示 PuddingAgent GitHub 链接：`https://github.com/PuddingAgent/PuddingAgent`。
8. Bootstrap 表单不出现“显示名称”字段。
9. Bootstrap card 不出现语义不明的“重置”按钮。
10. Bootstrap card 有初始化步骤引导和提交过程状态。
11. 初始化成功后不硬跳转；有 token 时动画进入 Chat，无 token 时动画切到 Login card。
12. 登录成功后先进入 `entering-chat`，再通过 `history.replace` 进入 Chat。
13. Register 未开放时不展示可执行假注册流程。
14. `prefers-reduced-motion` 下业务流程不依赖动画。
15. 单测覆盖 bootstrap card、login card、转场状态、footer 品牌和 redirect 归一化。

---

## 6. 后续

后续真正开放注册时，只需要接入 `RegisterCard`，不得新增第三套入口页面。注册能力的 API、权限和安全策略需要另立 ADR。
