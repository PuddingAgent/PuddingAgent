// ── ExecutionDisclosureRow：共享行式折叠 chrome（CU-05，对齐消息 UI §5 + §6.1）──
// TurnStatus / ReasoningDisclosureRow(CU-06) / ToolCallRow(CU-07) / DelegationRow(CU-09)
// 复用同一行式 chrome：
//  - leading 16px 固定槽（状态点 10px / 工具图标 14–16px）
//  - chevron 16px 占位稳定：不可展开时占位隐藏，行首对齐不跳动
//  - 整行可点击（可点击区 ≥32px）；Enter/Space 键盘展开；:focus-visible 焦点环可见
//  - 展开体与行内容同列（左缩进 32px = 8 + 16 + 8），圆角 10px（§6.1）
//  - 可展开时使用 div role=button + aria-expanded（D5 范式，见 ToolCallRow）
import React, { useCallback, useState } from 'react';
import { useExecutionFlowStyles } from '../../styles/execution-flow.styles';

export interface ExecutionDisclosureRowProps {
  children: React.ReactNode;
  leading?: React.ReactNode;
  expandedContent?: React.ReactNode;
  expanded?: boolean;
  defaultExpanded?: boolean;
  onExpandedChange?: (expanded: boolean) => void;
  ariaLabel?: string;
  ariaLive?: 'polite' | 'assertive' | 'off';
  testId?: string;
  className?: string;
  /** 透传到行容器的 data-* 属性（消费方自定义契约，如 toolcall-row 的 data-status/data-toolname）。 */
  dataAttrs?: Record<string, string | number | boolean | undefined>;
}

export const ExecutionDisclosureRow: React.FC<ExecutionDisclosureRowProps> = ({
  children,
  leading,
  expandedContent,
  expanded: expandedProp,
  defaultExpanded = false,
  onExpandedChange,
  ariaLabel,
  ariaLive,
  testId,
  className,
  dataAttrs,
}) => {
  const { styles, cx } = useExecutionFlowStyles();
  const [internalExpanded, setInternalExpanded] = useState(defaultExpanded);
  const expanded = expandedProp ?? internalExpanded;
  const expandable = expandedContent !== undefined;

  const toggle = useCallback(() => {
    const next = !expanded;
    setInternalExpanded(next);
    onExpandedChange?.(next);
  }, [expanded, onExpandedChange]);

  const handleKeyDown = useCallback(
    (event: React.KeyboardEvent) => {
      if (event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        toggle();
      }
    },
    [toggle],
  );

  return (
    <>
      <div
        className={cx(styles.row, expandable && styles.rowClickable, className)}
        data-testid={testId}
        {...dataAttrs}
        {...(expandable
          ? {
              role: 'button' as const,
              tabIndex: 0,
              'aria-expanded': expanded,
              'aria-label': ariaLabel,
              onClick: toggle,
              onKeyDown: handleKeyDown,
            }
          : {})}
        {...(ariaLive ? { 'aria-live': ariaLive } : {})}
      >
        <span className={styles.leading} aria-hidden="true">
          {leading}
        </span>
        <div className={styles.body}>{children}</div>
        <span
          className={cx(
            styles.chevron,
            !expandable && styles.chevronPlaceholder,
          )}
          aria-hidden="true"
        >
          {expanded ? '▾' : '▸'}
        </span>
      </div>
      {expandable && expanded && (
        <div
          className={styles.expanded}
          data-testid={testId ? `${testId}-expanded` : undefined}
        >
          {expandedContent}
        </div>
      )}
    </>
  );
};

export default React.memo(ExecutionDisclosureRow);
