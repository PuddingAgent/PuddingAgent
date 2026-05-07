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
  Divider,
  Form,
  Popconfirm,
  Space,
  Tag,
  Badge,
  Typography,
  Select,
  message,
} from 'antd';
import { PlusOutlined, EditOutlined, DeleteOutlined } from '@ant-design/icons';
import React, { useRef, useState, useEffect } from 'react';
import { history } from '@umijs/max';
import {
  listWorkspaces,
  listGlobalAgentTemplates,
  listWorkspaceAgentTemplates,
  createWorkspaceAgentTemplate,
  updateWorkspaceAgentTemplate,
  deleteWorkspaceAgentTemplate,
  listLlmProviders,
  listLlmModels,
  listCapabilities,
  type WorkspaceDefinition,
  type WorkspaceAgentTemplateDto,
  type GlobalAgentTemplateDto,
  type UpsertWorkspaceAgentTemplateRequest,
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

const WorkspaceAgentTemplatePage: React.FC = () => {
  const tableRef = useRef<ActionType | null>(null);
  const [filterWorkspaceId, setFilterWorkspaceId] = useState<string | undefined>();
  const [workspaces, setWorkspaces] = useState<WorkspaceDefinition[]>([]);
  const [globalTemplates, setGlobalTemplates] = useState<GlobalAgentTemplateDto[]>([]);
  const [providers, setProviders] = useState<LlmProviderDto[]>([]);
  const [models, setModels] = useState<LlmModelDto[]>([]);
  const [capabilities, setCapabilities] = useState<CapabilityDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [formDrawer, setFormDrawer] = useState(false);
  const [editItem, setEditItem] = useState<WorkspaceAgentTemplateDto | null>(null);
  const [form] = Form.useForm<UpsertWorkspaceAgentTemplateRequest>();

  useEffect(() => {
    history.replace('/workspace');
  }, []);

  return (
    <PageContainer
      title="页面已迁移"
      subTitle="场景模板管理已合并到场景详情页（模板管理 Tab）。"
      extra={(
        <Button type="primary" onClick={() => history.push('/workspace')}>
          前往场景设置
        </Button>
      )}
    />
  );

  const handleProviderChange = async (providerId: string) => {
    form.setFieldValue('preferredModelId', undefined);
    if (!providerId) { setModels([]); return; }
    setLoadingModels(true);
    try {
      const ms = await listLlmModels(providerId);
      setModels(ms.filter((m) => !m.isDeprecated));
    } finally {
      setLoadingModels(false);
    }
  };

  const handleGlobalTemplateChange = (templateId: string) => {
    if (!templateId) return;
    const tpl = globalTemplates.find((t) => t.templateId === templateId);
    if (!tpl) return;
    // 用全局模板预填字段（用户可覆盖）
    form.setFieldsValue({
      avatarEmoji: tpl.avatarEmoji,
      role: tpl.role,
      systemPrompt: tpl.systemPrompt,
      userPromptTemplate: tpl.userPromptTemplate,
      personaPrompt: tpl.personaPrompt,
      toolsDescription: tpl.toolsDescription,
      bootstrapTemplate: tpl.bootstrapTemplate,
      preferredProviderId: tpl.preferredProviderId,
      preferredModelId: tpl.preferredModelId,
      maxContextTokens: tpl.maxContextTokens,
      maxReplyTokens: tpl.maxReplyTokens,
      containerImage: tpl.containerImage,
      selectedCapabilityIds: tpl.selectedCapabilityIds,
    });
    if (tpl.preferredProviderId) handleProviderChange(tpl.preferredProviderId);
  };

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({
      role: 'Service',
      isEnabled: true,
      sortOrder: 100,
      maxContextTokens: 8192,
      maxReplyTokens: 2048,
      workspaceId: filterWorkspaceId,
      selectedCapabilityIds: [],
    });
    setModels([]);
    setFormDrawer(true);
  };

  const openEdit = async (item: WorkspaceAgentTemplateDto) => {
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
      await updateWorkspaceAgentTemplate(editItem.id, values);
      message.success('模板已更新');
    } else {
      await createWorkspaceAgentTemplate(values);
      message.success('模板已创建');
    }
    setFormDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (id: number) => {
    await deleteWorkspaceAgentTemplate(id);
    message.success('模板已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkspaceAgentTemplateDto>[] = [
    {
      title: 'Workspace',
      dataIndex: 'workspaceId',
      width: 160,
      render: (_, r) => {
        const ws = workspaces.find((w) => w.workspaceId === r.workspaceId);
        return (
          <Space direction="vertical" size={0}>
            <Text style={{ fontSize: 12 }}>{ws?.name ?? r.workspaceId}</Text>
            <Text type="secondary" style={{ fontSize: 11 }}>
              {r.workspaceId}
            </Text>
          </Space>
        );
      },
    },
    {
      title: '模板 ID',
      dataIndex: 'templateId',
      copyable: true,
      width: 160,
      ellipsis: true,
    },
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '角色类型',
      dataIndex: 'role',
      width: 120,
      render: (_, r) => <Tag color={roleColorMap[r.role] ?? 'default'}>{r.role}</Tag>,
    },
    {
      title: '继承自',
      width: 150,
      render: (_, r) =>
        r.baseGlobalTemplateId ? (
          <Tag color="geekblue">{r.baseGlobalTemplateId}</Tag>
        ) : (
          <Text type="secondary">—</Text>
        ),
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
      title: '首选模型',
      width: 150,
      ellipsis: true,
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
      title: '系统提示词',
      ellipsis: true,
      render: (_, r) =>
        r.systemPrompt ? (
          <Text type="secondary">
            {r.systemPrompt.slice(0, 60)}
            {r.systemPrompt.length > 60 ? '…' : ''}
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
      width: 90,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(r.id)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer
      title="Workspace Agent 模板"
      subTitle="在 Workspace 内定义和管理专属 Agent 模板"
      extra={
        <Select
          allowClear
          placeholder="筛选 Workspace"
          style={{ width: 220 }}
          options={workspaces.map((w) => ({ label: w.name, value: w.workspaceId }))}
          onChange={(v) => {
            setFilterWorkspaceId(v);
            tableRef.current?.reload();
          }}
        />
      }
    >
      <ProTable<WorkspaceAgentTemplateDto>
        actionRef={tableRef}
        rowKey="id"
        columns={columns}
        request={async () => {
          const data = await listWorkspaceAgentTemplates(filterWorkspaceId);
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
        title={editItem ? '编辑 Workspace Agent 模板' : '创建 Workspace Agent 模板'}
        open={formDrawer}
        width={620}
        onClose={() => setFormDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormSelect
            name="workspaceId"
            label="所属 Workspace"
            rules={[{ required: true }]}
            options={workspaces.map((w) => ({ label: w.name, value: w.workspaceId }))}
            disabled={!!editItem}
          />
          <ProFormText
            name="templateId"
            label="模板 ID"
            rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' }]}
            disabled={!!editItem}
            placeholder="如 my-assistant"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />

          <ProFormSelect
            name="baseGlobalTemplateId"
            label="继承自全局模板（可选）"
            options={globalTemplates.map((t) => ({ label: `${t.name} (${t.templateId})`, value: t.templateId }))}
            placeholder="选择后自动预填字段，可继续编辑覆盖"
            fieldProps={{ onChange: handleGlobalTemplateChange, allowClear: true }}
          />

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
            placeholder="覆盖继承模板的系统提示词，或直接填写…"
          />

          <Divider orientation="left">个性设置</Divider>
          <ProFormText
            name="avatarEmoji"
            label="头像 Emoji"
            placeholder="如 🤖"
            fieldProps={{ maxLength: 8 }}
          />
          <ProFormTextArea
            name="personaPrompt"
            label="人设 / 语气 / 边界（SOUL）"
            rows={4}
            placeholder="定义该 Agent 的表达风格、边界与约束"
          />
          <ProFormTextArea
            name="toolsDescription"
            label="工具使用约定（TOOLS）"
            rows={4}
            placeholder="约定何时调用工具、失败兜底与结果解释方式"
          />
          <ProFormTextArea
            name="bootstrapTemplate"
            label="首次引导模板（BOOTSTRAP）"
            rows={6}
            placeholder="定义首次对话的开场白与引导话术"
          />

          <Divider orientation="left">潜意识模型（记忆探索）</Divider>
          <ProFormText
            name="memoryLlmEndpoint"
            label="潜意识模型 Endpoint"
            placeholder="如 https://api.deepseek.com/v1"
            extra="未配置时使用主聊天模型处理记忆"
          />
          <ProFormText.Password
            name="memoryLlmApiKey"
            label="潜意识模型 ApiKey"
            placeholder="可留空，留空时回退主聊天模型"
          />
          <ProFormText
            name="memoryLlmModelId"
            label="潜意识模型 ModelId"
            placeholder="如 deepseek-chat"
            extra="强烈建议：DeepSeek/Haiku 等轻量模型。用于记忆深度探索，比主模型慢但决定整场对话的上下文方向。"
          />
          <ProFormSelect
            name="memorySearchMode"
            label="记忆搜索模式"
            options={[
              { label: '关闭（仅关键词+标签检索）', value: 'off' },
              { label: '即时（关键词+标签+后台异步探索）', value: 'instant' },
              { label: '深度（同步探索，首次冷启动≤60s，上下文最精准）', value: 'deep' },
            ]}
            initialValue="deep"
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
            placeholder="如 docker.xuanyuan.run/library/ubuntu:latest，留空则继承全局模板或平台默认"
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

export default WorkspaceAgentTemplatePage;
