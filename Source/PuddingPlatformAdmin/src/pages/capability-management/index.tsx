import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormTextArea,
  ProFormSwitch,
  ProFormDigit,
} from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { Button, Card, Col, Drawer, Form, Popconfirm, Radio, Row, Space, Badge, message, Tag, Typography } from 'antd';
import { DeleteOutlined, EditOutlined, PlusOutlined, AppstoreOutlined, TableOutlined, ApiOutlined } from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  listCapabilities,
  createCapability,
  updateCapability,
  deleteCapability,
  type CapabilityDto,
  type UpsertCapabilityRequest,
} from '@/services/platform/api';

const { Text } = Typography;

/** 能力分类颜色：蓝=Query、青=Tool、紫=Memory、橙=Execute、红=Security、绿=Local */
const getCategoryColor = (toolName: string): string => {
  const lower = toolName.toLowerCase();
  if (lower.includes('memory') || lower.includes('memo') || lower.includes('recall') || lower.includes('grep_memory') || lower.includes('save_memory') || lower.includes('manage_memory')) return '#7c3aed'; // Memory → 紫
  if (lower.includes('query') || lower.includes('search') || lower.includes('find') || lower.includes('list')) return '#3b82f6'; // Query → 蓝
  if (lower.includes('execute') || lower.includes('run') || lower.includes('shell') || lower.includes('bash') || lower.includes('terminal')) return '#f97316'; // Execute → 橙
  if (lower.includes('security') || lower.includes('auth') || lower.includes('vault') || lower.includes('key')) return '#ef4444'; // Security → 红
  if (lower.includes('local') || lower.includes('file') || lower.includes('read') || lower.includes('write')) return '#22c55e'; // Local → 绿
  return '#22d3ee'; // 其余 Tool → 青
};

const getCategoryLabel = (toolName: string): string => {
  const lower = toolName.toLowerCase();
  if (lower.includes('memory') || lower.includes('memo') || lower.includes('recall') || lower.includes('grep_memory') || lower.includes('save_memory') || lower.includes('manage_memory')) return 'Memory';
  if (lower.includes('query') || lower.includes('search') || lower.includes('find') || lower.includes('list')) return 'Query';
  if (lower.includes('execute') || lower.includes('run') || lower.includes('shell') || lower.includes('bash') || lower.includes('terminal')) return 'Execute';
  if (lower.includes('security') || lower.includes('auth') || lower.includes('vault') || lower.includes('key')) return 'Security';
  if (lower.includes('local') || lower.includes('file') || lower.includes('read') || lower.includes('write')) return 'Local';
  return 'Tool';
};

type ViewMode = 'card' | 'table';

const CapabilityManagementPage: React.FC = () => {
  const tableRef = useRef<ActionType | undefined>(undefined);
  const [viewMode, setViewMode] = useState<ViewMode>('card');
  const [formDrawer, setFormDrawer] = useState(false);
  const [editItem, setEditItem] = useState<CapabilityDto | null>(null);
  const [form] = Form.useForm<UpsertCapabilityRequest>();

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({
      isEnabled: true,
      sortOrder: 100,
      requiresShellExecution: false,
      requiresFileWrite: false,
      requiresNetworkAccess: false,
      toolParametersJson: '{"type":"object","properties":{},"required":[]}',
    });
    setFormDrawer(true);
  };

  const openEdit = (item: CapabilityDto) => {
    setEditItem(item);
    form.setFieldsValue(item);
    setFormDrawer(true);
  };

  const handleSave = async () => {
    const values = await form.validateFields();
    if (editItem) {
      await updateCapability(editItem.capabilityId, values);
      message.success('能力已更新');
    } else {
      await createCapability(values);
      message.success('能力已创建');
    }
    setFormDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (capabilityId: string) => {
    await deleteCapability(capabilityId);
    message.success('能力已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<CapabilityDto>[] = [
    {
      title: '能力 ID',
      dataIndex: 'capabilityId',
      width: 180,
      copyable: true,
    },
    {
      title: '名称',
      dataIndex: 'name',
      width: 180,
    },
    {
      title: '工具名',
      dataIndex: 'toolName',
      width: 120,
      render: (_, r) => <Text code>{r.toolName}</Text>,
    },
    {
      title: '权限需求',
      width: 220,
      render: (_, r) => (
        <Space wrap>
          {r.requiresShellExecution && <Tag color="volcano">Shell</Tag>}
          {r.requiresFileWrite && <Tag color="blue">FileWrite</Tag>}
          {r.requiresNetworkAccess && <Tag color="purple">Network</Tag>}
          {!r.requiresShellExecution && !r.requiresFileWrite && !r.requiresNetworkAccess && (
            <Text type="secondary">无</Text>
          )}
        </Space>
      ),
    },
    {
      title: '描述',
      ellipsis: true,
      render: (_, r) => <Text type="secondary">{r.description || '—'}</Text>,
    },
    {
      title: '状态',
      width: 90,
      render: (_, r) =>
        r.isEnabled ? <Badge status="processing" text="启用" /> : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除该能力？" onConfirm={() => handleDelete(r.capabilityId)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer title="能力管理" subTitle="注册并管理可供 Agent 模板选择的能力（工具）">
      {viewMode === 'card' ? (
        <ProTable<CapabilityDto>
          actionRef={tableRef}
          rowKey="id"
          columns={[]}
          request={async () => {
            const data = await listCapabilities();
            return { data, success: true };
          }}
          search={false}
          toolBarRender={() => [
            <Radio.Group
              key="viewToggle"
              value={viewMode}
              onChange={(e) => setViewMode(e.target.value)}
              optionType="button"
              buttonStyle="solid"
              size="small"
              style={{ marginRight: 8 }}
            >
              <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
              <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
            </Radio.Group>,
            <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
              创建能力
            </Button>,
          ]}
          tableRender={(_, tableProps) => {
            const items = (tableProps?.dataSource ?? []) as CapabilityDto[];
            return (
              <Row gutter={[16, 16]} style={{ padding: '0 0 16px' }}>
                {items.map((item) => {
                  const catColor = getCategoryColor(item.toolName);
                  const catLabel = getCategoryLabel(item.toolName);
                  return (
                    <Col xs={24} sm={12} lg={8} xl={6} key={item.id}>
                      <Card
                        hoverable
                        size="small"
                        style={{
                          borderRadius: 14,
                          borderLeft: `4px solid ${catColor}`,
                          background: 'rgba(250,250,247,0.78)',
                          backdropFilter: 'blur(8px)',
                          boxShadow: '0 2px 16px rgba(0,0,0,0.04)',
                        }}
                        bodyStyle={{ padding: '16px' }}
                      >
                        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 }}>
                          <Space>
                            <ApiOutlined style={{ color: catColor, fontSize: 18 }} />
                            <div>
                              <Text strong style={{ fontSize: 15, display: 'block' }}>{item.name}</Text>
                              <Text code style={{ fontSize: 11 }}>{item.toolName}</Text>
                            </div>
                          </Space>
                          <Tag color={catColor} style={{ fontSize: 11 }}>{catLabel}</Tag>
                        </div>

                        {item.description && (
                          <Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 8 }}>
                            {item.description.length > 50 ? `${item.description.slice(0, 50)}…` : item.description}
                          </Text>
                        )}

                        <Space size={4} wrap style={{ marginBottom: 8 }}>
                          {item.requiresShellExecution && <Tag color="volcano" style={{ fontSize: 10 }}>Shell</Tag>}
                          {item.requiresFileWrite && <Tag color="blue" style={{ fontSize: 10 }}>FileWrite</Tag>}
                          {item.requiresNetworkAccess && <Tag color="purple" style={{ fontSize: 10 }}>Network</Tag>}
                        </Space>

                        <div style={{ borderTop: '1px solid #f0f0f0', paddingTop: 10, display: 'flex', gap: 4 }}>
                          <Badge status={item.isEnabled ? 'processing' : 'default'} text={item.isEnabled ? '启用' : '停用'} />
                          <div style={{ flex: 1 }} />
                          <Button size="small" icon={<EditOutlined />} type="text" onClick={() => openEdit(item)} />
                          <Popconfirm title="确认删除该能力？" onConfirm={() => handleDelete(item.capabilityId)}>
                            <Button size="small" danger icon={<DeleteOutlined />} type="text" />
                          </Popconfirm>
                        </div>
                      </Card>
                    </Col>
                  );
                })}
              </Row>
            );
          }}
        />
      ) : (
        <ProTable<CapabilityDto>
          actionRef={tableRef}
          rowKey="id"
          columns={columns}
          request={async () => {
            const data = await listCapabilities();
            return { data, success: true };
          }}
          search={false}
          toolBarRender={() => [
            <Radio.Group
              key="viewToggle"
              value={viewMode}
              onChange={(e) => setViewMode(e.target.value)}
              optionType="button"
              buttonStyle="solid"
              size="small"
              style={{ marginRight: 8 }}
            >
              <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
              <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
            </Radio.Group>,
            <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
              创建能力
            </Button>,
          ]}
        />
      )}

      <Drawer
        title={editItem ? '编辑能力' : '创建能力'}
        open={formDrawer}
        width={640}
        onClose={() => setFormDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText
            name="capabilityId"
            label="能力 ID"
            rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' }]}
            disabled={!!editItem}
            placeholder="如 cap-bash"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />

          <ProFormText name="toolName" label="工具名（skillId）" rules={[{ required: true }]} placeholder="如 bash" />
          <ProFormTextArea name="toolDescription" label="工具说明" rows={2} />
          <ProFormTextArea
            name="toolParametersJson"
            label="工具参数 Schema(JSON)"
            rows={4}
            placeholder='如 {"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}'
          />

          <Space size="large" align="start">
            <ProFormSwitch name="requiresShellExecution" label="需要 Shell 权限" />
            <ProFormSwitch name="requiresFileWrite" label="需要文件写权限" />
            <ProFormSwitch name="requiresNetworkAccess" label="需要网络权限" />
          </Space>

          <Space size="large">
            <ProFormDigit name="sortOrder" label="排序权重" min={0} />
            <ProFormSwitch name="isEnabled" label="启用" />
          </Space>
        </ProForm>
      </Drawer>
    </PageContainer>
  );
};

export default CapabilityManagementPage;
