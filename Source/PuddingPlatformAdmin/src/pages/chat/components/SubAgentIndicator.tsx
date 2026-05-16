// ── SubAgentIndicator：状态栏子代理运行状态指示器 + 点击弹出管理器面板 ────
import React, { useState, useEffect, useCallback, useRef } from 'react';
import { Tooltip, Modal, Button, Space, Tag, Spin, Typography } from 'antd';
import { SendOutlined, ReloadOutlined, CloseOutlined } from '@ant-design/icons';
import { useChatStyles } from '../styles';
import { getSessionSubAgents, type SubAgentStatusDto } from '../../../services/platform/api';

const { Text } = Typography;

interface SubAgentIndicatorProps {
  sessionId?: string | null;
}

const POLL_INTERVAL = 5000;

const statusConfig: Record<string, { color: string; label: string; icon: string }> = {
  running: { color: '#22c55e', label: '运行中', icon: '🔄' },
  completed: { color: '#3b82f6', label: '已完成', icon: '✅' },
  failed: { color: '#ef4444', label: '失败', icon: '❌' },
};

const SubAgentIndicator: React.FC<SubAgentIndicatorProps> = ({ sessionId }) => {
  const { styles } = useChatStyles();
  const [runningCount, setRunningCount] = useState(0);
  const [lastCompleted, setLastCompleted] = useState<string | undefined>();
  const [panelOpen, setPanelOpen] = useState(false);
  const [subAgents, setSubAgents] = useState<SubAgentStatusDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState<string>('all');
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const fetchSubAgents = useCallback(async () => {
    if (!sessionId) return;
    try {
      setLoading(true);
      const data = await getSessionSubAgents(sessionId);
      setSubAgents(data);
      setRunningCount(data.filter(s => s.status === 'running').length);
      const lastDone = data.find(s => s.status === 'completed');
      setLastCompleted(lastDone?.subSessionId?.slice(-12));
    } catch {
      // silent fail
    } finally {
      setLoading(false);
    }
  }, [sessionId]);

  // 面板打开时定时轮询
  useEffect(() => {
    if (panelOpen && sessionId) {
      fetchSubAgents();
      timerRef.current = setInterval(fetchSubAgents, POLL_INTERVAL);
      return () => { if (timerRef.current) clearInterval(timerRef.current); };
    } else {
      if (timerRef.current) { clearInterval(timerRef.current); timerRef.current = null; }
    }
  }, [panelOpen, sessionId, fetchSubAgents]);

  // sessionId 变化时刷新
  useEffect(() => { fetchSubAgents(); }, [sessionId, fetchSubAgents]);

  const active = runningCount > 0;

  const filteredAgents = filter === 'all'
    ? subAgents
    : subAgents.filter(s => s.status === filter);

  const shortId = (id: string) => id.length > 16 ? '...' + id.slice(-12) : id;

  const formatTime = (iso?: string) => {
    if (!iso) return '-';
    return new Date(iso).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  };

  return (
    <>
      <Tooltip
        title={
          active
            ? `${runningCount} 个子代理运行中${lastCompleted ? ' · 最近完成: ' + lastCompleted : ''}`
            : lastCompleted
              ? '子代理空闲 · 最近完成: ' + lastCompleted
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
            {active ? `${runningCount}` : '0'}
          </span>
        </span>
      </Tooltip>

      <Modal
        title={
          <Space>
            <SendOutlined style={{ color: active ? '#22c55e' : 'var(--earth-brown)' }} />
            <span>子代理管理器</span>
            {sessionId && <Text type="secondary" style={{ fontSize: 11 }}>{shortId(sessionId)}</Text>}
          </Space>
        }
        open={panelOpen}
        onCancel={() => setPanelOpen(false)}
        footer={null}
        width={560}
        styles={{ body: { maxHeight: '60vh', overflowY: 'auto', padding: '12px 16px' } }}
        closeIcon={<CloseOutlined />}
        extra={
          <Button size="small" icon={<ReloadOutlined spin={loading} />} onClick={fetchSubAgents}>
            刷新
          </Button>
        }
      >
        <div style={{ marginBottom: 12, display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {(['all', 'running', 'completed', 'failed'] as const).map(f => (
            <Tag
              key={f}
              color={filter === f ? (f === 'running' ? 'green' : f === 'failed' ? 'red' : f === 'completed' ? 'blue' : undefined) : undefined}
              style={{ cursor: 'pointer', opacity: filter === f ? 1 : 0.5 }}
              onClick={() => setFilter(f)}
            >
              {f === 'all' ? `全部 (${subAgents.length})` :
               f === 'running' ? `运行中 (${subAgents.filter(s => s.status === 'running').length})` :
               f === 'completed' ? `已完成 (${subAgents.filter(s => s.status === 'completed').length})` :
               `失败 (${subAgents.filter(s => s.status === 'failed').length})`}
            </Tag>
          ))}
        </div>

        {loading && filteredAgents.length === 0 && (
          <div style={{ textAlign: 'center', padding: 20 }}><Spin /></div>
        )}

        {!loading && filteredAgents.length === 0 && (
          <div style={{ textAlign: 'center', padding: 20, color: 'var(--earth-brown)', opacity: 0.5 }}>
            暂无子代理记录
          </div>
        )}

        {filteredAgents.map(sa => {
          const cfg = statusConfig[sa.status] || statusConfig.failed;
          return (
            <div
              key={sa.subSessionId}
              style={{
                padding: '8px 12px',
                marginBottom: 6,
                borderRadius: 6,
                background: 'var(--ant-color-fill-secondary)',
                borderLeft: `3px solid ${cfg.color}`,
                fontSize: 12,
              }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
                <Space size={4}>
                  <span>{cfg.icon}</span>
                  <Tag color={cfg.color} style={{ margin: 0, fontSize: 10, lineHeight: '16px' }}>{cfg.label}</Tag>
                  <Text code style={{ fontSize: 10 }}>{shortId(sa.subSessionId)}</Text>
                </Space>
                <Space size={4}>
                  {sa.templateId && <Tag style={{ fontSize: 10 }}>{sa.templateId}</Tag>}
                  {sa.modelId && <Tag style={{ fontSize: 10 }} color="purple">{sa.modelId}</Tag>}
                </Space>
              </div>
              <div style={{ color: 'var(--earth-brown)', marginBottom: 4, lineHeight: 1.5 }}>
                {sa.taskSummary}
              </div>
              <Space size={8} style={{ fontSize: 10, color: 'var(--earth-brown)', opacity: 0.6 }}>
                <span>创建: {formatTime(sa.spawnedAt)}</span>
                {sa.completedAt && <span>完成: {formatTime(sa.completedAt)}</span>}
              </Space>
              {sa.resultSummary && (
                <div style={{ marginTop: 4, fontSize: 10, color: 'var(--earth-brown)', opacity: 0.5, maxHeight: 40, overflow: 'hidden' }}>
                  {sa.resultSummary}
                </div>
              )}
            </div>
          );
        })}
      </Modal>
    </>
  );
};

export default SubAgentIndicator;
