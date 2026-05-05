// ── MessageList：消息列表容器 ───────────────────────────────
import { Alert, Button, Spin, Typography } from 'antd';
import { ArrowDownOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import { useChatStyles } from '../styles';
import type { ChatTurn } from '../types';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import MessageGroup from './MessageGroup';

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
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, blockId: string) => void;
  onContextMenu: (e: React.MouseEvent, turnId: string, role: 'user' | 'assistant') => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
}

/** 保存各会话的滚动位置 */
const sessionScrollMap = new Map<string, number>();

const MessageList: React.FC<MessageListProps> = ({
  turns, agentId, selectedAgent, error, historyLoading, loadingMore, hasMoreMessages,
  onClearError, onLoadMore, formatTime, getStepTone, onDeleteTurn, onToggleReasoning, onContextMenu,
  onRerunTurn, onPinTurn,
  messageListRef, listEndRef,
}) => {
  const { styles } = useChatStyles();
  const [isAtBottom, setIsAtBottom] = useState(true);
  const [unreadCount, setUnreadCount] = useState(0);
  const prevTurnCountRef = useRef(turns.length);
  const isUserScrollingRef = useRef(false);

  // 检查是否在底部
  const checkAtBottom = useCallback(() => {
    const el = messageListRef.current;
    if (!el) return true;
    const threshold = 60;
    return el.scrollHeight - el.scrollTop - el.clientHeight < threshold;
  }, [messageListRef]);

  // 滚动到底部
  const scrollToBottom = useCallback((smooth = true) => {
    listEndRef.current?.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto' });
    setUnreadCount(0);
    setIsAtBottom(true);
  }, [listEndRef]);

  // 监听滚动事件
  const handleScroll = useCallback(() => {
    const atBottom = checkAtBottom();
    setIsAtBottom(atBottom);
    if (atBottom) setUnreadCount(0);
  }, [checkAtBottom]);

  // 会话切换时保存/恢复滚动位置
  useEffect(() => {
    const el = messageListRef.current;
    if (!el) return;
    const key = agentId ?? '__no_agent__';
    // 恢复之前保存的滚动位置
    const saved = sessionScrollMap.get(key);
    if (saved !== undefined && turns.length > 0) {
      requestAnimationFrame(() => { el.scrollTop = saved; });
    }
    el.addEventListener('scroll', handleScroll, { passive: true });
    return () => {
      // 保存当前滚动位置
      sessionScrollMap.set(key, el.scrollTop);
      el.removeEventListener('scroll', handleScroll);
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [agentId]);

  // 新消息到达时的处理
  useEffect(() => {
    const newCount = turns.length - prevTurnCountRef.current;
    if (newCount > 0 && turns.length > prevTurnCountRef.current) {
      if (isAtBottom) {
        requestAnimationFrame(() => scrollToBottom(false));
      } else {
        setUnreadCount(prev => prev + newCount);
      }
    }
    prevTurnCountRef.current = turns.length;
  }, [turns.length, isAtBottom, scrollToBottom]);

  // 点击未读按钮
  const handleUnreadClick = useCallback(() => {
    isUserScrollingRef.current = true;
    scrollToBottom(true);
    setTimeout(() => { isUserScrollingRef.current = false; }, 500);
  }, [scrollToBottom]);

  return (
    <div className={styles.messageList} ref={messageListRef} style={{ position: 'relative' }}>
      {!agentId && !error && (
        <div className={styles.onboardingState}>
          <img src="/admin/assets/images/logo.png" alt="Pudding" className={styles.onboardingLogo} />
          <Title level={2} className={styles.onboardingTitle}>你好，我是布丁</Title>
          <Text className={styles.onboardingSubtitle}>选择一个工作空间和 Agent，然后把任务交给我。</Text>
        </div>
      )}
      {agentId && turns.length === 0 && !error && !historyLoading && (
        <div className={styles.emptyState}>开始和 Agent 对话吧</div>
      )}
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
      {turns.map((turn, idx) => (
        <MessageGroup
          key={turn.turnId}
          turn={turn}
          turns={turns}
          isLatest={idx === turns.length - 1}
          selectedAgent={selectedAgent}
          formatTime={formatTime}
          getStepTone={getStepTone}
          onDeleteTurn={onDeleteTurn}
          onToggleReasoning={onToggleReasoning}
          onContextMenu={onContextMenu}
          onRerunTurn={onRerunTurn}
          onPinTurn={onPinTurn}
        />
      ))}
      {error && (
        <Alert type="error" message={error} closable onClose={onClearError} className={styles.errorAlert} />
      )}
      {/* 未读提示按钮 */}
      {!isAtBottom && unreadCount > 0 && (
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
            {unreadCount} 条新消息 ↓
          </Button>
        </div>
      )}
      <div ref={listEndRef} />
    </div>
  );
};

export default MessageList;
