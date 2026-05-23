# 36 ADR-035 登录页与 Chat 视觉统一

> 状态：**accepted**  
> 日期：2026-05-23  
> 范围：`/admin/user/login` 首屏登录体验、Runtime Entry Shell、登录成功转场、主题 token 使用、与 `/admin/chat` Quiet Runtime 视觉语言的一致性  
> 关联：[20AdminChat简约克制界面ADR](20AdminChat简约克制界面ADR.md)、[34ADR-033AdminChatComposer浮层重设计ADR](34ADR-033AdminChatComposer浮层重设计ADR.md)、[../Features/Login页与Chat视觉统一设计方案](../Features/Login页与Chat视觉统一设计方案.md)、[../Tasks/task14-skill-plugin.md](../Tasks/task14-skill-plugin.md)

---

## 1. 背景

登录页是用户进入 Pudding Runtime 的第一个页面。浏览器评审选中的当前页面使用深色玻璃拟态、强紫色渐变、16px 大圆角、发光阴影和居中营销式品牌卡片：

```text
深蓝黑背景 + 玻璃卡片 + 大号 Pudding Runtime 标题 + 紫色渐变主按钮
```

这与 `/admin/chat` 当前形成的产品语言不一致。Chat 页面已经收敛到 Quiet Runtime 方向：

- 暖纸张背景：`--warm-beige`、`--soft-white`；
- 低饱和分隔线：`color-mix(in srgb, var(--earth-brown) 6%-15%, transparent)`；
- 紫色只作为低频强调：`--accent-purple`；
- 8px 半径、紧凑头部、克制阴影；
- 运行状态采用渐进披露，不把系统能力做成炫技式视觉。

同时，Pudding 的 Skill / Plugin 方向强调“本地能力、可插拔、可靠执行”。登录页应成为这个工作台体验的入口，而不是独立的暗色落地页。

本 ADR 决定登录页采用与 Chat 一致的 Quiet Runtime 视觉系统，并将登录页改造成左右分栏的 Runtime Entry Shell：左侧承载软件形象与 Workspace / Agent / Skills / Chat 的轻量运行态动画，右侧承载浮动认证卡片。认证 API、token 写入和初始化判断保持不变；登录成功后使用页面内转场再进入 Chat。

---

## 2. 决策驱动因素

| 驱动因素 | 说明 |
|----------|------|
| 首屏一致性 | 用户登录后立即进入 `/admin/chat`，两个页面必须属于同一产品。 |
| 操作型产品气质 | Pudding 是本地 AI Agent 工作台，不是营销站；首屏要安静、可靠、可执行。 |
| 复用设计 token | 登录页应复用 `global.style.ts` 与 Chat 已使用的暖色、棕色、紫色 token。 |
| 降低视觉噪音 | 删除深色玻璃、发光、强渐变和大面积暗蓝背景，避免与 Chat 的纸面工作区冲突。 |
| 保持改动局部 | 优先改登录页结构与样式，不改变 Auth API；共享入口壳后续可供 Bootstrap / Register 复用。 |
| 可访问性 | 表单需要真实 label、明确错误提示、键盘焦点、移动端触达尺寸和对比度。 |

---

## 3. 方案对比

### 方案 A：只替换颜色，保留玻璃卡片结构

- **做法**：把深蓝背景替换为暖色，保留大玻璃卡、渐变按钮、发光阴影。
- **优点**：改动最小。
- **缺点**：结构仍然是营销式登录页；16px 玻璃卡和强视觉效果仍与 Chat 的 8px、低阴影、紧凑操作面板不一致。
- **结论**：不采纳。

### 方案 B：登录页改为 Runtime Entry Shell（采纳）

- **做法**：复用 Chat 的暖纸张背景、低饱和边框、8px 登录面板和克制紫色强调；页面分为左侧 Runtime Visual 与右侧认证卡片；登录成功先进入 `entering-chat` 视觉状态，再通过 SPA route replace 进入 Chat。
- **优点**：与 Chat 统一；首屏直接传达“本地运行时 / Agent 工作台 / Skill 能力”；为 Bootstrap / Register 卡片预留同一入口壳；跳转观感从硬切换变为连续转场。
- **缺点**：需要重写登录页样式，测试需要覆盖状态与转场。
- **结论**：采纳。

### 方案 C：直接跳过登录页，做 Chat 内嵌登录抽屉

- **做法**：匿名访问 `/admin/chat` 时弹出登录抽屉，登录后关闭。
- **优点**：入口和目标页面完全一体化。
- **缺点**：会触碰路由、鉴权、初始化状态和 ProLayout 初始化逻辑；当前需求只是视觉统一，改动过大。
- **结论**：不采纳。

---

## 4. 决策

### ADR-035-A：登录页必须使用 Quiet Runtime 设计语言

登录页默认视觉应与 `/admin/chat` 同源：

| 维度 | 采用 |
|------|------|
| 背景 | `var(--warm-beige)`，可叠加非常轻的纸面分区，不使用深蓝黑背景。 |
| 表面 | `var(--soft-white)` 或 `color-mix(in srgb, var(--soft-white) 86%, transparent)`。 |
| 边框 | `color-mix(in srgb, var(--earth-brown) 8%-14%, transparent)`。 |
| 强调 | `var(--accent-purple)` 只用于主按钮、焦点线、极少量状态点。 |
| 半径 | 常规容器 8px；品牌图标可使用 12px；不再使用 16px 玻璃登录卡。 |
| 阴影 | 默认无重阴影；只允许 `0 1px 6px rgba(0,0,0,0.04)` 级别的轻阴影。 |
| 动效 | 150-220ms opacity / translate 微动；遵守 `prefers-reduced-motion`。 |

### ADR-035-B：登录页是产品工作台入口，不是营销 Hero

首屏文案应该降低营销感，强调用户即将进入一个本地 Agent 运行环境：

```text
Pudding Runtime
本地 AI Agent 工作台
```

副文案可以表达：

```text
连接工作空间、Agent 与 Skills，安静地理解，可靠地执行。
```

不得把 slogan 放成超大 Hero，也不得用强发光、背景大渐变或虚拟科技装饰来表达“AI 感”。

### ADR-035-C：页面采用左右分栏 Runtime Entry Shell

桌面端采用左右两区：

- 左侧 `RuntimeEntryVisual`：展示 Pudding Runtime 的软件形象、Workspace / Agent / Skills / Chat 的轻量拓扑、低频 CSS 动画和当前运行态线索。
- 右侧 `AuthCardSwitcher`：浮动认证卡片区域，当前展示 login card；后续 bootstrap / register 复用同一壳层，不再各自创建独立视觉语言。

移动端改为单列堆叠，左侧视觉收敛为短文案与紧凑运行态区，认证卡片仍是唯一强边界容器。

### ADR-035-D：认证卡片采用可扩展状态机

认证卡片不应写死成单一登录页。入口壳需要支持以下状态：

| 状态 | 当前策略 |
|------|----------|
| `bootstrap` | 系统未初始化时展示初始化卡片。当前路由已有 Bootstrap 页面，本 ADR 要求后续将其迁入同一壳层。 |
| `login` | 当前默认状态，使用账号密码登录。 |
| `register` | 后期支持注册功能时启用；当前仅保留入口壳和禁用态提示，不暴露假功能。 |
| `entering-chat` | 登录成功后的短暂转场状态，卡片淡出 / 页面轻移后进入 Chat。 |

卡片切换使用 opacity / translate / rotate 的短动效，时长控制在 150-300ms，并遵守 `prefers-reduced-motion`。

### ADR-035-E：登录表单采用紧凑工作台面板

登录面板应像 Chat Composer / DevPanel 的同族组件：

- 宽度 360-420px；
- 8px border radius；
- 32-36px 外内边距；
- 输入框高度 40-44px；
- 按钮高度 40-44px；
- 表单项间距 14-18px；
- 主按钮使用实色 `--accent-purple`，hover 加深或提高透明度，不使用线性渐变。

### ADR-035-F：首屏需要保留轻量上下文，但不得堆功能说明

登录页允许显示一组非常轻的上下文线索，用于与 Chat / Skill 系统建立连续性：

- `Workspace`
- `Agent`
- `Skills`
- `Chat`

这些线索应是小号文本、状态点或低权重标签。它们只说明系统对象，不承担功能介绍，不写成长段落，不做卡片网格。

### ADR-035-G：语言切换和页脚必须融入同一视觉系统

当前语言切换按钮位于右上角，颜色为白色半透明，依赖深色背景。改版后：

- 语言按钮使用 `earth-brown` 低透明度；
- hover 使用浅棕填充；
- 右上角位置可保留；
- 页脚不再落在深色渐变背景上，应使用 `earth-brown` 低透明度文本；
- 如果页脚来自共享 `Footer`，登录页只负责容器背景和颜色兼容。

### ADR-035-H：认证 API 不变，跳转改为页面内转场后路由替换

本次设计不改变认证 API 和 token 语义：

- `login({ ...values, type: 'account' })`；
- token 写入 `localStorage.setItem('pudding_token', msg.token)`；
- 错误状态 `status === 'error'`；
- bootstrap / login 路由判断；
- i18n key 与默认中文文案。

成功后不再使用硬跳转语义，而是：

1. 设置页面状态为 `entering-chat`；
2. 播放短转场；
3. 通过 `history.replace(normalizedRedirect || '/chat')` 进入 Chat。

在当前 Umi base 下，`/chat` 对应用户看到的 `/admin/chat`。

### ADR-035-I：可访问性是验收项

登录表单必须满足：

- 用户名、密码、自动登录都有真实可感知 label 或 `aria-label`；
- 不以 placeholder 作为唯一标签；
- 错误提示靠近表单，并能被屏幕阅读器感知；
- 键盘 Tab 顺序为语言切换、用户名、密码、自动登录、提交；
- 输入框和按钮 focus-visible 清晰；
- 主文本对比度不低于 4.5:1；
- 移动端交互目标不小于 44px。

---

## 5. 影响范围

### 前端

主要文件：

```text
Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/__snapshots__/login.test.tsx.snap
Source/PuddingPlatformAdmin/jest.config.ts
Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx
Source/PuddingPlatformAdmin/src/components/RightContent/index.tsx
```

可参考但不直接耦合：

```text
Source/PuddingPlatformAdmin/src/global.style.ts
Source/PuddingPlatformAdmin/src/pages/chat/styles.ts
Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx
Source/PuddingPlatformAdmin/src/components/GlobalActions/index.tsx
```

### 后端

无后端变更。

### 路由与认证

认证路由和权限不变。登录成功由硬跳转改为 SPA route replace 前的短转场。Bootstrap 页面暂不迁移，但后续应复用 Runtime Entry Shell。

---

## 6. 验收标准

1. 打开 `http://localhost:5000/admin/user/login`，首屏不再出现深蓝黑玻璃拟态页面。
2. 登录页背景、边框、按钮、字体密度与 `/admin/chat` 看起来属于同一产品。
3. 登录成功后先出现短转场，再进入 `/admin/chat` 或 URL 中的 `redirect`。
4. 错误账号仍显示错误提示。
5. 375px、768px、1024px、1440px 下无水平滚动、文字重叠或按钮溢出。
6. 键盘可完成完整登录操作。
7. `prefers-reduced-motion` 下无持续动画。
8. 登录页测试更新后通过，不再保留旧玻璃拟态快照。

---

## 7. 后续

本 ADR 不要求在登录页实现完整 Skill 管理、Agent 选择、工作空间选择或注册能力。Bootstrap 初始化卡片和 Register 卡片作为同一壳层的后续增量。
