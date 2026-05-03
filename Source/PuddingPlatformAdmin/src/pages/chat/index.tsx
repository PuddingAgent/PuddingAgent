import { SendOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import { Alert, Button, Input, Spin } from 'antd';
import { createStyles } from 'antd-style';
import React, { useEffect, useRef, useState } from 'react';

// ── 类型 ─────────────────────────────────────────────
interface ChatMessage {
  role: 'user' | 'agent';
  text: string;
}

interface ChatResponse {
  sessionId: string;
  reply: string;
  isSuccess: boolean;
}

// ── 样式 ─────────────────────────────────────────────
const useStyles = createStyles(({ token }) => ({
  container: {
    display: 'flex',
    flexDirection: 'column',
    height: 'calc(100vh - 140px)',
    maxWidth: 800,
    margin: '0 auto',
  },
  messageList: {
    flex: 1,
    overflowY: 'auto' as const,
    padding: '16px 0',
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 12,
  },
  messageRow: {
    display: 'flex',
  },
  userRow: {
    justifyContent: 'flex-end',
  },
  agentRow: {
    justifyContent: 'flex-start',
  },
  bubble: {
    maxWidth: '70%',
    padding: '10px 16px',
    borderRadius: 12,
    lineHeight: 1.6,
    wordBreak: 'break-word' as const,
    whiteSpace: 'pre-wrap' as const,
  },
  userBubble: {
    background: '#6366f1',
    color: '#fff',
    borderBottomRightRadius: 4,
  },
  agentBubble: {
    background: token.colorBgElevated,
    color: token.colorText,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderBottomLeftRadius: 4,
  },
  inputArea: {
    display: 'flex',
    gap: 8,
    padding: '12px 0',
    borderTop: `1px solid ${token.colorBorderSecondary}`,
    background: token.colorBgContainer,
  },
  loadingRow: {
    display: 'flex',
    justifyContent: 'flex-start',
    padding: '4px 0',
  },
  emptyState: {
    flex: 1,
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: token.colorTextQuaternary,
    fontSize: 16,
  },
}));

// ── 组件 ─────────────────────────────────────────────
const ChatPage: React.FC = () => {
  const { styles, cx } = useStyles();

  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [inputValue, setInputValue] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const sessionIdRef = useRef<string | undefined>(undefined);
  const listEndRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<any>(null);

  // 自动滚动到底部
  useEffect(() => {
    listEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // 发送消息
  const handleSend = async () => {
    const text = inputValue.trim();
    if (!text || loading) return;

    setInputValue('');
    setError(null);

    const userMessage: ChatMessage = { role: 'user', text };
    setMessages((prev) => [...prev, userMessage]);
    setLoading(true);

    try {
      const res = await fetch('/api/chat', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          message: text,
          sessionId: sessionIdRef.current,
        }),
      });

      if (!res.ok) {
        throw new Error(`请求失败 (${res.status})`);
      }

      const data: ChatResponse = await res.json();
      sessionIdRef.current = data.sessionId;

      const agentMessage: ChatMessage = { role: 'agent', text: data.reply };
      setMessages((prev) => [...prev, agentMessage]);
    } catch (err: any) {
      setError(err.message || '网络错误，请检查后端服务是否启动');
    } finally {
      setLoading(false);
    }
  };

  // Enter 发送
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  return (
    <PageContainer header={{ title: 'Chat', ghost: true }}>
      <div className={styles.container}>
        {/* 消息列表 */}
        <div className={styles.messageList}>
          {messages.length === 0 && !error && (
            <div className={styles.emptyState}>
              发送消息开始对话
            </div>
          )}

          {messages.map((msg, idx) => (
            <div
              key={idx}
              className={cx(
                styles.messageRow,
                msg.role === 'user' ? styles.userRow : styles.agentRow,
              )}
            >
              <div
                className={cx(
                  styles.bubble,
                  msg.role === 'user' ? styles.userBubble : styles.agentBubble,
                )}
              >
                {msg.text}
              </div>
            </div>
          ))}

          {/* Loading 骨架 */}
          {loading && (
            <div className={styles.loadingRow}>
              <Spin size="small" style={{ marginLeft: 8 }} />
            </div>
          )}

          {/* 错误提示 */}
          {error && (
            <Alert
              type="error"
              message={error}
              closable
              onClose={() => setError(null)}
              style={{ margin: '8px 0' }}
            />
          )}

          <div ref={listEndRef} />
        </div>

        {/* 输入区域 */}
        <div className={styles.inputArea}>
          <Input
            ref={inputRef}
            value={inputValue}
            onChange={(e) => setInputValue(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="输入消息，Enter 发送"
            disabled={loading}
            style={{ flex: 1 }}
          />
          <Button
            type="primary"
            icon={<SendOutlined />}
            onClick={handleSend}
            loading={loading}
            disabled={!inputValue.trim()}
          >
            发送
          </Button>
        </div>
      </div>
    </PageContainer>
  );
};

export default ChatPage;
