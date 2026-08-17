import React from 'react';
import { useReasoningStyles } from '../styles/reasoning.styles';
import StateDot from './StateDot';

export interface ReasoningPreviewProps {
  lines: { id: string; text: string }[];
  isCurrent?: boolean;
}

/**
 * DeepSeek Harness-style reasoning disclosure:
 * one compact trajectory row by default, complete model-visible reasoning on demand.
 */
export const ReasoningPreview: React.FC<ReasoningPreviewProps> = ({
  lines,
  isCurrent = true,
}) => {
  const { styles, cx } = useReasoningStyles();
  const [expanded, setExpanded] = React.useState(false);
  const visibleLines = React.useMemo(
    () => lines.filter((line) => line.text.trim().length > 0),
    [lines],
  );

  if (visibleLines.length === 0) return null;

  const summary = isCurrent
    ? visibleLines[visibleLines.length - 1].text
    : visibleLines[0].text;
  const fullText = visibleLines.map((line) => line.text).join('\n\n');
  const toggle = () => setExpanded((value) => !value);

  return (
    <div className={styles.disclosure} data-testid="reasoning-disclosure">
      <button
        type="button"
        aria-expanded={expanded}
        aria-label="思考过程"
        className={cx(styles.row, isCurrent && styles.rowRunning)}
        onClick={toggle}
      >
        <StateDot state={isCurrent ? 'ongoing' : 'done'} size={8} />
        <span className={styles.title}>思考</span>
        <span className={styles.separator} aria-hidden="true">
          ··
        </span>
        <span className={styles.summary} title={summary}>
          {summary}
        </span>
        <span className={styles.chevron} aria-hidden="true">
          {expanded ? '⌃' : '⌄'}
        </span>
      </button>
      {expanded && (
        <pre className={styles.body} data-testid="reasoning-disclosure-body">
          {fullText}
        </pre>
      )}
    </div>
  );
};
