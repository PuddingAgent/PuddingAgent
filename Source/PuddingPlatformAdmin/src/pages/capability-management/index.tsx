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
import { Button, Drawer, Form, Popconfirm, Space, Badge, message, Tag, Typography } from 'antd';
import { DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons';
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

const CapabilityManagementPage: React.FC = () => {
  const tableRef = useRef<ActionType | undefined>(undefined);
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
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            创建能力
          </Button>,
        ]}
      />

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
