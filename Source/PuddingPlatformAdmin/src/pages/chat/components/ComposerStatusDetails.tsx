// ── ComposerStatusDetails：运行状态详情（面向普通用户的摘要）──
import { CodeOutlined } from '@ant-design/icons';
import React from 'react';
import { useChatStyles } from '../styles';
import type { ChatStatus } from './InputArea';

/** Composer 运行时摘要视图模型 */
export interface ComposerRuntimeSummary {
  status: ChatStatus;
  statusLabel: string;
  token?: { used: number; limit: number; percentage: number };
  cacheHitRate?: number;
  /** 上下文服务（ASP/LSP 的普通模式翻译） */
  contextService: 'available' | 'idle' | 'disabled' | 'error';
  /** 索引状态 */
  index: 'available' | 'building' | 'disabled' | 'error';
  /** 后台记忆整理 */
  backgroundMemory: 'idle' | 'running' | 'disabled' | 'error';
  /** 运行中的子任务数 */
  subAgentsRunning: number;
  /** 模型服务 */
  modelService: 'available' | 'warning' | 'error';
}

interface ComposerStatusDetailsProps {
  summary: ComposerRuntimeSummary;
  onOpenDevDetails?: () => void;
}

/** 状态文案映射 */
const STATUS_LABEL: Record<ComposerRuntimeSummary['status'], string> = {
  idle: '就绪',
  composing: '输入中…',
  thinking: '正在整理上下文…',
  tool_executing: '正在调用工具…',
  streaming: '正在生成回复…',
  completed: '已完成',
  error: '出错了，可重试',
};

/** 服务状态 → 圆点色 */
const SERVICE_COLOR: Record<string, string> = {
  available: '#6f8f72',
  idle: '#b8a99a',
  running: '#6f8f72',
  building: '#c4944c',
  warning: '#c4944c',
  error: '#c4944c',
  disabled: '#d1c9c0',
};

/** 服务状态 → 文案 */
const SERVICE_LABEL: Record<string, string> = {
  available: '可用',
  idle: '待机',
  running: '运行中',
  building: '建立中',
  warning: '需注意',
  error: '异常',
  disabled: '未启用',
};

/** 格式化 Token 数 */
const fmtTokens = (n: number): string => {
  if (n >= 1000) return (n / 1000).toFixed(1) + 'k';
  return String(n);
};

const ComposerStatusDetails: React.FC<ComposerStatusDetailsProps> = ({ summary, onOpenDevDetails }) => {
  const { styles } = useChatStyles();

  return (
    <div className={styles.composerStatusDetails}>
      {/* ── 状态标题行 ── */}
      <div className={styles.composerStatusDetailsHeader}>
        <span className={styles.composerStatusDetailsDot} style={{
          background: summary.status === 'error' ? '#c4944c' :
                      summary.status === 'streaming' || summary.status === 'thinking' || summary.status === 'tool_executing' ? '#6f8f72' :
                      '#d1c9c0',
        }} />
        <span className={styles.composerStatusDetailsTitle}>
          {STATUS_LABEL[summary.status] ?? '就绪'}
        </span>
      </div>

      {/* ── 本轮摘要 ── */}
      <div className={styles.composerStatusDetailsGroup}>
        <div className={styles.composerStatusDetailsGroupTitle}>本轮摘要</div>
        {summary.token && summary.token.limit > 0 && (
          <div className={styles.composerStatusDetailRow}>
            <span className={styles.composerStatusDetailLabel}>Token</span>
            <span className={styles.composerStatusDetailValue}>
              {fmtTokens(summary.token.used)} / {fmtTokens(summary.token.limit)}
            </span>
          </div>
        )}
        {summary.cacheHitRate !== undefined && (
          <div className={styles.composerStatusDetailRow}>
            <span className={styles.composerStatusDetailLabel}>缓存命中</span>
            <span className={styles.composerStatusDetailValue}>
              {(summary.cacheHitRate * 100).toFixed(0)}%
            </span>
          </div>
        )}
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>上下文服务</span>
          <span className={styles.composerStatusDetailValue}>
            <span className={styles.composerStatusDetailDot} style={{
              background: SERVICE_COLOR[summary.contextService] ?? '#d1c9c0',
              width: 6, height: 6, display: 'inline-block', borderRadius: '50%',
              marginRight: 4, verticalAlign: 'middle',
            }} />
            {SERVICE_LABEL[summary.contextService] ?? '未知'}
          </span>
        </div>
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>后台记忆整理</span>
          <span className={styles.composerStatusDetailValue}>
            <span className={styles.composerStatusDetailDot} style={{
              background: SERVICE_COLOR[summary.backgroundMemory] ?? '#d1c9c0',
              width: 6, height: 6, display: 'inline-block', borderRadius: '50%',
              marginRight: 4, verticalAlign: 'middle',
            }} />
            {SERVICE_LABEL[summary.backgroundMemory] ?? '未知'}
          </span>
        </div>
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>索引</span>
          <span className={styles.composerStatusDetailValue}>
            <span className={styles.composerStatusDetailDot} style={{
              background: SERVICE_COLOR[summary.index] ?? '#d1c9c0',
              width: 6, height: 6, display: 'inline-block', borderRadius: '50%',
              marginRight: 4, verticalAlign: 'middle',
            }} />
            {SERVICE_LABEL[summary.index] ?? '未知'}
          </span>
        </div>
        {summary.subAgentsRunning > 0 && (
          <div className={styles.composerStatusDetailRow}>
            <span className={styles.composerStatusDetailLabel}>子任务</span>
            <span className={styles.composerStatusDetailValue}>
              {summary.subAgentsRunning} 个运行中
            </span>
          </div>
        )}
        <div className={styles.composerStatusDetailRow}>
          <span className={styles.composerStatusDetailLabel}>模型服务</span>
          <span className={styles.composerStatusDetailValue}>
            <span className={styles.composerStatusDetailDot} style={{
              background: SERVICE_COLOR[summary.modelService] ?? '#d1c9c0',
              width: 6, height: 6, display: 'inline-block', borderRadius: '50%',
              marginRight: 4, verticalAlign: 'middle',
            }} />
            {SERVICE_LABEL[summary.modelService] ?? '未知'}
          </span>
        </div>
      </div>

      {/* ── 开发者详情入口 ── */}
      {onOpenDevDetails && (
        <div className={styles.composerStatusDetailsDevEntry}>
          <button
            className={styles.composerStatusDetailsDevButton}
            onClick={onOpenDevDetails}
            aria-label="打开开发者详情"
          >
            <CodeOutlined />
            <span>打开开发者详情</span>
          </button>
        </div>
      )}
    </div>
  );
};

export default ComposerStatusDetails;
