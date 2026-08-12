// ── P2#9 RecentlyDeniedPanel：Recently denied 面板 ──────────────
// 对齐 Claude Code /permissions 的 Recently denied（可 r 重试）：
// 展示最近被 classifier 拦截或用户拒绝的工具调用，支持重试 / 移除 / 清空。
import { DeleteOutlined, ReloadOutlined } from '@ant-design/icons';
import { Button, Empty, Tag, Tooltip, Typography } from 'antd';
import React from 'react';
import {
  AUTO_REVIEW_BLOCK_RULE_LABELS,
  type RecentlyDeniedItem,
} from '../classifier/autoReviewClassifier';

interface RecentlyDeniedPanelProps {
  items: RecentlyDeniedItem[];
  onRetry?: (item: RecentlyDeniedItem) => void;
  onRemove?: (id: string) => void;
  onClear?: () => void;
}

const RISK_COLOR: Record<RecentlyDeniedItem['riskLevel'], string> = {
  low: '#389e0d',
  medium: '#d48806',
  high: '#d46b08',
  critical: '#cf1322',
};

const panelStyle: React.CSSProperties = {
  width: 'min(420px, calc(100vw - 48px))',
  maxHeight: 320,
  overflowY: 'auto',
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  padding: '8px 4px',
};

const headerStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 8,
  padding: '0 4px',
};

const titleStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  color: 'var(--pudding-chat-text)',
};

const itemStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  padding: '8px 10px',
  borderRadius: 8,
  border: '1px solid color-mix(in srgb, var(--pudding-chat-border) 70%, transparent)',
  background: 'var(--pudding-chat-surface-muted)',
};

const itemHeaderStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 8,
  flexWrap: 'wrap',
};

const toolNameStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 600,
  color: 'var(--pudding-chat-text)',
  wordBreak: 'break-all',
};

const descriptionStyle: React.CSSProperties = {
  margin: 0,
  fontSize: 11,
  lineHeight: 1.5,
  color: 'var(--pudding-chat-text-muted)',
  wordBreak: 'break-word',
};

const timeStyle: React.CSSProperties = {
  fontSize: 10,
  color: 'var(--pudding-chat-text-muted)',
  opacity: 0.7,
};

const formatTime = (timestamp: number): string => {
  if (!Number.isFinite(timestamp)) return '';
  const diff = Date.now() - timestamp;
  if (diff < 60_000) return '刚刚';
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)} 分钟前`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)} 小时前`;
  return new Date(timestamp).toLocaleString();
};

const RecentlyDeniedPanel: React.FC<RecentlyDeniedPanelProps> = ({
  items,
  onRetry,
  onRemove,
  onClear,
}) => {
  return (
    <div data-testid="recently-denied-panel" style={panelStyle}>
      <div style={headerStyle}>
        <span style={titleStyle}>Recently denied</span>
        {items.length > 0 && onClear && (
          <Button
            size="small"
            type="text"
            onClick={onClear}
            data-testid="recently-denied-clear"
          >
            清空
          </Button>
        )}
      </div>

      {items.length === 0 ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              暂无被拒绝的工具调用
            </Typography.Text>
          }
          data-testid="recently-denied-empty"
        />
      ) : (
        items.map((item) => (
          <div
            key={item.id}
            style={itemStyle}
            data-testid="recently-denied-item"
            data-denied-id={item.id}
          >
            <div style={itemHeaderStyle}>
              <span style={toolNameStyle}>
                🔧 {item.toolName || '工具调用'}
              </span>
              <Tag
                color={RISK_COLOR[item.riskLevel] ?? '#d48806'}
                style={{ marginInlineEnd: 0, fontSize: 10 }}
              >
                {AUTO_REVIEW_BLOCK_RULE_LABELS[item.rule] ?? '未知风险'}
              </Tag>
            </div>
            {item.description && (
              <p style={descriptionStyle} title={item.description}>
                {item.description}
              </p>
            )}
            <div
              style={{
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'space-between',
                gap: 8,
              }}
            >
              <span style={timeStyle}>
                {formatTime(item.blockedAt)}
                {item.source === 'user_deny' ? ' · 手动拒绝' : ' · 自动拦截'}
              </span>
              <span style={{ display: 'inline-flex', gap: 4 }}>
                {onRetry && (
                  <Tooltip title="重试该调用">
                    <Button
                      size="small"
                      type="text"
                      icon={<ReloadOutlined />}
                      aria-label="重试"
                      onClick={() => onRetry(item)}
                      data-testid="recently-denied-retry"
                    />
                  </Tooltip>
                )}
                {onRemove && (
                  <Tooltip title="移除">
                    <Button
                      size="small"
                      type="text"
                      icon={<DeleteOutlined />}
                      aria-label="移除"
                      onClick={() => onRemove(item.id)}
                      data-testid="recently-denied-remove"
                    />
                  </Tooltip>
                )}
              </span>
            </div>
          </div>
        ))
      )}
    </div>
  );
};

export default React.memo(RecentlyDeniedPanel);
