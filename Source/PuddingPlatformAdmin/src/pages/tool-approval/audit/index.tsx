import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import { Button, Card, Col, Row, Select, Space, Statistic, Tag, Typography, message } from 'antd';
import { AuditOutlined, CheckCircleOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  getToolApprovalStats,
  listToolApprovalAuditEvents,
  type ToolApprovalAuditEventDto,
  type ToolApprovalStatsDto,
} from '@/services/platform/api';

const EVENT_OPTIONS = [
  { label: '全部事件', value: '' },
  { label: '工单提交', value: 'ticket_submitted' },
  { label: '工单批准', value: 'ticket_approved' },
  { label: '工单拒绝', value: 'ticket_denied' },
  { label: '需要人工', value: 'ticket_need_human' },
  { label: '隐式批准', value: 'implicit_approved' },
  { label: '隐式拒绝', value: 'implicit_denied' },
  { label: '白名单命中', value: 'allowlist_hit' },
  { label: '规则创建', value: 'allowlist_rule_created' },
  { label: '规则更新', value: 'allowlist_rule_updated' },
  { label: '规则禁用', value: 'allowlist_rule_disabled' },
];

const eventColor = (eventType: string) => {
  if (eventType.includes('approved') || eventType.includes('hit')) return 'success';
  if (eventType.includes('denied') || eventType.includes('disabled')) return 'error';
  if (eventType.includes('human')) return 'warning';
  return 'processing';
};

const formatTime = (value?: string) => (value ? new Date(value).toLocaleString() : '-');

const emptyStats: ToolApprovalStatsDto = {
  ticketSubmittedCount: 0,
  ticketApprovedCount: 0,
  ticketDeniedCount: 0,
  ticketNeedHumanCount: 0,
  implicitApprovedCount: 0,
  implicitDeniedCount: 0,
  allowlistHitCount: 0,
  allowlistRuleCount: 0,
  enabledAllowlistRuleCount: 0,
  builtInAllowlistRuleCount: 0,
  dynamicAllowlistRuleCount: 0,
};

const ToolApprovalAuditPage: React.FC = () => {
  const [stats, setStats] = useState<ToolApprovalStatsDto>(emptyStats);
  const [events, setEvents] = useState<ToolApprovalAuditEventDto[]>([]);
  const [eventType, setEventType] = useState('');
  const [loading, setLoading] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [nextStats, nextEvents] = await Promise.all([
        getToolApprovalStats(),
        listToolApprovalAuditEvents({ eventType: eventType || undefined, limit: 300 }),
      ]);
      setStats(nextStats);
      setEvents(nextEvents.items || []);
    } catch {
      message.error('加载审批审计数据失败');
    } finally {
      setLoading(false);
    }
  }, [eventType]);

  useEffect(() => {
    load();
  }, [load]);

  const columns = useMemo<ProColumns<ToolApprovalAuditEventDto>[]>(() => [
    {
      title: '时间',
      dataIndex: 'createdAtUtc',
      width: 170,
      render: (_, record) => formatTime(record.createdAtUtc),
    },
    {
      title: '事件',
      dataIndex: 'eventType',
      width: 150,
      render: (_, record) => <Tag color={eventColor(record.eventType)}>{record.eventType}</Tag>,
    },
    {
      title: '范围',
      dataIndex: 'workspaceId',
      width: 210,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{record.workspaceId || '全局'}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.agentInstanceId || record.userId || '-'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '工具',
      dataIndex: 'toolId',
      width: 100,
      render: (_, record) => (record.toolId ? <Tag>{record.toolId}</Tag> : '-'),
    },
    {
      title: '原始命令 / 参数',
      dataIndex: 'command',
      ellipsis: true,
      render: (_, record) => (
        <Space direction="vertical" size={0} style={{ maxWidth: 420 }}>
          <Typography.Text code ellipsis>
            {record.originalCommand || record.command || '-'}
          </Typography.Text>
          {record.originalArgumentsJson || record.argumentsJson ? (
            <Typography.Text type="secondary" ellipsis style={{ fontSize: 12 }}>
              {record.originalArgumentsJson || record.argumentsJson}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: '决策',
      dataIndex: 'decision',
      width: 100,
      render: (_, record) => record.decision ? <Tag>{record.decision}</Tag> : '-',
    },
    {
      title: '来源',
      dataIndex: 'source',
      width: 110,
      render: (_, record) => record.source ? <Tag>{record.source}</Tag> : '-',
    },
    {
      title: '引用',
      dataIndex: 'ticketId',
      width: 210,
      ellipsis: true,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text copyable={record.ticketId ? { text: record.ticketId } : false} ellipsis>
            {record.ticketId || '-'}
          </Typography.Text>
          <Typography.Text
            type="secondary"
            copyable={record.allowlistRuleId ? { text: record.allowlistRuleId } : false}
            ellipsis
            style={{ fontSize: 12 }}
          >
            {record.allowlistRuleId || ''}
          </Typography.Text>
          {typeof record.allowlistRuleHitCount === 'number' ? (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              命中 {record.allowlistRuleHitCount}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: '原因',
      dataIndex: 'reason',
      ellipsis: true,
    },
  ], []);

  return (
    <PageContainer
      title="审批审计"
      extra={[
        <Select
          key="event"
          value={eventType}
          style={{ width: 180 }}
          options={EVENT_OPTIONS}
          onChange={setEventType}
        />,
        <Button key="reload" icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>,
      ]}
    >
      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="工单提交" value={stats.ticketSubmittedCount} prefix={<AuditOutlined />} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="批准" value={stats.ticketApprovedCount} prefix={<CheckCircleOutlined />} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="拒绝" value={stats.ticketDeniedCount} prefix={<StopOutlined />} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="需人工" value={stats.ticketNeedHumanCount} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="隐式批准" value={stats.implicitApprovedCount ?? 0} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="隐式拒绝" value={stats.implicitDeniedCount ?? 0} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic title="白名单放行" value={stats.allowlistHitCount} />
          </Card>
        </Col>
        <Col xs={24} md={8} xl={3}>
          <Card>
            <Statistic
              title="启用规则"
              value={stats.enabledAllowlistRuleCount}
              suffix={`/ ${stats.allowlistRuleCount}`}
            />
          </Card>
        </Col>
      </Row>

      <ProTable<ToolApprovalAuditEventDto>
        rowKey="eventId"
        search={false}
        loading={loading}
        dataSource={events}
        columns={columns}
        pagination={{ pageSize: 20, showSizeChanger: true }}
        scroll={{ x: 1300 }}
      />
    </PageContainer>
  );
};

export default ToolApprovalAuditPage;
