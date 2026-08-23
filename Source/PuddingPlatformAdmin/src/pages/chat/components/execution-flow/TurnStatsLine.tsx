// ── TurnStatsLine：turn 终态计量行（行为链升级 §3.3，对齐 harness StatsLine）──
// turn 落定后在正文下方渲染一行 caption 灰计量：`N 段思考 · M 工具 · 3m01s · 4.2k tokens`。
// 数据全部来自 canonical 投影（段数/工具数/终态时间）与 usage（token），刷新不归零；
// 缺失的计量项直接省略（不伪造），全缺失时整行不渲染。
import React from 'react';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import { formatDurationMs, formatTokenCount } from '../../utils/formatDuration';

export interface TurnStatsLineProps {
  /** 思考段数（投影 reasoning 节点数）；0/null 省略。 */
  reasoningSegments?: number | null;
  /** 工具调用数（含子调用）；0/null 省略。 */
  toolCount?: number | null;
  /** turn 总时长（毫秒：turn 起点 → 终态 occurredAt）；缺失省略。 */
  totalDurationMs?: number | null;
  /** token 用量；缺失省略。 */
  totalTokens?: number | null;
}

/** 组装计量项（顺序固定：思考段 → 工具 → 时长 → token）。 */
export const buildTurnStatsParts = ({
  reasoningSegments,
  toolCount,
  totalDurationMs,
  totalTokens,
}: TurnStatsLineProps): string[] => {
  const parts: string[] = [];
  if (typeof reasoningSegments === 'number' && reasoningSegments > 0) {
    parts.push(`${reasoningSegments} 段思考`);
  }
  if (typeof toolCount === 'number' && toolCount > 0) {
    parts.push(`${toolCount} 工具`);
  }
  const duration = formatDurationMs(totalDurationMs);
  if (duration) parts.push(duration);
  const tokens = formatTokenCount(totalTokens);
  if (tokens) parts.push(tokens);
  return parts;
};

export const TurnStatsLine: React.FC<TurnStatsLineProps> = (props) => {
  const { styles } = useExecutionFlowStyles();
  const parts = buildTurnStatsParts(props);
  if (parts.length === 0) return null;

  return (
    <div className={styles.statsLine} data-testid="turn-stats-line">
      {parts.map((part, index) => (
        <React.Fragment key={part}>
          {index > 0 && (
            <span className={styles.statsDot} aria-hidden="true" />
          )}
          <span>{part}</span>
        </React.Fragment>
      ))}
    </div>
  );
};

export default React.memo(TurnStatsLine);
