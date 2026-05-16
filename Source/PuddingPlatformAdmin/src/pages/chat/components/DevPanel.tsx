import { request } from '@umijs/max';
import { Collapse, Empty, Progress, Spin, Tabs, Tag, Timeline, Typography } from 'antd';
import { CheckCircleOutlined, LoadingOutlined, ExclamationCircleOutlined, BulbOutlined, ThunderboltOutlined, CodeOutlined, DatabaseOutlined, SettingOutlined, SendOutlined, SyncOutlined } from '@ant-design/icons';
import dayjs from 'dayjs';
import React, { useEffect, useMemo, useState } from 'react';
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
}

/** 噪音事件：心跳/keepalive/ping/comment等不应展示的事件 */
const NOISE_EVENT_TYPES = new Set(['heartbeat', 'keepalive', 'ping', 'comment', '']);

const DevPanel: React.FC<DevPanelProps> = ({ workspaceId, sessionId, rawEvents }) => {
  const { styles } = useChatStyles();
  const [contextData, setContextData] = useState<ContextSnapshot | null>(null);
  const [subconsciousData, setSubconsciousData] = useState<SubconsciousResult | null>(null);
  const [loadingContext, setLoadingContext] = useState(false);
  const [loadingSubconscious, setLoadingSubconscious] = useState(false);
  const [resolvedSessionId, setResolvedSessionId] = useState<string | null>(sessionId ?? null);
  // 是否展开 Inspector（默认折叠）
  const [inspectorOpen, setInspectorOpen] = useState(false);

  useEffect(() => {
    setResolvedSessionId(sessionId ?? null);
  }, [sessionId]);

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
    const timer = window.setInterval(() => { void loadContext(); }, 4000);
    return () => {
      alive = false;
      window.clearInterval(timer);
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
    const timer = window.setInterval(() => { void loadSubconscious(); }, 5000);
    return () => {
      alive = false;
      window.clearInterval(timer);
    };
  }, [workspaceId, resolvedSessionId]);

  // 过滤噪音事件
  const filteredEvents = useMemo(
    () => rawEvents.filter((e) => !NOISE_EVENT_TYPES.has(e.event?.toLowerCase())),
    [rawEvents],
  );
  const eventCountLabel = useMemo(() => `${filteredEvents.length} 条`, [filteredEvents.length]);

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
          defaultActiveKey="thought"
          items={[
            {
              key: 'thought',
              label: 'Thought',
              children: loadingSubconscious ? (
                <div className={styles.devPanelLoading}><Spin size="small" /></div>
              ) : !subconsciousData ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无思考数据" />
              ) : (
                <div className={styles.devPanelSection}>
                  <div className={styles.subconsciousTimeline}>
                    <Timeline
                      items={[
                        {
                          color: subconsciousData.job?.startedAt ? 'blue' : 'gray',
                          dot: subconsciousData.job?.startedAt ? <LoadingOutlined /> : <ExclamationCircleOutlined />,
                          children: (
                            <div>
                              <Text strong>开始处理</Text>
                              <br />
                              <Text type="secondary" className={styles.devEventTime}>
                                {subconsciousData.job?.startedAt
                                  ? dayjs(subconsciousData.job.startedAt).format('HH:mm:ss')
                                  : '-'}
                              </Text>
                            </div>
                          ),
                        },
                        {
                          color: subconsciousData.job?.factsExtracted != null ? 'blue' : 'gray',
                          dot: subconsciousData.job?.factsExtracted != null ? <BulbOutlined /> : undefined,
                          children: (
                            <div>
                              <Text strong>LLM 分析中</Text>
                              <br />
                              <Text type="secondary">
                                提取了 {subconsciousData.job?.factsExtracted ?? 0} 条事实，
                                合并 {subconsciousData.job?.factsMerged ?? 0} 条
                              </Text>
                            </div>
                          ),
                        },
                        {
                          color: subconsciousData.job?.status === 'completed' ? 'green' : subconsciousData.job?.status === 'failed' ? 'red' : 'gray',
                          dot: subconsciousData.job?.status === 'completed'
                            ? <CheckCircleOutlined />
                            : subconsciousData.job?.status === 'failed'
                              ? <ExclamationCircleOutlined />
                              : undefined,
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
                                {subconsciousData.job?.llmModelId ? ` · ${subconsciousData.job.llmModelId}` : ''}
                              </Text>
                              {subconsciousData.job?.errorMessage && (
                                <Paragraph className={styles.devErrorText}>{subconsciousData.job.errorMessage}</Paragraph>
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
                              <div key={f.factId} className={styles.devListItem}>
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
                              <div key={p.preferenceId} className={styles.devListItem}>
                                <Tag>{p.category}</Tag>
                                <Text>{p.key} = {p.value}</Text>
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
              children: filteredEvents.length === 0 ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无有效事件" />
              ) : (
                <div className={styles.devEventList}>
                  <Timeline
                    items={filteredEvents.slice().reverse().map((evt) => ({
                      color: evt.event?.toLowerCase().includes('error') ? 'red' : 'blue',
                      children: (
                        <div>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 2 }}>
                            <Tag color="blue">{evt.event}</Tag>
                            <Text className={styles.devEventTime}>{dayjs(evt.timestamp).format('HH:mm:ss.SSS')}</Text>
                          </div>
                          <pre className={styles.devEventPayload} style={{ maxHeight: 120, overflow: 'auto' }}>{evt.payload}</pre>
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
                <div className={styles.devPanelLoading}><Spin size="small" /></div>
              ) : !contextData ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无上下文诊断" />
              ) : (
                <div className={styles.devPanelSection}>
                  <SpaceLine label="组装时间" value={contextData.assembledAt ? dayjs(contextData.assembledAt).format('YYYY-MM-DD HH:mm:ss') : '-'} />
                  <div style={{ marginBottom: 8 }}>
                    <Text type="secondary" style={{ fontSize: 12 }}>上下文预算</Text>
                    <Progress
                      percent={Math.min((contextData.totalTokens || 0) / 200 * 100, 100)}
                      size="small"
                      strokeColor={
                        (contextData.totalTokens || 0) > 160 ? 'var(--warning-signal, #F97316)'
                          : 'var(--memory-glow, #A78BFA)'
                      }
                      format={() => `${contextData.totalTokens || 0} tokens`}
                    />
                  </div>
                  {contextData.message && <Paragraph className={styles.devPanelHint}>{contextData.message}</Paragraph>}
                  {/* 上下文分层摘要，不展示完整 JSON dump */}
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                    {(contextData.layers || []).map((layer) => (
                      <div key={layer.layerName} style={{
                        display: 'flex', justifyContent: 'space-between', alignItems: 'center',
                        padding: '4px 8px', background: 'var(--ant-colorFillQuaternary)', borderRadius: 6, fontSize: 12,
                      }}>
                        <span style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                          {layer.layerName.toLowerCase().includes('system') ? <SettingOutlined style={{ fontSize: 11 }} /> :
                            layer.layerName.toLowerCase().includes('memory') ? <DatabaseOutlined style={{ fontSize: 11 }} /> :
                              <CodeOutlined style={{ fontSize: 11 }} />}
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
              children: !contextData && !subconsciousData?.job ? (
                <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无 Token 数据" />
              ) : (
                <div className={styles.devPanelSection}>
                  {/* Total context tokens */}
                  <div>
                    <Text type="secondary" style={{ fontSize: 11 }}>上下文 Token 用量</Text>
                    <Progress
                      percent={Math.min(((contextData?.totalTokens || 0) / 200) * 100, 100)}
                      size="small"
                      strokeColor={
                        (contextData?.totalTokens || 0) > 160 ? 'var(--warning-signal, #F97316)'
                          : 'var(--memory-glow, #A78BFA)'
                      }
                      format={() => `${contextData?.totalTokens || 0}`}
                    />
                  </div>

                  {/* LLM tokens used in subconscious */}
                  {subconsciousData?.job?.llmTokensUsed != null && (
                    <div>
                      <Text type="secondary" style={{ fontSize: 11 }}>潜意识 LLM 消耗</Text>
                      <Progress
                        percent={Math.min((subconsciousData.job.llmTokensUsed / 4000) * 100, 100)}
                        size="small"
                        strokeColor="var(--tool-signal, #22D3EE)"
                        format={() => `${subconsciousData.job!.llmTokensUsed}`}
                      />
                    </div>
                  )}

                  {/* Context layer breakdown */}
                  {(contextData?.layers || []).length > 0 && (
                    <div>
                      <Text type="secondary" style={{ fontSize: 11 }}>分层 Token 分布</Text>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 3, marginTop: 4 }}>
                        {(contextData?.layers || []).map((layer) => {
                          const pct = contextData?.totalTokens ? (layer.tokenCount / contextData.totalTokens) * 100 : 0;
                          return (
                            <div key={layer.layerName} style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 11 }}>
                              <span style={{ width: 80, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', opacity: 0.7 }}>{layer.layerName}</span>
                              <Progress
                                percent={pct}
                                size="small"
                                showInfo={false}
                                style={{ flex: 1, margin: 0 }}
                                strokeColor={
                                  layer.layerName.toLowerCase().includes('system') ? '#94A3B8'
                                    : layer.layerName.toLowerCase().includes('memory') ? 'var(--memory-glow, #A78BFA)'
                                      : 'var(--tool-signal, #22D3EE)'
                                }
                              />
                              <span style={{ width: 36, textAlign: 'right', opacity: 0.6 }}>{layer.tokenCount}</span>
                            </div>
                          );
                        })}
                      </div>
                    </div>
                  )}

                  {/* Subconscious job stats */}
                  {subconsciousData?.job && (
                    <div style={{ fontSize: 11, color: 'var(--text-muted)', opacity: 0.6, marginTop: 4 }}>
                      耗时 {subconsciousData.job.elapsedMs}ms
                      {subconsciousData.job.llmModelId ? ` · ${subconsciousData.job.llmModelId}` : ''}
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

const SpaceLine: React.FC<{ label: string; value: string }> = ({ label, value }) => {
  const { styles } = useChatStyles();
  return (
    <div className={styles.devLine}>
      <Text type="secondary">{label}</Text>
      <Text>{value}</Text>
    </div>
  );
};

export default DevPanel;
