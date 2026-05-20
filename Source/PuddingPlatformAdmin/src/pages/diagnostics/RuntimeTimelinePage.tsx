// ─── RuntimeTimelinePage：运行时时间线表格 (ADR-023 Phase 2) ─────
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Tag,
  Space,
  Input,
  Select,
  Tooltip,
  Typography,
} from 'antd';
import { SearchOutlined } from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import { getRuntimeTimeline } from './api';
import type { RuntimeTimelineItem } from './types';
import dayjs from 'dayjs';

const { Text } = Typography;

/** 状态 → 颜色映射 */
const statusColor: Record<string, string> = {
  succeeded: 'green',
  failed: 'red',
  running: 'blue',
  cancelled: 'default',
  pending: 'orange',
};

/** 格式化持续时间 */
const fmtDuration = (ms?: number): string => {
  if (ms == null) return '-';
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  return `${(ms / 60_000).toFixed(1)}m`;
};

/** 格式化 UTC 时间为本地时间 */
const fmtTime = (utc: string): string => {
  try {
    return dayjs(utc).format('YYYY-MM-DD HH:mm:ss');
  } catch (_e) {
    return utc;
  }
};

const RuntimeTimelinePage: React.FC = () => {
  const actionRef = useRef<ActionType>(null!);
  const [filters, setFilters] = useState({
    sessionId: '',
    traceId: '',
    component: '',
    status: '',
  });

  const columns: ProColumns<RuntimeTimelineItem>[] = [
    {
      title: '开始时间',
      dataIndex: 'startedAtUtc',
      width: 180,
      render: (_, record) => fmtTime(record.startedAtUtc),
    },
    {
      title: '组件',
      dataIndex: 'component',
      width: 140,
      ellipsis: true,
    },
    {
      title: '操作',
      dataIndex: 'operation',
      width: 160,
      ellipsis: true,
      render: (text) => (
        <Tooltip title={text}>
          <Text strong>{text}</Text>
        </Tooltip>
      ),
    },
    {
      title: '类型',
      dataIndex: 'kind',
      width: 100,
      render: (_, record) => {
        const kindLabel: Record<string, string> = {
          activity: '活动',
          event: '事件',
          session_frame: '会话帧',
          subagent_run: '子代理',
        };
        return <Tag>{kindLabel[record.kind] || record.kind}</Tag>;
      },
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
      title: '耗时',
      dataIndex: 'durationMs',
      width: 100,
      render: (_, record) => fmtDuration(record.durationMs),
    },
    {
      title: '摘要',
      dataIndex: 'summary',
      ellipsis: true,
      width: 200,
      render: (text) => text || '-',
    },
    {
      title: 'Session ID',
      dataIndex: 'sessionId',
      width: 180,
      ellipsis: true,
      render: (_, record) =>
        record.sessionId ? <Text copyable={{ text: record.sessionId }} style={{ fontSize: 12 }}>{record.sessionId.slice(0, 8)}...</Text> : '-',
    },
  ];

  return (
    <PageContainer header={{ title: '运行时时间线' }}>
      <ProTable<RuntimeTimelineItem>
        actionRef={actionRef}
        columns={columns}
        rowKey="id"
        search={false}
        toolbar={{
          title: (
            <Space wrap>
              <Input
                placeholder="Trace ID"
                allowClear
                style={{ width: 180 }}
                value={filters.traceId}
                onChange={(e) => setFilters((f) => ({ ...f, traceId: e.target.value }))}
                data-testid="diagnostics-filter-trace"
              />
              <Input
                placeholder="Session ID"
                allowClear
                style={{ width: 180 }}
                value={filters.sessionId}
                onChange={(e) => setFilters((f) => ({ ...f, sessionId: e.target.value }))}
                data-testid="diagnostics-filter-session"
              />
              <Input
                placeholder="组件"
                allowClear
                style={{ width: 140 }}
                value={filters.component}
                onChange={(e) => setFilters((f) => ({ ...f, component: e.target.value }))}
                data-testid="diagnostics-filter-component"
              />
              <Select
                placeholder="状态"
                allowClear
                style={{ width: 120 }}
                value={filters.status || undefined}
                onChange={(val) => setFilters((f) => ({ ...f, status: val || '' }))}
                options={[
                  { label: '成功', value: 'succeeded' },
                  { label: '失败', value: 'failed' },
                  { label: '运行中', value: 'running' },
                  { label: '已取消', value: 'cancelled' },
                  { label: '待处理', value: 'pending' },
                ]}
                data-testid="diagnostics-filter-status"
              />
            </Space>
          ),
        }}
        request={async (params) => {
          const { current, pageSize } = params;
          const res = await getRuntimeTimeline({
            page: current,
            pageSize,
            ...(filters.sessionId && { sessionId: filters.sessionId }),
            ...(filters.traceId && { traceId: filters.traceId }),
            ...(filters.component && { component: filters.component }),
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
            'data-testid': `timeline-row-${record.id}`,
          } as React.HTMLAttributes<HTMLElement>;
        }}
        pagination={{ pageSize: 20, showSizeChanger: true }}
        dateFormatter="string"
      />
    </PageContainer>
  );
};

export default RuntimeTimelinePage;
