import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import {
  Badge,
  Radio,
  Select,
  Space,
  Tag,
  Timeline,
  Typography,
} from 'antd';
import {
  ClockCircleOutlined,
  SwapOutlined,
  ToolOutlined,
  CloseCircleOutlined,
  CheckCircleOutlined,
  TableOutlined,
  FieldTimeOutlined,
} from '@ant-design/icons';
import React, { useEffect, useMemo, useState } from 'react';
import { listSessions, listWorkspaces, type SessionRecord, type SessionStatus, type WorkspaceWithPermDto } from '@/services/platform/api';

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

/** Timeline 节点颜色映射：会话开始灰、Agent切换紫、工具调用青、失败红、完成绿 */
const getTimelineDotColor = (status: SessionStatus): string => {
  switch (status) {
    case 'Active': return '#7c3aed';     // Agent切换 → 紫
    case 'Completed': return '#22c55e';  // 完成 → 绿
    case 'Failed': return '#ef4444';     // 失败 → 红
    case 'Frozen': return '#9ca3af';     // 冻结 → 灰
    case 'Idle': return '#f97316';       // 空闲 → 橙(警告)
    default: return '#6b7280';
  }
};

const getTimelineIcon = (status: SessionStatus): React.ReactNode => {
  switch (status) {
    case 'Active': return <SwapOutlined />;
    case 'Completed': return <CheckCircleOutlined />;
    case 'Failed': return <CloseCircleOutlined />;
    case 'Idle': return <ClockCircleOutlined />;
    default: return <ClockCircleOutlined />;
  }
};

type ViewMode = 'table' | 'timeline';

const SessionPage: React.FC = () => {
  const [viewMode, setViewMode] = useState<ViewMode>('table');
  const [selectedWorkspace, setSelectedWorkspace] = useState<string | undefined>();
  const [selectedAgent, setSelectedAgent] = useState<string | undefined>();
  const [selectedStatus, setSelectedStatus] = useState<SessionStatus | undefined>();
  const [workspaceOptions, setWorkspaceOptions] = useState<{ label: string; value: string }[]>([]);

  useEffect(() => {
    listWorkspaces().then((ws: WorkspaceWithPermDto[]) => {
      setWorkspaceOptions(ws.map((w) => ({ label: w.name, value: w.workspaceId })));
    }).catch(() => {});
  }, []);

  // 表格列定义
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
      title: '场景',
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

  // 筛选器工具栏
  const filterBar = (
    <Space wrap>
      <Select
        placeholder="筛选场景"
        allowClear
        style={{ width: 180 }}
        value={selectedWorkspace}
        onChange={(val) => setSelectedWorkspace(val)}
        options={workspaceOptions}
      />
      <Select
        placeholder="筛选 Agent"
        allowClear
        style={{ width: 180 }}
        value={selectedAgent}
        onChange={(val) => setSelectedAgent(val)}
        options={[]}
      />
      <Select
        placeholder="筛选状态"
        allowClear
        style={{ width: 140 }}
        value={selectedStatus}
        onChange={(val) => setSelectedStatus(val)}
        options={[
          { label: '活跃', value: 'Active' },
          { label: '空闲', value: 'Idle' },
          { label: '已完成', value: 'Completed' },
          { label: '失败', value: 'Failed' },
          { label: '已冻结', value: 'Frozen' },
        ]}
      />
      <Radio.Group
        value={viewMode}
        onChange={(e) => setViewMode(e.target.value)}
        optionType="button"
        buttonStyle="solid"
        size="small"
      >
        <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
        <Radio.Button value="timeline"><FieldTimeOutlined /> 时间线</Radio.Button>
      </Radio.Group>
    </Space>
  );

  return (
    <PageContainer
      header={{
        title: '会话记录',
        subTitle: '查看所有场景的历史会话',
        extra: filterBar,
      }}
    >
      {viewMode === 'table' ? (
        <ProTable<SessionRecord>
          rowKey="sessionId"
          columns={columns}
          request={async () => {
            const data = await listSessions(selectedWorkspace);
            const filtered = data.filter((s) => {
              if (selectedAgent && s.agentTemplateId !== selectedAgent) return false;
              if (selectedStatus && s.status !== selectedStatus) return false;
              return true;
            });
            return { data: filtered, success: true, total: filtered.length };
          }}
          params={{ workspaceId: selectedWorkspace }}
          search={false}
          pagination={{ pageSize: 20 }}
          options={{ reload: true, density: true }}
          cardBordered
          scroll={{ x: 1200 }}
        />
      ) : (
        <ProTable<SessionRecord>
          rowKey="sessionId"
          columns={[]}
          request={async () => {
            const data = await listSessions(selectedWorkspace);
            const filtered = data.filter((s) => {
              if (selectedAgent && s.agentTemplateId !== selectedAgent) return false;
              if (selectedStatus && s.status !== selectedStatus) return false;
              return true;
            });
            return { data: filtered, success: true, total: filtered.length };
          }}
          search={false}
          pagination={{ pageSize: 20 }}
          options={{ reload: true }}
          cardBordered
          tableRender={(_, tableProps) => {
            const items = (tableProps?.dataSource ?? []) as SessionRecord[];
            if (items.length === 0) {
              return (
                <div style={{ textAlign: 'center', padding: 48, color: '#94a3b8' }}>
                  暂无会话记录
                </div>
              );
            }
            return (
              <div style={{ padding: '24px 32px' }}>
                <Timeline
                  mode="left"
                  items={items.map((session) => {
                    const cfg = statusConfig[session.status] ?? { badge: 'default' as const, label: session.status };
                    const dotColor = getTimelineDotColor(session.status);
                    return {
                      color: dotColor,
                      dot: <span style={{ color: dotColor, fontSize: 14 }}>{getTimelineIcon(session.status)}</span>,
                      children: (
                        <div
                          style={{
                            background: 'rgba(250,250,247,0.72)',
                            backdropFilter: 'blur(8px)',
                            borderRadius: 12,
                            padding: '12px 16px',
                            border: `1px solid ${dotColor}22`,
                            marginBottom: 4,
                          }}
                        >
                          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 4 }}>
                            <Text strong style={{ fontSize: 14 }}>
                              {session.title || session.sessionId.slice(0, 12)}
                            </Text>
                            <Badge status={cfg.badge} text={cfg.label} />
                          </div>
                          <Space size={12} wrap style={{ fontSize: 12 }}>
                            <Text type="secondary">
                              <Text code style={{ fontSize: 11 }}>{session.sessionId.slice(0, 16)}…</Text>
                            </Text>
                            <Text type="secondary">场景: {session.workspaceId}</Text>
                            <Text type="secondary">Agent: {session.agentTemplateId}</Text>
                            <Text type="secondary">用户: {session.ownerUserId}</Text>
                            <Text type="secondary">
                              {new Date(session.createdAt).toLocaleString('zh-CN')}
                            </Text>
                          </Space>
                        </div>
                      ),
                    };
                  })}
                />
              </div>
            );
          }}
        />
      )}
    </PageContainer>
  );
};

export default SessionPage;
