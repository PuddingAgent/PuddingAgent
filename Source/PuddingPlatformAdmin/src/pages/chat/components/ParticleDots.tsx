// ── 等待粒子：WaitingBubble 上方缓慢上升 + 消散的微型发光圆点 ─────────────────
import React from 'react';
import { useAgentStyles } from '../styles/agent.styles';

/**
 * P2: 等待粒子 (Waiting Particles)
 * - 渲染 4 个 <span> 粒子，在等待气泡周围错峰上升
 * - 3-4px 直径圆点，紫色渐变 + 轻量外发光
 * - animationDelay 错开，particleFloatUp 2.4s ease-out infinite
 * - 纯 CSS 动画；容器 pointer-events: none，不阻挡点击
 */
export const ParticleDots: React.FC = () => {
  const { styles } = useAgentStyles();
  const particles = [
    { left: '12%', bottom: '5px', delay: '0s', drift: '-5px', size: 3 },
    { left: '38%', bottom: '1px', delay: '0.6s', drift: '3px', size: 4 },
    { left: '66%', bottom: '6px', delay: '1.2s', drift: '-2px', size: 3 },
    { left: '88%', bottom: '2px', delay: '1.8s', drift: '5px', size: 4 },
  ] as const;

  return (
    <div className={styles.particleContainer} aria-hidden="true">
      {particles.map(({ left, bottom, delay, drift, size }) => (
        <span
          key={`${left}:${delay}`}
          className={styles.particleDot}
          style={
            {
              left,
              bottom,
              width: size,
              height: size,
              animationDelay: delay,
              '--particle-drift': drift,
            } as React.CSSProperties
          }
        />
      ))}
    </div>
  );
};
