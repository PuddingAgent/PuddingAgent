import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import {
  Button,
  Form,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Tag,
  Typography,
  message,
} from 'antd';
import { EditOutlined, PlusOutlined, ReloadOutlined, StopOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  createToolApprovalAllowlistRule,
  disableToolApprovalAllowlistRule,
  listToolApprovalAllowlist,
  updateToolApprovalAllowlistRule,
  type ToolApprovalAllowlistRuleDto,
  type ToolApprovalAllowlistRuleMutation,
  type ToolApprovalAllowlistSource,
  type ToolApprovalAllowlistStatus,
} from '@/services/platform/api';

const SOURCE_OPTIONS: { label: string; value: ToolApprovalAllowlistSource }[] = [
  { label: '内置', value: 'built_in' },
  { label: '审计 Agent', value: 'audit_agent' },
  { label: '人工', value: 'human' },
];

const STATUS_OPTIONS: { label: string; value: ToolApprovalAllowlistStatus }[] = [
  { label: '启用', value: 'enabled' },
  { label: '禁用', value: 'disabled' },
];

const sourceColor: Record<ToolApprovalAllowlistSource, string> = {
  built_in: 'blue',
  audit_agent: 'purple',
  human: 'green',
};

const statusColor: Record<ToolApprovalAllowlistStatus, string> = {
  enabled: 'success',
  disabled: 'default',
};

const formatTime = (value?: string) => (value ? new Date(value).toLocaleString() : '-');

const ToolApprovalAllowlistPage: React.FC = () => {
  const [form] = Form.useForm<ToolApprovalAllowlistRuleMutation>();
  const [items, setItems] = useState<ToolApprovalAllowlistRuleDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<ToolApprovalAllowlistRuleDto | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await listToolApprovalAllowlist();
      setItems(result.items || []);
    } catch (error) {
      message.error('加载审批白名单失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
  }, [load]);

  const openCreate = () => {
    setEditing(null);
    form.setFieldsValue({
      toolId: 'shell',
      source: 'human',
      status: 'enabled',
    });
    setModalOpen(true);
  };

  const openEdit = (record: ToolApprovalAllowlistRuleDto) => {
    setEditing(record);
    form.setFieldsValue({
      workspaceId: record.workspaceId,
      toolId: record.toolId,
      command: record.command,
      argumentsJson: record.argumentsJson,
      source: record.source,
      status: record.status,
      approvedByAgentInstanceId: record.approvedByAgentInstanceId,
      approvedByUserId: record.approvedByUserId,
      approvalTicketId: record.approvalTicketId,
      reason: record.reason,
    });
    setModalOpen(true);
  };

  const save = async () => {
    const values = await form.validateFields();
    setSaving(true);
    try {
      if (editing) {
        await updateToolApprovalAllowlistRule(editing.ruleId, values);
        message.success('白名单条目已更新');
      } else {
        await createToolApprovalAllowlistRule(values);
        message.success('白名单条目已创建');
      }
      setModalOpen(false);
      await load();
    } catch (error) {
      message.error('保存白名单条目失败');
    } finally {
      setSaving(false);
    }
  };

  const disableRule = async (record: ToolApprovalAllowlistRuleDto) => {
    try {
      await disableToolApprovalAllowlistRule(record.ruleId);
      message.success('白名单条目已禁用');
      await load();
    } catch (error) {
      message.error('禁用白名单条目失败');
    }
  };

  const columns = useMemo<ProColumns<ToolApprovalAllowlistRuleDto>[]>(() => [
    {
      title: '规则',
      dataIndex: 'ruleId',
      width: 180,
      ellipsis: true,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text copyable={{ text: record.ruleId }} style={{ maxWidth: 180 }} ellipsis>
            {record.ruleId}
          </Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {record.workspaceId || '全局'}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '工具',
      dataIndex: 'toolId',
      width: 110,
      render: (_, record) => <Tag>{record.toolId}</Tag>,
      filters: [...new Set(items.map((item) => item.toolId))].map((toolId) => ({ text: toolId, value: toolId })),
      onFilter: (value, record) => record.toolId === value,
    },
    {
      title: '命令 / 参数',
      dataIndex: 'command',
      ellipsis: true,
      render: (_, record) => (
        <Space direction="vertical" size={0} style={{ maxWidth: 420 }}>
          <Typography.Text code ellipsis>
            {record.command || '-'}
          </Typography.Text>
          {record.argumentsJson ? (
            <Typography.Text type="secondary" ellipsis style={{ fontSize: 12 }}>
              {record.argumentsJson}
            </Typography.Text>
          ) : null}
        </Space>
      ),
    },
    {
      title: '来源',
      dataIndex: 'source',
      width: 110,
      valueEnum: {
        built_in: { text: '内置' },
        audit_agent: { text: '审计 Agent' },
        human: { text: '人工' },
      },
      render: (_, record) => <Tag color={sourceColor[record.source]}>{record.source}</Tag>,
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 90,
      valueEnum: {
        enabled: { text: '启用' },
        disabled: { text: '禁用' },
      },
      render: (_, record) => <Tag color={statusColor[record.status]}>{record.status}</Tag>,
    },
    {
      title: '命中',
      dataIndex: 'hitCount',
      width: 110,
      sorter: (a, b) => a.hitCount - b.hitCount,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <span>{record.hitCount}</span>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {formatTime(record.lastHitAtUtc)}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '原因',
      dataIndex: 'reason',
      ellipsis: true,
    },
    {
      title: '更新时间',
      dataIndex: 'updatedAtUtc',
      width: 170,
      render: (_, record) => formatTime(record.updatedAtUtc || record.createdAtUtc),
    },
    {
      title: '操作',
      valueType: 'option',
      width: 150,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} />
          <Popconfirm
            title="禁用白名单条目"
            description="禁用后不会继续快速放行，但会保留审计记录。"
            onConfirm={() => disableRule(record)}
            disabled={record.status === 'disabled'}
          >
            <Button
              size="small"
              danger
              icon={<StopOutlined />}
              disabled={record.status === 'disabled'}
            />
          </Popconfirm>
        </Space>
      ),
    },
  ], [items]);

  return (
    <PageContainer
      title="审批白名单"
      extra={[
        <Button key="reload" icon={<ReloadOutlined />} onClick={load}>
          刷新
        </Button>,
        <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
          新建条目
        </Button>,
      ]}
    >
      <ProTable<ToolApprovalAllowlistRuleDto>
        rowKey="ruleId"
        search={false}
        loading={loading}
        dataSource={items}
        columns={columns}
        pagination={{ pageSize: 20, showSizeChanger: true }}
        scroll={{ x: 1200 }}
      />

      <Modal
        title={editing ? '编辑白名单条目' : '新建白名单条目'}
        open={modalOpen}
        onCancel={() => setModalOpen(false)}
        onOk={save}
        confirmLoading={saving}
        width={720}
        destroyOnHidden
      >
        <Form form={form} layout="vertical">
          <Space style={{ width: '100%' }} size={16} align="start">
            <Form.Item
              name="toolId"
              label="工具 ID"
              rules={[{ required: true, message: '请输入工具 ID' }]}
              style={{ width: 220 }}
            >
              <Input placeholder="shell" />
            </Form.Item>
            <Form.Item name="workspaceId" label="工作区" style={{ width: 220 }}>
              <Input placeholder="留空表示全局" />
            </Form.Item>
            <Form.Item name="status" label="状态" rules={[{ required: true }]} style={{ width: 140 }}>
              <Select options={STATUS_OPTIONS} />
            </Form.Item>
          </Space>
          <Form.Item name="command" label="命令字符串">
            <Input placeholder="例如 ls -la 或 Get-ChildItem" />
          </Form.Item>
          <Form.Item name="argumentsJson" label="精确参数 JSON">
            <Input.TextArea rows={4} placeholder='例如 {"command":"pwd","shell":"auto"}' />
          </Form.Item>
          <Space style={{ width: '100%' }} size={16} align="start">
            <Form.Item name="source" label="来源" rules={[{ required: true }]} style={{ width: 180 }}>
              <Select options={SOURCE_OPTIONS} />
            </Form.Item>
            <Form.Item name="approvedByAgentInstanceId" label="审计 Agent" style={{ width: 240 }}>
              <Input placeholder="可选" />
            </Form.Item>
            <Form.Item name="approvedByUserId" label="批准用户" style={{ width: 180 }}>
              <Input placeholder="可选" />
            </Form.Item>
          </Space>
          <Form.Item name="approvalTicketId" label="关联工单">
            <Input placeholder="可选" />
          </Form.Item>
          <Form.Item name="reason" label="批准原因">
            <Input.TextArea rows={3} placeholder="为什么可以快速审批这条命令或参数" />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
};

export default ToolApprovalAllowlistPage;
