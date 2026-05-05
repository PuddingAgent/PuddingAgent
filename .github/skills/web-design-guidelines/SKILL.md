---
name: web-design-guidelines
description: Use when reviewing UI/UX code for Pudding design system compliance. Review Vue/React components, CSS, or frontend code against Pudding's Japanese anime cinematic aesthetic. Triggers: "review my UI", "check design", "audit UI", "review UX".
argument-hint: "指定要审查的UI文件，例如 'Source/PuddingPlatformAdmin/src/' 或 'Source/PuddingAgent/Views/'"
---

# Pudding Design Guidelines — Design System & Code Review

## Design Philosophy

UI is not a coat of paint. It is the window through which your thinking meets the user's eyes.
Every pixel is a word in a conversation you are having with someone you may never meet.

### The First Glance

When a user opens Pudding for the first time, they should feel:
**"Ah… this place is quiet. I can breathe here."**

Not excitement. Not awe. Not "wow, so many features."
Just a gentle exhale. A subtle unclenching. The feeling of stepping into a Ghibli scene —
a sunlit empty classroom, a quiet train station at dusk, a hillside with wind through grass.

This is not a tool. This is a room. And the user just walked in.

### How We Speak

An interface has a voice. Ours is:

| Quality | What it means |
|---------|---------------|
| **Soft-spoken** | Never shouts. Alerts are whispers. Buttons suggest, they don't demand. |
| **Patient** | Gives the user time. No countdown anxiety. No FOMO. The "ma" (間) between actions is sacred. |
| **Warm** | Like a friend who doesn't need to fill silence. Present, not pushy. |
| **Precise** | Few words, each one intentional. No filler. No jargon. |

Every label, every placeholder, every empty state text — ask: "Would I say this to a friend sitting next to me on a quiet afternoon?"

### The Collision

Your thinking (structured, logical, systematic) meets their perception (emotional, intuitive, felt).
UI is where these two collide — and good UI doesn't resolve the tension, it harmonizes it.

- **Your logic** becomes their intuition through consistent patterns
- **Your structure** becomes their comfort through predictable rhythm
- **Your complexity** becomes their clarity through progressive revelation
- **Your cold data** becomes their warm understanding through thoughtful presentation

### What We Refuse

We say no to:
- Dark patterns, urgency tricks, engagement traps
- Information overload — if everything is highlighted, nothing is
- Cold corporate aesthetic — blue-gray enterprise dashboards that feel like spreadsheets
- Novelty for its own sake — clever animations that waste the user's time

### The Score

Think of the entire experience as a musical score — not a feature list:
- **Rhythm** — consistent spacing, predictable timing, breathing room between interactions
- **Dynamics** — most things are *piano* (quiet), emphasis is *mezzo-piano* at most
- **Silence** — the rest between notes. Empty states, transitions, loading — these are not gaps, they are part of the composition

**In one sentence: Pudding should feel like a quiet friend who listens more than they speak, in a sunlit room, on a spring afternoon.**

---

## Visual Identity

Japanese anime cinematic style, inspired by *The Disappearance of Haruhi Suzumiya* and Studio Ghibli (Miyazaki).
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

| Token | Role | Notes |
|-------|------|-------|
| `--misty-blue` | Primary bg, glass panels | Low saturation blue-gray, like distant mountains |
| `--warm-beige` | Secondary warm bg | Warm beige, neutral, like aged paper |
| `--soft-white` | Card / text backgrounds | Never pure `#fff` — cloud-white, slightly warm |
| `--pale-yellow-sunlight` | Accent / highlight | Golden-hour warmth, sunbeam glow |
| `--desaturated-green` | Success / positive | Muted sage, moss, meadow — never neon |
| `--earth-brown` | Borders, dividers, subtle lines | Warm umber, tree-bark tone |
| `--sky-soft` | Top banners, header accents | Pale cerulean, midday sky haze |
| `--blush-pink` | Emphasis, warm accent | Faint cherry-blossom pink, barely-there warmth |

**Rules:**
- ❌ No vivid/saturated colors — Miyazaki avoids pure primaries
- ❌ No high contrast — prefer soft, diffuse, watercolor-blended tones
- ✅ Favor earth tones + pastels over digital neons
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

| Property | Spec |
|----------|------|
| Duration | UI: 200–500ms / Background ambient: 3–10s loop |
| Easing | `ease-in-out`, smooth cubic-bezier |
| Primary effect | `fade` + `translateY(5–10px)` |
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
| Loading | breathing opacity pulse + "..." dots |
| Typing | slow, human-like pace |

### Background Motion

Slow ambient loops, Miyazaki-inspired:
- **Cloud drift** — large soft clouds moving 0.5–2px/s horizontally
- **Tree/leaf sway** — gentle 3–8s oscillation, ±1–3px
- **Light shift** — subtle brightness pulse, 5–10s cycle, ±3%
- **Wind lines** — thin horizontal strokes fading in/out, 8–15s loop
- **Floating particles** — dust motes or pollen drifting slowly (like Ghibli "ma" — the space between)

Always subtle — if the user notices it immediately, it's too strong.

---

## Interaction Rules

- Chat messages appear with **fade + slight upward motion** — never instant pop-in
- Smooth + inertia scrolling
- Empty states: atmospheric bg + minimal poetic text, not blank
- Notifications: soft, non-intrusive; avoid alerts/harsh warnings
- Focus on **emotional comfort** and **distraction-free conversation**
- **"Ma" (間)** — embrace pauses and breathing room. Not every moment needs a response. Empty space between messages, gentle typing indicators, quiet transitions. This is the Ghibli art of the pause.

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
