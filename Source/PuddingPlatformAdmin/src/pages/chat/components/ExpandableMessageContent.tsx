import React from 'react';
import { useExecutionFlowStyles } from '../styles/execution-flow.styles';

/** Measure rendered content so tables, code and narrow screens share one budget. */
const ExpandableMessageContent: React.FC<{
  children: React.ReactNode;
  label?: string;
  previewHeight?: number;
  disabled?: boolean;
  /** false = 永不折叠（正文/过程这类「给用户看」的内容，不提供折叠入口）。 */
  collapsible?: boolean;
}> = ({
  children,
  label = '正文',
  previewHeight = 360,
  disabled = false,
  collapsible = true,
}) => {
  const { styles } = useExecutionFlowStyles();
  const contentRef = React.useRef<HTMLDivElement>(null);
  const contentId = React.useId();
  const [overflowing, setOverflowing] = React.useState(false);
  const [expanded, setExpanded] = React.useState(false);
  // 用户批注（2026-09-19）：「正文是给用户看的，你折叠的话，用户怎么看」——
  // 正文调用点一律传 collapsible={false}；此时连测量都跳过（无按钮、无预览），
  // 内容始终完整渲染。过程类内容同理，其折叠由行为组自身的标题行承担。
  const collapseEnabled = collapsible && !disabled;
  React.useLayoutEffect(() => {
    const content = contentRef.current;
    if (!content || !collapseEnabled) return;
    const measure = () =>
      setOverflowing(content.getBoundingClientRect().height > previewHeight + 80);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(content);
    return () => observer.disconnect();
  }, [children, collapseEnabled, previewHeight]);
  const collapsibleContent = collapseEnabled && overflowing;
  const collapsed = collapsibleContent && !expanded;
  const toggle = () => setExpanded((value) => !value);
  return (
    <div
      className={styles.readingDisclosure}
      data-testid="reading-disclosure"
      data-collapsed={collapsed}
    >
      {collapsibleContent && expanded && (
        <button
          key="top"
          type="button"
          className={styles.readingToggle}
          aria-expanded="true"
          aria-controls={contentId}
          onClick={toggle}
        >
          收起{label}
        </button>
      )}
      <div
        id={contentId}
        className={collapsed ? styles.readingPreview : undefined}
        style={collapsed ? { maxHeight: previewHeight } : undefined}
        onFocusCapture={() => {
          if (collapsed) setExpanded(true);
        }}
      >
        <div ref={contentRef} style={{ display: 'flow-root' }}>
          {children}
        </div>
      </div>
      {collapsibleContent && (
        <button
          key="bottom"
          type="button"
          className={styles.readingToggle}
          aria-expanded={expanded}
          aria-controls={contentId}
          onClick={toggle}
        >
          {expanded ? `收起${label}` : `展开完整${label}`}{' '}
          <span aria-hidden="true">{expanded ? '⌃' : '⌄'}</span>
        </button>
      )}
    </div>
  );
};

export default ExpandableMessageContent;
