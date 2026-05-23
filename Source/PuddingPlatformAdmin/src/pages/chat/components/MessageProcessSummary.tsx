// ── MessageProcessSummary：过程摘要（默认折叠）─────────────
import React, { useState } from 'react';
import { useChatStyles } from '../styles';
import type { TimelineItem } from '../types';

interface MessageProcessSummaryProps {
  items: TimelineItem[];
  status: string;
  onRerun?: () => void;
}

/** 生成过程摘要文本 */
const getCompletionSummary = (items: TimelineItem[]): string | null => {
  if (!items || items.length === 0) return null;
  const parts: string[] = [];
  const thinkingCount = items.filter(i => i.type === 'thinking').length;
  const toolSuccessCount = items.filter(i =>
    (i.type === 'tool_call' || i.type === 'tool_result') &&
    (i.status?.toLowerCase().includes('success') || i.status?.toLowerCase().includes('done'))
  ).length;
  if (thinkingCount > 0) parts.push(`已思考 ${thinkingCount} 步`);
  if (toolSuccessCount > 0) parts.push(`已调用 ${toolSuccessCount} 个工具`);
  if (parts.length === 0) parts.push('已完成');
  return parts.join(' · ');
};

const getItemTone = (item: TimelineItem): 'executing' | 'success' | 'error' => {
  const s = (item.status || '').toLowerCase();
  if (s.includes('error') || s.includes('fail') || s.includes('cancel')) return 'error';
  if (s.includes('success') || s.includes('done') || s.includes('complete') || (item.type === 'tool_result' && item.exitCode === 0)) return 'success';
  return 'executing';
};

const MessageProcessSummary: React.FC<MessageProcessSummaryProps> = ({ items, status, onRerun }) => {
  const { styles, cx } = useChatStyles();
  const [expanded, setExpanded] = useState(false);
  const [expandedItems, setExpandedItems] = useState<Set<string>>(new Set());

  if (!items || items.length === 0) {
    if (status === 'streaming') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processThinkingLabel}>正在生成回复...</span>
        </div>
      );
    }
    if (status === 'thinking') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processThinkingLabel}>正在思考...</span>
        </div>
      );
    }
    if (status === 'error') {
      return (
        <div className={styles.processSummaryRow}>
          <span className={styles.processRetryBtn} onClick={onRerun}>重试</span>
        </div>
      );
    }
    return null;
  }

  const summary = getCompletionSummary(items);

  if (!expanded) {
    return (
      <div
        className={styles.processSummaryRow}
        onClick={() => setExpanded(true)}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(true); } }}
      >
        <span className={styles.processSummaryDot} />
        {summary && <span className={styles.processSummaryText}>{summary}</span>}
        <span className={styles.processSummaryLink}>查看过程</span>
      </div>
    );
  }

  return (
    <div style={{ marginTop: 4 }}>
      <div
        className={styles.processCollapseLink}
        onClick={() => setExpanded(false)}
        role="button"
        tabIndex={0}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(false); } }}
      >
        ▲ 收起过程
      </div>
      <div className={styles.processExpandedArea}>
        {items.map((item) => {
          const tone = getItemTone(item);
          const isExpanded = expandedItems.has(item.id);

          if (item.type === 'thinking') {
            const preview = (item.text || '').length > 80
              ? (item.text || '').slice(0, 80) + '…'
              : item.text;
            return (
              <div key={item.id} className={styles.processItem}>
                <span className={styles.processItemName}>思考</span>
                {isExpanded ? (
                  <>
                    <div
                      className={styles.processItemToggle}
                      onClick={() => {
                        const next = new Set(expandedItems);
                        next.delete(item.id);
                        setExpandedItems(next);
                      }}
                    >
                      收起
                    </div>
                    <div className={styles.processItemDetail}>{item.text}</div>
                  </>
                ) : (
                  <>
                    <span className={styles.processItemDetail}>{preview}</span>
                    {(item.text || '').length > 80 && (
                      <div
                        className={styles.processItemToggle}
                        onClick={() => {
                          const next = new Set(expandedItems);
                          next.add(item.id);
                          setExpandedItems(next);
                        }}
                      >
                        展开
                      </div>
                    )}
                  </>
                )}
              </div>
            );
          }

          const toolName = item.name || item.status || '工具';
          const statusClass = tone === 'executing' ? styles.processItemStatusRunning
            : tone === 'error' ? styles.processItemStatusError
            : styles.processItemStatusSuccess;
          const statusLabel = tone === 'executing' ? '执行中' : tone === 'error' ? '失败' : '完成';

          return (
            <div key={item.id} className={styles.processItem}>
              <span className={styles.processItemName}>{toolName}</span>
              <span className={`${styles.processItemStatus} ${statusClass}`}>{statusLabel}</span>
              {item.message && (
                isExpanded ? (
                  <>
                    <div
                      className={styles.processItemToggle}
                      onClick={() => {
                        const next = new Set(expandedItems);
                        next.delete(item.id);
                        setExpandedItems(next);
                      }}
                    >
                      收起详情
                    </div>
                    <div className={styles.processItemDetail}>{item.message}</div>
                  </>
                ) : (
                  item.message.length > 100 ? (
                    <>
                      <div className={styles.processItemDetail}>
                        {item.message.slice(0, 100)}…
                      </div>
                      <div
                        className={styles.processItemToggle}
                        onClick={() => {
                          const next = new Set(expandedItems);
                          next.add(item.id);
                          setExpandedItems(next);
                        }}
                      >
                        查看详情
                      </div>
                    </>
                  ) : (
                    <div className={styles.processItemDetail}>{item.message}</div>
                  )
                )
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
};

export default React.memo(MessageProcessSummary);
