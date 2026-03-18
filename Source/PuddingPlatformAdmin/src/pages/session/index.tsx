import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import { Badge, Select, Space, Tag, Typography } from 'antd';
import React, { useState } from 'react';
import { listSessions, listWorkspaces, type SessionRecord, type SessionStatus } from '@/services/platform/api';

const { Text } = Typography;

const statusConfig: Record<SessionStatus, { badge: 'success' | 'processing' | 'default' | 'error' | 'warning'; label: string }> = {
  Active: { badge: 'processing', label: '活跃' },
  Idle: { badge: 'warning', label: '空闲' },
  Completed: { badge: 'success', label: '已完成' },
  Failed: { badge: 'error', label: '失败' },
  Frozen: { badge: 'default', label: '已冻结' },
};

const sessionTypeLabel: Record<string, string> = {
  ServiceSession: '服务会话',
  TaskSession: '任务会话',
  AuditSession: '审计会话',
};

const SessionPage: React.FC = () => {
  const [selectedWorkspace, setSelectedWorkspace] = useState<string | undefined>();

  const columns: ProColumns<SessionRecord>[] = [
    {
      title: 'Session ID',
      dataIndex: 'sessionId',
      copyable: true,
      width: 200,
      ellipsis: true,
      render: (_, record) => (
        <Text code style={{ fontSize: 12 }}>{record.sessionId}</Text>
      ),
    },
    {
      title: '工作空间',
      dataIndex: 'workspaceId',
      ellipsis: true,
      width: 120,
    },
    {
      title: 'Agent 模板',
      dataIndex: 'agentTemplateId',
      ellipsis: true,
      width: 140,
    },
    {
      title: '渠道',
      dataIndex: 'channelId',
      width: 100,
      render: (val) => <Tag color="blue">{String(val)}</Tag>,
    },
    {
      title: '类型',
      dataIndex: 'sessionType',
      width: 100,
      render: (val) => sessionTypeLabel[String(val)] ?? String(val),
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 90,
      render: (_, record) => {
        const cfg = statusConfig[record.status] ?? { badge: 'default', label: record.status };
        return <Badge status={cfg.badge} text={cfg.label} />;
      },
    },
    {
      title: '用户',
      dataIndex: 'ownerUserId',
      ellipsis: true,
      width: 120,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      valueType: 'dateTime',
      width: 170,
      render: (_, record) => new Date(record.createdAt).toLocaleString('zh-CN'),
    },
    {
      title: '最近活跃',
      dataIndex: 'lastActiveAt',
      valueType: 'dateTime',
      width: 170,
      render: (_, record) => new Date(record.lastActiveAt).toLocaleString('zh-CN'),
    },
  ];

  return (
    <PageContainer
      header={{
        title: '会话记录',
        subTitle: '查看所有工作空间的历史会话',
        extra: (
          <Select
            placeholder="筛选工作空间"
            allowClear
            style={{ width: 200 }}
            onChange={(val) => setSelectedWorkspace(val)}
            options={[]}
          />
        ),
      }}
    >
      <ProTable<SessionRecord>
        rowKey="sessionId"
        columns={columns}
        request={async () => {
          const data = await listSessions(selectedWorkspace);
          return { data, success: true, total: data.length };
        }}
        params={{ workspaceId: selectedWorkspace }}
        search={false}
        pagination={{ pageSize: 20 }}
        options={{ reload: true, density: true }}
        cardBordered
        scroll={{ x: 1200 }}
      />
    </PageContainer>
  );
};

export default SessionPage;
