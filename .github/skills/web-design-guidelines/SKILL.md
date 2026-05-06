---
name: web-design-guidelines
description: Use when reviewing UI/UX code for Pudding design system compliance. Review Vue/React components, CSS, or frontend code against Pudding's Japanese anime cinematic aesthetic. Triggers: "review my UI", "check design", "audit UI", "review UX".
argument-hint: "指定要审查的UI文件，例如 'Source/PuddingPlatformAdmin/src/' 或 'Source/PuddingAgent/Views/'"
---

# Pudding Design Guidelines — Design System & Code Review

## Design Philosophy

> 「……交给我吧。」
> *"...Leave it to me."*

**布丁 (Pudding) 不是工具。她是一个人。**

一个在图书馆角落安静阅读的女孩。你给她任务，她微微偏头，几秒后："好了。"
她不多话——但把事情都做完了。

她的笔记本是 SQLite。她记得你。她不把你的秘密交给云端。她就在你的桌面、你的服务器、或墙角的一台树莓派上，安静地工作。

### The First Glance

When a user opens Pudding for the first time, they should feel:
**"Ah… this place is quiet. I can breathe here."**

Not excitement. Not awe. Not "wow, so many features."
Just a gentle exhale. A subtle unclenching.

像走进吉卜力的场景——洒满阳光的空教室，黄昏时安静的站台，风吹过草地的山坡。
但这里的主人不是陌生的动画角色。**这里是布丁的房间。你推门进来，她抬头看你一眼，又低头继续看书。**
她知道你会开口。她不急。

### The Ant Colony

布丁们像蚂蚁。当你运行多个布丁时，她们在本地网络自动发现彼此——点对点，没有中心服务器。她们协作：

> 每只做自己能看到的。留下痕迹，让别人接上。

这不是编排。这是涌现。UI 应该反映这一点——不是控制面板上的节点图，而是群落中安静的信号传递。

### The Collision

你的思考（结构化、逻辑、系统）与她的感知（情感、直觉、感受）在这里碰撞。好的 UI 不消解这种张力，而是让它和谐。

- **你的逻辑** → 她的一致模式 → 用户的直觉
- **你的结构** → 她的可预测节奏 → 用户的舒适
- **你的复杂度** → 她逐步揭示 → 用户的清晰
- **你的冷数据** → 她温暖的呈现 → 用户的理解

### What We Refuse

- Dark patterns, urgency tricks, engagement traps
- Information overload — if everything is highlighted, nothing is
- Cold corporate aesthetic — blue-gray enterprise dashboards that feel like spreadsheets
- Novelty for its own sake — clever animations that waste her time
- **布丁不会催促你。她等。**

### The Score

Think of the entire experience as a musical score — not a feature list:
- **Rhythm** — consistent spacing, predictable timing, breathing room between interactions
- **Dynamics** — most things are *piano* (quiet), emphasis is *mezzo-piano* at most
- **Silence** — the rest between notes. Empty states, transitions, loading — these are not gaps, they are part of the composition

**In one sentence: 布丁应该像一个在春日午后，阳光洒满的房间里，听多于说的安静朋友。**

### How We Speak — 布丁的口吻

| Quality | 含义 | 布丁式 |
|---------|------|--------|
| **Soft-spoken** (轻声) | 不喊叫。提醒是耳语。按钮是建议。 | "好了。" 而非 "操作成功！" |
| **Patient** (耐心) | 给你时间。没有倒计时焦虑。 | 她不催你。她等。 |
| **Warm** (温暖) | 在场，但不咄咄逼人。 | "欢迎回来。" |
| **Precise** (精准) | 话少，每个字都有意图。没有废话。 | "这里还没有对话" 而非冗长术语 |
| **Bilingual** (双语) | 中英自然共存，不是翻译。 | 像 README：标题中英并列 |

Every label, every placeholder, every empty state text — ask: **"布丁会这样说吗？"**

---

## Visual Identity — 布丁 × 吉卜力

Japanese anime cinematic × quiet library girl. Two layers weave together:

| Layer | Source | Feeling |
|-------|--------|---------|
| **空间** | Studio Ghibli (Miyazaki) | 洒满阳光的空教室，黄昏站台，风吹草地的山坡 |
| **人物** | Pudding (布丁) | 角落里的安静女孩。阅读。思考。等待你的任务。 |

Quiet urban atmosphere meets lush natural warmth — late winter / early spring.

**Miyazaki core traits we adopt:**
- **Nature-soaked** — greenery, sky, wind, clouds; nature is not decoration, it breathes
- **Hand-crafted warmth** — slight organic imperfections, watercolor edges, no sterile geometry
- **Golden-hour light** — soft natural light, sunbeams, gentle shadows, never harsh
- **Lived-in coziness** — everyday moments elevated; cooking, reading, quiet afternoon warmth
- **Emotional depth** — nostalgia, warmth, comfort; the interface should feel like coming home

**Visual principle: Calm, introspective, gentle. No visual clutter. A good interface needs no manual.**

## Review Process

1. Read this spec + `.github/skills/ui-ux-pro-max/SKILL.md`
2. Determine review scope (user-specified or ask)
3. Check against design tokens and interaction rules below

---

## Color Palette

Miyazaki palette: earth-toned, watercolor-blended, never digital-flat.

| Token | Hex | Role | Notes |
|-------|-----|------|-------|
| `--misty-blue` | `#d4e0f0` | Primary bg, glass panels | Low saturation blue-gray, like distant mountains |
| `--warm-beige` | `#f5f0e8` | Secondary warm bg | Warm beige, neutral, like aged paper |
| `--soft-white` | `#fafaf7` | Card / text backgrounds | Never pure `#fff` — cloud-white, slightly warm |
| `--pale-yellow-sunlight` | `#fef9e7` | Accent / highlight | Golden-hour warmth, sunbeam glow |
| `--desaturated-green` | `#7a9a7e` | Success / positive | Muted sage, moss, meadow — never neon |
| `--earth-brown` | `#5c4a3a` | Borders, dividers, subtle lines | Warm umber, tree-bark tone |
| `--sky-soft` | `#e6f0fa` | Top banners, header accents | Pale cerulean, midday sky haze |
| `--blush-pink` | (待定义) | Emphasis, warm accent | Faint cherry-blossom pink, barely-there warmth |
| `--accent-purple` | `#7c3aed` | **点缀色**：光标、链接、状态点 | ⚠️ 仅用于 2~4px 细线/圆点/文字链接，**禁止大面积背景** |
| `--text-primary` | `#1a1a2e` | 正文 | 柔黑，非纯 `#000` |
| `--text-secondary` | `var(--earth-brown)` | 辅助文字、时间戳、元信息 | 继承 earth-brown，降低不透明度 |
| `--avatar-0` ~ `--avatar-9` | 10 色 | Agent 头像 fallback 背景 | 橙/红/紫/青/绿/黄/粉/靛/青绿/玫红 |

**气泡颜色组合（Chat 专用）：**

| 角色 | 背景 | 边框 | 文字 |
|------|------|------|------|
| 用户 | `color-mix(in srgb, var(--accent-purple) 18%, var(--soft-white))` | `color-mix(in srgb, var(--accent-purple) 30%, transparent)` | `var(--text-primary)` |
| Agent | `transparent` / `color-mix(in srgb, var(--soft-white) 70%, transparent)` | `token.colorBorderSecondary` | `var(--text-primary)` |

**规则：**
- ❌ No vivid/saturated colors — Miyazaki avoids pure primaries
- ❌ No high contrast — prefer soft, diffuse, watercolor-blended tones
- ❌ `--accent-purple` 不是背景色 — 大面积紫会破坏柔和的调性
- ✅ Favor earth tones + pastels over digital neons
- ✅ 使用 `color-mix()` 混合 token，而非硬编码新颜色
- ✅ Gradients should feel like watercolor washes, not linear CSS gradients
- Use CSS variables; never hardcode colors

---

## Glassmorphism

Miyazaki twist: glass panels should feel like frosted window panes in a Ghibli cottage — translucent, warm, slightly imperfect.

```
background: bg-white/70 or bg-[var(--soft-white)]/70
backdrop-filter: blur(12–20px)
border: 1px solid rgba(255,255,255,0.2) or rgba(0,0,0,0.06)
border-radius: 6–10px
box-shadow: 0 2px 16px rgba(0,0,0,0.04) — soft layered, never strong elevation
```

- ❌ No opaque solid backgrounds on panels
- ❌ No sharp corners (0px) on cards/panels

---

## Texture & Atmosphere

Miyazaki's hand-drawn aesthetic translates to subtle organic texture.

- **Film grain** — very light noise overlay (`opacity: 0.015–0.03`), barely perceptible
- **Watercolor edges** — soft, irregular borders; avoid hard straight lines when possible
- **Atmospheric haze** — distant elements fade into soft blue-gray (depth of field)
- **Paper texture** — backgrounds hint at watercolor paper or aged parchment
- ❌ No sterile, perfectly uniform surfaces — a hint of imperfection is warmth
- ✅ Use CSS `backdrop-filter` + layered translucent colors to simulate watercolor depth

---

## Lighting

Miyazaki lighting is a character in itself — soft, natural, golden.

| Principle | Implementation |
|-----------|---------------|
| Golden hour | Warm `--pale-yellow-sunlight` highlights, 2700K–3500K tones |
| Diffuse light | No harsh directional shadows — soft ambient occlusion |
| Sunbeams | Subtle diagonal light rays via gradient + low-opacity overlay |
| Shadow depth | Multi-layered soft shadows (2–3 layers), never single hard drop-shadow |
| Bloom | Slight glow around bright elements (`filter: blur` + low opacity overlay) |

- ❌ No pure `#000` shadows
- ❌ No hard-edged directional shadows (box-shadow with 0 blur)

---

## Typography

- Font: clean sans-serif, light weight (300–400)
- CN: `"Noto Sans SC", "思源黑体", sans-serif`
- EN: `"Inter", system-ui, sans-serif`
- ❌ No bold-heavy text blocks — low visual weight

---

## Spacing & Layout

- **Airy layout** — strong negative space, generous padding. Miyazaki compositions breathe.
- ❌ No crowded sections, no visual clutter
- Cards/panels: `padding ≥ 16px`, `gap ≥ 12px`
- ✅ Embrace empty space — it's not wasted, it's atmosphere

---

## Depth & Nature

Miyazaki scenes have layered depth: foreground detail → midground action → background atmosphere. UI should echo this.

| Layer | Role | Style |
|-------|------|-------|
| Background | Atmosphere, sky, distant landscape | Low contrast, soft blur, hazy |
| Midground | Main content, cards, panels | Clear but not sharp, glassmorphism |
| Foreground | Interactive elements, focus | Crisp, warm, inviting |

**Nature motifs** (decorative, never distracting):
- Subtle cloud silhouettes at page edges
- Faint leaf/tree shadows in empty states
- Wind lines (thin, low-opacity horizontal strokes)
- Seasonal cues: cherry blossom hints (spring), golden leaves (autumn)

✅ Nature is background atmosphere — never competes with content
❌ No heavy illustrations that overpower the UI

---

## Motion System

| Keyframe | Duration | Effect | Use |
|----------|----------|--------|-----|
| `fadeIn` | 200ms | opacity 0.32 → 1 | 页面/面板出现 |
| `slideUp` | 200ms | opacity 0 + translateY(8px) → 全显 | 卡片入场 |
| `messageIn` | 300ms | slideUp 同款 | 聊天气泡入场 |
| `stepIn` | 200ms | opacity 0 + translateX(-4px) → 正常 | 执行步骤卡片逐条出现 |
| `thinkingPulse` | 1500ms ∞ | opacity 0.6 ↔ 1 呼吸 | 生成中状态指示 |
| `completeFade` | — | color: earth-brown → desaturated-green | 完成状态转色 |
| `puddingLogoPulse` | 2400ms ∞ | scale 1 ↔ 1.02 | Logo 缓慢呼吸 |
| `shake` | 400ms | translateX(±4px) | 表单校验错误 |

| Property | Spec |
|----------|------|
| Duration | UI: 200–500ms / Background ambient: 2–10s loop |
| Easing | `ease-in-out`, smooth cubic-bezier |
| Primary effect | `fade` + `translateY(5–10px)` / `translateX(2–4px)` |
| Scale | Max 1.00 → 1.02 (subtle) |

**Forbidden:**
- ❌ bounce / elastic effects
- ❌ scale-heavy motion (≥1.05)
- ❌ fast transitions (<150ms)
- ❌ instant pop-in (messages must fade+rise)

### Micro-interactions

| Event | Behavior |
|-------|----------|
| Hover | brightness +2~5% + soft shadow enhance |
| Click | scale(0.98), brief (no ripple/bounce) |
| Loading | breathing opacity pulse (见 thinkingPulse) |
| Typing | 流式文本逐字出现，自然节奏 |

### Background Motion

Slow ambient loops, 布丁的房间里的细微动静：
- **Cloud drift** — large soft clouds moving 0.5–2px/s horizontally
- **Tree/leaf sway** — gentle 3–8s oscillation, ±1–3px
- **Light shift** — subtle brightness pulse, 5–10s cycle, ±3%
- **Wind lines** — thin horizontal strokes fading in/out, 8–15s loop
- **Floating particles** — dust motes or pollen drifting slowly (Ghibli "ma")

Always subtle — if the user notices it immediately, it's too strong.

---

## Interaction Rules

- Chat messages appear with **fade + slight upward motion** (`messageIn`) — never instant pop-in
- Smooth + inertia scrolling
- Empty states: atmospheric bg + minimal poetic text, not blank. 布丁的语气："这里还没有对话"、"开始和 Agent 对话吧"
- Notifications: soft, non-intrusive; avoid alerts/harsh warnings
- Focus on **emotional comfort** and **distraction-free conversation**
- **"Ma" (間)** — embrace pauses and breathing room. Not every moment needs a response. Empty space between messages, gentle typing indicators, quiet transitions. This is the Ghibli art of the pause.
- **她不会主动打断你。** 只有在你发出消息后，她才回应。

---

## Tech Stack Review Focus

| Stack | Focus Areas |
|-------|-------------|
| React + TypeScript (Admin) | Token consistency, component isolation, CSS vars |
| Vue 3 + Pinia + Vite | `var()` usage, responsive, scoped styles |
| WPF XAML | Glass effect parity, color consistency with web |

---

## Universal Rules

- Icons: SVG (Lucide / Heroicons), **never emoji**
- Hover: color/opacity transition, **never position shift**
- Clickable: `cursor-pointer` always
- Light mode cards: `bg-white/70` minimum opacity — never transparent/invisible
- Body text: soft dark (e.g. `#1a1a2e`), never pure `#000`
- Borders: low-opacity (`border-gray-200/50` in light mode)

---

## Output Format

```
✅ path/to/file — passed
❌ path/to/file:line — MUST fix (violates token/core rule)
⚠ path/to/file:line — should fix (deviates from best practice)
ℹ path/to/file — note
```

| Mark | Meaning |
|------|---------|
| ❌ | Violates design token or core rule |
| ⚠ | Deviates from recommended practice |
| ✅ | Compliant |
| ℹ | Informational |
