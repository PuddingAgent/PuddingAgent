---
name: web-design-guidelines
description: Use when designing, reviewing, or implementing Pudding UI/UX. Applies to chat pages, admin panels, empty states, streaming output, component styling, layout, colors, typography, animation, and visual QA. Enforces Pudding's restrained "Quiet Local Intelligence" design language.
argument-hint: "UI scope to review or design, for example Source/PuddingPlatformAdmin/src/pages/chat"
---

# Pudding Web Design Guidelines

## North Star

Pudding's product feeling is:

**Quiet Local Intelligence**

安静、本地、可信、克制的 AI 工作台。

Pudding is not a spectacle-driven AI product, not a marketing landing page, and not a dense enterprise dashboard. It should feel like a calm local workspace where the user can think, write, inspect, and delegate without being interrupted.

The emotional direction may borrow from Miyazaki-style warmth: natural light, paper-like softness, quiet pauses, gentle transitions. But this must be translated into product UI restraint, not decorative illustration or cinematic excess.

## Core Principles

1. **Workbench first**
   The first screen must be usable. Avoid hero sections, decorative feature tours, or large visual centerpieces unless they directly support the current task.

2. **Stable identity anchor**
   The Pudding logo/avatar is the primary visual anchor. Loading, empty, and ready states must not replace it with unrelated spectacle such as particle spheres or full-screen animation.

3. **Text is the main surface**
   Chat, documents, logs, and task results must read like clean working material. AI output should feel stable, document-like, and easy to scan.

4. **Status over spectacle**
   Show runtime state through compact status lines, subtle dots, tool rows, or progress text. Do not use large animated scenes to communicate simple states.

5. **Low-motion calm**
   Motion should reduce uncertainty, not attract attention. Prefer opacity, color, and very small transform changes. Avoid layout jumps, elastic motion, repeated re-animation, and card resizing during streaming.

6. **Dense but breathable**
   This is a work tool. Layouts should be compact enough for repeated use, but with clear hierarchy and sufficient breathing room.

7. **Warm, not sweet**
   Use warm paper tones and muted accents. Avoid candy palettes, excessive purple, large gradients, glassmorphism overload, and cute decorative language.

## Visual Language

| Area | Rule |
| --- | --- |
| Background | Warm off-white or paper-like neutral. Never pure white full-page glare. |
| Text | Deep ink color for primary text; muted gray-brown for metadata. Never pure black for large text blocks. |
| Accent | Pudding purple is a signal color only: focus rings, links, small status dots, selected states. Do not use it as a dominant page background. |
| Surface | Soft-white panels, restrained borders, weak or no shadow. |
| Radius | 6-8px for cards and panels. Avoid pill-heavy UI except small chips. |
| Shadow | Very soft depth only. No hard drop shadows. |
| Icons | Use line icons such as lucide where available. Avoid emoji as UI icons. |
| Illustration | Optional and rare. Must never compete with content or replace core product identity. |

Recommended token direction:

```css
:root {
  --pudding-bg: #f7f3ec;
  --pudding-surface: #fffefa;
  --pudding-surface-soft: #f3eee7;
  --pudding-text: #1d1b24;
  --pudding-text-muted: #756b5f;
  --pudding-line: rgba(92, 74, 58, 0.16);
  --pudding-accent: #8b5cf6;
  --pudding-accent-soft: rgba(139, 92, 246, 0.14);
  --pudding-success: #6f8f72;
  --pudding-warning: #c4944c;
}
```

Use project tokens when they already exist. Do not hardcode new one-off colors unless creating or extending the design token layer.

## Typography

- Prefer system sans fonts with good Chinese rendering.
- Chinese text must remain readable at workbench density.
- Use bold only for hierarchy, not decoration.
- Avoid oversized headings inside chat cards, sidebars, tool panels, and compact admin surfaces.
- Letter spacing should normally be `0`.
- Do not scale font size directly with viewport width.

## Layout

- Do not make card-in-card compositions.
- Do not make page sections look like floating marketing cards.
- Use stable dimensions for fixed-format elements such as sidebars, toolbars, icon buttons, chips, and input areas.
- Dynamic content must not cause adjacent controls to shift unexpectedly.
- The chat input area should feel like a quiet console: fixed, stable, and predictable.

## Chat Experience

### Sidebar

- Session history must be stable and predictable.
- Directly sending the first message must create/select a visible session item.
- Selected session state should be subtle: soft background, thin border, or small accent signal.
- Avoid high-saturation selected blocks.

### Message Timeline

- User messages may use a light accent-tinted bubble.
- Agent messages should feel like clean document surfaces.
- Runtime metadata belongs in a small status row, not a large visual takeover.
- Existing messages must not re-animate when new streaming content arrives.
- Do not scroll-jump unless the user is already near the bottom.

### Streaming Output

Streaming should feel like ink becoming visible:

- New text appears gradually from transparent/light ink to solid ink.
- Stable Markdown blocks must not be repeatedly reparsed and reanimated on every delta.
- The message card may grow naturally with content, but it must not pulse, twitch, or repeatedly resize from animation.
- Cursor/blinking indicators should be small and quiet.

Preferred implementation pattern:

1. Buffer incoming deltas.
2. Flush at animation-frame or short interval cadence.
3. Split stable rendered Markdown from live text.
4. Animate only newly committed tokens.
5. Finalize into normal Markdown after streaming completes.

Example token animation:

```css
.pudding-stream-token {
  opacity: 0;
  color: rgba(29, 27, 36, 0.32);
  filter: blur(1px);
  animation: puddingInkIn 220ms ease-out forwards;
}

@keyframes puddingInkIn {
  to {
    opacity: 1;
    color: rgba(29, 27, 36, 1);
    filter: blur(0);
  }
}
```

## Empty State

Empty states should be quiet ready states, not showcase scenes.

Required:

- Use stable Pudding logo/avatar as the main visual.
- Keep copy short and calm.
- Use at most 2-3 low-emphasis suggested actions.
- Keep the input area visible and ready.
- Preserve layout height so entering or leaving the state does not cause a jarring jump.

Avoid:

- Particle sphere replacing the logo.
- Full-screen animated hero.
- Large decorative backgrounds.
- Long feature explanation.
- Marketing copy.

Recommended states:

| State | Visual | Copy direction |
| --- | --- | --- |
| Booting | Logo + small status dot | "正在准备工作区..." |
| Ready | Logo + short line | "Pudding 已准备好" |
| No agent | Logo muted | "请选择一个 Agent" |
| Loading session | Skeleton rows | "正在加载会话..." |
| Error | Stable logo + retry | Short cause + retry action |

## Motion System

| Use | Duration | Properties |
| --- | --- | --- |
| Small UI transition | 120-180ms | color, opacity, border-color |
| Panel/message entry | 180-240ms | opacity + translateY(4px max) |
| Streaming token | 160-260ms | opacity + color + slight blur |
| Background ambient | Avoid by default | Only if very subtle and nonessential |

Forbidden:

- Bounce or elastic easing.
- Scale above `1.02`.
- Repeated animation of existing content.
- Height/max-height animation for streaming cards.
- Motion that shifts text while the user is reading.

Always support `prefers-reduced-motion`.

## Component Rules

- Buttons: use icons for common tools; text or icon+text only for clear commands.
- Icon buttons require accessible labels or tooltips.
- Chips should be small and low-emphasis.
- Cards are for repeated items, modals, or framed tools only.
- Tables and lists should prioritize scanning over decoration.
- Loading should preserve the previous layout whenever possible.
- Error states should be calm, specific, and recoverable.

## Voice And Copy

Pudding copy should be:

- Short.
- Specific.
- Calm.
- Non-urgent.
- Work-oriented.

Use:

- "已完成"
- "正在整理上下文..."
- "请选择一个 Agent"
- "这里还没有对话"

Avoid:

- "太棒了！"
- "马上开始你的 AI 之旅！"
- "强大的智能体平台"
- Long feature explanations in the product surface.

## Anti-Patterns

These are design violations:

- A particle/globe/AI sphere replaces the Pudding avatar in default loading or empty state.
- Streaming output causes full message cards to flicker, jump, resize, or repaint visibly.
- Purple or blue gradients dominate the page.
- The first viewport behaves like a landing page instead of an app.
- UI cards are nested inside other UI cards.
- Decorative animation competes with the user's task.
- Emoji are used as structural UI icons.
- Important state is expressed only through color.
- Existing content disappears during loading when it could remain visible.
- Copy explains the interface instead of making the interface self-evident.

## Review Checklist

When reviewing Pudding UI, check:

1. Does the screen feel like a calm workbench, not a showcase?
2. Is the Pudding identity stable across loading, empty, and ready states?
3. Are text, hierarchy, and controls readable at normal working density?
4. Are accent colors used sparingly?
5. Does streaming update without flicker or layout instability?
6. Are cards, borders, shadows, and radius restrained?
7. Are animations short, subtle, and nonessential?
8. Does the UI remain usable with reduced motion?
9. Does the copy sound calm and precise?
10. Would this screen still feel appropriate after hours of repeated use?

## Output Format For Reviews

Use this format when asked to review UI/code:

```text
Findings
- [P1] path:line — issue and impact
- [P2] path:line — issue and impact

Recommendations
- Specific fix proposal
- Specific token/component/motion adjustment

Verdict
Pass / Needs revision
```

Prioritize behavioral and usability regressions over taste comments. Always connect visual feedback to user impact.
