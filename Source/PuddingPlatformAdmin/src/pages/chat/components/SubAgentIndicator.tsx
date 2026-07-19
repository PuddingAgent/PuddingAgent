import {
  CheckCircleOutlined,
  CloseCircleOutlined,
  CloseOutlined,
  ClockCircleOutlined,
  SendOutlined,
  SyncOutlined,
  ToolOutlined,
} from '@ant-design/icons';
import { Modal, Progress, Select, Space, Tag, Tooltip, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import type {
  SubAgentCard,
  SubAgentCardMap,
  SubAgentCardStatus,
} from '../types';
import { useChatStyles } from '../styles';

const { Text } = Typography;

interface SubAgentIndicatorProps {
  sessionId?: string | null;
  subAgentCards?: SubAgentCardMap;
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  renderTrigger?: boolean;
}

type TimeRangeFilter = '1d' | '7d' | '30d' | 'all';

const terminalStatuses = new Set<SubAgentCardStatus>([
  'completed',
  'failed',
  'cancelled',
  'timed_out',
  'interrupted',
]);

const statusConfig: Record<
  SubAgentCardStatus,
  { color: string; label: string; icon: React.ReactNode }
> = {
  spawning: { color: '#faad14', label: '创建中', icon: <SyncOutlined spin /> },
  running: { color: '#22c55e', label: '运行中', icon: <SyncOutlined spin /> },
  completed: {
    color: '#3b82f6',
    label: '已完成',
    icon: <CheckCircleOutlined />,
  },
  failed: { color: '#ef4444', label: '失败', icon: <CloseCircleOutlined /> },
  cancelled: {
    color: '#8c8c8c',
    label: '已取消',
    icon: <CloseCircleOutlined />,
  },
  timed_out: {
    color: '#fa8c16',
    label: '已超时',
    icon: <ClockCircleOutlined />,
  },
  interrupted: {
    color: '#722ed1',
    label: '已中断',
    icon: <CloseCircleOutlined />,
  },
};

const phaseLabel: Record<string, string> = {
  created: '已登记',
  starting: '启动运行时',
  context: '装配上下文',
  round: '处理轮次',
  llm: '调用模型',
  tool: '执行工具',
  completed: '运行结束',
};

const formatDuration = (durationMs: number): string => {
  if (durationMs < 1000) return `${Math.max(0, Math.round(durationMs))}ms`;
  const seconds = Math.floor(durationMs / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remain = seconds % 60;
  return `${minutes}m ${remain}s`;
};

const formatTokens = (tokens?: number): string => {
  if (!tokens) return '0';
  if (tokens < 1000) return String(tokens);
  if (tokens < 1_000_000) return `${(tokens / 1000).toFixed(1)}K`;
  return `${(tokens / 1_000_000).toFixed(2)}M`;
};

const shortId = (id: string): string =>
  id.length > 18 ? `${id.slice(0, 7)}…${id.slice(-7)}` : id;

const SubAgentIndicator: React.FC<SubAgentIndicatorProps> = ({
  sessionId,
  subAgentCards = {},
  open,
  onOpenChange,
  renderTrigger = true,
}) => {
  const { styles } = useChatStyles();
  const [internalPanelOpen, setInternalPanelOpen] = useState(false);
  const [filter, setFilter] = useState<SubAgentCardStatus | 'all'>('all');
  const [timeRange, setTimeRange] = useState<TimeRangeFilter>('7d');
  const [now, setNow] = useState(Date.now());
  const panelOpen = open ?? internalPanelOpen;

  const setPanelOpen = useCallback(
    (nextOpen: boolean) => {
      if (open === undefined) setInternalPanelOpen(nextOpen);
      onOpenChange?.(nextOpen);
    },
    [onOpenChange, open],
  );

  const subAgents = useMemo(
    () =>
      Object.values(subAgentCards)
        .filter(
          (card) =>
            !sessionId ||
            !card.parentSessionId ||
            card.parentSessionId === sessionId,
        )
        .sort((a, b) => b.spawnedAt - a.spawnedAt),
    [sessionId, subAgentCards],
  );

  const runningCount = subAgents.filter(
    (item) => item.status === 'running' || item.status === 'spawning',
  ).length;
  const active = runningCount > 0;
  const lastCompleted = subAgents.find((item) =>
    terminalStatuses.has(item.status),
  );

  useEffect(() => {
    if (!panelOpen || !active) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [active, panelOpen]);

  const timeFilteredAgents = useMemo(() => {
    if (timeRange === 'all') return subAgents;
    const days = timeRange === '1d' ? 1 : timeRange === '30d' ? 30 : 7;
    const cutoff = now - days * 24 * 60 * 60 * 1000;
    return subAgents.filter(
      (item) => !terminalStatuses.has(item.status) || item.spawnedAt >= cutoff,
    );
  }, [now, subAgents, timeRange]);

  const filteredAgents =
    filter === 'all'
      ? timeFilteredAgents
      : timeFilteredAgents.filter((item) => item.status === filter);

  const renderRun = (run: SubAgentCard) => {
    const cfg = statusConfig[run.status] ?? statusConfig.failed;
    const endAt = run.completedAt ?? now;
    const elapsedMs = Math.max(0, endAt - run.spawnedAt);
    const timeoutMs = (run.timeoutSeconds ?? 0) * 1000;
    const timeoutPercent =
      timeoutMs > 0 && !terminalStatuses.has(run.status)
        ? Math.min(100, Math.round((elapsedMs / timeoutMs) * 100))
        : undefined;
    const roundText = run.maxRounds
      ? `${run.currentRound ?? 0}/${run.maxRounds}`
      : String(run.currentRound ?? 0);

    return (
      <div
        key={run.runId ?? run.subSessionId}
        data-testid={`subagent-run-${run.runId ?? run.subSessionId}`}
        style={{
          padding: '12px 14px',
          marginBottom: 10,
          borderRadius: 8,
          background: 'var(--ant-color-fill-secondary)',
          borderLeft: `3px solid ${cfg.color}`,
          fontSize: 12,
        }}
      >
        <div
          style={{
            display: 'flex',
            justifyContent: 'space-between',
            gap: 12,
            alignItems: 'flex-start',
          }}
        >
          <Space size={6} wrap>
            <span>{cfg.icon}</span>
            <Tag color={cfg.color} style={{ margin: 0 }}>
              {cfg.label}
            </Tag>
            <Tag style={{ margin: 0 }}>
              {phaseLabel[run.phase ?? ''] ?? run.phase ?? '运行中'}
            </Tag>
            {run.originToolId && <Tag color="geekblue">{run.originToolId}</Tag>}
            {run.role && <Tag color="cyan">{run.role}</Tag>}
          </Space>
          <Text code style={{ fontSize: 10 }} copyable={Boolean(run.runId)}>
            {shortId(run.runId ?? run.subSessionId)}
          </Text>
        </div>

        <div style={{ marginTop: 8, lineHeight: 1.55 }}>
          {run.taskSummary}
        </div>

        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
            gap: '6px 12px',
            marginTop: 10,
            color: 'var(--ant-color-text-secondary)',
          }}
        >
          <span>模型：{run.modelId ?? '-'}</span>
          <span>Provider：{run.providerId ?? '-'}</span>
          <span>轮次：{roundText}</span>
          <span>耗时：{formatDuration(elapsedMs)}</span>
          <span>Token：{formatTokens(run.totalTokens)}</span>
          <span>
            工具：{run.toolCount ?? 0}
            {run.failedToolCount ? ` / 失败 ${run.failedToolCount}` : ''}
          </span>
        </div>

        {run.activeToolName && (
          <div style={{ marginTop: 8 }}>
            <ToolOutlined /> 正在调用：<Text code>{run.activeToolName}</Text>
          </div>
        )}
        {!run.activeToolName && run.lastToolName && (
          <div style={{ marginTop: 8, color: 'var(--ant-color-text-secondary)' }}>
            最近工具：<Text code>{run.lastToolName}</Text>
          </div>
        )}

        {timeoutPercent !== undefined && (
          <div style={{ marginTop: 8 }}>
            <Progress
              size="small"
              percent={timeoutPercent}
              status={timeoutPercent >= 90 ? 'exception' : 'active'}
              format={() =>
                `超时预算 ${formatDuration(elapsedMs)} / ${formatDuration(timeoutMs)}`
              }
            />
          </div>
        )}

        {run.error && (
          <div
            style={{
              marginTop: 8,
              color: 'var(--ant-color-error)',
              whiteSpace: 'pre-wrap',
            }}
          >
            {run.error}
          </div>
        )}
      </div>
    );
  };

  return (
    <>
      {renderTrigger && (
        <Tooltip
          title={
            active
              ? `${runningCount} 个子代理运行中`
              : lastCompleted
                ? `子代理空闲 · 最近 ${statusConfig[lastCompleted.status].label}`
                : '子代理空闲'
          }
        >
          <span
            className={styles.statusIconGroup}
            style={{ cursor: 'pointer' }}
            onClick={() => setPanelOpen(true)}
          >
            <SendOutlined
              style={{
                fontSize: 12,
                color: active ? '#22c55e' : 'var(--earth-brown)',
                opacity: active ? 1 : 0.4,
              }}
            />
            <span
              className={styles.statusIconLabel}
              style={{
                color: active ? '#22c55e' : undefined,
                opacity: active ? 1 : 0.5,
                fontWeight: active ? 600 : undefined,
              }}
            >
              {runningCount}
            </span>
          </span>
        </Tooltip>
      )}

      <Modal
        title={
          <Space>
            <SendOutlined
              style={{ color: active ? '#22c55e' : 'var(--earth-brown)' }}
            />
            <span>子代理运行</span>
            <Tag color={active ? 'green' : 'default'}>
              {active ? `${runningCount} 运行中` : '实时事件已连接'}
            </Tag>
          </Space>
        }
        open={panelOpen}
        onCancel={() => setPanelOpen(false)}
        footer={null}
        width={760}
        styles={{
          body: { maxHeight: '68vh', overflowY: 'auto', padding: '12px 16px' },
        }}
        closeIcon={<CloseOutlined />}
      >
        <div
          style={{
            display: 'flex',
            justifyContent: 'space-between',
            alignItems: 'center',
            marginBottom: 12,
            gap: 12,
          }}
        >
          <Select<TimeRangeFilter>
            size="small"
            value={timeRange}
            aria-label="子代理时间范围"
            style={{ width: 118 }}
            onChange={setTimeRange}
            options={[
              { value: '1d', label: '最近 1 天' },
              { value: '7d', label: '最近 7 天' },
              { value: '30d', label: '最近 30 天' },
              { value: 'all', label: '全部记录' },
            ]}
          />
          <Text type="secondary" style={{ fontSize: 11 }}>
            状态来自会话 SSE；断线后按 sequence 自动补放
          </Text>
        </div>

        <div
          style={{
            marginBottom: 12,
            display: 'flex',
            gap: 8,
            flexWrap: 'wrap',
          }}
        >
          {(
            [
              'all',
              'running',
              'completed',
              'failed',
              'timed_out',
              'cancelled',
            ] as const
          ).map((value) => (
            <Tag
              key={value}
              color={filter === value ? 'blue' : undefined}
              style={{ cursor: 'pointer', opacity: filter === value ? 1 : 0.6 }}
              onClick={() => setFilter(value)}
            >
              {value === 'all'
                ? `全部 (${timeFilteredAgents.length})`
                : `${statusConfig[value].label} (${
                    timeFilteredAgents.filter((item) => item.status === value)
                      .length
                  })`}
            </Tag>
          ))}
        </div>

        {filteredAgents.length === 0 ? (
          <div
            style={{
              textAlign: 'center',
              padding: 24,
              color: 'var(--ant-color-text-secondary)',
            }}
          >
            暂无子代理运行事件
          </div>
        ) : (
          filteredAgents.map(renderRun)
        )}
      </Modal>
    </>
  );
};

export default SubAgentIndicator;
