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
  const [unreadCount, setUnreadCount] = useState(0);
  const [showScrollButton, setShowScrollButton] = useState(false);
  const prevTurnCountRef = useRef(turns.length);
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

  // Streaming 内容增长时：用 ResizeObserver 监听最后一个 turn 的 DOM 变化
  useEffect(() => {
    const el = messageListRef.current;
    if (!el) return;

    const lastTurn = el.querySelector('[data-turn-last="true"]');
    if (!lastTurn) return;

    const observer = new ResizeObserver(() => {
      if (followRef.current) {
        listEndRef.current?.scrollIntoView({ behavior: 'auto' });
      }
    });
    observer.observe(lastTurn);

    return () => observer.disconnect();
  }, [turns.length, listEndRef, messageListRef]);

  // 点击未读按钮 → 强制 FOLLOWING
  const handleUnreadClick = useCallback(() => {
    forceFollow();
  }, [forceFollow]);

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
      {turns.map((turn, idx) => {
        const isLast = idx === turns.length - 1;
        const groupElem = (
          <MessageGroup
            key={turn.turnId}
            turn={turn}
            turns={turns}
            isLatest={isLast}
            selectedAgent={selectedAgent}
            formatTime={formatTime}
            getStepTone={getStepTone}
            onDeleteTurn={onDeleteTurn}
            onToggleReasoning={onToggleReasoning}
            onContextMenu={onContextMenu}
            onRerunTurn={onRerunTurn}
            onPinTurn={onPinTurn}
          />
        );
        return isLast
          ? <div key={turn.turnId} data-turn-last="true">{groupElem}</div>
          : groupElem;
      })}
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

export default MessageList;
