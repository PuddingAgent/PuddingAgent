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
import ClassifierHealthBanner from '../components/ClassifierHealthBanner';

// 事件类型取值与文案。
// **单一事实源在后端**：`Source/PuddingCore/Tools/ToolApprovalWire.cs` 的
// `ToolApprovalWire.ToWire(ToolApprovalAuditEventType)`（稳定 snake_case）。后端新增枚举成员时必须同步本表，
// 否则管理 API 的 eventType 筛选（按 wire 名精确比对）对该事件失效。
// （历史事故：后端曾有 13 个事件落到“丢下划线”的兜底名，而本表只列了旧事件，两边都错。）
const EVENT_OPTIONS = [
  { label: '全部事件', value: '' },
  { label: '工单提交', value: 'ticket_submitted' },
  { label: '工单批准', value: 'ticket_approved' },
  { label: '工单拒绝', value: 'ticket_denied' },
  { label: '需要人工', value: 'ticket_need_human' },
  { label: '工单匹配', value: 'ticket_matched' },
  { label: '工单消费', value: 'ticket_consumed' },
  { label: '工单不匹配', value: 'ticket_mismatch' },
  { label: '依赖等待', value: 'ticket_deferred_dependency' },
  { label: '隐式批准', value: 'implicit_approved' },
  { label: '隐式拒绝', value: 'implicit_denied' },
  { label: '白名单命中', value: 'allowlist_hit' },
  { label: '规则创建', value: 'allowlist_rule_created' },
  { label: '规则更新', value: 'allowlist_rule_updated' },
  { label: '规则禁用', value: 'allowlist_rule_disabled' },
  { label: '黑名单规则创建', value: 'denylist_rule_created' },
  { label: '黑名单规则禁用', value: 'denylist_rule_disabled' },
  { label: '规则冲突', value: 'rule_conflict_detected' },
  { label: '定义漂移', value: 'definition_drift_detected' },
  { label: '分类器已裁决', value: 'classifier_invoked' },
  { label: '分类器不可用', value: 'classifier_unavailable' },
  { label: '完全访问：申请', value: 'full_access_requested' },
  { label: '完全访问：授予', value: 'full_access_granted' },
  { label: '完全访问：拒绝', value: 'full_access_denied' },
  { label: '完全访问：到期', value: 'full_access_expired' },
  { label: '完全访问：撤销', value: 'full_access_revoked' },
  { label: '完全访问：放行', value: 'full_access_gate_bypass' },
];

// 配色按语义分组（而非逐个硬编码）：安全相关（绕过闸门/冲突/不可用/拒绝/禁用）一律 error，
// 授予与批准类 success，待定/可恢复类 warning，其余默认。
const eventColor = (eventType: string) => {
  if (
    eventType.includes('bypass') ||
    eventType.includes('conflict') ||
    eventType.includes('unavailable') ||
    eventType.includes('denied') ||
    eventType.includes('disabled')
  )
    return 'error';
  if (eventType.includes('approved') || eventType.includes('hit') || eventType.includes('granted'))
    return 'success';
  if (
    eventType.includes('human') ||
    eventType.includes('deferred') ||
    eventType.includes('expired') ||
    eventType.includes('revoked') ||
    eventType.includes('requested') ||
    eventType.includes('drift')
  )
    return 'warning';
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
      <ClassifierHealthBanner />
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
