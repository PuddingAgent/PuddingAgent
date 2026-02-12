# 37 ADR-036 Admin Console 去 Ant Design Pro 化与 Pudding 设计语言统一

> 状态：**proposed**  
> 日期：2026-05-23  
> 范围：`/admin/*` Console 壳层、导航、页头、列表/表格/表单/详情页面视觉语言；不改变后端 API 和业务数据契约  
> 触发：浏览器评论指出 `/admin/workspace` 仍呈现 Ant Design Pro 风格，与 Pudding 整体设计语言冲突  
> 关联：[20AdminChat简约克制界面ADR](20AdminChat简约克制界面ADR.md)、[../Features/PuddingUiUxRedesign](../Features/PuddingUiUxRedesign.md)、[../Features/AdminConsole去AntDesignPro化代码级设计方案](../Features/AdminConsole去AntDesignPro化代码级设计方案.md)

---

## 1. 背景

`/admin/chat` 已经通过“静谧书斋式 AI Agent”的方向逐步脱离 Ant Design Pro 的后台模板感；但 `/admin/workspace` 等 Console 页面仍明显继承 Ant Design Pro：

- 页面底部出现 `Ant Design Pro`、`Ant Design`、`Powered by Ant Desgin` 链接和文案；
- ProLayout 默认侧栏、页头、水印、SettingDrawer、模板链接仍是产品壳层的一部分；
- 页面结构使用 `PageContainer`、`ProTable`、`DefaultFooter` 的默认语义；
- 管理页大量 inline style 自行拼接玻璃卡片、紫色按钮和 Pro 表格，缺少统一 token；
- 卡片视图在桌面端过于营销化，信息密度不足，且和 Chat 的“安静纸面”气质不一致。

这不是单个页面的 CSS 问题，而是 Console 壳层仍以 Ant Design Pro 作为默认产品语言。Pudding 的定位已经在 `PuddingUiUxRedesign.md` 中明确：主界面是私人 AI 代理，后台是低频工具箱。因此 Admin Console 必须服务于 Pudding 的整体产品气质，而不是继续呈现企业后台模板。

---

## 2. 决策驱动因素

| 驱动因素 | 说明 |
|----------|------|
| 产品一致性 | Admin Console 必须和 Chat、Login、Runtime 空间共享 Pudding 设计语言。 |
| 低频工具箱定位 | Console 是配置和诊断工具，不是主舞台；视觉应安静、紧凑、可扫描。 |
| 保留工程效率 | 允许继续使用 Ant Design 的底层组件能力，但不能继承 Ant Design Pro 的模板外观。 |
| 可施工性 | 先统一 shell、token、wrapper，再逐页迁移，避免一次性重写全部页面。 |
| 可回归验证 | 通过静态扫描、Playwright 截图和可访问性检查阻断模板痕迹回归。 |

---

## 3. 方案对比

### 方案 A：继续使用 Ant Design Pro，仅微调主题色

- **做法**：保留 ProLayout、PageContainer、DefaultFooter 和 ProTable 默认外观，只改主题色、圆角、背景。
- **优点**：改动最小，风险低。
- **缺点**：无法消除模板页脚、水印、Pro 页头和企业后台气质；新页面仍会自然回到 Pro 默认模式。
- **结论**：不采纳。

### 方案 B：移除 Ant Design 和 Pro Components，全面重写 UI

- **做法**：放弃 antd/pro-components，改为自研组件库或 Tailwind/shadcn 风格。
- **优点**：视觉控制力最强。
- **缺点**：改造范围过大；现有表格、表单、弹窗、国际化和 Umi Max 集成成本高；不符合本阶段收益。
- **结论**：不采纳。

### 方案 C：保留 Ant Design 能力，建立 Pudding Admin Shell 和 Wrapper（采纳）

- **做法**：保留 `antd` 组件能力；逐步减少 `@ant-design/pro-components` 默认 UI 暴露；用 Pudding tokens、shell、page header、toolbar、surface、data table wrapper 接管视觉语言。
- **优点**：兼顾施工成本、可维护性和产品一致性；可以先从 `/admin/workspace` 样板页开始增量迁移。
- **缺点**：短期仍会存在部分 Pro 组件依赖，需要迁移门禁防止新债务。
- **结论**：采纳。

---

## 4. 决策

### ADR-036-A：Admin Console 的设计定位是“低频工具箱”

Admin Console 不再采用 Ant Design Pro 的“企业后台控制台”气质。它的设计定位是：

> Pudding 主体验旁边的安静配置工具箱。

这意味着：

1. Chat 是主舞台，Console 是配置、诊断、资源管理入口；
2. Console 页面应强调可扫描、可编辑、可审计，而不是卡片化展示或营销式首屏；
3. 桌面端默认优先使用表格/列表，卡片仅用于少量实体摘要、移动端或需要状态概览的页面；
4. 页面标题、说明、操作区应紧凑，不使用 Pro 的大页头和模板 footer。

### ADR-036-B：保留 `antd`，限制 `@ant-design/pro-components` 默认视觉

允许继续使用：

- `antd` 的 Button、Input、Select、Table、Form、Modal、Drawer、Tooltip、Popover、Tag、Badge；
- `@ant-design/icons`；
- `ConfigProvider` 和 theme token；
- 经过 Pudding wrapper 包裹后的 Pro 能力。

限制使用：

- `PageContainer` 不得作为新页面默认壳层；
- `DefaultFooter` 不得进入生产页面；
- `ProTable` 不得直接暴露默认卡片、工具栏、密度、页头外观；
- `SettingDrawer` 仅允许开发环境且必须隐藏在调试入口后，不能常驻渲染到普通页面；
- `waterMarkProps` 不得用于普通 Console 页面。

### ADR-036-C：新增 Pudding Admin Shell 作为唯一 Console 壳层

Console 页面统一落入以下壳层：

```text
PuddingAdminShell
├── PuddingSideNav
├── PuddingTopBar
└── PuddingMain
    ├── PuddingPageHeader
    ├── PuddingToolbar
    └── PageContent
```

壳层职责：

- `PuddingSideNav`：窄侧栏，图标优先，文本 tooltip，当前项弱紫强调；
- `PuddingTopBar`：品牌、当前位置、全局操作、用户菜单；
- `PuddingPageHeader`：页面标题、简短描述、主操作、低频视图切换；
- `PuddingToolbar`：搜索、筛选、批量动作、刷新；
- `PageContent`：页面主体，使用表格、详情、表单或实体卡片。

### ADR-036-D：Pudding Admin Tokens 是唯一视觉事实源

新增或整理 admin 语义 token：

| Token | Light | Dark | 用途 |
|-------|-------|------|------|
| `--pudding-bg` | `#f5f0e8` | `#0b1020` | 页面底色 |
| `--pudding-bg-subtle` | `#ede5d9` | `#111827` | 次级底色 |
| `--pudding-surface` | `#fafaf7` | `#172033` | 主纸面 |
| `--pudding-surface-muted` | `#f2eee7` | `#1f2937` | hover/弱区域 |
| `--pudding-border` | `rgba(92,74,58,.12)` | `rgba(167,139,250,.18)` | 分隔线 |
| `--pudding-text` | `#1a1a2e` | `#f8fafc` | 主文本 |
| `--pudding-text-muted` | `#5c4a3a` | `#cbd5e1` | 次级文本 |
| `--pudding-accent` | `#7c3aed` | `#a78bfa` | 品牌强调 |
| `--pudding-accent-soft` | `rgba(124,58,237,.08)` | `rgba(167,139,250,.12)` | 当前态/轻强调 |
| `--pudding-success` | `#4f7f58` | `#86efac` | 成功 |
| `--pudding-warning` | `#b7791f` | `#facc15` | 警告 |
| `--pudding-danger` | `#b42318` | `#fca5a5` | 风险 |

组件不得使用散落的 `#7c3aed`、`rgba(124,58,237,...)`、大面积蓝色背景或 Ant Pro 默认蓝作为页面级样式。

### ADR-036-E：`/admin/workspace` 作为第一批样板页

`/admin/workspace` 必须先完成样板迁移：

1. 移除 `PageContainer`；
2. 移除 Pro 页脚可见痕迹；
3. 桌面端默认表格视图，卡片视图作为辅助；
4. 使用 `PuddingPageHeader`、`PuddingToolbar`、`PuddingDataTable`、`PuddingStatusBadge`；
5. 新建场景为唯一主按钮；
6. 删除、进入 Chat 等操作收敛为表格行操作，图标按钮必须有 tooltip 和 `aria-label`；
7. 默认场景的“内置”状态用低饱和 badge，不使用醒目蓝 tag。

### ADR-036-F：模板痕迹是发布阻断项

以下内容不得出现在生产 Console 界面：

- `Ant Design Pro`；
- `Powered by Ant Design` 或拼写错误的 `Powered by Ant Desgin`；
- Ant Design Pro GitHub 链接；
- Pro 默认 footer；
- Pro 默认水印；
- 普通用户可见 SettingDrawer；
- `/admin` 普通页面的大面积 Ant Pro 蓝色模板背景。

### ADR-036-G：本 ADR 不改变业务能力

本 ADR 不要求：

- 新后端 API；
- 新数据库字段；
- 新权限模型；
- 新路由语义；
- 重写 Chat 页面；
- 一次性迁移全部管理页。

本 ADR 只确定 Admin Console 设计语言、组件边界、迁移顺序和验收门禁。

---

## 5. 影响面

| 文件 / 区域 | 影响类型 | 说明 |
|-------------|----------|------|
| `Source/PuddingPlatformAdmin/src/app.tsx` | 壳层收敛 | 移除/替换 footer、watermark、links、SettingDrawer 默认暴露。 |
| `Source/PuddingPlatformAdmin/config/defaultSettings.ts` | ProLayout 降噪 | 保留必要配置，移除不再代表产品语言的 Pro 默认行为。 |
| `Source/PuddingPlatformAdmin/src/global.style.ts` | token | 增加 Pudding admin tokens 和全局覆盖。 |
| `Source/PuddingPlatformAdmin/src/components/PuddingAdminShell/*` | 新增 | Console 唯一壳层。 |
| `Source/PuddingPlatformAdmin/src/components/PuddingPageHeader/*` | 新增 | 替代 `PageContainer` header。 |
| `Source/PuddingPlatformAdmin/src/components/PuddingToolbar/*` | 新增 | 统一筛选、搜索、视图切换。 |
| `Source/PuddingPlatformAdmin/src/components/PuddingDataTable/*` | 新增 | 包裹 antd Table / ProTable 能力。 |
| `Source/PuddingPlatformAdmin/src/pages/workspace/index.tsx` | 样板迁移 | 首个落地页面。 |
| `Source/PuddingPlatformAdmin/src/components/Footer/index.tsx` | 删除或替换 | 不再渲染 Ant Design Pro footer。 |
| `Source/PuddingPlatformAdmin/README.md`、`package.json` | 模板清理 | 后续清理 Ant Design Pro 项目描述。 |

---

## 6. 迁移顺序

1. **Shell hardening**：移除生产环境 Ant Pro footer、水印、模板链接和常驻 SettingDrawer。
2. **Token foundation**：在 `global.style.ts` 和 `ThemeMode` 中定义 Pudding Admin tokens。
3. **Wrapper components**：新增 shell/header/toolbar/table/status/card wrapper。
4. **Workspace sample**：迁移 `/admin/workspace`，作为视觉与代码样板。
5. **Management pages migration**：迁移用户、角色、团队、能力、KeyVault、模型池、模板管理等页面。
6. **Template cleanup**：清理 README、package metadata、旧示例页、`ant-design-pro` service 命名。
7. **Regression gates**：增加静态扫描和 Playwright 截图验收。

---

## 7. 风险与缓解

| 风险 | 影响 | 缓解 |
|------|------|------|
| ProLayout 深度绑定 Umi Max | 一次性替换成本高 | 先 hardening，再引入自有 shell；必要时保留路由菜单数据。 |
| 现有页面大量使用 ProTable | 迁移成本高 | 先用 `PuddingDataTable` 包裹现有能力，再逐页替换。 |
| 视觉过度“Chat 化”导致管理页信息密度不足 | 管理效率下降 | Console 使用更紧凑表格和 toolbar；继承 token，不继承 Chat 的宽松消息布局。 |
| 暗色模式不一致 | 主题切换质量下降 | 所有 token 同时定义 light/dark，Playwright 双主题截图。 |
| 新页面继续直接用 Pro 默认组件 | 债务回流 | lint/static scan 阻断 `PageContainer`、`DefaultFooter`、裸 `ProTable` 默认使用。 |

---

## 8. 验收标准

### 8.1 产品痕迹

- 生产环境 `/admin/workspace` 不出现 `Ant Design Pro`、`Powered by Ant Design`、Ant Pro GitHub 链接。
- Console 页面不显示默认水印。
- 普通用户看不到 SettingDrawer。

### 8.2 视觉一致性

- `/admin/workspace` 与 `/admin/chat` 共享暖纸面、低饱和紫色、克制动效基线。
- 桌面端默认表格信息密度合理，卡片视图不抢占默认体验。
- 紫色只用于主操作、当前态、焦点态，不作为大面积背景。
- 卡片和 surface 圆角默认不超过 8px，特殊浮层不超过 12px。

### 8.3 可访问性

- 所有 icon-only button 有 `aria-label` 和 tooltip。
- 键盘焦点可见。
- 普通文本对比度达到 WCAG AA。
- 触屏场景交互目标不小于 44px，桌面紧凑操作不小于 36px。

### 8.4 工程门禁

- `npm run tsc` 通过。
- `npm run biome:lint` 通过。
- Playwright 截图覆盖 375、768、1024、1440 宽度。
- 静态扫描阻断模板文本和直接暴露的 Pro 默认组件。

---

## 9. 回滚策略

本 ADR 不改变业务数据和 API，回滚控制在前端层：

1. Shell hardening 可以通过恢复 `app.tsx` runtime layout 配置回滚；
2. token 和 wrapper 可逐页启用，不影响未迁移页面；
3. `/admin/workspace` 可保留旧实现分支直到样板页验收；
4. 若信息密度不足，优先调整 `PuddingDataTable` 密度和 toolbar，而不是恢复 Pro 默认页头和 footer。

---

## 10. 结论

采纳 **方案 C：保留 Ant Design 能力，建立 Pudding Admin Shell 和 Wrapper**。

Admin Console 的目标不是“换主题色的 Ant Design Pro”，而是 Pudding 主体验旁边的低频、安静、可信、可施工的管理工具箱。`/admin/workspace` 是第一批样板页，必须先完成去模板化和 token 化，再推广到其他管理页。
