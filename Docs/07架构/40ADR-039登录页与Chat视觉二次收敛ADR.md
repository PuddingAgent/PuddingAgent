# 40 ADR-039 登录页与 Chat 视觉二次收敛

> 状态：**accepted**  
> 日期：2026-05-23  
> 范围：`/admin/user/login`、`RuntimeEntryShell` 入口视觉、登录到 Chat 的首屏连续性  
> 关联：[36ADR-035登录页与Chat视觉统一ADR](36ADR-035登录页与Chat视觉统一ADR.md)、[39ADR-038RuntimeEntryShell统一入口体验ADR](39ADR-038RuntimeEntryShell统一入口体验ADR.md)、[../Features/Login页与Chat视觉二次收敛施工方案](../Features/Login页与Chat视觉二次收敛施工方案.md)

---

## 1. 背景

`/admin/user/login` 已经从旧的深色玻璃拟态切换为浅色 Runtime Entry 页面，并保留了左右分栏、登录卡片、`entering-chat` 转场和 `history.replace` 跳转。这一方向是正确的。

但当前实现又产生了新的风格漂移：

- 页面背景使用黄绿径向光、米色渐变和网格纹理，视觉温度与 `/admin/chat` 的纯暖纸面不同；
- 左侧主视觉是森林、房屋、山丘、发光粒子，像宣传插画或主题皮肤，不像操作型 Chat 工作台的前厅；
- 登录卡片和插画容器使用 `18px` 圆角、较重阴影和玻璃模糊，偏离 Chat 的 `8px`、细边框和低阴影；
- 主按钮使用绿色渐变，与 Chat 的 `--accent-purple` 低频强调体系不一致；
- 文案中“森林边的本地运行工坊”和 `Local AI Workshop` 引入了独立叙事，会削弱 PuddingAgent 作为本地 AI Agent 工作台的产品身份。

本 ADR 决定对登录页做二次收敛：保留 Runtime Entry 的结构和转场，移除森林插画化表达，把登录页重新压回与 Chat 同源的 Quiet Runtime 视觉系统。

---

## 2. 决策

### ADR-039-A：登录页是 Chat 前厅，不是独立主题页

登录页必须看起来像 `/admin/chat` 的进入态，而不是另一套产品视觉。

允许保留：

- 左右分栏；
- 左侧 Runtime 关系表达；
- 右侧认证卡片；
- 登录成功后的 `entering-chat` 转场；
- `Workspace / Agent / Skills / Chat` 的轻量上下文。

必须移除或重写：

- 森林、房屋、山丘、树、云、太阳光等具象风景插画；
- `Local AI Workshop`、`森林边的本地运行工坊` 等独立主题文案；
- 黄绿大面积渐变和高饱和光晕；
- 与 Chat 无关的宣传式场景叙事。

### ADR-039-B：背景回到 Chat 的暖纸面基线

登录页背景采用与 Chat 相同的基线：

```css
background: var(--warm-beige);
```

允许叠加极弱纸面纹理，但必须满足：

- 透明度低，不形成视觉中心；
- 不使用黄绿径向光斑；
- 不使用大面积渐变；
- 不使用离散装饰圆点或光球；
- 与 Chat `layout` / `chatBody` 的背景观感一致。

### ADR-039-C：面板指标必须与 Chat 对齐

认证卡片、左侧 Runtime 视觉容器、标签和输入控件统一采用 Chat 的工作台指标：

| 维度 | 决策 |
|------|------|
| 圆角 | 常规容器 `8px`；品牌小标可到 `10-12px`；不得使用 `18px` 大圆角。 |
| 表面 | `var(--soft-white)` 或 `color-mix(in srgb, var(--soft-white) 86%-94%, transparent)`。 |
| 边框 | `color-mix(in srgb, var(--earth-brown) 6%-12%, transparent)`。 |
| 阴影 | 默认无阴影或 `0 1px 6px rgba(0,0,0,0.04)`；不得使用大投影。 |
| 模糊 | 不使用玻璃拟态；必要时仅在与 Chat header/input panel 一致的区域使用低强度 blur。 |
| 字号 | 标题控制在 `24-28px`，卡片标题约 `20-22px`，正文 `13-15px`。 |

### ADR-039-D：强调色回到 `--accent-purple`

登录主按钮、focus ring、当前节点状态点使用 `--accent-purple`。

主按钮样式：

```css
background: var(--accent-purple);
border-radius: 8px;
box-shadow: none;
```

hover 只允许轻微改变背景或透明度，不使用：

- 绿色渐变；
- 强投影；
- 发光；
- `translateY` 上浮；
- shimmer 或持续流光。

### ADR-039-E：Runtime Visual 改为抽象拓扑，不再用插画场景

左侧 Runtime Visual 表达的是系统对象关系，不是装饰插图。推荐结构：

```text
PuddingAgent
本地 AI Agent 工作台
连接工作空间、Agent 与 Skills，安静地理解，可靠地执行。

Workspace -> Agent -> Skills
                 |
                Chat
```

视觉要求：

- 使用低饱和标签、细线、小状态点、浅色面板；
- 拓扑节点最多 4 个；
- 可以有 150-300ms 的进入动效或低频状态点呼吸；
- `prefers-reduced-motion` 下关闭持续动画；
- 不展示功能卡片网格、统计数字、完整 Skill 列表或 Agent 选择器。

### ADR-039-F：登录转场和认证逻辑不变

本次只收敛视觉，不改变认证行为：

- `login({ ...values, type: 'account' })` 不变；
- `pudding_token` 写入不变；
- `initialState.fetchUserInfo` 不变；
- redirect 归一化不变；
- 成功后仍先进入 `entering-chat`，再 `history.replace(normalizedRedirect || '/chat')`；
- 不使用 `window.location.href`。

### ADR-039-G：RuntimeEntryShell 后续复用同一收敛结果

Bootstrap / Register 后续复用 `RuntimeEntryShell` 时，必须继承本 ADR 的视觉收敛规则。不得让 Bootstrap 使用一套风景插画、Login 使用另一套抽象拓扑。

入口壳共享后，允许差异只存在于右侧 card 内容和状态步骤，不允许差异存在于背景、品牌、边框、圆角、按钮色和左侧 Runtime Visual 语言。

### ADR-039-H：可访问性和移动端是验收项

登录页必须满足：

- 用户名、密码、自动登录都有可感知 label；
- 375px 宽度无横向滚动；
- 输入框和按钮高度不小于 `44px`；
- focus-visible 清晰；
- 主文本对比度不低于 4.5:1；
- `prefers-reduced-motion` 下无持续动画；
- 错误提示靠近表单并能被读屏感知。

---

## 3. 影响范围

主要修改：

```text
Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx
Source/PuddingPlatformAdmin/src/pages/user/login/__snapshots__/login.test.tsx.snap
```

参考文件：

```text
Source/PuddingPlatformAdmin/src/global.style.ts
Source/PuddingPlatformAdmin/src/pages/chat/styles.ts
Docs/Features/Login页与Chat视觉二次收敛施工方案.md
```

---

## 4. 验收标准

1. 打开 `http://localhost:5000/admin/user/login?redirect=%2Fadmin%2Fchat`，首屏不再出现森林、房屋、山丘或黄绿强渐变。
2. 登录页背景、边框、圆角、按钮和表面材质与 `/admin/chat` 看起来属于同一产品。
3. 主按钮使用 `--accent-purple` 实色，hover 不发光、不上浮。
4. 常规容器圆角为 `8px`。
5. 左侧只展示 PuddingAgent 品牌、简短定位和 Runtime 拓扑。
6. 登录成功仍进入 `entering-chat` 状态，再到 `/admin/chat`。
7. 错误登录、自动登录、redirect、token 写入行为不回归。
8. 375px、768px、1024px、1440px 下无布局溢出、文字重叠或按钮挤压。
9. 登录页单测和相关快照更新后通过。

---

## 5. 非目标

本 ADR 不要求：

- 重写 Chat 页面；
- 新增注册、找回密码、SSO；
- 修改认证 API；
- 修改 token 存储方式；
- 把 Bootstrap 迁入共享入口壳；
- 新增真实图片或品牌插画资源；
- 建立完整全局设计 token 重构。

本轮只做登录页视觉二次收敛，为后续 RuntimeEntryShell 共享化提供稳定基线。
