# QA 审阅报告 — Task-UI-03 全局视觉系统升级

| 字段 | 内容 |
|------|------|
| 任务 | Task-UI-03 全局视觉系统升级 |
| 开发者 | GPT-5.3-Codex (@dev) |
| 审阅者 | Claude Sonnet 4.6 (@qa-sonnet) |
| 初审日期 | 2026-05-03 |
| 重审日期 | 2026-05-03 |
| 构建状态 | ✅ `npm run build` 通过（Exit Code 0） |
| **最终结论** | **PASS_WITH_NOTES** |

---

## 验收标准逐条结果

| AC | 描述 | 结果 | 说明 |
|----|------|------|------|
| AC1 | 全局亮/暗主题切换，按钮在顶栏，默认跟随系统 | ✅ PASS | `ThemeToggleAction` 在 `actionsRender`，`getStoredThemeMode` 默认返回 `'system'`，localStorage 持久化，系统偏好 `matchMedia` 监听正确 |
| AC2 | 暗色主题覆盖所有页面，无亮色残留 | ✅ PASS | `ConfigProvider + darkAlgorithm` 覆盖所有 antd 组件，`html/body/#root` background+transition 经 `injectGlobal` 注入，主题切换有 200ms 平滑渐变 |
| AC3 | 基于 `createStyles` 建立 Design Token 体系，消除硬编码颜色 | ✅ PASS | 全部 11 个文件完成 token 迁移，页面级文件中未找到任何硬编码 hex 颜色（已验证） |
| AC4 | 主色 Violet `#7c3aed`，圆角 8px/16px | ✅ PASS | `defaultSettings.ts` 和 `ConfigProvider.token` 均设置 `colorPrimary: '#7c3aed'`，`borderRadius: 8`，`borderRadiusLG: 8`，`borderRadiusXL: 16` |
| AC5 | 品牌元素：微动效 Logo、首屏品牌插画 | ✅ PASS | `puddingLogoPulse` keyframe 通过 `injectGlobal` 正确注入，Logo 选择器（`.ant-pro-global-header-logo img` 等）`.` 前缀已修正，脉冲动效可生效 |
| AC6 | fadeIn 页面过渡（200ms）、slideUp 消息气泡（300ms）、按钮 hover scale(1.02) | ✅ PASS | 三个 keyframe 均通过 `injectGlobal` 注入 DOM，选择器前缀修正后全局按钮 hover/页面过渡/消息气泡动效均可生效 |

---

## 问题清单

### P1（阻断）

#### ISSUE-001：`global.style.ts` 从未被消费 — AC6 全部失效，AC5 Logo 动效失效

**位置**：`src/global.style.ts`

**现象**：
- 该文件定义了 `useStyles` hook 并 `export default useStyles`
- 经全局搜索确认，任何 `.tsx`/`.ts` 文件均未 `import` 此文件，hook 从未被调用
- 结果：`@keyframes fadeIn`、`@keyframes slideUp`、`@keyframes puddingLogoPulse` **永远不会注入 DOM**

**影响**：
1. `chat/index.tsx` 的 `messageRow` 样式引用 `animation: 'slideUp 300ms ease-out'` → 消息气泡无动画（silently fails）
2. `chat/index.tsx` 的 `onboardingLogo` 引用 `animation: 'puddingLogoPulse 2400ms ease-in-out infinite'` → Logo 无脉冲效果
3. `chat/index.tsx` 的 `onboardingIllustration` 引用 `animation: 'fadeIn 200ms ease-out'` → 品牌插画无淡入
4. 页面切换 fadeIn 过渡样式永远不生效
5. 全局按钮 `scale(1.02)` hover 效果永远不生效

**根因**：误用了 `createStyles`（适用于组件级 scoped 样式）来管理全局样式。全局 keyframe 注入应使用 `injectGlobal`（antd-style API）或在根组件中调用此 hook。

**修复方案**：
```tsx
// 方案 A（推荐）：改用 antd-style 的 injectGlobal，在 app.tsx 或 ThemeProviderContainer 中调用
import { injectGlobal } from 'antd-style';

// 在 ThemeProviderContainer 内部：
injectGlobal`
  @keyframes fadeIn { from { opacity: 0.32; } to { opacity: 1; } }
  @keyframes slideUp { from { opacity: 0; transform: translateY(8px); } to { opacity: 1; transform: translateY(0); } }
  @keyframes puddingLogoPulse { 0% { transform: scale(1); } 50% { transform: scale(1.035); } 100% { transform: scale(1); } }
`;

// 方案 B：在 ThemeProviderContainer 中调用 useStyles()，并将返回值应用到根 div（不推荐，需要包裹 div）
```

---

#### ISSUE-002：`global.style.ts` CSS 选择器语法错误 — 即使注入也不会生效

**位置**：`src/global.style.ts` 第 26–43 行

**现象**：以下选择器缺少 `.` 前缀：

```ts
'ant-layout': { ... }                            // 应为 '.ant-layout'
'ant-pro-sider.ant-layout-sider...': { ... }    // 应为 '.ant-pro-sider.ant-layout-sider...'
'ant-btn:not(.ant-btn-icon-only)': { ... }      // 应为 '.ant-btn:not(.ant-btn-icon-only)'
'ant-pro-layout .ant-pro-layout-content, ...': { ... } // 各选择器均缺 '.'
'ant-pro-global-header-logo img, ...': { ... }  // 各选择器均缺 '.'
```

**根因**：`createStyles` 的 key 是 CSS class name（会被 hash 为 scoped class），不是全局 CSS 选择器。就算 hook 被调用，`ant-layout` 也只会生成 `.ant-layout-[hash]` 的新类，无法覆盖 antd 组件的 `.ant-layout` 原生类。

**修复方案**：全局样式使用 `injectGlobal` 替代 `createStyles`，并确保选择器有正确的 `.` 前缀。

---

### P2（改进）

#### ISSUE-003：`borderRadiusXL` 不是 antd v5 标准 Token

**位置**：`src/app.tsx` `ThemeProviderContainer.themeConfig`、`src/pages/chat/index.tsx`

**现象**：`ConfigProvider.token.borderRadiusXL: 16` 不是 antd v5 `AliasToken` 的标准字段（标准字段为 `borderRadius`、`borderRadiusSM`、`borderRadiusLG`、`borderRadiusXS`）。构建通过但 TypeScript 类型不安全。

**建议**：使用自定义 `token` 类型声明扩展，或使用 `borderRadiusOuter` / CSS 变量替代。

---

#### ISSUE-004：`ThemeToggleAction` 的 `setInitialState` 依赖可能不稳定

**位置**：`src/app.tsx` `ThemeToggleAction` 第 136–155 行

**现象**：`useEffect` 依赖 `[isDark, setInitialState]`，若 UmiJS 的 `setInitialState` 函数引用每次 render 变化，会触发不必要的 navTheme 更新。虽有 `navTheme === nextNavTheme` 的 guard 防止无限循环，但仍可能造成不必要的 re-render。

**建议**：`setInitialState` 依赖使用 `useRef` 缓存，或确认 UmiJS 保证其引用稳定性。

---

#### ISSUE-005：`global.style.ts` 中 `html,body,#root` 背景过渡与 `global.less` 重复定义

**位置**：`src/global.style.ts` vs `src/global.less`

**现象**：`global.less` 已定义 `html, body, #root { height: 100%; margin: 0; ... }`，`global.style.ts` 中重复定义了这些元素的样式（加了 token-based 颜色和过渡）。需要协调两者，避免混乱。

**建议**：将 token-aware 的背景/过渡直接写在 `global.less` 里使用 CSS 变量（配合 `cssVar: true`），或集中在 `injectGlobal` 中处理。

---

## 正确实现验证

| 检查项 | 结论 |
|--------|------|
| `ThemeProviderContainer` 通过 `rootContainer` 包裹整个应用 | ✅ 正确 |
| `ConfigProvider + darkAlgorithm` 响应式切换 | ✅ 正确 |
| `localStorage` 持久化 + 系统偏好监听 | ✅ 正确，无内存泄漏（`removeEventListener` 正确清理）|
| `data-pudding-theme` 属性同步写入 `document.documentElement` | ✅ 正确 |
| `ThemeToggleAction` `aria-label="切换主题"` 可访问性 | ✅ 正确 |
| `onboardingIllustration` `aria-hidden="true"` 装饰性元素无障碍处理 | ✅ 正确 |
| 11 个页面 hardcoded hex 颜色清零 | ✅ 已验证，全部迁移至 token |
| `skill-management` 裸 `<input>` → antd `<Input>` 替换 | ✅ 正确 |
| `ThemeToggleAction` 双击重置到 `system` 模式 | ✅ 实现（`onDoubleClick={() => setThemeMode('system')}`）|
| 构建通过 | ✅ `npm run build` Exit Code 0 |

---

## 结论

**PASS_WITH_NOTES** — 两个 P1 阻断问题均已修复，全部 6 条 AC 通过：

- **ISSUE-001 修复验证**：`createStyles` hook 完全移除，改用 `injectGlobal` 模块顶层调用；`app.tsx` 第 19 行增加 `import './global.style'` 副作用导入，模块加载时三个 keyframe 正确注入 DOM。
- **ISSUE-002 修复验证**：所有全局 CSS 选择器均已补全 `.` 前缀（`.ant-layout`、`.ant-btn:not(...)`、`.ant-pro-layout` 等），`injectGlobal` 模板字符串语法正确。

遗留 P2（不阻断合并）：
- ISSUE-003：`borderRadiusXL: 16` 非 antd v5 标准 `AliasToken` 字段，TypeScript 类型不安全
- ISSUE-004：`ThemeToggleAction` 的 `setInitialState` 依赖稳定性存隐患，有 guard 防循环
- ISSUE-005：`injectGlobal` 与 `global.less` 对 `html/body/#root` 存在重复定义，建议后续整合

---

## 重审记录（2026-05-03）

| 修复项 | 修复方式 | 验证结论 |
|--------|---------|---------|
| ISSUE-001：global.style.ts 从未被消费 | `createStyles` → `injectGlobal` + `import './global.style'` | ✅ 修复正确 |
| ISSUE-002：CSS 选择器缺少 `.` 前缀 | 迁移至 `injectGlobal` 模板字符串，补全所有选择器前缀 | ✅ 修复正确 |

