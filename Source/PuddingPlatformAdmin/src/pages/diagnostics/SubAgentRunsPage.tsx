// ─── SubAgentRunsPage：子代理运行列表 (ADR-023 Phase 2) ──────────
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Tag,
  Descriptions,
  Typography,
  Space,
  Input,
  Select,
  Spin,
  Alert,
} from 'antd';
import {
  ThunderboltOutlined,
  ToolOutlined,
} from '@ant-design/icons';
import React, { useRef, useState, useCallback } from 'react';
import { getSubAgentRuns, getSubAgentRunDetail } from './api';
import type { SubAgentRunSummary, SubAgentRunDetail } from './types';
import dayjs from 'dayjs';

const { Text, Paragraph } = Typography;

/** 状态 → 颜色映射 */
const statusColor: Record<string, string> = {
  succeeded: 'green',
  completed: 'green',
  failed: 'red',
  running: 'blue',
  pending: 'orange',
  cancelled: 'default',
};

/** 格式化持续时间 */
const fmtDuration = (ms: number): string => {
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  return `${(ms / 60_000).toFixed(1)}m`;
};

/** 格式化时间 */
const fmtTime = (utc: string): string => {
  try {
    return dayjs(utc).format('YYYY-MM-DD HH:mm:ss');
  } catch (_e) {
    return utc;
  }
};

const SubAgentRunsPage: React.FC = () => {
  const actionRef = useRef<ActionType>(null!);
  const [filters, setFilters] = useState({ parentSessionId: '', status: '' });
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailData, setDetailData] = useState<SubAgentRunDetail | null>(null);
  const [detailError, setDetailError] = useState<string | null>(null);

  /** 加载子代理运行详情 */
  const loadDetail = useCallback(async (runId: string) => {
    setDetailLoading(true);
    setDetailError(null);
    try {
      const detail = await getSubAgentRunDetail(runId);
      setDetailData(detail);
    } catch (e: any) {
      setDetailError(e?.message || '加载详情失败');
      setDetailData(null);
    } finally {
      setDetailLoading(false);
    }
  }, []);

  const columns: ProColumns<SubAgentRunSummary>[] = [
    {
      title: 'Run ID',
      dataIndex: 'runId',
      width: 180,
      ellipsis: true,
      render: (text) => (
        <Text copyable={{ text: text as string }} style={{ fontSize: 12 }}>
          {(text as string).slice(0, 12)}...
        </Text>
      ),
    },
    {
      title: '模板',
      dataIndex: 'templateId',
      width: 140,
      ellipsis: true,
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 100,
      render: (_, record) => (
        <Tag color={statusColor[record.status] || 'default'} data-testid={`status-badge-${record.status}`}>
          {record.status}
        </Tag>
      ),
    },
    {
      title: '开始时间',
      dataIndex: 'startedAt',
      width: 180,
      render: (_, record) => fmtTime(record.startedAt),
    },
    {
      title: '耗时',
      dataIndex: 'totalDurationMs',
      width: 100,
      render: (_, record) => fmtDuration(record.totalDurationMs),
    },
    {
      title: '轮次',
      dataIndex: 'totalRounds',
      width: 80,
      render: (_, record) => (
        <Space>
          <ThunderboltOutlined />
          {record.totalRounds}
        </Space>
      ),
    },
    {
      title: '工具调用',
      dataIndex: 'totalToolCalls',
      width: 80,
      render: (_, record) => (
        <Space>
          <ToolOutlined />
          {record.totalToolCalls}
        </Space>
      ),
    },
    {
      title: 'Session ID',
      dataIndex: 'parentSessionId',
      width: 180,
      ellipsis: true,
      hideInTable: true,
    },
  ];

  return (
    <PageContainer header={{ title: '子代理运行' }}>
      <ProTable<SubAgentRunSummary>
        actionRef={actionRef}
        columns={columns}
        rowKey="runId"
        search={false}
        toolbar={{
          title: (
            <Space wrap>
              <Input
                placeholder="Parent Session ID"
                allowClear
                style={{ width: 200 }}
                value={filters.parentSessionId}
                onChange={(e) => setFilters((f) => ({ ...f, parentSessionId: e.target.value }))}
                data-testid="diagnostics-filter-session"
              />
              <Select
                placeholder="状态"
                allowClear
                style={{ width: 120 }}
                value={filters.status || undefined}
                onChange={(val) => setFilters((f) => ({ ...f, status: val || '' }))}
                options={[
                  { label: '成功', value: 'succeeded' },
                  { label: '完成', value: 'completed' },
                  { label: '失败', value: 'failed' },
                  { label: '运行中', value: 'running' },
                  { label: '待处理', value: 'pending' },
                  { label: '已取消', value: 'cancelled' },
                ]}
                data-testid="diagnostics-filter-status"
              />
            </Space>
          ),
        }}
        request={async (params) => {
          const { current, pageSize } = params;
          const res = await getSubAgentRuns({
            offset: ((current || 1) - 1) * (pageSize || 20),
            limit: pageSize,
            ...(filters.parentSessionId && { parentSessionId: filters.parentSessionId }),
            ...(filters.status && { status: filters.status }),
          });
          return {
            data: res.items,
            total: res.total,
            success: true,
          };
        }}
        onRow={(record) => {
          return {
            'data-testid': `subagent-run-row-${record.runId}`,
            onClick: () => loadDetail(record.runId),
            style: { cursor: 'pointer' },
          } as React.HTMLAttributes<HTMLElement>;
        }}
        expandable={{
          expandedRowRender: () => null,
          expandRowByClick: false,
        }}
        pagination={{ pageSize: 20, showSizeChanger: true }}
        dateFormatter="string"
      />

      {/* ── 详情面板 ── */}
      <div data-testid="subagent-detail-panel" style={{ marginTop: 16 }}>
        {detailLoading && (
          <div style={{ textAlign: 'center', padding: 24 }}>
            <Spin tip="加载详情中..." />
          </div>
        )}
        {detailError && (
          <Alert message={detailError} type="error" showIcon closable onClose={() => setDetailError(null)} />
        )}
        {detailData && !detailLoading && (
          <Descriptions
            title="运行详情"
            bordered
            size="small"
            column={{ xs: 1, sm: 2, md: 3 }}
            style={{ background: '#fafafa' }}
          >
            <Descriptions.Item label="Run ID">
              <Text copyable>{detailData.summary.runId}</Text>
            </Descriptions.Item>
            <Descriptions.Item label="状态">
              <Tag color={statusColor[detailData.summary.status] || 'default'} data-testid={`status-badge-${detailData.summary.status}`}>
                {detailData.summary.status}
              </Tag>
            </Descriptions.Item>
            <Descriptions.Item label="耗时">{fmtDuration(detailData.summary.totalDurationMs)}</Descriptions.Item>
            <Descriptions.Item label="父会话">
              <Text copyable={{ text: detailData.summary.parentSessionId }}>
                {detailData.summary.parentSessionId?.slice(0, 12)}...
              </Text>
            </Descriptions.Item>
            <Descriptions.Item label="子会话">
              <Text copyable={{ text: detailData.summary.subSessionId }}>
                {detailData.summary.subSessionId?.slice(0, 12)}...
              </Text>
            </Descriptions.Item>
            <Descriptions.Item label="工作区">{detailData.summary.workspaceId}</Descriptions.Item>
            <Descriptions.Item label="模板">{detailData.summary.templateId}</Descriptions.Item>
            <Descriptions.Item label="Agent 实例">{detailData.summary.agentInstanceId}</Descriptions.Item>
            <Descriptions.Item label="轮次">{detailData.summary.totalRounds}</Descriptions.Item>
            <Descriptions.Item label="开始时间">{fmtTime(detailData.summary.startedAt)}</Descriptions.Item>
            <Descriptions.Item label="完成时间">
              {detailData.summary.completedAt ? fmtTime(detailData.summary.completedAt) : '-'}
            </Descriptions.Item>
            <Descriptions.Item label="事件数">{detailData.eventCount}</Descriptions.Item>
            <Descriptions.Item label="工具调用">{detailData.toolCallCount}</Descriptions.Item>
            {detailData.summary.errorMessage && (
              <Descriptions.Item label="错误信息">
                <Text type="danger">{detailData.summary.errorMessage}</Text>
              </Descriptions.Item>
            )}
            {detailData.task && (
              <Descriptions.Item label="任务" span={3}>
                <Paragraph style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{detailData.task}</Paragraph>
              </Descriptions.Item>
            )}
            {detailData.output && (
              <Descriptions.Item label="输出" span={3}>
                <Paragraph style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{detailData.output}</Paragraph>
              </Descriptions.Item>
            )}
          </Descriptions>
        )}
      </div>
    </PageContainer>
  );
};

export default SubAgentRunsPage;
