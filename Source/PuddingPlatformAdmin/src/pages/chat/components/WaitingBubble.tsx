// ── 等待气泡：首 Token 前无运行事件时的轻量等待态 ─────────────────
import cx from 'classnames';
import React from 'react';
import { useAgentStyles } from '../styles/agent.styles';
import { ParticleDots } from './ParticleDots';

export interface WaitingBubbleProps {
  waitSeconds: number;
  agentName?: string;
}

export const WaitingBubble: React.FC<WaitingBubbleProps> = ({
  waitSeconds,
  agentName = '主代理',
}) => {
  const { styles } = useAgentStyles();

  const isSlow = waitSeconds >= 3;
  const isVerySlow = waitSeconds >= 10;
  const isExtreme = waitSeconds >= 30;

  const phase = isExtreme
    ? '模型正在进行复杂推理'
    : isVerySlow
      ? '模型正在深入分析'
      : isSlow
        ? '等待模型响应'
        : '正在请求模型';
  const elapsed =
    waitSeconds < 60
      ? `${waitSeconds} 秒`
      : `${Math.floor(waitSeconds / 60)} 分 ${waitSeconds % 60} 秒`;

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
      data-testid="agent-waiting-monitor"
      aria-live="polite"
    >
      {!isExtreme && <ParticleDots />}
      <div className={styles.waitingHeader}>
        <div className={styles.waitingDots}>
          {[0, 0.2, 0.4].map((delay) => (
            <span
              key={delay}
              className={cx(
                styles.waitingDot,
                isVerySlow && styles.waitingDotSlow,
              )}
              style={{ animationDelay: `${delay}s` }}
            />
          ))}
        </div>
        <span className={styles.waitingTitle}>{agentName} 正在运行</span>
        <span className={styles.waitingElapsed}>已等待 {elapsed}</span>
      </div>
      <div
        className={cx(
          styles.waitingLabel,
          isVerySlow && styles.waitingLabelWarning,
        )}
      >
        {phase}
      </div>
      <div className={styles.waitingTrack}>
        <span className={styles.waitingTrackDone}>已接收任务</span>
        <span className={styles.waitingTrackArrow}>→</span>
        <span className={styles.waitingTrackCurrent}>等待首个可见事件</span>
      </div>
      <div className={styles.waitingHint}>
        这是主代理的等待占位：尚未收到主代理可展示的推理摘要或工具事件。子代理活动会显示在右侧托盘坞和运行检查器中。
      </div>
    </div>
  );
};
