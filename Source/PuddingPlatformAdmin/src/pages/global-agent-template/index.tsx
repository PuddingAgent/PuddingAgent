import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormSelect,
  ProFormSwitch,
  ProFormTextArea,
  ProFormDigit,
} from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Form,
  Popconfirm,
  Space,
  Tag,
  Badge,
  Typography,
  message,
  Alert,
} from 'antd';
import { PlusOutlined, EditOutlined, DeleteOutlined, LockOutlined } from '@ant-design/icons';
import React, { useRef, useState, useEffect } from 'react';
import {
  listGlobalAgentTemplates,
  createGlobalAgentTemplate,
  updateGlobalAgentTemplate,
  deleteGlobalAgentTemplate,
  listLlmProviders,
  listLlmModels,
  listCapabilities,
  type GlobalAgentTemplateDto,
  type UpsertGlobalAgentTemplateRequest,
  type LlmProviderDto,
  type LlmModelDto,
  type CapabilityDto,
} from '@/services/platform/api';

const { Text } = Typography;

const ROLES = [
  { label: '服务型 (Service)', value: 'Service' },
  { label: '任务型 (Task)', value: 'Task' },
  { label: '审计型 (Audit)', value: 'Audit' },
  { label: '自定义 (Custom)', value: 'Custom' },
];

const roleColorMap: Record<string, string> = {
  Service: 'blue',
  Task: 'green',
  Audit: 'orange',
  Custom: 'purple',
};

const GlobalAgentTemplatePage: React.FC = () => {
  const tableRef = useRef<ActionType>();
  const [formDrawer, setFormDrawer] = useState(false);
  const [editItem, setEditItem] = useState<GlobalAgentTemplateDto | null>(null);
  const [form] = Form.useForm<UpsertGlobalAgentTemplateRequest>();
  const [providers, setProviders] = useState<LlmProviderDto[]>([]);
  const [models, setModels] = useState<LlmModelDto[]>([]);
  const [capabilities, setCapabilities] = useState<CapabilityDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);

  useEffect(() => {
    listLlmProviders().then(setProviders).catch(() => {});
    listCapabilities(true).then(setCapabilities).catch(() => {});
  }, []);

  const handleProviderChange = async (providerId: string) => {
    form.setFieldValue('preferredModelId', undefined);
    if (!providerId) {
      setModels([]);
      return;
    }
    setLoadingModels(true);
    try {
      const ms = await listLlmModels(providerId);
      setModels(ms.filter((m) => !m.isDeprecated));
    } finally {
      setLoadingModels(false);
    }
  };

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ role: 'Service', isEnabled: true, sortOrder: 100, maxContextTokens: 8192, maxReplyTokens: 2048, selectedCapabilityIds: [] });
    setModels([]);
    setFormDrawer(true);
  };

  const openEdit = async (item: GlobalAgentTemplateDto) => {
    setEditItem(item);
    if (item.preferredProviderId) {
      const ms = await listLlmModels(item.preferredProviderId);
      setModels(ms.filter((m) => !m.isDeprecated));
    } else {
      setModels([]);
    }
    form.setFieldsValue(item);
    setFormDrawer(true);
  };

  const handleSave = async () => {
    const values = await form.validateFields();
    if (editItem) {
      await updateGlobalAgentTemplate(editItem.templateId, values);
      message.success('模板已更新');
    } else {
      await createGlobalAgentTemplate(values);
      message.success('模板已创建');
    }
    setFormDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (templateId: string) => {
    await deleteGlobalAgentTemplate(templateId);
    message.success('模板已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<GlobalAgentTemplateDto>[] = [
    {
      title: '模板 ID',
      dataIndex: 'templateId',
      copyable: true,
      width: 180,
      ellipsis: true,
    },
    {
      title: '名称',
      dataIndex: 'name',
      render: (_, r) => (
        <Space>
          <span style={{ fontWeight: 500 }}>{r.name}</span>
          {r.isBuiltIn && (
            <Tag icon={<LockOutlined />} color="gold">
              内置
            </Tag>
          )}
        </Space>
      ),
    },
    {
      title: '角色类型',
      dataIndex: 'role',
      width: 120,
      render: (_, r) => <Tag color={roleColorMap[r.role] ?? 'default'}>{r.role}</Tag>,
    },
    {
      title: '能力',
      width: 220,
      render: (_, r) => {
        if (!r.selectedCapabilityIds?.length) return <Text type="secondary">—</Text>;
        return (
          <Space wrap>
            {r.selectedCapabilityIds.map((id) => {
              const cap = capabilities.find((x) => x.capabilityId === id);
              return <Tag key={id}>{cap?.name ?? id}</Tag>;
            })}
          </Space>
        );
      },
    },
    {
      title: '首选模型',
      width: 160,
      render: (_, r) =>
        r.preferredModelId ? (
          <Text code style={{ fontSize: 12 }}>
            {r.preferredModelId}
          </Text>
        ) : (
          <Text type="secondary">平台默认</Text>
        ),
    },
    {
      title: '上下文',
      width: 110,
      render: (_, r) => <Tag>{(r.maxContextTokens / 1000).toFixed(0)}K tokens</Tag>,
    },
    {
      title: '容器镜像',
      width: 200,
      ellipsis: true,
      render: (_, r) =>
        r.containerImage ? (
          <Text code style={{ fontSize: 11 }}>{r.containerImage}</Text>
        ) : (
          <Text type="secondary">平台默认</Text>
        ),
    },
    {
      title: '系统提示词',
      ellipsis: true,
      render: (_, r) =>
        r.systemPrompt ? (
          <Text type="secondary" ellipsis>
            {r.systemPrompt.slice(0, 80)}
            {r.systemPrompt.length > 80 ? '…' : ''}
          </Text>
        ) : (
          <Text type="secondary">—</Text>
        ),
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      width: 80,
      render: (_, r) =>
        r.isEnabled ? <Badge status="processing" text="启用" /> : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          {r.isBuiltIn ? (
            <Popconfirm title="系统内置模板不允许删除" showCancel={false} okText="知道了">
              <Button size="small" danger icon={<DeleteOutlined />} disabled />
            </Popconfirm>
          ) : (
            <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(r.templateId)}>
              <Button size="small" danger icon={<DeleteOutlined />} />
            </Popconfirm>
          )}
        </Space>
      ),
    },
  ];

  return (
    <PageContainer title="全局 Agent 模板" subTitle="配置系统内置的 Agent 提示词与首选模型">
      <ProTable<GlobalAgentTemplateDto>
        actionRef={tableRef}
        rowKey="id"
        columns={columns}
        request={async () => {
          const data = await listGlobalAgentTemplates();
          return { data, success: true };
        }}
        search={false}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            创建模板
          </Button>,
        ]}
      />

      <Drawer
        title={editItem ? '编辑 Agent 模板' : '创建 Agent 模板'}
        open={formDrawer}
        width={600}
        onClose={() => setFormDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        {editItem?.isBuiltIn && (
          <Alert
            style={{ marginBottom: 16 }}
            type="warning"
            message="这是系统内置模板，不允许修改模板 ID，但可以编辑其他字段。"
            showIcon
          />
        )}
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText
            name="templateId"
            label="模板 ID"
            rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' }]}
            disabled={!!editItem}
            placeholder="如 code-reviewer"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormSelect name="role" label="角色类型" options={ROLES} rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />

          <ProFormSelect
            name="selectedCapabilityIds"
            label="能力选择（多选）"
            mode="multiple"
            options={capabilities.map((c) => ({ label: `${c.name} (${c.toolName})`, value: c.capabilityId }))}
            placeholder="选择能力后，Runtime 会在上下文注入对应工具"
          />

          <ProFormTextArea
            name="systemPrompt"
            label="系统 Prompt"
            rows={6}
            placeholder="输入 Agent 的角色定义和行为准则…"
          />
          <ProFormTextArea
            name="userPromptTemplate"
            label="用户 Prompt 模板"
            rows={3}
            placeholder="可选，支持 {{variable}} 占位符"
          />

          <ProFormText
            name="containerImage"
            label="容器镜像"
            placeholder="如 docker.xuanyuan.run/library/ubuntu:latest，留空则使用平台默认"
          />

          <ProFormSelect
            name="preferredProviderId"
            label="首选服务商"
            options={providers.filter((p) => p.isEnabled).map((p) => ({ label: p.name, value: p.providerId }))}
            placeholder="不选则使用平台默认"
            fieldProps={{ onChange: handleProviderChange, allowClear: true }}
          />
          <ProFormSelect
            name="preferredModelId"
            label="首选模型"
            options={models.map((m) => ({
              label: `${m.name} (${(m.maxContextTokens / 1000).toFixed(0)}K)`,
              value: m.modelId,
            }))}
            placeholder="不选则使用服务商默认"
            fieldProps={{ loading: loadingModels, allowClear: true }}
          />

          <Space size="large">
            <ProFormDigit name="maxContextTokens" label="上下文 tokens" min={1024} />
            <ProFormDigit name="maxReplyTokens" label="最大回复 tokens" min={128} />
            <ProFormDigit name="sortOrder" label="排序权重" min={0} />
          </Space>

          <ProFormSwitch name="isEnabled" label="启用" />
        </ProForm>
      </Drawer>
    </PageContainer>
  );
};

export default GlobalAgentTemplatePage;
