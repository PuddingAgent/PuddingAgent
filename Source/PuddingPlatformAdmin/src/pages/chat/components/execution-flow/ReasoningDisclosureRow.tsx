// ── ReasoningDisclosureRow：行式推理披露（CU-06，Phase A）──────────────────
// 迁移自 ReasoningPreview（退役）：
//  - 整个 turn 内保持同一行（消费点不再用 isBeforeFirstToken 门控，正文流式后仍在主视图）
//  - 折叠态：运行中取 reasoning 最新非空行、完成后取首条非空行；单行 ellipsis
//  - 展开态：完整可审计文本（保留换行），内容区最大高度 320px、超出内部滚动，复制按钮
//  - 无 reasoning payload 时不渲染（不用字符数/占位文案伪造内容）
// 复用 ExecutionDisclosureRow 行式 chrome（16px leading 槽 / chevron / 32px 缩进展开体，
// 对齐 CU-05 §5.1 + §6.1；TurnStatus / ToolCallRow(CU-07) / DelegationRow(CU-09) 同源）。
import React, { useCallback, useState } from 'react';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import StateDot from '../StateDot';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';

export interface ReasoningDisclosureRowProps {
  /** 已清洗的 reasoning 行（全空/空数组 → 不渲染，不伪造内容）。 */
  lines: { id: string; text: string }[];
  /** true=running（摘要=最新非空行）；false=completed（摘要=首条非空行）。 */
  isCurrent?: boolean;
}

export const ReasoningDisclosureRow: React.FC<ReasoningDisclosureRowProps> = ({
  lines,
  isCurrent = true,
}) => {
  const { styles } = useExecutionFlowStyles();
  const [copied, setCopied] = useState(false);

  const visibleLines = React.useMemo(
    () => lines.filter((line) => line.text.trim().length > 0),
    [lines],
  );

  // 无 payload → 不渲染（验收 3：不用字符数/占位文案伪造内容）
  if (visibleLines.length === 0) return null;

  const summary = (isCurrent
    ? visibleLines[visibleLines.length - 1].text
    : visibleLines[0].text
  ).trim();
  const fullText = visibleLines.map((line) => line.text).join('\n\n');

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(fullText);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard 不可用（非安全上下文/权限拒绝）时静默失败，不阻塞展开交互
    }
  }, [fullText]);

  return (
    <ExecutionDisclosureRow
      leading={<StateDot state={isCurrent ? 'ongoing' : 'done'} size={10} />}
      testId="reasoning-disclosure-row"
      ariaLabel="思考过程"
      expandedContent={
        <div className={styles.reasoningWrap}>
          <button
            type="button"
            className={styles.reasoningCopy}
            data-testid="reasoning-copy"
            onClick={handleCopy}
          >
            {copied ? '已复制' : '复制'}
          </button>
          <div
            className={styles.reasoningBody}
            data-testid="reasoning-disclosure-body"
          >
            <pre className={styles.reasoningText}>{fullText}</pre>
          </div>
        </div>
      }
    >
      <span className={styles.reasoningTitle}>思考</span>
      <span className={styles.reasoningSummary} title={summary}>
        {summary}
      </span>
    </ExecutionDisclosureRow>
  );
};

export default React.memo(ReasoningDisclosureRow);
