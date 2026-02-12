---
name: ui-ux-pro-max
description: "Comprehensive UI/UX design system for web and mobile apps. Use when: designing, building, creating, implementing, reviewing, fixing, or improving UI/UX. Covers 67 styles, 96 color palettes, 57 font pairings, 99 UX guidelines, 25 chart types across 13 stacks (html-tailwind, react, nextjs, vue, svelte, shadcn, flutter, swiftui, react-native, jetpack-compose). Triggers: landing page, dashboard, SaaS, e-commerce, design system, color palette, typography, glassmorphism, dark mode, responsive, accessibility, animations."
argument-hint: "Describe the UI/UX task, e.g. 'landing page for fintech SaaS' or 'dashboard for healthcare'"
---

# UI/UX Pro Max

Web 和移动端应用的专业 UI/UX 设计系统，包含完整的设计数据库和工作流程。

## 工作流程

### Step 1: 分析需求
- **产品类型**：SaaS、电商、作品集、仪表盘、落地页等
- **风格关键词**：极简、活泼、专业、优雅、暗色模式等
- **行业**：医疗、金融科技、游戏、教育等
- **技术栈**：React、Vue、Next.js，默认使用 `html-tailwind`

### Step 2: 生成设计系统（必须）

```bash
python3 ./scripts/search.py "<product_type> <industry> <keywords>" --design-system [-p "Project Name"]
```

持久化保存（跨会话）：
```bash
python3 ./scripts/search.py "<query>" --design-system --persist -p "Project Name"
```

### Step 3: 补充详细搜索

| 需求 | 领域 | 示例 |
|------|------|------|
| 更多样式选项 | `style` | `--domain style "glassmorphism dark"` |
| 图表推荐 | `chart` | `--domain chart "real-time dashboard"` |
| UX 最佳实践 | `ux` | `--domain ux "animation accessibility"` |
| 字体选项 | `typography` | `--domain typography "elegant luxury"` |
| 落地页结构 | `landing` | `--domain landing "hero social-proof"` |

```bash
python3 ./scripts/search.py "<keyword>" --domain <domain> [-n <max_results>]
```

### Step 4: 技术栈指南（默认 html-tailwind）

```bash
python3 ./scripts/search.py "<keyword>" --stack html-tailwind
```

可用技术栈：`html-tailwind`、`react`、`nextjs`、`vue`、`svelte`、`swiftui`、`react-native`、`flutter`、`shadcn`、`jetpack-compose`

## 可用领域

| 领域 | 用途 | 示例关键词 |
|------|------|-----------|
| `product` | 产品类型推荐 | SaaS, e-commerce, portfolio, healthcare, beauty |
| `style` | UI 样式、颜色、效果 | glassmorphism, minimalism, dark mode, brutalism |
| `typography` | 字体搭配，Google Fonts | elegant, playful, professional, modern |
| `color` | 按产品类型的颜色方案 | saas, ecommerce, healthcare, beauty, fintech |
| `landing` | 页面结构，CTA 策略 | hero, testimonial, pricing, social-proof |
| `chart` | 图表类型，库推荐 | trend, comparison, timeline, funnel, pie |
| `ux` | 最佳实践，反模式 | animation, accessibility, z-index, loading |
| `web` | Web 界面指南 | aria, focus, keyboard, semantic, virtualize |

## 专业 UI 通用规则

### 图标与视觉元素
- **不用 emoji 作图标**，使用 SVG（Heroicons、Lucide、Simple Icons）
- **悬停状态稳定**，用颜色/透明度过渡，不用位移变换
- **统一图标尺寸**，固定 viewBox(24x24)，w-6 h-6

### 交互
- 所有可点击元素加 `cursor-pointer`
- 提供清晰的悬停视觉反馈（颜色、阴影、边框）
- 使用 `transition-colors duration-200` 平滑过渡

### 亮/暗模式对比度
- 亮模式玻璃卡片：`bg-white/80` 或更高不透明度
- 正文文字：`#0F172A`（slate-900），不用 gray-400 或更浅
- 边框可见：亮模式用 `border-gray-200`

### 布局与间距
- 浮动导航栏：添加 `top-4 left-4 right-4` 间距
- 保持统一最大宽度（`max-w-6xl` 或 `max-w-7xl`）
- 固定导航栏下方内容要有足够内边距

## 交付前检查清单
- [ ] 无 emoji 作为图标（用 SVG 替代）
- [ ] 所有可点击元素有 `cursor-pointer`
- [ ] 悬停状态提供清晰视觉反馈
- [ ] 亮模式文字对比度满足 4.5:1
- [ ] 响应式：375px、768px、1024px、1440px
- [ ] 移动端无横向滚动
- [ ] 图片有 alt 文字，表单输入有 label
- [ ] 尊重 `prefers-reduced-motion`
