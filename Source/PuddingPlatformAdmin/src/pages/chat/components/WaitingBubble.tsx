// ── 等待气泡：首 Token 前无运行事件时的单行等待态（P1-3，对齐 deepseek-harness D6 TurnStatus）──
// 形态：[StateDot(ongoing)] + 「{agentName} 正在运行」 + （≥15s）「· 已等待 Xs/Xm」；
// 阶段文案（正在请求模型/等待模型响应/深入分析/复杂推理）折叠进 Tooltip，hover 可见。
import cx from 'classnames';
import React, { useEffect, useRef, useState } from 'react';
import { Tooltip } from 'antd';
import { useAgentStyles } from '../styles/agent.styles';
import { useWaitingStyles } from '../styles/waiting.styles';
import StateDot from './StateDot';

export interface WaitingBubbleProps {
  /** 服务端锚定的已等待秒数（turn/createdAt 起点，reload 不归零） */
  waitSeconds: number;
  /** 展示名，默认主代理 */
  agentName?: string;
}

/** 与 deepseek-harness TurnStatus 对齐：≥15s 才显示运行时钟 */
const CLOCK_THRESHOLD_SECONDS = 15;

/** 「Xs」/「Xm」：<60s 显示秒，≥60s 取整分钟 */
function formatElapsed(seconds: number): string {
  return seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m`;
}

export const WaitingBubble: React.FC<WaitingBubbleProps> = ({
  waitSeconds,
  agentName = '主代理',
}) => {
  const { styles } = useAgentStyles();
  const { styles: waitingStyles } = useWaitingStyles();

  // 等待起点锚定：首次进入 running 态（组件挂载即等待态）时记录，重渲染不归零；
  // 与父级服务端锚点 waitSeconds 取较大值，reload 后时钟仍连续。
  const anchorRef = useRef<number>(0);
  if (anchorRef.current === 0) anchorRef.current = Date.now();
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);

  const localElapsed = Math.max(0, Math.floor((now - anchorRef.current) / 1000));
  const elapsedSeconds = Math.max(waitSeconds, localElapsed);

  const isSlow = elapsedSeconds >= 3;
  const isVerySlow = elapsedSeconds >= 10;
  const isExtreme = elapsedSeconds >= 30;
  const phase = isExtreme
    ? '模型正在进行复杂推理'
    : isVerySlow
      ? '模型正在深入分析'
      : isSlow
        ? '等待模型响应'
        : '正在请求模型';

  const showClock = elapsedSeconds >= CLOCK_THRESHOLD_SECONDS;

  return (
    <div
      className={cx(
        styles.agentBubbleNew,
        styles.agentBubbleEntrance,
        styles.agentBubbleStreaming,
        styles.agentActiveOutputSurface,
        waitingStyles.row,
      )}
      data-testid="agent-waiting-monitor"
      aria-live="polite"
    >
      <Tooltip
        title={
          showClock
            ? `${phase} · 已等待 ${formatElapsed(elapsedSeconds)}`
            : phase
        }
        mouseEnterDelay={0.2}
        mouseLeaveDelay={0}
      >
        <span className={waitingStyles.line}>
          <StateDot state="ongoing" />
          <span className={waitingStyles.title}>{agentName} 正在运行</span>
          {showClock && (
            <span className={waitingStyles.elapsed}>
              · 已等待 {formatElapsed(elapsedSeconds)}
            </span>
          )}
        </span>
      </Tooltip>
    </div>
  );
};
