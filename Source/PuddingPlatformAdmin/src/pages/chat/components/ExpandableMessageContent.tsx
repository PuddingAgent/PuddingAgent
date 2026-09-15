import React from 'react';
import { useExecutionFlowStyles } from '../styles/execution-flow.styles';

/** Measure rendered content so tables, code and narrow screens share one budget. */
const ExpandableMessageContent: React.FC<{
  children: React.ReactNode;
  label?: string;
  previewHeight?: number;
  disabled?: boolean;
}> = ({ children, label = '正文', previewHeight = 360, disabled = false }) => {
  const { styles } = useExecutionFlowStyles();
  const contentRef = React.useRef<HTMLDivElement>(null);
  const contentId = React.useId();
  const [overflowing, setOverflowing] = React.useState(false);
  const [expanded, setExpanded] = React.useState(false);
  React.useLayoutEffect(() => {
    const content = contentRef.current;
    if (!content || disabled) return;
    const measure = () => setOverflowing(content.getBoundingClientRect().height > previewHeight + 80);
    measure();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(measure);
    observer.observe(content);
    return () => observer.disconnect();
  }, [children, disabled, previewHeight]);
  const collapsible = !disabled && overflowing;
  const collapsed = collapsible && !expanded;
  const toggle = () => setExpanded((value) => !value);
  return (
    <div className={styles.readingDisclosure} data-testid="reading-disclosure" data-collapsed={collapsed}>
      {collapsible && expanded && (
        <button key="top" type="button" className={styles.readingToggle} aria-expanded="true" aria-controls={contentId} onClick={toggle}>
          收起{label}
        </button>
      )}
      <div id={contentId} className={collapsed ? styles.readingPreview : undefined} style={collapsed ? { maxHeight: previewHeight } : undefined}
        onFocusCapture={() => { if (collapsed) setExpanded(true); }}>
        <div ref={contentRef} style={{ display: 'flow-root' }}>{children}</div>
      </div>
      {collapsible && (
        <button key="bottom" type="button" className={styles.readingToggle} aria-expanded={expanded} aria-controls={contentId} onClick={toggle}>
          {expanded ? `收起${label}` : `展开完整${label}`} <span aria-hidden="true">{expanded ? '⌃' : '⌄'}</span>
        </button>
      )}
    </div>
  );
};

export default ExpandableMessageContent;
