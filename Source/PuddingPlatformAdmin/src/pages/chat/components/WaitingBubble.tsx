// ── 等待气泡：首 Token 前无运行事件时的轻量等待态 ─────────────────
import cx from 'classnames';
import React from 'react';
import { useAgentStyles } from '../styles/agent.styles';
import { ParticleDots } from './ParticleDots';

export interface WaitingBubbleProps {
  waitSeconds: number;
}

export const WaitingBubble: React.FC<WaitingBubbleProps> = ({
  waitSeconds,
}) => {
  const { styles } = useAgentStyles();

  const isSlow = waitSeconds >= 3;
  const isVerySlow = waitSeconds >= 10;
  const isExtreme = waitSeconds >= 30;

  const msg = isExtreme
    ? `模型正在进行复杂推理（${waitSeconds}s），请稍候...`
    : isVerySlow
      ? `深入分析中（${waitSeconds}s），请耐心等待...`
      : isSlow
        ? `模型响应较慢（${waitSeconds}s）...`
        : '正在思考...';

  return (
    <div
      className={cx(
        styles.agentBubbleNew,
        styles.agentBubbleEntrance,
        styles.agentBubbleStreaming,
        styles.agentActiveOutputSurface,
        styles.agentWaitingBubble,
        isVerySlow && styles.agentBubbleWarning,
      )}
    >
      {!isExtreme && <ParticleDots />}
      <div className={styles.waitingDots}>
        <span
          className={cx(styles.waitingDot, isVerySlow && styles.waitingDotSlow)}
          style={{ animationDelay: '0s' }}
        />
        <span
          className={cx(styles.waitingDot, isVerySlow && styles.waitingDotSlow)}
          style={{ animationDelay: '0.2s' }}
        />
        <span
          className={cx(styles.waitingDot, isVerySlow && styles.waitingDotSlow)}
          style={{ animationDelay: '0.4s' }}
        />
      </div>
      <span
        className={cx(
          styles.waitingLabel,
          isVerySlow && styles.waitingLabelWarning,
        )}
      >
        {msg}
      </span>
    </div>
  );
};
