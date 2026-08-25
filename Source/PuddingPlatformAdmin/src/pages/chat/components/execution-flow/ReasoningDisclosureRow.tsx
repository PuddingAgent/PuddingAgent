// ── ReasoningDisclosureRow：行式推理披露（CU-06 + 行为链 §3.3 计量 chip）──────
// 迁移自 ReasoningPreview（退役）：
//  - 整个 turn 内保持同一行（消费点不再用 isBeforeFirstToken 门控，正文流式后仍在主视图）
//  - 折叠态：运行中取 reasoning 最新非空行 + 行扫光；完成后取首条非空行，
//    且有段时长时渲染「12s」计量 chip（对齐 harness/ChatGPT "Thought for Ns" 模式）
//  - 展开态：完整可审计文本（保留换行），内容区最大高度 320px、超出内部滚动，复制按钮
//  - 无 reasoning payload 时不渲染（不用字符数/占位文案伪造内容）
// 复用 ExecutionDisclosureRow 行式 chrome（16px leading 槽 / chevron / 32px 缩进展开体，
// 对齐 CU-05 §5.1 + §6.1；TurnStatus / ToolCallRow(CU-07) / DelegationRow(CU-09) 同源）。
// P2 多段：每个 ReasoningNode（一段连续推理）渲染一行；本组件不做跨段聚合。
import React, { useCallback, useState } from 'react';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';
import { formatDurationMs } from '../../utils/formatDuration';
import StateDot from '../StateDot';
import { ExecutionDisclosureRow } from './ExecutionDisclosureRow';

export interface ReasoningDisclosureRowProps {
  /** 已清洗的 reasoning 行（全空/空数组 → 不渲染，不伪造内容）。 */
  lines: { id: string; text: string }[];
  /** true=running（摘要=最新非空行 + 行扫光）；false=completed（计量 chip + 首行）。 */
  isCurrent?: boolean;
  /** 段时长（毫秒，服务端事实派生：段首/段末 occurredAt 差）；缺失时不渲染 chip。 */
  durationMs?: number | null;
  /** 受控展开（TurnContentStream 注册表）；未传时内部自管。 */
  expanded?: boolean;
  onExpandedChange?: (expanded: boolean) => void;
}

export const ReasoningDisclosureRow: React.FC<ReasoningDisclosureRowProps> = ({
  lines,
  isCurrent = true,
  durationMs,
  expanded,
  onExpandedChange,
}) => {
  const { styles, cx } = useExecutionFlowStyles();
  const [copied, setCopied] = useState(false);

  const visibleLines = React.useMemo(
    () => lines.filter((line) => line.text.trim().length > 0),
    [lines],
  );

  const summary =
    visibleLines.length > 0
      ? (isCurrent
          ? visibleLines[visibleLines.length - 1].text
          : visibleLines[0].text
        ).trim()
      : '';
  const fullText = visibleLines.map((line) => line.text).join('\n\n');
  const chipText = isCurrent ? null : formatDurationMs(durationMs);

  const handleCopy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(fullText);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // clipboard 不可用（非安全上下文/权限拒绝）时静默失败，不阻塞展开交互
    }
  }, [fullText]);

  // 无 payload → 不渲染（验收 3：不用字符数/占位文案伪造内容）
  if (visibleLines.length === 0) return null;

  return (
    <ExecutionDisclosureRow
      leading={<StateDot state={isCurrent ? 'ongoing' : 'done'} size={10} />}
      testId="reasoning-disclosure-row"
      ariaLabel="思考过程"
      className={cx(isCurrent && styles.rowSweep)}
      expanded={expanded}
      onExpandedChange={onExpandedChange}
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
      {chipText && (
        <span
          className={styles.reasoningChip}
          data-testid="reasoning-duration-chip"
        >
          {chipText}
        </span>
      )}
      <span className={styles.titleDot} aria-hidden="true" />
      <span className={styles.reasoningSummary} title={summary}>
        {summary}
      </span>
    </ExecutionDisclosureRow>
  );
};

export default React.memo(ReasoningDisclosureRow);
