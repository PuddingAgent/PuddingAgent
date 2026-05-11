import { request } from '@umijs/max';
import { Collapse, Empty, Spin, Tabs, Tag, Timeline, Typography } from 'antd';
import { CheckCircleOutlined, LoadingOutlined, ExclamationCircleOutlined, BulbOutlined } from '@ant-design/icons';
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

const DevPanel: React.FC<DevPanelProps> = ({ workspaceId, sessionId, rawEvents }) => {
  const { styles } = useChatStyles();
  const [contextData, setContextData] = useState<ContextSnapshot | null>(null);
  const [subconsciousData, setSubconsciousData] = useState<SubconsciousResult | null>(null);
  const [loadingContext, setLoadingContext] = useState(false);
  const [loadingSubconscious, setLoadingSubconscious] = useState(false);
  const [resolvedSessionId, setResolvedSessionId] = useState<string | null>(sessionId ?? null);

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

  const eventCountLabel = useMemo(() => `${rawEvents.length} 条`, [rawEvents.length]);

  return (
    <aside className={styles.devPanel}>
      <div className={styles.devPanelHeader}>
        <span>开发者模式</span>
        <Tag color="processing">session: {resolvedSessionId || 'N/A'}</Tag>
      </div>

      <Tabs
        size="small"
        className={styles.devPanelTabs}
        items={[
          {
            key: 'context',
            label: '上下文组装',
            children: loadingContext ? (
              <div className={styles.devPanelLoading}><Spin size="small" /></div>
            ) : !contextData ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无上下文诊断" />
            ) : (
              <div className={styles.devPanelSection}>
                <SpaceLine label="组装时间" value={contextData.assembledAt ? dayjs(contextData.assembledAt).format('YYYY-MM-DD HH:mm:ss') : '-'} />
                <SpaceLine label="总Token" value={String(contextData.totalTokens || 0)} />
                {contextData.message && <Paragraph className={styles.devPanelHint}>{contextData.message}</Paragraph>}
                <Collapse
                  size="small"
                  ghost
                  items={(contextData.layers || []).map((layer) => ({
                    key: layer.layerName,
                    label: (
                      <div className={styles.devLayerTitle}>
                        <span>{layer.layerName}</span>
                        <Tag>{layer.tokenCount} tk</Tag>
                      </div>
                    ),
                    children: <pre className={styles.devPreview}>{layer.contentPreview || '(empty)'}</pre>,
                  }))}
                />
              </div>
            ),
          },
          {
            key: 'subconscious',
            label: '潜意识LLM',
            children: loadingSubconscious ? (
              <div className={styles.devPanelLoading}><Spin size="small" /></div>
            ) : !subconsciousData ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无潜意识数据" />
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
                    {
                      key: 'raw',
                      label: 'LLM 原始响应',
                      children: (
                        <pre className={styles.devPreview}>{subconsciousData.llmRawResponse || subconsciousData.note || '(not available)'}</pre>
                      ),
                    },
                  ]}
                />
              </div>
            ),
          },
          {
            key: 'events',
            label: `原始事件 (${eventCountLabel})`,
            children: rawEvents.length === 0 ? (
              <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无事件" />
            ) : (
              <div className={styles.devEventList}>
                {rawEvents.slice().reverse().map((evt) => (
                  <div key={evt.id} className={styles.devEventItem}>
                    <Tag color="blue">{evt.event}</Tag>
                    <Text className={styles.devEventTime}>{dayjs(evt.timestamp).format('HH:mm:ss.SSS')}</Text>
                    <pre className={styles.devEventPayload}>{evt.payload}</pre>
                  </div>
                ))}
              </div>
            ),
          },
        ]}
      />
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
