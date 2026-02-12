import {
  BulbOutlined,
  CheckCircleOutlined,
  CodeOutlined,
  CopyOutlined,
  DatabaseOutlined,
  DownloadOutlined,
  ExclamationCircleOutlined,
  LoadingOutlined,
  PauseCircleOutlined,
  PlayCircleOutlined,
  SettingOutlined,
  SyncOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { request } from '@umijs/max';
import {
  Button,
  Collapse,
  Empty,
  Progress,
  Select,
  Spin,
  Switch,
  Tabs,
  Tag,
  Timeline,
  Typography,
} from 'antd';
import dayjs from 'dayjs';
import React, { useEffect, useMemo, useState } from 'react';
import {
  type BenchmarkCaseSummaryDto,
  getBenchmarkCase,
  listBenchmarkCases,
  prepareBenchmarkCase,
} from '@/services/platform/api';
import {
  buildPerfDiagnosticSnapshot,
  clearPerfEvents,
  getPerfEvents,
  installPerfDiagnostics,
  isPerfDiagnosticsEnabled,
  type PuddingPerfCaptureMetadata,
  type PuddingPerfEvent,
  recordPerfEvent,
  setPerfDiagnosticsEnabled,
  summarizePerfEvents,
} from '@/utils/debug';
import { useChatStyles } from '../styles';

const { Text, Paragraph } = Typography;

export interface DevRawEvent {
  id: string;
  timestamp: number;
  event: string;
  payload: string;
}

interface ContextLayerInfo {
  layerName: string;
  tokenCount: number;
  contentPreview: string;
}

interface ContextSnapshot {
  sessionId: string;
  assembledAt?: string;
  layers: ContextLayerInfo[];
  totalTokens: number;
  message?: string;
}

interface SubconsciousResult {
  sessionId: string;
  job?: {
    jobId: string;
    status: string;
    factsExtracted: number;
    factsMerged: number;
    factsDiscarded: number;
    chaptersCreated: number;
    llmTokensUsed: number;
    llmModelId?: string;
    elapsedMs: number;
    errorMessage?: string;
    startedAt?: number;
    completedAt?: number;
    createdAt: number;
  };
  facts: Array<{
    factId: string;
    statement: string;
    confidence: number;
    category: string;
    status: string;
    updatedAt: number;
  }>;
  preferences: Array<{
    preferenceId: string;
    category: string;
    key: string;
    value: string;
    updatedAt: number;
  }>;
  llmRawResponse?: string | null;
  note?: string;
}

interface DevPanelProps {
  workspaceId?: string;
  sessionId?: string | null;
  rawEvents: DevRawEvent[];
  onRunBenchmarkPrompt?: (
    prompt: string,
    metadata: Record<string, string>,
  ) => Promise<void> | void;
}

/** 噪音事件：心跳/keepalive/ping/comment等不应展示的事件 */
const NOISE_EVENT_TYPES = new Set([
  'heartbeat',
  'keepalive',
  'ping',
  'comment',
  '',
]);

const getNestedNumber = (
  obj: Record<string, unknown> | null,
  path: string,
): number | null => {
  const value = path.split('.').reduce<unknown>((current, key) => {
    if (!current || typeof current !== 'object') return undefined;
    return (current as Record<string, unknown>)[key];
  }, obj);
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
};

const formatMetric = (value: number | null, suffix = '') =>
  value == null ? '-' : `${value}${suffix}`;

const PerfMetric: React.FC<{
  label: string;
  value: string;
  tone?: 'normal' | 'warn' | 'ok';
}> = ({ label, value, tone = 'normal' }) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.devPerfMetric} data-tone={tone}>
      <Text type="secondary" className={styles.devPerfMetricLabel}>
        {label}
      </Text>
      <Text className={styles.devPerfMetricValue}>{value}</Text>
    </div>
  );
};

const getEventTone = (name: string) => {
  if (name.includes('longtask') || name.includes('unmapped')) return 'error';
  if (name.includes('paint') || name.includes('commit')) return 'success';
  if (name.includes('typewriter')) return 'processing';
  if (name.includes('delta') || name.includes('event.apply')) return 'blue';
  return 'default';
};

type PerfCaptureState = PuddingPerfCaptureMetadata & {
  startedAtMs?: number;
  endedAtMs?: number;
};

const safeFilePart = (value: string | null | undefined) =>
  (value || 'na').replace(/[^a-zA-Z0-9._-]+/g, '-').slice(0, 64);

const DevPanel: React.FC<DevPanelProps> = ({
  workspaceId,
  sessionId,
  rawEvents,
  onRunBenchmarkPrompt,
}) => {
  const { styles } = useChatStyles();
  const [contextData, setContextData] = useState<ContextSnapshot | null>(null);
  const [subconsciousData, setSubconsciousData] =
    useState<SubconsciousResult | null>(null);
  const [loadingContext, setLoadingContext] = useState(false);
  const [loadingSubconscious, setLoadingSubconscious] = useState(false);
  const [resolvedSessionId, setResolvedSessionId] = useState<string | null>(
    sessionId ?? null,
  );
  // 诊断面板打开时直接显示 tabs，避免用户进入后还要再次点击标题展开。
  const [inspectorOpen, setInspectorOpen] = useState(true);
  const [perfSummary, setPerfSummary] = useState<Record<
    string,
    unknown
  > | null>(null);
  const [perfEvents, setPerfEvents] = useState<PuddingPerfEvent[]>([]);
  const [diagnosticCopiedAt, setDiagnosticCopiedAt] = useState<number | null>(
    null,
  );
  const [captureState, setCaptureState] = useState<PerfCaptureState>({
    status: 'idle',
  });
  const [diagnosticsEnabled, setDiagnosticsEnabledState] = useState(() =>
    isPerfDiagnosticsEnabled(),
  );
  const [benchmarkCases, setBenchmarkCases] = useState<
    BenchmarkCaseSummaryDto[]
  >([]);
  const [selectedBenchmarkCaseId, setSelectedBenchmarkCaseId] = useState<
    string | undefined
  >();
  const [loadingBenchmarkCases, setLoadingBenchmarkCases] = useState(false);
  const [sendingBenchmarkCase, setSendingBenchmarkCase] = useState(false);
  const [benchmarkError, setBenchmarkError] = useState<string | null>(null);

  useEffect(() => {
    setResolvedSessionId(sessionId ?? null);
  }, [sessionId]);

  useEffect(() => {
    if (!diagnosticsEnabled) {
      setPerfSummary(summarizePerfEvents());
      setPerfEvents(getPerfEvents().slice(-40).reverse());
      return undefined;
    }

    installPerfDiagnostics();

    const refresh = () => {
      setPerfSummary(summarizePerfEvents());
      setPerfEvents(getPerfEvents().slice(-40).reverse());
    };
    refresh();

    // P2-perf: 仅在面板展开且页面可见时刷新，避免后台轮询压力
    let timer: number | null = null;
    const schedule = () => {
      if (timer != null) return;
      timer = window.setInterval(refresh, 1000);
    };
    const cancel = () => {
      if (timer != null) {
        window.clearInterval(timer);
        timer = null;
      }
    };

    if (inspectorOpen && document.visibilityState !== 'hidden') {
      schedule();
    }

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        cancel();
      } else if (inspectorOpen) {
        schedule();
      }
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      cancel();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [diagnosticsEnabled, inspectorOpen]);

  useEffect(() => {
    if (!inspectorOpen) return;
    let alive = true;
    setLoadingBenchmarkCases(true);
    setBenchmarkError(null);

    const loadBenchmarkCases = async () => {
      try {
        const result = await listBenchmarkCases();
        if (!alive) return;
        const sorted = [...(result || [])].sort(
          (a, b) => a.sortOrder - b.sortOrder || a.title.localeCompare(b.title),
        );
        setBenchmarkCases(sorted);
        setSelectedBenchmarkCaseId((current) =>
          current && sorted.some((item) => item.id === current)
            ? current
            : sorted[0]?.id,
        );
      } catch {
        if (alive) {
          setBenchmarkCases([]);
          setSelectedBenchmarkCaseId(undefined);
          setBenchmarkError('试题列表加载失败');
        }
      } finally {
        if (alive) setLoadingBenchmarkCases(false);
      }
    };

    void loadBenchmarkCases();
    return () => {
      alive = false;
    };
  }, [inspectorOpen]);

  useEffect(() => {
    if (resolvedSessionId || !workspaceId) return;
    let alive = true;

    const loadLatestSession = async () => {
      try {
        const sessions = await request<Array<{ sessionId: string }>>(
          `/api/sessions?workspaceId=${encodeURIComponent(workspaceId)}`,
          { method: 'GET' },
        );
        if (alive && Array.isArray(sessions) && sessions.length > 0) {
          setResolvedSessionId(sessions[0]?.sessionId ?? null);
        }
      } catch {
        // no-op
      }
    };

    void loadLatestSession();
    return () => {
      alive = false;
    };
  }, [workspaceId, resolvedSessionId]);

  useEffect(() => {
    if (!workspaceId || !resolvedSessionId) {
      setContextData(null);
      return;
    }
    let alive = true;
    setLoadingContext(true);
    const loadContext = async () => {
      try {
        const result = await request<ContextSnapshot>(
          `/api/workspaces/${encodeURIComponent(workspaceId)}/debug/context/${encodeURIComponent(resolvedSessionId)}`,
          { method: 'GET' },
        );
        if (alive) setContextData(result);
      } catch {
        if (alive) setContextData(null);
      } finally {
        if (alive) setLoadingContext(false);
      }
    };

    void loadContext();
    // P2-perf: 仅面板展开且页面可见时轮询
    let timer: number | null = null;
    const schedule = () => {
      if (timer != null) return;
      timer = window.setInterval(() => {
        void loadContext();
      }, 4000);
    };
    const cancel = () => {
      if (timer != null) {
        window.clearInterval(timer);
        timer = null;
      }
    };
    if (document.visibilityState !== 'hidden') schedule();
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') cancel();
      else schedule();
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      alive = false;
      cancel();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [workspaceId, resolvedSessionId]);

  useEffect(() => {
    if (!workspaceId || !resolvedSessionId) {
      setSubconsciousData(null);
      return;
    }
    let alive = true;
    setLoadingSubconscious(true);
    const loadSubconscious = async () => {
      try {
        const result = await request<SubconsciousResult>(
          `/api/workspaces/${encodeURIComponent(workspaceId)}/debug/subconscious/${encodeURIComponent(resolvedSessionId)}`,
          { method: 'GET' },
        );
        if (alive) setSubconsciousData(result);
      } catch {
        if (alive) setSubconsciousData(null);
      } finally {
        if (alive) setLoadingSubconscious(false);
      }
    };

    void loadSubconscious();
    // P2-perf: 仅页面可见时轮询
    let timer: number | null = null;
    const schedule = () => {
      if (timer != null) return;
      timer = window.setInterval(() => {
        void loadSubconscious();
      }, 5000);
    };
    const cancel = () => {
      if (timer != null) {
        window.clearInterval(timer);
        timer = null;
      }
    };
    if (document.visibilityState !== 'hidden') schedule();
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') cancel();
      else schedule();
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      alive = false;
      cancel();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [workspaceId, resolvedSessionId]);

  // 过滤噪音事件
  const filteredEvents = useMemo(
    () =>
      rawEvents.filter((e) => !NOISE_EVENT_TYPES.has(e.event?.toLowerCase())),
    [rawEvents],
  );
  const eventCountLabel = useMemo(
    () => `${filteredEvents.length} 条`,
    [filteredEvents.length],
  );
  const diagnosticSnapshot = useMemo(
    () =>
      buildPerfDiagnosticSnapshot({
        workspaceId,
        sessionId: resolvedSessionId,
        rawEvents: filteredEvents,
        capture: {
          status: captureState.status,
          startedAt: captureState.startedAt,
          endedAt: captureState.endedAt,
          durationMs: captureState.durationMs,
        },
      }),
    [
      workspaceId,
      resolvedSessionId,
      filteredEvents,
      perfSummary,
      perfEvents,
      captureState,
    ],
  );
  const copyDiagnosticSnapshot = async () => {
    const text = JSON.stringify(diagnosticSnapshot, null, 2);
    await navigator.clipboard.writeText(text);
    setDiagnosticCopiedAt(Date.now());
  };
  const updateDiagnosticsEnabled = (enabled: boolean) => {
    setPerfDiagnosticsEnabled(enabled);
    setDiagnosticsEnabledState(enabled);
    setDiagnosticCopiedAt(null);
    if (enabled) {
      installPerfDiagnostics();
      recordPerfEvent('diagnostics.mode.enabled', {
        workspaceId,
        sessionId: resolvedSessionId,
      });
      setPerfSummary(summarizePerfEvents());
      setPerfEvents(getPerfEvents().slice(-40).reverse());
      return;
    }

    clearPerfEvents();
    setCaptureState({ status: 'idle' });
    setPerfSummary(summarizePerfEvents());
    setPerfEvents([]);
  };
  const startCapture = () => {
    if (!diagnosticsEnabled) return;
    clearPerfEvents();
    const startedAtMs = Date.now();
    const startedAt = new Date(startedAtMs).toISOString();
    const nextState: PerfCaptureState = {
      status: 'recording',
      startedAt,
      startedAtMs,
    };
    setCaptureState(nextState);
    setPerfSummary(summarizePerfEvents());
    setPerfEvents([]);
    setDiagnosticCopiedAt(null);
    recordPerfEvent('diagnostics.capture.start', {
      workspaceId,
      sessionId: resolvedSessionId,
      startedAt,
    });
  };
  const stopCapture = () => {
    if (!diagnosticsEnabled) return;
    const endedAtMs = Date.now();
    const durationMs = captureState.startedAtMs
      ? Math.max(0, endedAtMs - captureState.startedAtMs)
      : undefined;
    const endedAt = new Date(endedAtMs).toISOString();
    const nextState: PerfCaptureState = {
      ...captureState,
      status: 'stopped',
      endedAt,
      endedAtMs,
      durationMs,
    };
    recordPerfEvent('diagnostics.capture.stop', {
      durationMs,
      endedAt,
    });
    setCaptureState(nextState);
    setPerfSummary(summarizePerfEvents());
    setPerfEvents(getPerfEvents().slice(-40).reverse());
  };
  const downloadDiagnosticSnapshot = () => {
    if (!diagnosticsEnabled) return;
    const capture = {
      status: captureState.status,
      startedAt: captureState.startedAt,
      endedAt: captureState.endedAt,
      durationMs: captureState.durationMs,
    };
    const snapshot = buildPerfDiagnosticSnapshot({
      workspaceId,
      sessionId: resolvedSessionId,
      rawEvents: filteredEvents,
      capture,
      perfEventLimit: 5000,
      rawEventLimit: 2000,
    });
    const blob = new Blob([JSON.stringify(snapshot, null, 2)], {
      type: 'application/json;charset=utf-8',
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `pudding-perf-${safeFilePart(resolvedSessionId)}-${dayjs().format('YYYYMMDD-HHmmss')}.json`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  };

  const sendSelectedBenchmarkCase = async () => {
    if (!selectedBenchmarkCaseId || !onRunBenchmarkPrompt) return;
    setSendingBenchmarkCase(true);
    setBenchmarkError(null);
    try {
      const prepared = workspaceId
        ? await prepareBenchmarkCase(
            selectedBenchmarkCaseId,
            workspaceId,
            resolvedSessionId,
          )
        : null;
      const detail = await getBenchmarkCase(selectedBenchmarkCaseId);
      await onRunBenchmarkPrompt(detail.prompt, {
        source: 'benchmark_launcher',
        benchmarkCaseId: detail.id,
        benchmarkTitle: detail.title,
        benchmarkRunId: prepared?.runId ?? '',
        benchmarkSeedId: prepared?.seed.seedId ?? '',
        benchmarkSeedFiles: String(prepared?.seed.files.length ?? 0),
      });
    } catch {
      setBenchmarkError('发送试题失败');
    } finally {
      setSendingBenchmarkCase(false);
    }
  };

  return (
    <aside className={styles.devPanel}>
      <div
        className={styles.devPanelHeader}
        onClick={() => setInspectorOpen(!inspectorOpen)}
        style={{ cursor: 'pointer', userSelect: 'none' }}
      >
        <span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <ThunderboltOutlined style={{ fontSize: 14 }} />
          Runtime Inspector
        </span>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          <Tag color="processing">session: {resolvedSessionId || 'N/A'}</Tag>
          <Tag>{inspectorOpen ? '▲' : '▼'}</Tag>
        </div>
      </div>

      {inspectorOpen && (
        <Tabs
          size="small"
          className={styles.devPanelTabs}
          defaultActiveKey="perf"
          items={[
            {
              key: 'perf',
              label: `Perf (${perfEvents.length})`,
              children: (
                <div className={styles.devPanelSection}>
                  <div className={styles.devPerfToolbar}>
                    <div
                      style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: 8,
                        flexWrap: 'wrap',
                      }}
                    >
                      <Text type="secondary" style={{ fontSize: 12 }}>
                        前端输出性能 · 最近{' '}
                        {formatMetric(
                          getNestedNumber(perfSummary, 'totalEvents'),
                        )}{' '}
                        条事件
                      </Text>
                      <Text style={{ fontSize: 12 }}>诊断模式</Text>
                      <Switch
                        size="small"
                        aria-label="诊断模式"
                        checked={diagnosticsEnabled}
                        checkedChildren="开"
                        unCheckedChildren="关"
                        onChange={updateDiagnosticsEnabled}
                      />
                    </div>
                    <div className={styles.devPerfToolbarActions}>
                      <Button
                        size="small"
                        icon={<CopyOutlined />}
                        disabled={!diagnosticsEnabled}
                        onClick={() => {
                          void copyDiagnosticSnapshot();
                        }}
                      >
                        {diagnosticCopiedAt ? '已复制' : '复制诊断'}
                      </Button>
                      <Button
                        size="small"
                        icon={<PlayCircleOutlined />}
                        disabled={
                          !diagnosticsEnabled ||
                          captureState.status === 'recording'
                        }
                        onClick={startCapture}
                      >
                        开始采集
                      </Button>
                      <Button
                        size="small"
                        icon={<PauseCircleOutlined />}
                        disabled={
                          !diagnosticsEnabled ||
                          captureState.status !== 'recording'
                        }
                        onClick={stopCapture}
                      >
                        停止采集
                      </Button>
                      <Button
                        size="small"
                        icon={<DownloadOutlined />}
                        disabled={!diagnosticsEnabled}
                        onClick={downloadDiagnosticSnapshot}
                      >
                        下载快照
                      </Button>
                      <Button
                        size="small"
                        icon={<SyncOutlined />}
                        onClick={() => {
                          clearPerfEvents();
                          setPerfSummary(summarizePerfEvents());
                          setPerfEvents([]);
                          setDiagnosticCopiedAt(null);
                          setCaptureState({ status: 'idle' });
                        }}
                      >
                        清空
                      </Button>
                    </div>
                  </div>

                  <div className={styles.devPerfDiagnosisList}>
                    {diagnosticSnapshot.diagnosis.map((item) => (
                      <div
                        key={item.code}
                        className={styles.devPerfDiagnosisItem}
                        data-severity={item.severity}
                      >
                        <Text strong>{item.title}</Text>
                        <Text type="secondary">{item.evidence}</Text>
                      </div>
                    ))}
                  </div>

                  <div className={styles.devPerfGrid}>
                    <PerfMetric
                      label="到达速率"
                      value={formatMetric(
                        getNestedNumber(
                          perfSummary,
                          'stream.incomingCharsPerSecond',
                        ),
                        ' chars/s',
                      )}
                      tone="ok"
                    />
                    <PerfMetric
                      label="可见速率"
                      value={formatMetric(
                        getNestedNumber(
                          perfSummary,
                          'output.activeCharsPerSecond',
                        ),
                        ' chars/s',
                      )}
                      tone="ok"
                    />
                    <PerfMetric
                      label="DOM 字符"
                      value={`${formatMetric(getNestedNumber(perfSummary, 'output.lastDomChars'))} / ${formatMetric(getNestedNumber(perfSummary, 'output.maxDomChars'))}`}
                    />
                    <PerfMetric
                      label="Commit→Paint 平均"
                      value={formatMetric(
                        getNestedNumber(
                          perfSummary,
                          'output.avgCommitToPaintMs',
                        ),
                        'ms',
                      )}
                      tone={
                        (getNestedNumber(
                          perfSummary,
                          'output.maxCommitToPaintMs',
                        ) ?? 0) > 50
                          ? 'warn'
                          : 'normal'
                      }
                    />
                    <PerfMetric
                      label="Commit→Paint 峰值"
                      value={formatMetric(
                        getNestedNumber(
                          perfSummary,
                          'output.maxCommitToPaintMs',
                        ),
                        'ms',
                      )}
                      tone={
                        (getNestedNumber(
                          perfSummary,
                          'output.maxCommitToPaintMs',
                        ) ?? 0) > 50
                          ? 'warn'
                          : 'normal'
                      }
                    />
                    <PerfMetric
                      label="事件应用峰值"
                      value={formatMetric(
                        getNestedNumber(perfSummary, 'react.maxEventApplyMs'),
                        'ms',
                      )}
                      tone={
                        (getNestedNumber(
                          perfSummary,
                          'react.maxEventApplyMs',
                        ) ?? 0) > 30
                          ? 'warn'
                          : 'normal'
                      }
                    />
                    <PerfMetric
                      label="Markdown 峰值"
                      value={formatMetric(
                        getNestedNumber(
                          perfSummary,
                          'react.maxMarkdownCommitMs',
                        ),
                        'ms',
                      )}
                      tone={
                        (getNestedNumber(
                          perfSummary,
                          'react.maxMarkdownCommitMs',
                        ) ?? 0) > 50
                          ? 'warn'
                          : 'normal'
                      }
                    />
                    <PerfMetric
                      label="长任务"
                      value={`${formatMetric(getNestedNumber(perfSummary, 'browser.longTasks'))} · ${formatMetric(getNestedNumber(perfSummary, 'browser.maxLongTaskMs'), 'ms')}`}
                      tone={
                        (getNestedNumber(perfSummary, 'browser.longTasks') ??
                          0) > 0
                          ? 'warn'
                          : 'normal'
                      }
                    />
                    <PerfMetric
                      label="活跃窗口"
                      value={`${formatMetric(getNestedNumber(perfSummary, 'output.activeWindowMs'), 'ms')} / ${formatMetric(getNestedNumber(perfSummary, 'stream.incomingWindowMs'), 'ms')}`}
                    />
                    <PerfMetric
                      label="流程步骤"
                      value={`${formatMetric(getNestedNumber(perfSummary, 'workflow.steps'))} · ${formatMetric(getNestedNumber(perfSummary, 'workflow.traces'))} traces`}
                    />
                    <PerfMetric
                      label="流程峰值"
                      value={formatMetric(
                        getNestedNumber(perfSummary, 'workflow.maxStepMs'),
                        'ms',
                      )}
                      tone={
                        (getNestedNumber(perfSummary, 'workflow.maxStepMs') ??
                          0) > 800
                          ? 'warn'
                          : 'normal'
                      }
                    />
                  </div>

                  <Collapse
                    size="small"
                    ghost
                    items={[
                      {
                        key: 'counts',
                        label: '事件计数',
                        children: (
                          <div className={styles.devPerfCounts}>
                            {Object.entries(
                              (perfSummary?.counts ?? {}) as Record<
                                string,
                                number
                              >,
                            )
                              .sort((a, b) => b[1] - a[1])
                              .map(([name, count]) => (
                                <Tag key={name} color={getEventTone(name)}>
                                  {name}: {count}
                                </Tag>
                              ))}
                          </div>
                        ),
                      },
                      {
                        key: 'workflow',
                        label: '最慢流程步骤',
                        children:
                          diagnosticSnapshot.top.workflowSteps.length === 0 ? (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description="暂无流程步骤"
                            />
                          ) : (
                            <div className={styles.devPerfCounts}>
                              {diagnosticSnapshot.top.workflowSteps.map(
                                (event, index) => {
                                  const payload = event.payload ?? {};
                                  const workflow =
                                    typeof payload.workflow === 'string'
                                      ? payload.workflow
                                      : 'workflow';
                                  const step =
                                    typeof payload.step === 'string'
                                      ? payload.step
                                      : 'step';
                                  const durationMs =
                                    typeof payload.durationMs === 'number'
                                      ? payload.durationMs
                                      : null;
                                  const traceId =
                                    typeof payload.traceId === 'string'
                                      ? payload.traceId
                                      : '';
                                  const status =
                                    typeof payload.status === 'string'
                                      ? payload.status
                                      : 'ok';
                                  return (
                                    <div
                                      key={`${traceId}-${workflow}-${step}-${index}`}
                                      className={styles.devPerfEventItem}
                                    >
                                      <div
                                        className={styles.devPerfEventHeader}
                                      >
                                        <Tag
                                          color={
                                            status === 'error'
                                              ? 'red'
                                              : durationMs != null &&
                                                  durationMs > 800
                                                ? 'orange'
                                                : 'blue'
                                          }
                                        >
                                          {workflow}.{step}
                                        </Tag>
                                        <Text className={styles.devEventTime}>
                                          {formatMetric(durationMs, 'ms')}
                                        </Text>
                                      </div>
                                      <pre className={styles.devEventPayload}>
                                        {JSON.stringify(payload, null, 2)}
                                      </pre>
                                    </div>
                                  );
                                },
                              )}
                            </div>
                          ),
                      },
                    ]}
                  />

                  <div className={styles.devEventList}>
                    {perfEvents.length === 0 ? (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="等待性能事件"
                      />
                    ) : (
                      perfEvents.map((event) => (
                        <div
                          key={`${event.name}-${event.at}-${JSON.stringify(event.payload ?? {})}`}
                          className={styles.devPerfEventItem}
                        >
                          <div className={styles.devPerfEventHeader}>
                            <Tag color={getEventTone(event.name)}>
                              {event.name}
                            </Tag>
                            <Text className={styles.devEventTime}>
                              {Math.round(event.at)}ms
                            </Text>
                          </div>
                          <pre className={styles.devEventPayload}>
                            {JSON.stringify(event.payload ?? {}, null, 2)}
                          </pre>
                        </div>
                      ))
                    )}
                  </div>

                  <Paragraph className={styles.devPanelHint}>
                    复制诊断会包含摘要、瓶颈判断、间隔抖动、Top
                    慢记录和最近原始事件。 Console
                    输出默认关闭；如需同时打印，设置
                    localStorage.pudding_perf_console = "1"。 摘要 API:
                    window.__PUDDING_PERF__.summary() / snapshot()
                  </Paragraph>
                </div>
              ),
            },
            {
              key: 'cases',
              label: `Cases (${benchmarkCases.length})`,
              children: (
                <div className={styles.devPanelSection}>
                  <Text type="secondary" style={{ fontSize: 12 }}>
                    从服务端配置拉取试题；发送时只提交题面文本。
                  </Text>
                  <Select
                    size="small"
                    loading={loadingBenchmarkCases}
                    value={selectedBenchmarkCaseId}
                    placeholder="选择试题"
                    aria-label="选择试题"
                    onChange={setSelectedBenchmarkCaseId}
                    options={benchmarkCases.map((item) => ({
                      value: item.id,
                      label: item.title,
                    }))}
                    style={{ width: '100%' }}
                    disabled={
                      loadingBenchmarkCases || benchmarkCases.length === 0
                    }
                  />
                  {selectedBenchmarkCaseId && (
                    <div className={styles.devPerfDiagnosisList}>
                      {benchmarkCases
                        .filter((item) => item.id === selectedBenchmarkCaseId)
                        .map((item) => (
                          <div
                            key={item.id}
                            className={styles.devPerfDiagnosisItem}
                            data-severity="info"
                          >
                            <Text strong>{item.title}</Text>
                            <Text type="secondary">
                              {item.category}
                              {' · '}
                              {item.difficulty}
                              {item.estimatedRounds
                                ? ` · ${item.estimatedRounds} 轮`
                                : ''}
                            </Text>
                            <div className={styles.devPerfCounts}>
                              {item.coverage.map((tag) => (
                                <Tag key={tag} color="blue">
                                  {tag}
                                </Tag>
                              ))}
                              {item.seedId && <Tag color="purple">seed</Tag>}
                            </div>
                          </div>
                        ))}
                    </div>
                  )}
                  {benchmarkError && (
                    <Text type="danger" style={{ fontSize: 12 }}>
                      {benchmarkError}
                    </Text>
                  )}
                  <Button
                    size="small"
                    type="primary"
                    icon={<PlayCircleOutlined />}
                    loading={sendingBenchmarkCase}
                    disabled={
                      !selectedBenchmarkCaseId ||
                      !onRunBenchmarkPrompt ||
                      loadingBenchmarkCases
                    }
                    onClick={() => {
                      void sendSelectedBenchmarkCase();
                    }}
                  >
                    发送题面
                  </Button>
                </div>
              ),
            },
            {
              key: 'thought',
              label: 'Thought',
              children: loadingSubconscious ? (
                <div className={styles.devPanelLoading}>
                  <Spin size="small" />
                </div>
              ) : !subconsciousData ? (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="暂无思考数据"
                />
              ) : (
                <div className={styles.devPanelSection}>
                  <div className={styles.subconsciousTimeline}>
                    <Timeline
                      items={[
                        {
                          color: subconsciousData.job?.startedAt
                            ? 'blue'
                            : 'gray',
                          dot: subconsciousData.job?.startedAt ? (
                            <LoadingOutlined />
                          ) : (
                            <ExclamationCircleOutlined />
                          ),
                          children: (
                            <div>
                              <Text strong>开始处理</Text>
                              <br />
                              <Text
                                type="secondary"
                                className={styles.devEventTime}
                              >
                                {subconsciousData.job?.startedAt
                                  ? dayjs(
                                      subconsciousData.job.startedAt,
                                    ).format('HH:mm:ss')
                                  : '-'}
                              </Text>
                            </div>
                          ),
                        },
                        {
                          color:
                            subconsciousData.job?.factsExtracted != null
                              ? 'blue'
                              : 'gray',
                          dot:
                            subconsciousData.job?.factsExtracted != null ? (
                              <BulbOutlined />
                            ) : undefined,
                          children: (
                            <div>
                              <Text strong>LLM 分析中</Text>
                              <br />
                              <Text type="secondary">
                                提取了{' '}
                                {subconsciousData.job?.factsExtracted ?? 0}{' '}
                                条事实， 合并{' '}
                                {subconsciousData.job?.factsMerged ?? 0} 条
                              </Text>
                            </div>
                          ),
                        },
                        {
                          color:
                            subconsciousData.job?.status === 'completed'
                              ? 'green'
                              : subconsciousData.job?.status === 'failed'
                                ? 'red'
                                : 'gray',
                          dot:
                            subconsciousData.job?.status === 'completed' ? (
                              <CheckCircleOutlined />
                            ) : subconsciousData.job?.status === 'failed' ? (
                              <ExclamationCircleOutlined />
                            ) : undefined,
                          children: (
                            <div>
                              <Text strong>
                                {subconsciousData.job?.status === 'completed'
                                  ? '处理完成'
                                  : subconsciousData.job?.status === 'failed'
                                    ? '处理失败'
                                    : '等待中...'}
                              </Text>
                              <br />
                              <Text type="secondary">
                                耗时 {subconsciousData.job?.elapsedMs ?? 0}ms
                                {subconsciousData.job?.llmModelId
                                  ? ` · ${subconsciousData.job.llmModelId}`
                                  : ''}
                              </Text>
                              {subconsciousData.job?.errorMessage && (
                                <Paragraph className={styles.devErrorText}>
                                  {subconsciousData.job.errorMessage}
                                </Paragraph>
                              )}
                            </div>
                          ),
                        },
                      ]}
                    />
                  </div>
                  <Collapse
                    size="small"
                    ghost
                    items={[
                      {
                        key: 'facts',
                        label: `抽取事实 (${subconsciousData.facts.length})`,
                        children: (
                          <div className={styles.devList}>
                            {subconsciousData.facts.map((f) => (
                              <div
                                key={f.factId}
                                className={styles.devListItem}
                              >
                                <Tag>{f.category}</Tag>
                                <Text>{f.statement}</Text>
                              </div>
                            ))}
                          </div>
                        ),
                      },
                      {
                        key: 'prefs',
                        label: `偏好 (${subconsciousData.preferences.length})`,
                        children: (
                          <div className={styles.devList}>
                            {subconsciousData.preferences.map((p) => (
                              <div
                                key={p.preferenceId}
                                className={styles.devListItem}
                              >
                                <Tag>{p.category}</Tag>
                                <Text>
                                  {p.key} = {p.value}
                                </Text>
                              </div>
                            ))}
                          </div>
                        ),
                      },
                    ]}
                  />
                </div>
              ),
            },
            {
              key: 'events',
              label: `Events (${eventCountLabel})`,
              children:
                filteredEvents.length === 0 ? (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="暂无有效事件"
                  />
                ) : (
                  <div className={styles.devEventList}>
                    <Timeline
                      items={filteredEvents
                        .slice()
                        .reverse()
                        .map((evt) => ({
                          color: evt.event?.toLowerCase().includes('error')
                            ? 'red'
                            : 'blue',
                          children: (
                            <div>
                              <div
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: 6,
                                  marginBottom: 2,
                                }}
                              >
                                <Tag color="blue">{evt.event}</Tag>
                                <Text className={styles.devEventTime}>
                                  {dayjs(evt.timestamp).format('HH:mm:ss.SSS')}
                                </Text>
                              </div>
                              <pre
                                className={styles.devEventPayload}
                                style={{ maxHeight: 120, overflow: 'auto' }}
                              >
                                {evt.payload}
                              </pre>
                            </div>
                          ),
                        }))}
                    />
                  </div>
                ),
            },
            {
              key: 'context',
              label: 'Context',
              children: loadingContext ? (
                <div className={styles.devPanelLoading}>
                  <Spin size="small" />
                </div>
              ) : !contextData ? (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="暂无上下文诊断"
                />
              ) : (
                <div className={styles.devPanelSection}>
                  <SpaceLine
                    label="组装时间"
                    value={
                      contextData.assembledAt
                        ? dayjs(contextData.assembledAt).format(
                            'YYYY-MM-DD HH:mm:ss',
                          )
                        : '-'
                    }
                  />
                  <div style={{ marginBottom: 8 }}>
                    <Text type="secondary" style={{ fontSize: 12 }}>
                      上下文预算
                    </Text>
                    <Progress
                      percent={Math.min(
                        ((contextData.totalTokens || 0) / 200) * 100,
                        100,
                      )}
                      size="small"
                      strokeColor={
                        (contextData.totalTokens || 0) > 160
                          ? 'var(--warning-signal, #F97316)'
                          : 'var(--memory-glow, #A78BFA)'
                      }
                      format={() => `${contextData.totalTokens || 0} tokens`}
                    />
                  </div>
                  {contextData.message && (
                    <Paragraph className={styles.devPanelHint}>
                      {contextData.message}
                    </Paragraph>
                  )}
                  {/* 上下文分层摘要，不展示完整 JSON dump */}
                  <div
                    style={{ display: 'flex', flexDirection: 'column', gap: 4 }}
                  >
                    {(contextData.layers || []).map((layer) => (
                      <div
                        key={layer.layerName}
                        style={{
                          display: 'flex',
                          justifyContent: 'space-between',
                          alignItems: 'center',
                          padding: '4px 8px',
                          background: 'var(--ant-colorFillQuaternary)',
                          borderRadius: 6,
                          fontSize: 12,
                        }}
                      >
                        <span
                          style={{
                            display: 'flex',
                            alignItems: 'center',
                            gap: 4,
                          }}
                        >
                          {layer.layerName.toLowerCase().includes('system') ? (
                            <SettingOutlined style={{ fontSize: 11 }} />
                          ) : layer.layerName
                              .toLowerCase()
                              .includes('memory') ? (
                            <DatabaseOutlined style={{ fontSize: 11 }} />
                          ) : (
                            <CodeOutlined style={{ fontSize: 11 }} />
                          )}
                          {layer.layerName}
                        </span>
                        <Tag>{layer.tokenCount} tk</Tag>
                      </div>
                    ))}
                  </div>
                </div>
              ),
            },
            {
              key: 'tokens',
              label: 'Tokens',
              children:
                !contextData && !subconsciousData?.job ? (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="暂无 Token 数据"
                  />
                ) : (
                  <div className={styles.devPanelSection}>
                    {/* Total context tokens */}
                    <div>
                      <Text type="secondary" style={{ fontSize: 11 }}>
                        上下文 Token 用量
                      </Text>
                      <Progress
                        percent={Math.min(
                          ((contextData?.totalTokens || 0) / 200) * 100,
                          100,
                        )}
                        size="small"
                        strokeColor={
                          (contextData?.totalTokens || 0) > 160
                            ? 'var(--warning-signal, #F97316)'
                            : 'var(--memory-glow, #A78BFA)'
                        }
                        format={() => `${contextData?.totalTokens || 0}`}
                      />
                    </div>

                    {/* LLM tokens used in subconscious */}
                    {subconsciousData?.job?.llmTokensUsed != null && (
                      <div>
                        <Text type="secondary" style={{ fontSize: 11 }}>
                          潜意识 LLM 消耗
                        </Text>
                        <Progress
                          percent={Math.min(
                            (subconsciousData.job.llmTokensUsed / 4000) * 100,
                            100,
                          )}
                          size="small"
                          strokeColor="var(--tool-signal, #22D3EE)"
                          format={() =>
                            `${subconsciousData.job?.llmTokensUsed}`
                          }
                        />
                      </div>
                    )}

                    {/* Context layer breakdown */}
                    {(contextData?.layers || []).length > 0 && (
                      <div>
                        <Text type="secondary" style={{ fontSize: 11 }}>
                          分层 Token 分布
                        </Text>
                        <div
                          style={{
                            display: 'flex',
                            flexDirection: 'column',
                            gap: 3,
                            marginTop: 4,
                          }}
                        >
                          {(contextData?.layers || []).map((layer) => {
                            const pct = contextData?.totalTokens
                              ? (layer.tokenCount / contextData.totalTokens) *
                                100
                              : 0;
                            return (
                              <div
                                key={layer.layerName}
                                style={{
                                  display: 'flex',
                                  alignItems: 'center',
                                  gap: 6,
                                  fontSize: 11,
                                }}
                              >
                                <span
                                  style={{
                                    width: 80,
                                    overflow: 'hidden',
                                    textOverflow: 'ellipsis',
                                    whiteSpace: 'nowrap',
                                    opacity: 0.7,
                                  }}
                                >
                                  {layer.layerName}
                                </span>
                                <Progress
                                  percent={pct}
                                  size="small"
                                  showInfo={false}
                                  style={{ flex: 1, margin: 0 }}
                                  strokeColor={
                                    layer.layerName
                                      .toLowerCase()
                                      .includes('system')
                                      ? '#94A3B8'
                                      : layer.layerName
                                            .toLowerCase()
                                            .includes('memory')
                                        ? 'var(--memory-glow, #A78BFA)'
                                        : 'var(--tool-signal, #22D3EE)'
                                  }
                                />
                                <span
                                  style={{
                                    width: 36,
                                    textAlign: 'right',
                                    opacity: 0.6,
                                  }}
                                >
                                  {layer.tokenCount}
                                </span>
                              </div>
                            );
                          })}
                        </div>
                      </div>
                    )}

                    {/* Subconscious job stats */}
                    {subconsciousData?.job && (
                      <div
                        style={{
                          fontSize: 11,
                          color: 'var(--text-muted)',
                          opacity: 0.6,
                          marginTop: 4,
                        }}
                      >
                        耗时 {subconsciousData.job.elapsedMs}ms
                        {subconsciousData.job.llmModelId
                          ? ` · ${subconsciousData.job.llmModelId}`
                          : ''}
                      </div>
                    )}
                  </div>
                ),
            },
          ]}
        />
      )}
    </aside>
  );
};

const SpaceLine: React.FC<{ label: string; value: string }> = ({
  label,
  value,
}) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.devLine}>
      <Text type="secondary">{label}</Text>
      <Text>{value}</Text>
    </div>
  );
};

export default DevPanel;
