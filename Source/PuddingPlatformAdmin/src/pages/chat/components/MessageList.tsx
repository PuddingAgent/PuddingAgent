// ── MessageList：消息列表容器 ───────────────────────────────
import { Alert, Button, Spin, Typography } from 'antd';
import { ArrowDownOutlined, RobotOutlined, LoadingOutlined, CheckCircleOutlined, CloseCircleOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn, SubAgentCardMap, SubAgentCard as SubAgentCardType } from '../types';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import MessageStream from './MessageStream';
import ChatEmptyState from './ChatEmptyState';
import type { ChatEmptyStateMode } from './ChatEmptyState';

const { Title, Text } = Typography;

interface MessageListProps {
  turns: ChatTurn[];
  agentId: string | undefined;
  selectedAgent?: WorkspaceAgentDto;
  error: string | null;
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  onClearError: () => void;
  onLoadMore: () => void;
  formatTime: (ts: number) => string;
  onDeleteTurn: (turnId: string) => void;
  onContextMenu: (e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  subAgentCards?: SubAgentCardMap;
}

/** 保存各会话的滚动位置 */
const sessionScrollMap = new Map<string, number>();

/** 子代理独立卡片 */
const SubAgentCard: React.FC<{ card: SubAgentCardType }> = ({ card }) => {
  const statusConfig: Record<string, { icon: React.ReactNode; color: string; label: string }> = {
    spawning: { icon: <LoadingOutlined spin />, color: '#faad14', label: '创建中' },
    running: { icon: <LoadingOutlined spin />, color: '#1890ff', label: '运行中' },
    completed: { icon: <CheckCircleOutlined />, color: '#52c41a', label: '已完成' },
    failed: { icon: <CloseCircleOutlined />, color: '#ff4d4f', label: '失败' },
  };
  const sc = statusConfig[card.status] || statusConfig.spawning;
  return (
    <div style={{
      margin: '12px 16px 12px 48px',
      padding: 12,
      borderRadius: 10,
      border: '1px solid var(--ant-color-border-secondary, #e8e8e8)',
      borderLeft: `4px solid ${sc.color}`,
      background: 'var(--ant-color-bg-elevated, #fafafa)',
      fontSize: 13,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
        <RobotOutlined style={{ color: sc.color }} />
        <Text strong style={{ color: sc.color }}>子代理</Text>
        <Text type="secondary" style={{ fontSize: 11 }}>
          {card.subSessionId?.slice(-12) || '?'}
        </Text>
        <span style={{ color: sc.color }}>{sc.icon}</span>
        <Text type="secondary" style={{ fontSize: 12 }}>{sc.label}</Text>
      </div>
      {card.taskSummary && (
        <Text type="secondary" style={{ fontSize: 12 }}>任务：{card.taskSummary}</Text>
      )}
      {card.output && (
        <div style={{
          marginTop: 8, padding: '8px 12px',
          background: 'var(--ant-color-bg-container, #fff)',
          borderRadius: 6, border: '1px solid var(--ant-color-border-secondary, #e8e8e8)',
          whiteSpace: 'pre-wrap', wordBreak: 'break-word',
          maxHeight: 200, overflowY: 'auto',
          fontSize: 12, lineHeight: 1.6,
          color: 'var(--ant-color-text, #333)',
        }}>
          {card.output}
        </div>
      )}
    </div>
  );
};

const MessageList: React.FC<MessageListProps> = ({
  turns, agentId, selectedAgent, error, historyLoading, loadingMore, hasMoreMessages,
  onClearError, onLoadMore, formatTime, onDeleteTurn, onContextMenu,
  onRerunTurn, onPinTurn,
  messageListRef, listEndRef, subAgentCards,
}) => {
  const { styles } = useChatStyles();
  const [unreadCount, setUnreadCount] = useState(0);
  const [showScrollButton, setShowScrollButton] = useState(false);
  const prevTurnCountRef = useRef(turns.length);
  // ADR-InkBloom: RAF 节流 scroll ref
  const scrollRafRef = useRef<number | null>(null);
  const scheduleFollowScroll = useCallback(() => {
    if (!followRef.current) return;
    if (scrollRafRef.current != null) return;
    scrollRafRef.current = requestAnimationFrame(() => {
      scrollRafRef.current = null;
      if (!followRef.current) return;
      listEndRef.current?.scrollIntoView({ behavior: 'auto' });
    });
  }, [listEndRef]);
  // ── 滚动跟随状态机（ref 避免 React 异步渲染时机问题）──
  const followRef = useRef(true);            // true=FOLLOWING, false=FREE
  const userInitiatedScrollRef = useRef(false);

  // 检查是否在底部
  const checkAtBottom = useCallback(() => {
    const el = messageListRef.current;
    if (!el) return true;
    const threshold = 80; // px，容忍度
    return el.scrollHeight - el.scrollTop - el.clientHeight < threshold;
  }, [messageListRef]);

  // 滚动到底部
  const scrollToBottom = useCallback((smooth = true) => {
    listEndRef.current?.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto' });
    setUnreadCount(0);
  }, [listEndRef]);

  // 用户滚动事件处理：即时更新 ref（不等 state），state 仅用于按钮显隐
  const handleScroll = useCallback(() => {
    const atBottom = checkAtBottom();
    followRef.current = atBottom;
    if (atBottom) {
      setUnreadCount(0);
      setShowScrollButton(false);
    } else {
      setShowScrollButton(true);
    }
  }, [checkAtBottom]);

  // 用户主动操作 → 强制进入 FOLLOWING 模式
  const forceFollow = useCallback(() => {
    followRef.current = true;
    scrollToBottom(true);
    setTimeout(() => { followRef.current = true; }, 500);
  }, [scrollToBottom]);

  // 会话切换时保存/恢复滚动位置 + 绑定 scroll 事件
  useEffect(() => {
    const el = messageListRef.current;
    if (!el) return;
    const key = agentId ?? '__no_agent__';
    const saved = sessionScrollMap.get(key);
    if (saved !== undefined && turns.length > 0) {
      requestAnimationFrame(() => { el.scrollTop = saved; });
    }
    // 切换会话时，检查当前位置决定初始跟随状态
    followRef.current = checkAtBottom();
    el.addEventListener('scroll', handleScroll, { passive: true });
    return () => {
      sessionScrollMap.set(key, el.scrollTop);
      el.removeEventListener('scroll', handleScroll);
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentId]);

  // 新 turn 到达时的处理：用户发送消息后强制 FOLLOWING
  useEffect(() => {
    const newCount = turns.length - prevTurnCountRef.current;
    if (newCount > 0 && turns.length > prevTurnCountRef.current) {
      followRef.current = true;
      requestAnimationFrame(() => scrollToBottom(false));
    }
    prevTurnCountRef.current = turns.length;
  }, [turns.length, scrollToBottom]);

  // Streaming 内容增长时：用 ResizeObserver 监听最后一个 turn 的 DOM 变化（RAF 节流）
  useEffect(() => {
    const el = messageListRef.current;
    if (!el) return;

    const lastTurn = el.querySelector('[data-turn-last="true"]');
    if (!lastTurn) return;

    const observer = new ResizeObserver(() => {
      scheduleFollowScroll();
    });
    observer.observe(lastTurn);

    return () => {
      observer.disconnect();
      if (scrollRafRef.current != null) {
        cancelAnimationFrame(scrollRafRef.current);
        scrollRafRef.current = null;
      }
    };
  }, [turns.length, listEndRef, messageListRef, scheduleFollowScroll]);

  // 点击未读按钮 → 强制 FOLLOWING
  const handleUnreadClick = useCallback(() => {
    forceFollow();
  }, [forceFollow]);

  return (
    <div className={styles.messageList} ref={messageListRef} style={{ position: 'relative' }} data-testid="chat-message-list">
      {(() => {
        const emptyStateMode: ChatEmptyStateMode | null = (() => {
          if (historyLoading || turns.length > 0) return null;
          if (error) return 'error';
          if (!agentId) return 'no-agent';
          return 'ready';
        })();
        return emptyStateMode ? (
          <ChatEmptyState
            mode={emptyStateMode}
            errorText={error ?? undefined}
            onRetry={onClearError}
            onSuggestionClick={(text) => {
              window.dispatchEvent(new CustomEvent('pudding:chat:suggestion', { detail: text }));
            }}
          />
        ) : null;
      })()}
      {historyLoading && (
        <div className={styles.historyLoading}><Spin /></div>
      )}
      {loadingMore && (
        <div style={{ textAlign: 'center', padding: 8 }}><Spin size="small" /></div>
      )}
      {hasMoreMessages && !loadingMore && (
        <div
          style={{ textAlign: 'center', padding: 8, cursor: 'pointer', color: 'var(--ant-color-primary)' }}
          onClick={onLoadMore}
        >
          加载更多历史消息
        </div>
      )}
      {turns.length > 0 && (
        <div data-turn-last={turns.length > 0 ? 'true' : undefined}>
          <MessageStream
            turns={turns}
            agentName={selectedAgent?.name || 'Pudding'}
            formatTime={formatTime}
            onContextMenu={onContextMenu}
            onRerunTurn={onRerunTurn}
            onPinTurn={onPinTurn}
            onDeleteTurn={onDeleteTurn}
          />
        </div>
      )}
      {/* 子代理卡片 */}
      {subAgentCards && Object.values(subAgentCards).map(card => (
        <SubAgentCard key={card.turnId} card={card} />
      ))}
      {error && (
        <Alert type="error" message={error} closable onClose={onClearError} className={styles.errorAlert} />
      )}
      {/* 未读提示按钮 */}
      {showScrollButton && (
        <div style={{
          position: 'sticky', bottom: 16, display: 'flex', justifyContent: 'flex-end',
          paddingRight: 8, zIndex: 10, pointerEvents: 'none',
        }}>
          <Button
            type="default"
            icon={<ArrowDownOutlined />}
            onClick={handleUnreadClick}
            style={{
              background: 'var(--accent-purple)', color: '#fff',
              borderRadius: 20, padding: '8px 16px', border: 'none',
              pointerEvents: 'auto', fontWeight: 500, fontSize: 13,
            }}
          >
            回到底部 ↓
          </Button>
        </div>
      )}
      <div ref={listEndRef} />
    </div>
  );
};

export default React.memo(MessageList);
