// ── ChatEmptyState：聊天空状态组件（稳定、简约、克制）────
// 四种状态：booting / ready / no-agent / error
// 主视觉始终是 Pudding logo，不使用 GlobeSphere 或 AmbientParticles。
import { Button, Typography } from 'antd';
import React from 'react';
import { useChatStyles } from '../styles';

export type ChatEmptyStateMode = 'booting' | 'ready' | 'no-agent' | 'error';

export interface ChatEmptyStateProps {
  mode: ChatEmptyStateMode;
  errorText?: string;
  onRetry?: () => void;
  onSuggestionClick?: (text: string) => void;
}

const suggestions = ['分析代码', '整理记录', '检索记忆'];

const copyMap: Record<ChatEmptyStateMode, { title: string; subtitle: string }> =
  {
    booting: {
      title: '正在准备工作空间...',
      subtitle: '',
    },
    ready: {
      title: 'Pudding 已就绪',
      subtitle: '一个本地 AI Agent 正在等待你的下一步指令',
    },
    'no-agent': {
      title: '选择一个 Agent 开始',
      subtitle: '选择工作空间和 Agent 后即可开始对话',
    },
    error: {
      title: '暂时无法准备 Agent',
      subtitle: '',
    },
  };

const ChatEmptyState: React.FC<ChatEmptyStateProps> = ({
  mode,
  errorText,
  onRetry,
  onSuggestionClick,
}) => {
  const { styles } = useChatStyles();
  const copy = copyMap[mode];
  const subtitle =
    mode === 'error' ? errorText || copy.subtitle : copy.subtitle;

  return (
    <div className={styles.emptyStateShell}>
      <div className={styles.emptyStateInner}>
        <div className={styles.emptyLogoFrame}>
          <img
            src="/admin/assets/images/logo.png"
            alt="Pudding"
            className={styles.emptyLogo}
          />
        </div>

        <Typography.Title level={2} className={styles.emptyTitle}>
          {copy.title}
        </Typography.Title>

        {subtitle && (
          <Typography.Text className={styles.emptySubtitle}>
            {subtitle}
          </Typography.Text>
        )}

        {mode === 'ready' && (
          <div className={styles.emptySuggestionRow}>
            {suggestions.map((item) => (
              <button
                key={item}
                type="button"
                className={styles.emptySuggestionButton}
                onClick={() => onSuggestionClick?.(item)}
              >
                {item}
              </button>
            ))}
          </div>
        )}

        {mode === 'error' && onRetry && (
          <Button size="small" onClick={onRetry}>
            重试
          </Button>
        )}
      </div>
    </div>
  );
};

export default ChatEmptyState;
