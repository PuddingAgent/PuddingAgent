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
