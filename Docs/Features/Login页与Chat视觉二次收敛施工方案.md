# Login 页与 Chat 视觉二次收敛施工方案

> 日期：2026-05-23  
> 页面：`/admin/user/login?redirect=%2Fadmin%2Fchat`  
> ADR：[40ADR-039登录页与Chat视觉二次收敛ADR](../07架构/40ADR-039登录页与Chat视觉二次收敛ADR.md)

---

## 1. 目标

当前登录页已经不是旧的深色玻璃风格，但它偏成了“森林工坊插画入口”。本方案给 Dev 的施工目标是：保留 Runtime Entry 的左右分栏和登录转场，把视觉语言收敛回 `/admin/chat` 的 Quiet Runtime 工作台。

施工完成后，登录页应像 Chat 的前厅：

- 同一暖纸面背景；
- 同一低饱和边框；
- 同一 `8px` 圆角；
- 同一 `--accent-purple` 强调色；
- 同一克制、密集、可长期使用的操作型产品气质。

---

## 2. 当前偏差

| 当前实现 | 偏差 |
|----------|------|
| 黄绿径向光 + 米色渐变背景 | 比 Chat 更饱和、更像主题皮肤。 |
| 森林、房屋、山丘、云、树插画 | 具象叙事过强，和 Chat 的工作台界面断裂。 |
| `workshopScene` / `Local AI Workshop` | 引入独立概念，不是 PuddingAgent 核心对象。 |
| 认证卡片 `18px` 圆角和重阴影 | 偏离 Chat 的 `8px`、细边框、低阴影。 |
| 绿色渐变主按钮 | 偏离 Chat 的 `--accent-purple` 强调体系。 |
| 输入 focus 使用绿色 ring | 和 Chat 的紫色状态反馈不一致。 |
| hover 上浮、发光点、较强光效 | 视觉噪音高于 Chat。 |

---

## 3. 施工原则

1. **不改认证逻辑**：只动登录页结构和样式，不改 API、token、redirect、`fetchUserInfo`。
2. **不做新插画**：本轮不生成图片、不引入 SVG 大插画、不使用风景主题。
3. **不新增功能**：不开放注册、不加 Workspace/Agent 选择器、不展示完整 Skills。
4. **对齐 Chat token**：优先使用 `global.style.ts` 已有变量。
5. **先收敛再抽象**：可以继续在 `login/index.tsx` 内落地；后续 Bootstrap 共享化再抽组件。

---

## 4. 设计规格

### 4.1 页面背景

将 `container` 背景改为：

```ts
background: 'var(--warm-beige)'
```

允许保留极轻纸面纹理：

```ts
'&::before': {
  content: '""',
  position: 'fixed',
  inset: 0,
  pointerEvents: 'none',
  background:
    'linear-gradient(90deg, rgba(92, 74, 58, 0.025) 1px, transparent 1px), linear-gradient(0deg, rgba(92, 74, 58, 0.018) 1px, transparent 1px)',
  backgroundSize: '96px 96px',
  opacity: 0.45,
}
```

删除：

- `radial-gradient(circle at 19% 16%, rgba(255, 235, 174...))`;
- `radial-gradient(circle at 80% 78%, rgba(132, 176, 138...))`;
- `linear-gradient(145deg, #d9eadf...#f7ead1...)`。

### 4.2 左侧 Runtime Visual

删除当前具象插画相关样式和 JSX：

```text
workshopScene
cloud
mountainBack
hillFront
workshopHouse
chimney
windowGlow
path
pine
signalTrail
signalDot
sceneLabel
sceneTags
sceneTag
workshopPulse
```

替换为抽象 Runtime 拓扑：

```tsx
<div className={styles.runtimeMap} data-testid="runtime-entry-map" aria-hidden="true">
  <div className={styles.runtimeNode}>Workspace</div>
  <div className={styles.runtimeConnector} />
  <div className={styles.runtimeNode}>Agent</div>
  <div className={styles.runtimeConnector} />
  <div className={styles.runtimeNode}>Skills</div>
  <div className={styles.runtimeDropConnector} />
  <div className={cx(styles.runtimeNode, styles.runtimeNodePrimary)}>Chat</div>
</div>
```

推荐样式：

```ts
runtimeMap: {
  width: 'min(520px, 100%)',
  minHeight: 180,
  padding: 16,
  borderRadius: 8,
  border: '1px solid color-mix(in srgb, var(--earth-brown) 8%, transparent)',
  background: 'color-mix(in srgb, var(--soft-white) 72%, transparent)',
  display: 'grid',
  gridTemplateColumns: '1fr 40px 1fr 40px 1fr',
  alignItems: 'center',
  gap: 8,
}
```

移动端可隐藏拓扑或压缩为单行标签：

```ts
'@media (max-width: 520px)': {
  runtimeMap: { display: 'none' },
}
```

### 4.3 文案

替换左侧文案：

```text
PuddingAgent
本地 AI Agent 工作台
连接工作空间、Agent 与 Skills，安静地理解，可靠地执行。
```

删除：

```text
森林边的本地运行工坊
Local AI Workshop
```

右侧卡片标题建议：

```text
登录 PuddingAgent
继续进入 Chat，接管当前工作空间中的 Agent 与 Skills。
```

### 4.4 认证卡片

将 `panel` 收敛为 Chat 同族表面：

```ts
panel: {
  width: '100%',
  padding: '32px',
  borderRadius: 8,
  background: 'color-mix(in srgb, var(--soft-white) 94%, transparent)',
  border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
  boxShadow: '0 1px 6px rgba(0,0,0,0.04)',
  backdropFilter: 'none',
}
```

删除 `panel::before` 网格遮罩。卡片内部不需要再次叠纹理，避免和背景争抢层级。

### 4.5 输入框与 focus

输入框与 Chat 面板一致：

```ts
'& .ant-input-affix-wrapper': {
  minHeight: 44,
  borderRadius: 8,
  background: 'var(--soft-white)',
  borderColor: 'color-mix(in srgb, var(--earth-brown) 12%, transparent)',
}
'& .ant-input-affix-wrapper-focused': {
  borderColor: 'color-mix(in srgb, var(--accent-purple) 36%, transparent)',
  boxShadow: '0 0 0 2px color-mix(in srgb, var(--accent-purple) 16%, transparent)',
}
```

### 4.6 主按钮

替换当前绿色渐变：

```ts
submitBtn: {
  height: '44px !important',
  borderRadius: '8px !important',
  border: 'none !important',
  background: 'var(--accent-purple) !important',
  fontSize: '15px !important',
  fontWeight: 600,
  boxShadow: 'none !important',
  transition: 'background-color 160ms ease, opacity 160ms ease',
  '&:hover': {
    background: 'color-mix(in srgb, var(--accent-purple) 88%, #000) !important',
  },
}
```

删除：

- `linear-gradient(135deg, #31583d, #6f8f4d)`;
- `0 14px 28px rgba(55, 88, 58, 0.28)`;
- `transform: translateY(-1px)`;
- `filter: brightness(...)`。

### 4.7 标签和 chip

`Workspace / Agent / Skills / Chat` 标签保持低权重：

```ts
capability: {
  minHeight: 28,
  padding: '0 10px',
  borderRadius: 8,
  background: 'color-mix(in srgb, var(--soft-white) 82%, transparent)',
  border: '1px solid color-mix(in srgb, var(--earth-brown) 10%, transparent)',
  color: 'var(--earth-brown)',
  fontSize: 12,
  fontWeight: 600,
  boxShadow: 'none',
}
capabilityDot: {
  width: 5,
  height: 5,
  borderRadius: '50%',
  background: 'var(--accent-purple)',
  opacity: 0.55,
}
```

### 4.8 动效

保留：

- `shellEntering`；
- `panelEntering`；
- 150-240ms opacity / translate 转场。

删除：

- `workshopPulse`；
- 信号点持续发光；
- hover 上浮。

`prefers-reduced-motion` 下：

```ts
'@media (prefers-reduced-motion: reduce)': {
  shell: { transition: 'none' },
  panel: { transition: 'none' },
}
```

---

## 5. 文件级施工清单

### 5.1 修改 `Source/PuddingPlatformAdmin/src/pages/user/login/index.tsx`

1. 调整 `sceneTags` 保留不变。
2. 在 `useStyles` 中删除森林插画样式。
3. 新增 `runtimeMap`、`runtimeNode`、`runtimeConnector`、`runtimeDropConnector`、`runtimeNodePrimary` 样式。
4. 将 `container.background` 改为 `var(--warm-beige)`。
5. 将 `brandMark` 文案从 `Pudding Runtime` 改为 `PuddingAgent`。
6. 删除 `sceneTitle` 或不再渲染。
7. 将 `workshopScene` JSX 替换为 `runtimeMap` JSX。
8. 将 `panel`、输入框、focus、`submitBtn` 改为本方案规格。
9. 将右侧标题从 `登录 Pudding Runtime` 改为 `登录 PuddingAgent`。
10. 保留 `handleSubmit`、`navigateToChat`、`normalizeRouteTarget` 逻辑。

### 5.2 修改 `Source/PuddingPlatformAdmin/src/pages/user/login/login.test.tsx`

测试应覆盖：

1. 页面渲染 `runtime-entry-shell`。
2. 页面渲染 `runtime-entry-map`。
3. 页面渲染 `auth-card-login`。
4. 登录成功后 `data-transition="entering-chat"`。
5. redirect 从 `/admin/chat` 归一化后进入 `/chat`。
6. 不再断言旧的 `workshop-illustration`。

### 5.3 修改快照

如果当前测试使用快照，更新：

```text
Source/PuddingPlatformAdmin/src/pages/user/login/__snapshots__/login.test.tsx.snap
```

快照里不应再出现：

```text
workshop-illustration
Local AI Workshop
森林边的本地运行工坊
```

---

## 6. 验证命令

在 `Source/PuddingPlatformAdmin` 下执行：

```powershell
npm run jest -- src/pages/user/login/login.test.tsx --runInBand
```

如果项目使用类型检查：

```powershell
npm run typecheck
```

如果没有 `typecheck` 脚本，执行：

```powershell
npm run build
```

浏览器验收地址：

```text
http://localhost:5000/admin/user/login?redirect=%2Fadmin%2Fchat
http://localhost:5000/admin/chat
```

检查视口：

```text
375px
768px
1024px
1440px
```

---

## 7. 验收清单

- [ ] 登录页背景主色为 `var(--warm-beige)`，没有黄绿强渐变。
- [ ] 首屏没有森林、房屋、山丘、云、树等具象插画。
- [ ] 左侧展示 PuddingAgent 品牌和抽象 Runtime 拓扑。
- [ ] 常规容器圆角为 `8px`。
- [ ] 认证卡片使用 `var(--soft-white)` / 暖白半透明表面。
- [ ] 主按钮为 `--accent-purple` 实色。
- [ ] hover 不发光、不上浮、不使用渐变。
- [ ] 输入 focus 使用紫色低透明 focus ring。
- [ ] 登录成功进入 `entering-chat` 后再进入 Chat。
- [ ] 登录失败错误提示仍正常展示。
- [ ] 自动登录 checkbox 行为不变。
- [ ] redirect 到 `/admin/chat` 行为不变。
- [ ] 375px 下无横向滚动、文字重叠或按钮溢出。
- [ ] `prefers-reduced-motion` 下没有持续动画。
- [ ] 登录页测试通过。

---

## 8. 非目标

本次不做：

- 重写 Chat 页面；
- 迁移 Bootstrap 页面；
- 抽象共享 `RuntimeEntryShell` 组件；
- 新增注册能力；
- 新增主题切换；
- 修改后端认证接口；
- 引入图片、SVG 大插画或第三方视觉资源。

这次施工只解决登录页和 Chat 页之间的风格、颜色、圆角、按钮和主视觉不一致问题。
