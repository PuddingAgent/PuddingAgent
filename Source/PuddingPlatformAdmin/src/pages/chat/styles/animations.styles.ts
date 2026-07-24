// ── 动画关键帧 & 动画类样式 ─────────────────────────────────
import { createStyles } from 'antd-style';

export const useAnimationStyles = createStyles(({ token }) => ({
  /* ── Stateful Motion: thinking, searching, memory, tool, error, success ── */
  '@keyframes neuralPulse': {
    '0%, 100%': { opacity: 0.4, transform: 'scale(1)' },
    '50%': { opacity: 0.8, transform: 'scale(1.02)' },
  },
  '@keyframes particleFlow': {
    '0%': { backgroundPosition: '0% 50%' },
    '100%': { backgroundPosition: '200% 50%' },
  },
  '@keyframes tokenStream': {
    '0%': { opacity: 0, transform: 'translateY(4px)' },
    '100%': { opacity: 1, transform: 'translateY(0)' },
  },
  '@keyframes waveScan': {
    '0%': { backgroundPosition: '-200% 50%' },
    '100%': { backgroundPosition: '200% 50%' },
  },
  '@keyframes breathe': {
    '0%, 100%': { opacity: 0.6, transform: 'scale(0.98)' },
    '50%': { opacity: 1, transform: 'scale(1)' },
  },
  '@keyframes ambientFloat': {
    '0%, 100%': { transform: 'translateY(0)' },
    '50%': { transform: 'translateY(-6px)' },
  },
  '@keyframes glitchShake': {
    '0%, 100%': { transform: 'translateX(0)' },
    '10%': { transform: 'translateX(-2px)' },
    '30%': { transform: 'translateX(2px)' },
    '50%': { transform: 'translateX(-1px)' },
    '70%': { transform: 'translateX(1px)' },
  },
  '@keyframes softDiffuse': {
    '0%': { boxShadow: '0 0 0 0 rgba(124,58,237,0)' },
    '50%': { boxShadow: '0 0 20px 4px rgba(124,58,237,0.12)' },
    '100%': { boxShadow: '0 0 0 0 rgba(124,58,237,0)' },
  },
  '@keyframes emptyFadeIn': {
    '0%': { opacity: 0, transform: 'translateY(4px)' },
    '100%': { opacity: 1, transform: 'translateY(0)' },
  },
  '@keyframes cursorBlink': {
    '0%, 100%': { opacity: 1 },
    '50%': { opacity: 0 },
  },
  '@keyframes revealDown': {
    '0%': { maxHeight: 0, opacity: 0 },
    '100%': { maxHeight: 2000, opacity: 1 },
  },
  '@keyframes charFade': {
    '0%': { opacity: 0 },
    '100%': { opacity: 1 },
  },
  '@keyframes streamBreathe': {
    '0%, 100%': { opacity: 1 },
    '50%': { opacity: 0.92 },
  },
  // Phase 1: 启用已定义未使用的 keyframes (messageIn/stepIn/blockCondense/glowSettle)
  '@keyframes messageIn': {
    '0%': { opacity: 0, transform: 'translateY(8px)' },
    '100%': { opacity: 1, transform: 'translateY(0)' },
  },
  // P1: 克制的 Q 弹入场 — 保留可感知的上浮与回弹，避免 0.8 倍缩放造成内容“炸入”
  '@keyframes messageBounceIn': {
    '0%': {
      opacity: 0,
      filter: 'blur(2px)',
      transform: 'translateY(14px) scale(0.92)',
    },
    '55%': {
      opacity: 1,
      filter: 'blur(0)',
      transform: 'translateY(-2px) scale(1.015)',
    },
    '78%': { opacity: 1, transform: 'translateY(1px) scale(0.996)' },
    '100%': {
      opacity: 1,
      filter: 'blur(0)',
      transform: 'translateY(0) scale(1)',
    },
  },
  // 消息入场光晕：紫色光圈从气泡边缘扩散并消散，与 messageIn 叠加使用（600ms ease-out）
  '@keyframes messageGlowIn': {
    '0%': {
      opacity: 0,
      boxShadow:
        '0 0 0 0 rgba(139, 63, 232, 0.28), 0 0 18px 2px rgba(139, 63, 232, 0.1)',
    },
    '40%': {
      opacity: 1,
      boxShadow:
        '0 0 0 4px rgba(139, 63, 232, 0), 0 0 26px 5px rgba(139, 63, 232, 0.065)',
    },
    '100%': {
      opacity: 1,
      boxShadow: '0 0 0 0 rgba(139, 63, 232, 0), 0 0 0 0 rgba(139, 63, 232, 0)',
    },
  },
  '@keyframes stepIn': {
    '0%': { opacity: 0, transform: 'translateY(4px)' },
    '100%': { opacity: 1, transform: 'translateY(0)' },
  },
  '@keyframes blockCondense': {
    '0%': { maxHeight: '2000px', opacity: 1 },
    '100%': { maxHeight: 0, opacity: 0 },
  },
  '@keyframes glowSettle': {
    '0%': { boxShadow: '0 0 12px 2px rgba(124,58,237,0.25)' },
    '100%': { boxShadow: '0 0 0 0 rgba(124,58,237,0)' },
  },
  // 思维链预览：行级淡入上浮
  '@keyframes reasoningLineFadeIn': {
    '0%': { opacity: 0, transform: 'translateY(4px)' },
    '100%': { opacity: 1, transform: 'translateY(0)' },
  },
  // 思维链预览：指示点紫色光晕呼吸
  '@keyframes reasoningGlowPulse': {
    '0%, 100%': { boxShadow: '0 0 4px rgba(139, 92, 246, 0.3)' },
    '50%': { boxShadow: '0 0 12px rgba(139, 92, 246, 0.6)' },
  },
  // E2: 流式停滞琥珀色慢脉冲
  '@keyframes stallPulse': {
    '0%, 100%': { borderColor: 'color-mix(in srgb, #d97706 16%, transparent)' },
    '50%': { borderColor: 'color-mix(in srgb, #d97706 30%, transparent)' },
  },
  // P2: 等待粒子 — 气泡周围的能量点错峰上升、轻微横向漂移并消散
  '@keyframes particleFloatUp': {
    '0%': { opacity: 0, transform: 'translate(0, 2px) scale(0.45)' },
    '18%': { opacity: 0.72, transform: 'translate(0, -3px) scale(1)' },
    '62%': {
      opacity: 0.38,
      transform: 'translate(var(--particle-drift), -18px) scale(0.82)',
    },
    '100%': {
      opacity: 0,
      transform: 'translate(var(--particle-drift), -34px) scale(0.35)',
    },
  },
  // P3: 完成粒子 — 回答落定时从右下角闪现后向四周飞散
  '@keyframes particleBurst': {
    '0%': { opacity: 0, transform: 'translate(0, 0) scale(0.35)' },
    '16%': { opacity: 0.78, transform: 'translate(0, -1px) scale(1.1)' },
    '62%': {
      opacity: 0.48,
      transform: 'translate(var(--bx), var(--by)) scale(0.9)',
    },
    '100%': {
      opacity: 0,
      transform: 'translate(var(--bx), var(--by)) scale(0.2)',
    },
  },
  streamingCursor: {
    display: 'inline-block',
    width: 8,
    marginLeft: 2,
    color: token.colorPrimary,
    animation: 'cursorBlink 1s steps(1) infinite',
  },
  latestTurn: { animation: 'messageIn 300ms ease-out' },
  stepCardAnimated: {
    animation: 'stepIn 200ms ease-out',
    opacity: 0,
    animationFillMode: 'forwards' as const,
  },
  ambientLayer: {
    display: 'none',
  },
  breathingCard: {
    transition: 'all 0.3s ease',
  },
  tokenStreaming: {
    animation: 'tokenStream 0.2s ease-out',
    opacity: 1,
  },
  progressiveReveal: {
    overflow: 'hidden' as const,
    animation: 'revealDown 0.4s ease-out',
  },
  blockCondensing: {
    animation: 'blockCondense 360ms ease-out',
  },
  charFadeIn: {
    animation: 'charFade 120ms ease-out',
  },
  answerSettled: {
    animation: 'glowSettle 800ms ease-out',
  },
  streamingBreathe: {},
}));
