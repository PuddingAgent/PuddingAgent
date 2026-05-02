---
name: web-design-guidelines
description: Use when reviewing UI/UX code for web design standards compliance. Review Vue components, XAML views, CSS, or frontend code against web interface best practices. Triggers: "review my UI", "check accessibility", "audit design", "review UX", "check my site against best practices".
argument-hint: "指定要审查的UI文件，例如 'Source/Pudding.PlatformAdmin/src/' 或 'Source/Pudding.Agent/Views/'"
---

# Web Design Guidelines — 前端界面规范审查

## 概述

对照 Vercel Web Interface Guidelines 和 Pudding UI 规范，
审查前端代码的可用性、可访问性和交互设计。

**核心原则：好的界面不需要说明书。**

## 审查流程

### 步骤 1: 读取规范

审查前先读取最新的 Vercel Web 界面规范：

```bash
# 获取最新规范（手动或通过 web fetch）
# 源: https://raw.githubusercontent.com/vercel-labs/web-interface-guidelines/main/command.md
```

同时在本地规范中读取：
- `Doc/UI-Guidelines.md` — Pudding UI/UX 设计规范
- `.github/skills/ui-ux-pro-max/SKILL.md` — 设计系统参考

### 步骤 2: 确定审查范围

- 用户指定的文件/目录
- 若未指定，询问审查哪些文件

### 步骤 3: 逐规则检查

输出格式：`文件路径:行号 — 问题描述`

## Pudding UI 技术栈

| 层 | 技术 | 规范重点 |
|----|------|---------|
| 层 | 技术 | 规范重点 |
|----|------|---------|
| Web 前端 | React + TypeScript | 响应式、可访问性、组件化 | XAML 布局、样式一致性、快捷键 |
| Web 前端 | Vue 3 + Pinia + Vite | 响应式、可访问性、状态管理 |
| Web 组件 | Pudding.WebUI / WPFWebComponents | WebView2 通信、边界处理 |

## React 前端审查规则

### 响应式与性能

- `- 条件渲染使用三元表达式或 &&
- 大列表使用虚拟滚动或分页
- 避免在 JSX 中使用复杂表达式

### 组件设计

- Props 有明确 TypeScript 类型
- 复杂组件拆分为容器组件（逻辑）和展示组件（UI）
- 使用 状态管理 管理跨组件状态，不通过 props 层层传递
- 事件命名使用 kebab-case

### 可访问性 (A11y)

- 所有图片有 `alt` 属性
- 表单输入有关联的 `<label>`
- 颜色不是传达信息的唯一方式
- 按钮和链接语义正确（`<button>` 用于操作，`<a>` 用于导航）
- `aria-label` 用于仅有图标的按钮

### 样式

- 使用 CSS 变量（`var(--primary)` 等）而非硬编码颜色
- 响应式设计：移动端优先（`@media (min-width: ...)`）
- 点击区域不小于 44×44px（移动端）

## 输出格式示例

```
✅ Source/Pudding.PlatformAdmin/src/components/LoginForm.vue — 表单可访问性良好
❌ Source/Pudding.Agent/Views/SystemInfoWindow.xaml:45 — SelectionChanged 未检查 e.Source
⚠ Source/Pudding.PlatformAdmin/src/views/Dashboard.vue:120 — 大列表未使用虚拟滚动，500+ 条数据可能卡顿
```

| 标记 | 含义 |
|------|------|
| ❌ | 必须修复 |
| ⚠ | 建议修复 |
| ✅ | 通过 |
| ℹ | 信息/备注 |
