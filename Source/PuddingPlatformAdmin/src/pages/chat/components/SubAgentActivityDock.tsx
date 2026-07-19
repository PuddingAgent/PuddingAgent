import {
  ArrowLeftOutlined,
  CheckCircleOutlined,
  ClockCircleOutlined,
  CloseCircleOutlined,
  LoadingOutlined,
  RobotOutlined,
  ToolOutlined,
} from '@ant-design/icons';
import {
  Button,
  Drawer,
  Empty,
  Popover,
  Progress,
  Space,
  Tag,
  Timeline,
  Tooltip,
  Typography,
} from 'antd';
import { createStyles } from 'antd-style';
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { getSubAgentRunOutput } from '@/services/platform/api';
import type {
  SubAgentActivity,
  SubAgentCard,
  SubAgentCardMap,
  SubAgentCardStatus,
} from '../types';

const { Paragraph, Text } = Typography;

interface SubAgentActivityDockProps {
  sessionId?: string | null;
  subAgentCards?: SubAgentCardMap;
  inspectorOpen: boolean;
  onInspectorOpenChange: (open: boolean) => void;
  selectedRunId?: string | null;
  onSelectedRunIdChange: (runId: string | null) => void;
}

type InspectorFilter = 'active' | 'recent' | 'errors' | 'all';
type RunOutputLoadStatus = 'idle' | 'loading' | 'loaded' | 'error';

interface RunOutputState {
  runId?: string;
  status: RunOutputLoadStatus;
  output?: string;
  error?: string;
}

const SUCCESS_LINGER_MS = 12_000;
const DOCK_VISIBLE_LIMIT = 4;
const RECENT_WINDOW_MS = 7 * 24 * 60 * 60 * 1000;

const activeStatuses = new Set<SubAgentCardStatus>(['spawning', 'running']);
const errorStatuses = new Set<SubAgentCardStatus>([
  'failed',
  'cancelled',
  'timed_out',
  'interrupted',
]);
const terminalStatuses = new Set<SubAgentCardStatus>([
  'completed',
  ...errorStatuses,
]);

const statusConfig: Record<
  SubAgentCardStatus,
  { color: string; label: string; icon: React.ReactNode }
> = {
  spawning: {
    color: '#d89614',
    label: '创建中',
    icon: <LoadingOutlined spin />,
  },
  running: {
    color: '#5f8f63',
    label: '运行中',
    icon: <LoadingOutlined spin />,
  },
  completed: {
    color: '#4f7f62',
    label: '已完成',
    icon: <CheckCircleOutlined />,
  },
  failed: { color: '#c64f52', label: '失败', icon: <CloseCircleOutlined /> },
  cancelled: {
    color: '#7d7d7d',
    label: '已取消',
    icon: <CloseCircleOutlined />,
  },
  timed_out: {
    color: '#c47f31',
    label: '已超时',
    icon: <ClockCircleOutlined />,
  },
  interrupted: {
    color: '#7b61a8',
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

const useStyles = createStyles(() => ({
  host: {
    position: 'absolute' as const,
    top: 12,
    right: 12,
    width: 40,
    minHeight: 0,
    zIndex: 20,
    pointerEvents: 'none' as const,
    '@media (max-width: 1023px)': {
      top: 10,
      right: 8,
      width: 40,
    },
    '@media (max-width: 767px)': {
      display: 'none',
    },
  },
  rail: {
    display: 'flex',
    flexDirection: 'column' as const,
    alignItems: 'center',
    gap: 8,
    pointerEvents: 'auto' as const,
  },
  railHidden: {
    display: 'none',
  },
  item: {
    position: 'relative' as const,
    width: 36,
    height: 36,
    padding: 0,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 11,
    background: 'var(--pudding-chat-surface)',
    color: 'var(--pudding-chat-text)',
    boxShadow: '0 6px 18px rgba(62, 43, 29, 0.14)',
    backdropFilter: 'blur(10px)',
    cursor: 'pointer',
    transition: 'transform 140ms ease, border-color 140ms ease',
    '&:hover': {
      transform: 'translateY(-1px)',
      borderColor: 'var(--pudding-chat-accent)',
    },
    '&:focus-visible': {
      outline: '2px solid var(--pudding-chat-accent)',
      outlineOffset: 2,
    },
  },
  itemRunning: {
    boxShadow: '0 0 0 3px color-mix(in srgb, #5f8f63 14%, transparent)',
  },
  itemLabel: {
    maxWidth: 28,
    overflow: 'hidden',
    whiteSpace: 'nowrap' as const,
    textOverflow: 'ellipsis',
    fontSize: 10,
    fontWeight: 700,
    textTransform: 'uppercase' as const,
  },
  statusDot: {
    position: 'absolute' as const,
    right: -2,
    bottom: -2,
    width: 9,
    height: 9,
    border: '2px solid var(--pudding-chat-surface)',
    borderRadius: '50%',
  },
  popover: {
    width: 300,
  },
  previewTitle: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
  },
  previewTask: {
    margin: '8px 0',
    maxHeight: 42,
    overflow: 'hidden',
    fontSize: 12,
    lineHeight: '20px',
  },
  previewActivity: {
    padding: '8px 10px',
    borderRadius: 8,
    background: 'var(--pudding-chat-surface-muted)',
    fontSize: 12,
    color: 'var(--pudding-chat-text)',
  },
  previewMeta: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '5px 10px',
    marginTop: 8,
    fontSize: 11,
    color: 'var(--pudding-chat-text-subtle)',
  },
  drawerBody: {
    display: 'flex',
    flexDirection: 'column' as const,
    minHeight: 0,
    height: '100%',
    minWidth: 0,
    overflow: 'hidden',
  },
  detailLayout: {
    display: 'grid',
    gridTemplateRows: 'minmax(0, 1fr) clamp(240px, 36vh, 380px)',
    minHeight: 0,
    height: '100%',
    minWidth: 0,
    overflow: 'hidden',
  },
  detailScroll: {
    minHeight: 0,
    minWidth: 0,
    paddingRight: 4,
    overflowY: 'auto' as const,
    overflowX: 'hidden' as const,
  },
  filterBar: {
    display: 'flex',
    gap: 6,
    flexWrap: 'wrap' as const,
    marginBottom: 12,
  },
  runList: {
    display: 'flex',
    flexDirection: 'column' as const,
    gap: 8,
  },
  runRow: {
    width: '100%',
    padding: '10px 12px',
    display: 'flex',
    alignItems: 'flex-start',
    gap: 10,
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 10,
    background: 'var(--pudding-chat-surface)',
    color: 'var(--pudding-chat-text)',
    textAlign: 'left' as const,
    cursor: 'pointer',
    '&:hover': {
      borderColor: 'var(--pudding-chat-accent)',
      background:
        'color-mix(in srgb, var(--pudding-chat-accent-soft) 34%, var(--pudding-chat-surface))',
    },
  },
  runRowCopy: {
    minWidth: 0,
    flex: 1,
  },
  runRowTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: 6,
    fontSize: 12,
    fontWeight: 650,
  },
  runRowTask: {
    marginTop: 3,
    overflow: 'hidden',
    whiteSpace: 'nowrap' as const,
    textOverflow: 'ellipsis',
    fontSize: 11,
    color: 'var(--pudding-chat-text-subtle)',
  },
  detailHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    marginBottom: 12,
  },
  detailHero: {
    padding: 14,
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 12,
    background:
      'color-mix(in srgb, var(--pudding-chat-surface-muted) 60%, transparent)',
  },
  metricGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '8px 12px',
    marginTop: 12,
    fontSize: 12,
    color: 'var(--pudding-chat-text-subtle)',
  },
  identifierGrid: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr)',
    gap: 6,
    marginTop: 12,
    paddingTop: 10,
    borderTop: '1px solid var(--pudding-chat-border)',
  },
  identifierRow: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    minWidth: 0,
    fontSize: 11,
  },
  identifierValue: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap' as const,
  },
  sectionTitle: {
    margin: '16px 0 10px',
    fontSize: 12,
    fontWeight: 700,
    color: 'var(--pudding-chat-text)',
  },
  error: {
    marginTop: 10,
    padding: '8px 10px',
    borderRadius: 8,
    background: 'color-mix(in srgb, #c64f52 9%, transparent)',
    color: 'var(--ant-color-error)',
    whiteSpace: 'pre-wrap' as const,
    fontSize: 12,
  },
  output: {
    flex: 1,
    minHeight: 0,
    minWidth: 0,
    overflowY: 'auto' as const,
    overflowX: 'hidden' as const,
    padding: '10px 12px',
    borderRadius: 8,
    background: 'var(--pudding-chat-surface-muted)',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    overflowWrap: 'anywhere' as const,
    fontSize: 12,
    lineHeight: 1.6,
  },
  resultPanel: {
    display: 'flex',
    flexDirection: 'column' as const,
    minHeight: 0,
    minWidth: 0,
    marginTop: 12,
    paddingTop: 12,
    borderTop: '1px solid var(--pudding-chat-border)',
    overflow: 'hidden',
  },
  resultHeader: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 8,
    minWidth: 0,
    marginBottom: 10,
  },
  resultTitle: {
    minWidth: 0,
    fontSize: 12,
    fontWeight: 700,
    color: 'var(--pudding-chat-text)',
  },
  resultHint: {
    padding: '12px',
    borderRadius: 8,
    background: 'var(--pudding-chat-surface-muted)',
    color: 'var(--pudding-chat-text-subtle)',
    fontSize: 12,
    lineHeight: 1.6,
  },
  activityMeta: {
    display: 'flex',
    gap: 8,
    flexWrap: 'wrap' as const,
    marginTop: 2,
    fontSize: 10,
    color: 'var(--pudding-chat-text-subtle)',
  },
  activityDetail: {
    marginTop: 7,
    border: '1px solid var(--pudding-chat-border)',
    borderRadius: 8,
    background: 'var(--pudding-chat-surface-muted)',
    overflow: 'hidden',
  },
  activityDetailSummary: {
    padding: '6px 9px',
    cursor: 'pointer',
    fontSize: 11,
    fontWeight: 600,
    color: 'var(--pudding-chat-text)',
  },
  activityDetailContent: {
    maxHeight: 220,
    margin: 0,
    padding: '8px 10px',
    overflow: 'auto',
    borderTop: '1px solid var(--pudding-chat-border)',
    whiteSpace: 'pre-wrap' as const,
    wordBreak: 'break-word' as const,
    fontSize: 11,
    lineHeight: 1.55,
    color: 'var(--pudding-chat-text)',
  },
}));

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

const shortRole = (run: SubAgentCard): string => {
  const source = run.role ?? run.originToolId?.replace(/^smart_/, '') ?? 'sub';
  return source.slice(0, 3);
};

const taskSummary = (run: SubAgentCard): string => {
  const raw = run.taskSummary?.replace(/\r/g, '').trim();
  if (!raw) return `${run.role ?? run.originToolId ?? '子代理'}任务`;

  const roleHeading = raw.match(
    /^[#\s\p{Extended_Pictographic}]*[A-Z][A-Z0-9_-]*\s+[—-]\s+([^#\n]+)/u,
  );
  const firstLine =
    roleHeading?.[1]?.trim() ??
    raw
      .split('\n')
      .map((line) => line.replace(/^#+\s*/, '').trim())
      .find(Boolean) ??
    raw;
  const compact = firstLine.replace(/\s+/g, ' ');
  return compact.length > 220 ? `${compact.slice(0, 217)}...` : compact;
};

const currentActivity = (run: SubAgentCard): string => {
  if (run.activeToolName) return `正在执行 ${run.activeToolName}`;
  switch (run.phase) {
    case 'created':
      return '等待 Runtime 接管';
    case 'starting':
      return '正在启动子代理运行时';
    case 'context':
      return '正在装配执行上下文';
    case 'llm':
      return `正在等待 ${run.modelId ?? '模型'} 返回`;
    case 'tool':
      return run.lastToolName
        ? `${run.lastToolName} 已结束，等待下一步`
        : '正在执行工具';
    case 'round':
      return `正在处理第 ${run.currentRound ?? 0} 轮`;
    default:
      return terminalStatuses.has(run.status)
        ? statusConfig[run.status].label
        : '正在运行';
  }
};

const activityColor = (activity: SubAgentActivity): string => {
  if (activity.type.endsWith('.failed')) return 'red';
  if (
    activity.type.endsWith('.timed_out') ||
    activity.type.endsWith('.interrupted')
  )
    return 'orange';
  if (
    activity.type.endsWith('.completed') ||
    activity.type === 'subagent.run.context_assembled'
  )
    return 'green';
  return 'blue';
};

const SubAgentActivityDock: React.FC<SubAgentActivityDockProps> = ({
  sessionId,
  subAgentCards = {},
  inspectorOpen,
  onInspectorOpenChange,
  selectedRunId,
  onSelectedRunIdChange,
}) => {
  const { styles, cx } = useStyles();
  const visibleSinceRef = useRef(Date.now());
  const [now, setNow] = useState(Date.now());
  const [dismissedRunIds, setDismissedRunIds] = useState<Set<string>>(
    () => new Set(),
  );
  const [filter, setFilter] = useState<InspectorFilter>('active');
  const [runOutput, setRunOutput] = useState<RunOutputState>({
    status: 'idle',
  });

  useEffect(() => {
    visibleSinceRef.current = Date.now();
    setDismissedRunIds(new Set());
    onSelectedRunIdChange(null);
  }, [sessionId, onSelectedRunIdChange]);

  const runs = useMemo(
    () =>
      Object.values(subAgentCards)
        .filter(
          (run) =>
            !sessionId ||
            !run.parentSessionId ||
            run.parentSessionId === sessionId,
        )
        .sort((a, b) => b.spawnedAt - a.spawnedAt),
    [sessionId, subAgentCards],
  );
  const activeRuns = useMemo(
    () => runs.filter((run) => activeStatuses.has(run.status)),
    [runs],
  );
  const freshTerminalRuns = useMemo(
    () =>
      runs.filter((run) => {
        if (!terminalStatuses.has(run.status)) return false;
        const runId = run.runId ?? run.subSessionId;
        if (dismissedRunIds.has(runId)) return false;
        const terminalAt = run.completedAt ?? run.lastActivityAt ?? 0;
        if (terminalAt < visibleSinceRef.current - 1000) return false;
        if (run.status === 'completed')
          return now - terminalAt <= SUCCESS_LINGER_MS;
        return true;
      }),
    [dismissedRunIds, now, runs],
  );
  const dockRuns = useMemo(
    () => [...activeRuns, ...freshTerminalRuns],
    [activeRuns, freshTerminalRuns],
  );

  useEffect(() => {
    if (!inspectorOpen && dockRuns.length === 0) return undefined;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [dockRuns.length, inspectorOpen]);

  useEffect(() => {
    if (inspectorOpen && filter === 'active' && activeRuns.length === 0)
      setFilter('recent');
  }, [activeRuns.length, filter, inspectorOpen]);

  const selectedRun = runs.find(
    (run) => (run.runId ?? run.subSessionId) === selectedRunId,
  );

  useEffect(() => {
    if (!inspectorOpen || !selectedRun) {
      setRunOutput({ status: 'idle' });
      return undefined;
    }

    const runId = selectedRun.runId ?? selectedRun.subSessionId;
    if (!terminalStatuses.has(selectedRun.status)) {
      setRunOutput({ runId, status: 'idle' });
      return undefined;
    }

    let disposed = false;
    setRunOutput({ runId, status: 'loading' });
    void getSubAgentRunOutput(runId)
      .then((response) => {
        if (disposed) return;
        setRunOutput({
          runId,
          status: 'loaded',
          output: response.output ?? undefined,
        });
      })
      .catch((error: unknown) => {
        if (disposed) return;
        setRunOutput({
          runId,
          status: 'error',
          error: error instanceof Error ? error.message : '完整结果加载失败',
        });
      });

    return () => {
      disposed = true;
    };
  }, [
    inspectorOpen,
    selectedRun?.runId,
    selectedRun?.status,
    selectedRun?.subSessionId,
  ]);
  const filteredRuns = useMemo(() => {
    const cutoff = now - RECENT_WINDOW_MS;
    switch (filter) {
      case 'active':
        return activeRuns;
      case 'errors':
        return runs.filter((run) => errorStatuses.has(run.status));
      case 'recent':
        return runs.filter(
          (run) =>
            activeStatuses.has(run.status) ||
            (run.completedAt ?? run.spawnedAt) >= cutoff,
        );
      default:
        return runs;
    }
  }, [activeRuns, filter, now, runs]);

  const openRun = useCallback(
    (runId?: string) => {
      onSelectedRunIdChange(runId ?? null);
      onInspectorOpenChange(true);
    },
    [onInspectorOpenChange, onSelectedRunIdChange],
  );
  const dismissRun = useCallback((run: SubAgentCard) => {
    const runId = run.runId ?? run.subSessionId;
    setDismissedRunIds((current) => new Set(current).add(runId));
  }, []);

  const renderPreview = (run: SubAgentCard) => {
    const cfg = statusConfig[run.status];
    const endAt = run.completedAt ?? now;
    const elapsedMs = Math.max(0, endAt - run.spawnedAt);
    const lastActivityAge = Math.max(
      0,
      now - (run.lastActivityAt ?? run.spawnedAt),
    );
    return (
      <div className={styles.popover}>
        <div className={styles.previewTitle}>
          <Space size={6}>
            <span style={{ color: cfg.color }}>{cfg.icon}</span>
            <Text strong>{run.originToolId ?? run.role ?? '子代理'}</Text>
            <Tag color={cfg.color}>{cfg.label}</Tag>
          </Space>
        </div>
        <div className={styles.previewTask}>{taskSummary(run)}</div>
        <div className={styles.previewActivity}>
          {currentActivity(run)}
          <div
            style={{
              marginTop: 3,
              fontSize: 10,
              color: 'var(--pudding-chat-text-subtle)',
            }}
          >
            最后事件 {formatDuration(lastActivityAge)} 前
          </div>
        </div>
        <div className={styles.previewMeta}>
          <span>模型：{run.modelId ?? '-'}</span>
          <span>
            轮次：{run.currentRound ?? 0}
            {run.maxRounds ? `/${run.maxRounds}` : ''}
          </span>
          <span>耗时：{formatDuration(elapsedMs)}</span>
          <span>Token：{formatTokens(run.totalTokens)}</span>
        </div>
        <Space style={{ marginTop: 10 }}>
          <Button
            size="small"
            type="primary"
            onClick={() => openRun(run.runId)}
          >
            查看详情
          </Button>
          {terminalStatuses.has(run.status) && run.status !== 'completed' && (
            <Button size="small" onClick={() => dismissRun(run)}>
              从运行坞移除
            </Button>
          )}
        </Space>
      </div>
    );
  };

  const renderRunDetail = (run: SubAgentCard) => {
    const cfg = statusConfig[run.status];
    const runId = run.runId ?? run.subSessionId;
    const loadedOutput =
      runOutput.runId === runId && runOutput.status === 'loaded'
        ? runOutput.output
        : undefined;
    const endAt = run.completedAt ?? now;
    const elapsedMs = Math.max(0, endAt - run.spawnedAt);
    const timeoutMs = (run.timeoutSeconds ?? 0) * 1000;
    const timeoutPercent =
      timeoutMs > 0 && activeStatuses.has(run.status)
        ? Math.min(100, Math.round((elapsedMs / timeoutMs) * 100))
        : undefined;
    return (
      <div
        className={styles.detailLayout}
        data-testid="subagent-run-detail-layout"
      >
        <div
          className={styles.detailScroll}
          data-testid="subagent-run-timeline-region"
        >
          <div className={styles.detailHeader}>
            <Button
              type="text"
              size="small"
              icon={<ArrowLeftOutlined />}
              onClick={() => onSelectedRunIdChange(null)}
            >
              返回运行列表
            </Button>
          </div>
          <div className={styles.detailHero}>
            <Space size={6} wrap>
              <Tag color={cfg.color}>{cfg.label}</Tag>
              <Tag>{phaseLabel[run.phase ?? ''] ?? run.phase ?? '运行中'}</Tag>
              {run.originToolId && (
                <Tag color="geekblue">{run.originToolId}</Tag>
              )}
              {run.role && <Tag color="cyan">{run.role}</Tag>}
            </Space>
            <Paragraph style={{ margin: '10px 0 0' }}>
              {taskSummary(run)}
            </Paragraph>
            <div className={styles.previewActivity}>{currentActivity(run)}</div>
            <div className={styles.metricGrid}>
              <span>模型：{run.modelId ?? '-'}</span>
              <span>Provider：{run.providerId ?? '-'}</span>
              <span>
                轮次：{run.currentRound ?? 0}
                {run.maxRounds ? `/${run.maxRounds}` : ''}
              </span>
              <span>耗时：{formatDuration(elapsedMs)}</span>
              <span>Token：{formatTokens(run.totalTokens)}</span>
              <span>
                工具：{run.toolCount ?? 0}
                {run.failedToolCount ? ` / 失败 ${run.failedToolCount}` : ''}
              </span>
            </div>
            <div className={styles.identifierGrid}>
              <div className={styles.identifierRow}>
                <Text type="secondary">Session ID</Text>
                <Text
                  code
                  copyable={{ text: run.subSessionId }}
                  className={styles.identifierValue}
                >
                  {run.subSessionId}
                </Text>
              </div>
              <div className={styles.identifierRow}>
                <Text type="secondary">Run ID</Text>
                <Text
                  code
                  copyable={{ text: runId }}
                  className={styles.identifierValue}
                >
                  {runId}
                </Text>
              </div>
            </div>
            {timeoutPercent !== undefined && (
              <Progress
                style={{ marginTop: 10 }}
                size="small"
                percent={timeoutPercent}
                status={timeoutPercent >= 90 ? 'exception' : 'active'}
                format={() =>
                  `${formatDuration(elapsedMs)} / ${formatDuration(timeoutMs)}`
                }
              />
            )}
            {run.error && <div className={styles.error}>{run.error}</div>}
          </div>

          <div className={styles.sectionTitle}>运行时间线</div>
          {run.activities?.length ? (
            <Timeline
              items={run.activities.map((activity) => ({
                color: activityColor(activity),
                children: (
                  <div>
                    <div style={{ fontSize: 12 }}>{activity.label}</div>
                    <div className={styles.activityMeta}>
                      <span>
                        {new Date(activity.occurredAt).toLocaleTimeString()}
                      </span>
                      {activity.round ? (
                        <span>第 {activity.round} 轮</span>
                      ) : null}
                      {activity.durationMs ? (
                        <span>{formatDuration(activity.durationMs)}</span>
                      ) : null}
                      {activity.totalTokens ? (
                        <span>{formatTokens(activity.totalTokens)} tokens</span>
                      ) : null}
                      {activity.toolCallId ? (
                        <Text
                          copyable={{ text: activity.toolCallId }}
                          type="secondary"
                          style={{ fontSize: 10 }}
                        >
                          Call ID: {activity.toolCallId}
                        </Text>
                      ) : null}
                    </div>
                    {activity.details?.map((detail) => (
                      <details
                        className={styles.activityDetail}
                        key={detail.kind}
                      >
                        <summary className={styles.activityDetailSummary}>
                          {detail.label}
                          {detail.truncated ? '（已截断）' : ''}
                        </summary>
                        <pre className={styles.activityDetailContent}>
                          {detail.content}
                        </pre>
                      </details>
                    ))}
                    {activity.error && (
                      <div className={styles.error}>{activity.error}</div>
                    )}
                  </div>
                ),
              }))}
            />
          ) : (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无运行事件"
            />
          )}
        </div>

        <section
          className={styles.resultPanel}
          aria-label="返回主 Agent 的完整结果"
          data-testid="subagent-run-output-region"
        >
          <div className={styles.resultHeader}>
            <span className={styles.resultTitle}>返回主 Agent 的完整结果</span>
            {loadedOutput ? (
              <Text
                type="secondary"
                copyable={{ text: loadedOutput }}
                style={{ fontSize: 11, whiteSpace: 'nowrap' }}
              >
                {loadedOutput.length.toLocaleString()} 字符
              </Text>
            ) : null}
          </div>
          {!terminalStatuses.has(run.status) ? (
            <div className={styles.resultHint}>
              子代理提交终态后，将从运行归档的 output.md 加载完整结果。
              实时时间线中的模型消息和工具输出仅是脱敏后的有界预览。
            </div>
          ) : runOutput.runId === runId && runOutput.status === 'loading' ? (
            <div className={styles.resultHint}>
              <LoadingOutlined spin style={{ marginRight: 8 }} />
              正在读取运行归档…
            </div>
          ) : runOutput.runId === runId && runOutput.status === 'error' ? (
            <div className={styles.error}>
              完整结果加载失败：{runOutput.error}
              {'\n'}事件投影中的摘要不会被用来冒充完整结果。
            </div>
          ) : loadedOutput ? (
            <div className={styles.output}>{loadedOutput}</div>
          ) : (
            <div className={styles.resultHint}>
              该运行没有写入最终输出。请结合终态错误与运行时间线诊断。
            </div>
          )}
        </section>
      </div>
    );
  };

  return (
    <>
      <aside
        className={styles.host}
        aria-label="子代理运行坞"
        data-testid="subagent-activity-dock"
      >
        <div
          className={cx(
            styles.rail,
            dockRuns.length === 0 && styles.railHidden,
          )}
        >
          {dockRuns.slice(0, DOCK_VISIBLE_LIMIT).map((run) => {
            const runId = run.runId ?? run.subSessionId;
            const cfg = statusConfig[run.status];
            return (
              <Popover
                key={runId}
                content={renderPreview(run)}
                trigger="hover"
                placement="leftTop"
                mouseEnterDelay={0.15}
              >
                <button
                  type="button"
                  className={cx(
                    styles.item,
                    activeStatuses.has(run.status) && styles.itemRunning,
                  )}
                  onClick={() => openRun(runId)}
                  aria-label={`${run.originToolId ?? run.role ?? '子代理'} ${cfg.label}，${currentActivity(run)}`}
                  data-testid={`subagent-dock-item-${runId}`}
                >
                  <span className={styles.itemLabel}>{shortRole(run)}</span>
                  <span
                    className={styles.statusDot}
                    style={{ background: cfg.color }}
                  />
                </button>
              </Popover>
            );
          })}
          {dockRuns.length > DOCK_VISIBLE_LIMIT && (
            <Tooltip
              title={`另有 ${dockRuns.length - DOCK_VISIBLE_LIMIT} 个子代理`}
            >
              <button
                type="button"
                className={styles.item}
                onClick={() => openRun()}
                aria-label="查看全部子代理"
              >
                <span className={styles.itemLabel}>
                  +{dockRuns.length - DOCK_VISIBLE_LIMIT}
                </span>
              </button>
            </Tooltip>
          )}
        </div>
      </aside>

      <Drawer
        title={
          <Space>
            <RobotOutlined style={{ color: 'var(--pudding-chat-accent)' }} />
            <span>子代理运行检查器</span>
            <Tag color={activeRuns.length ? 'green' : 'default'}>
              {activeRuns.length
                ? `${activeRuns.length} 运行中`
                : '实时事件已连接'}
            </Tag>
          </Space>
        }
        open={inspectorOpen}
        onClose={() => onInspectorOpenChange(false)}
        placement="right"
        width="min(520px, 100vw)"
        mask={false}
        styles={{ body: { padding: 16, overflow: 'hidden' } }}
      >
        <div className={styles.drawerBody}>
          {selectedRun ? (
            renderRunDetail(selectedRun)
          ) : (
            <>
              <div className={styles.filterBar}>
                {(
                  [
                    ['active', `运行中 ${activeRuns.length}`],
                    ['recent', '最近 7 天'],
                    ['errors', '异常'],
                    ['all', '全部'],
                  ] as Array<[InspectorFilter, string]>
                ).map(([value, label]) => (
                  <Tag
                    key={value}
                    color={filter === value ? 'blue' : undefined}
                    style={{ cursor: 'pointer', margin: 0 }}
                    onClick={() => setFilter(value)}
                  >
                    {label}
                  </Tag>
                ))}
              </div>
              {filteredRuns.length ? (
                <div className={styles.runList}>
                  {filteredRuns.map((run) => {
                    const runId = run.runId ?? run.subSessionId;
                    const cfg = statusConfig[run.status];
                    return (
                      <button
                        type="button"
                        key={runId}
                        className={styles.runRow}
                        onClick={() => onSelectedRunIdChange(runId)}
                        data-testid={`subagent-inspector-run-${runId}`}
                      >
                        <span style={{ color: cfg.color }}>{cfg.icon}</span>
                        <span className={styles.runRowCopy}>
                          <span className={styles.runRowTitle}>
                            <span>
                              {run.originToolId ?? run.role ?? '子代理'}
                            </span>
                            <Tag color={cfg.color} style={{ margin: 0 }}>
                              {cfg.label}
                            </Tag>
                          </span>
                          <span className={styles.runRowTask}>
                            {currentActivity(run)} · {taskSummary(run)}
                          </span>
                        </span>
                        {run.activeToolName && <ToolOutlined />}
                      </button>
                    );
                  })}
                </div>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="当前筛选范围没有子代理运行"
                />
              )}
            </>
          )}
        </div>
      </Drawer>
    </>
  );
};

export default SubAgentActivityDock;
