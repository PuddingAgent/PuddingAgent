// ── 思维链预览：模型推理时在气泡内实时展示思考过程 ─────────────────
import React from 'react';
import { useReasoningStyles } from '../styles/reasoning.styles';

export interface ReasoningPreviewProps {
  lines: { id: string; text: string }[];
  waitSeconds: number;
  isCurrent?: boolean;
  currentActivityTitle?: string;
}

export const ReasoningPreview: React.FC<ReasoningPreviewProps> = ({
  lines,
  waitSeconds,
  isCurrent = true,
  currentActivityTitle,
}) => {
  const { styles } = useReasoningStyles();
  // 只显示最后 3 行，避免气泡过高
  const visibleLines = lines.slice(-3);

  return (
    <div className={styles.reasoningContainer}>
      <div className={styles.reasoningHeader}>
        <span className={styles.reasoningIcon}>💭</span>
        <span className={styles.reasoningTitle}>
          {isCurrent ? '推理摘要' : '最近推理摘要'}
        </span>
      </div>
      <div className={styles.reasoningLines}>
        {visibleLines.map((line, i) => (
          <div
            key={line.id}
            className={styles.reasoningLine}
            style={{ animationDelay: `${i * 0.05}s` }}
          >
            {line.text}
          </div>
        ))}
      </div>
      <div className={styles.reasoningFooter}>
        <span className={styles.reasoningDot} />
        <span className={styles.reasoningLabel}>
          {isCurrent
            ? waitSeconds >= 60
              ? `深度推理中（${waitSeconds}s）...`
              : '持续推理中...'
            : currentActivityTitle
              ? `当前：${currentActivityTitle}`
              : '已进入下一执行阶段'}
        </span>
      </div>
    </div>
  );
};
