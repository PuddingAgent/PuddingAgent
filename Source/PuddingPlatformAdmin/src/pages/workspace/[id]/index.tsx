import {
  ArrowLeftOutlined,
  CheckCircleOutlined,
  CloseCircleOutlined,
  CodeOutlined,
  DeleteOutlined,
  EditOutlined,
  MessageOutlined,
  PlusOutlined,
  RobotOutlined,
  SendOutlined,
  SettingOutlined,
  TeamOutlined,
  ThunderboltOutlined,
  ToolOutlined,
  UserAddOutlined,
} from '@ant-design/icons';
import {
  PageContainer,
  ProColumns,
  ProForm,
  ProFormDigit,
  ProFormSelect,
  ProFormSwitch,
  ProFormText,
  ProFormTextArea,
  ProTable,
  type ActionType,
} from '@ant-design/pro-components';
import {
  Alert,
  App,
  Badge,
  Button,
  Collapse,
  Descriptions,
  Divider,
  Drawer,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Spin,
  Table,
  Tag,
  Tabs,
  Typography,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { history, useLocation, useParams } from '@umijs/max';
import {
  addWorkspaceMember,
  createWorkspaceAgent,
  createWorkspaceAgentTemplate,
  createWorkspaceChannel,
  createWorkspaceSkill,
  createKnowledgeBase,
  createWorkflow,
  deleteWorkspaceAgent,
  deleteWorkspaceAgentTemplate,
  deleteWorkspaceChannel,
  deleteWorkspaceSkill,
  deleteKnowledgeBase,
  deleteWorkflow,
  getWorkspace,
  listGlobalAgentTemplates,
  listKnowledgeBases,
  listLlmModels,
  listLlmProviders,
  listUsers,
  listWorkspaceAgentTemplates,
  listWorkspaceAgents,
  listWorkspaceChannels,
  listWorkspaceMembers,
  listWorkspaceSkills,
  listP2pPeers,
  listWorkflows,
  removeWorkspaceMember,
  sendAdminChatMessage,
  updateWorkspace,
  updateWorkspaceAgent,
  updateWorkspaceAgentTemplate,
  updateWorkspaceChannel,
  updateWorkspaceSkill,
  updateKnowledgeBase,
  updateWorkflow,
  type AddWorkspaceMemberRequest,
  type AdminChatRequest,
  type AppUserDto,
  type CreateWorkspaceAgentRequest,
  type GlobalAgentTemplateDto,
  type KnowledgeBaseDto,
  type LlmModelDto,
  type LlmProviderDto,
  type PeerNodeDto,
  type TurnStep,
  type UpsertKnowledgeBaseRequest,
  type UpsertWorkflowRequest,
  type UpsertWorkspaceChannelRequest,
  type UpsertWorkspaceSkillRequest,
  type UpdateWorkspaceAgentRequest,
  type UpdateWorkspaceRequest,
  type UpsertWorkspaceAgentTemplateRequest,
  type WorkspaceAccessPolicy,
  type WorkspaceAgentDto,
  type WorkspaceAgentTemplateDto,
  type WorkspaceChannelDto,
  type WorkflowDto,
  type WorkspaceMemberDto,
  type WorkspaceSkillDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

const { Text } = Typography;

const POLICY_COLOURS: Record<WorkspaceAccessPolicy, string> = {
  None: 'default',
  ReadOnly: 'blue',
  Write: 'green',
  Manage: 'red',
};

const ACCESS_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
  { label: '只读', value: 'ReadOnly' },
  { label: '可读写', value: 'Write' },
  { label: '可管理', value: 'Manage' },
];

// ─── Agent 模板 Tab ───────────────────────────────────────────────────────────

const AGENT_ROLES = [
  { label: '服务型 (Service)', value: 'Service' },
  { label: '任务型 (Task)', value: 'Task' },
  { label: '审计型 (Audit)', value: 'Audit' },
  { label: '自定义 (Custom)', value: 'Custom' },
];

const ROLE_COLORS: Record<string, string> = {
  Service: 'blue', Task: 'green', Audit: 'orange', Custom: 'purple',
};

const AgentTemplatesTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<WorkspaceAgentTemplateDto | null>(null);
  const [globalTemplates, setGlobalTemplates] = useState<GlobalAgentTemplateDto[]>([]);
  const [providers, setProviders] = useState<LlmProviderDto[]>([]);
  const [models, setModels] = useState<LlmModelDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [form] = Form.useForm<UpsertWorkspaceAgentTemplateRequest>();

  useEffect(() => {
    listGlobalAgentTemplates().then(setGlobalTemplates).catch(() => {});
    listLlmProviders().then(setProviders).catch(() => {});
  }, []);

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
    });
    if (tpl.preferredProviderId) handleProviderChange(tpl.preferredProviderId);
  };

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({
      workspaceId,
      role: 'Service',
      isEnabled: true,
      sortOrder: 100,
      maxContextTokens: 8192,
      maxReplyTokens: 2048,
    });
    setModels([]);
    setDrawerOpen(true);
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
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateWorkspaceAgentTemplate(editItem.id, values);
        message.success('模板已更新');
      } else {
        await createWorkspaceAgentTemplate(values);
        message.success('模板已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败，请稍后重试');
    }
  };

  const handleDelete = async (id: number) => {
    await deleteWorkspaceAgentTemplate(id);
    message.success('模板已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkspaceAgentTemplateDto>[] = [
    { title: '模板 ID', dataIndex: 'templateId', copyable: true, width: 160, ellipsis: true },
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '角色类型',
      dataIndex: 'role',
      width: 120,
      render: (_, r) => <Tag color={ROLE_COLORS[r.role] ?? 'default'}>{r.role}</Tag>,
    },
    {
      title: '继承自全局模板',
      width: 180,
      ellipsis: true,
      render: (_, r) =>
        r.baseGlobalTemplateId ? (
          <Tag color="geekblue">{r.baseGlobalTemplateId}</Tag>
        ) : (
          <Text type="secondary">—</Text>
        ),
    },
    {
      title: '首选模型',
      width: 150,
      ellipsis: true,
      render: (_, r) =>
        r.preferredModelId ? (
          <Text code style={{ fontSize: 12 }}>{r.preferredModelId}</Text>
        ) : (
          <Text type="secondary">平台默认</Text>
        ),
    },
    {
      title: '系统提示词',
      ellipsis: true,
      render: (_, r) =>
        r.systemPrompt ? (
          <Text type="secondary">{r.systemPrompt.slice(0, 50)}{r.systemPrompt.length > 50 ? '…' : ''}</Text>
        ) : (
          <Text type="secondary">—</Text>
        ),
    },
    {
      title: '状态',
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
          <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(r.id)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <>
      <ProTable<WorkspaceAgentTemplateDto>
        actionRef={tableRef}
        rowKey="id"
        columns={columns}
        request={async () => {
          const data = await listWorkspaceAgentTemplates(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无 Agent 模板，点击上方按钮创建"
            />
          ),
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            创建模板
          </Button>,
        ]}
      />

      <Drawer
        title={editItem ? '编辑 Agent 模板' : '创建 Agent 模板'}
        open={drawerOpen}
        width={620}
        onClose={() => setDrawerOpen(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        <ProForm form={form} submitter={false} layout="vertical">
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
          <ProFormSelect name="role" label="角色类型" options={AGENT_ROLES} rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
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

          <ProFormTextArea
            name="userPromptTemplate"
            label="用户 Prompt 模板"
            rows={3}
            placeholder="可选，支持 {{variable}} 占位符"
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
    </>
  );
};

// ─── 工作流 Tab ───────────────────────────────────────────────────────────────

const WORKFLOW_STATUSES = [
  { label: '草稿', value: 'Draft' },
  { label: '激活', value: 'Active' },
  { label: '暂停', value: 'Paused' },
];

const STATUS_COLORS: Record<string, string> = {
  Draft: 'default', Active: 'green', Paused: 'orange',
};

const WorkflowsTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const isLimited = true;
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<WorkflowDto | null>(null);
  const [form] = Form.useForm<UpsertWorkflowRequest>();

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ status: 'Draft', isEnabled: true });
    setDrawerOpen(true);
  };

  const openEdit = (item: WorkflowDto) => {
    setEditItem(item);
    form.setFieldsValue(item);
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateWorkflow(workspaceId, editItem.workflowId, values);
        message.success('工作流已更新');
      } else {
        await createWorkflow(workspaceId, values);
        message.success('工作流已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败');
    }
  };

  const handleDelete = async (workflowId: string) => {
    await deleteWorkflow(workspaceId, workflowId);
    message.success('工作流已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkflowDto>[] = [
    { title: '名称', dataIndex: 'name', width: 160 },
    {
      title: '状态',
      dataIndex: 'status',
      width: 100,
      render: (_, r) => <Tag color={STATUS_COLORS[r.status] ?? 'default'}>{r.status}</Tag>,
    },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '启用',
      dataIndex: 'isEnabled',
      width: 80,
      render: (_, r) => r.isEnabled
        ? <Badge status="processing" text="启用" />
        : <Badge status="default" text="停用" />,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      width: 160,
      render: (v) => new Date(v as string).toLocaleString('zh-CN'),
    },
  ];

  if (!isLimited) {
    columns.push({
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除？" onConfirm={() => handleDelete(r.workflowId)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    });
  }

  return (
    <>
      <Alert
        showIcon
        type="info"
        message="工作流功能将在后续版本开放"
        description="V1 阶段暂不支持在场景内创建和编辑工作流。"
        style={{ marginBottom: 12 }}
      />
      <ProTable<WorkflowDto>
        actionRef={tableRef}
        rowKey="workflowId"
        columns={columns}
        request={async () => {
          const data = await listWorkflows(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无工作流，功能将在后续版本开放"
            />
          ),
        }}
        toolBarRender={isLimited ? false : () => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新建工作流</Button>,
        ]}
      />
      {!isLimited && (
        <Drawer
          title={editItem ? '编辑工作流' : '新建工作流'}
          open={drawerOpen}
          width={560}
          onClose={() => setDrawerOpen(false)}
          extra={<Button type="primary" onClick={handleSave}>保存</Button>}
        >
          <ProForm form={form} submitter={false} layout="vertical">
            <ProFormText name="name" label="名称" rules={[{ required: true }]} />
            <ProFormSelect name="status" label="状态" options={WORKFLOW_STATUSES} rules={[{ required: true }]} />
            <ProFormTextArea name="description" label="描述" rows={2} />
            <ProFormTextArea name="definitionJson" label="工作流定义 JSON" rows={8} placeholder='{"nodes": [], "edges": []}' />
            <ProFormSwitch name="isEnabled" label="启用" />
          </ProForm>
        </Drawer>
      )}
    </>
  );
};

// ─── Agent 管理 Tab ───────────────────────────────────────────────────────────

const WorkspaceAgentsTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<WorkspaceAgentDto | null>(null);
  const [providers, setProviders] = useState<LlmProviderDto[]>([]);
  const [models, setModels] = useState<LlmModelDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [globalTemplates, setGlobalTemplates] = useState<GlobalAgentTemplateDto[]>([]);
  const [form] = Form.useForm<CreateWorkspaceAgentRequest & UpdateWorkspaceAgentRequest>();

  useEffect(() => {
    listLlmProviders().then(setProviders).catch(() => {});
    listGlobalAgentTemplates().then(setGlobalTemplates).catch(() => {});
  }, [workspaceId]);

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

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ isEnabled: true });
    setModels([]);
    setDrawerOpen(true);
  };

  const openEdit = async (item: WorkspaceAgentDto) => {
    setEditItem(item);
    if (item.preferredProviderId) {
      const ms = await listLlmModels(item.preferredProviderId);
      setModels(ms.filter((m) => !m.isDeprecated));
    } else {
      setModels([]);
    }
    form.setFieldsValue({ ...item });
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateWorkspaceAgent(workspaceId, editItem.agentId, values as UpdateWorkspaceAgentRequest);
        message.success('Agent 已更新');
      } else {
        await createWorkspaceAgent(workspaceId, values as CreateWorkspaceAgentRequest);
        message.success('Agent 已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败');
    }
  };

  const handleDelete = async (agentId: string) => {
    await deleteWorkspaceAgent(workspaceId, agentId);
    message.success('Agent 已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkspaceAgentDto>[] = [
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '来源模板',
      dataIndex: 'sourceTemplateId',
      ellipsis: true,
      render: (_, r) => r.sourceTemplateId
        ? <Tag color="geekblue">{r.sourceTemplateId}</Tag>
        : <Text type="secondary">—</Text>,
    },
    {
      title: '首选模型',
      width: 140,
      ellipsis: true,
      render: (_, r) => r.preferredModelId
        ? <Text code style={{ fontSize: 12 }}>{r.preferredModelId}</Text>
        : <Text type="secondary">默认</Text>,
    },
    {
      title: '状态',
      width: 90,
      render: (_, r) => r.isFrozen
        ? <Badge status="error" text="冻结" />
        : r.isEnabled
          ? <Badge status="processing" text="运行中" />
          : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除该 Agent？" onConfirm={() => handleDelete(r.agentId)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <>
      <ProTable<WorkspaceAgentDto>
        actionRef={tableRef}
        rowKey="agentId"
        columns={columns}
        request={async () => {
          const data = await listWorkspaceAgents(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无 Agent，点击上方按钮新增"
            />
          ),
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新增 Agent</Button>,
        ]}
      />
      <Drawer
        title={editItem ? '编辑 Agent' : '新增 Agent'}
        open={drawerOpen}
        width={580}
        onClose={() => setDrawerOpen(false)}
        extra={<Button type="primary" onClick={handleSave}>保存</Button>}
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText name="name" label="Agent 名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
          <ProFormSelect
            name="sourceTemplateId"
            label="来源模板（可选）"
            placeholder="选择已有模板作为基础配置"
            fieldProps={{
              allowClear: true,
              options: globalTemplates.map((t) => ({
                label: `${t.name} (${t.templateId})`,
                value: `global:${t.templateId}`,
              })),
            }}
          />
          <ProFormTextArea name="systemPromptOverride" label="覆盖系统提示词" rows={5} placeholder="留空则使用模板的系统提示词" />
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
            options={models.map((m) => ({ label: `${m.name} (${(m.maxContextTokens / 1000).toFixed(0)}K)`, value: m.modelId }))}
            placeholder="不选则使用服务商默认"
            fieldProps={{ loading: loadingModels, allowClear: true }}
          />
          {editItem && <ProFormSwitch name="isEnabled" label="启用" />}
        </ProForm>
      </Drawer>
    </>
  );
};

// ─── Chat 界面 Tab ─────────────────────────────────────────────────────────────

interface ChatMessage {
  role: 'user' | 'assistant' | 'tool-trace';
  content: string;
  timestamp: string;
  /** 工具调用步骤（仅 role=tool-trace 时有值）。 */
  turns?: TurnStep[];
}

/** 工具调用步骤卡片（内嵌在消息列表中）。 */
const ToolTraceCard: React.FC<{ turns: TurnStep[] }> = ({ turns }) => {
  // 只展示有工具调用的轮次，纯 LLM 推理轮不单独列出
  const toolTurns = turns.filter((t) => t.toolName);
  const totalRounds = turns.length;

  if (toolTurns.length === 0) return null;

  const items = toolTurns.map((t) => {
    const success = t.toolSuccess !== false;
    const icon = success
      ? <CheckCircleOutlined style={{ color: '#52c41a' }} />
      : <CloseCircleOutlined style={{ color: '#ff4d4f' }} />;

    let argsPreview = '';
    if (t.toolArgs) {
      try {
        const parsed = JSON.parse(t.toolArgs);
        const cmd = parsed.command ?? parsed.input ?? parsed.query ?? Object.values(parsed)[0];
        argsPreview = typeof cmd === 'string' ? cmd.slice(0, 120) : t.toolArgs.slice(0, 120);
      } catch {
        argsPreview = t.toolArgs.slice(0, 120);
      }
    }

    const label = (
      <Space size={6}>
        {icon}
        <Text strong style={{ fontSize: 12 }}>{t.toolName}</Text>
        {argsPreview && (
          <Text type="secondary" style={{ fontSize: 11, fontFamily: 'monospace' }}>
            {argsPreview}{argsPreview.length >= 120 ? '…' : ''}
          </Text>
        )}
        {t.durationMs != null && (
          <Text type="secondary" style={{ fontSize: 11 }}>{t.durationMs}ms</Text>
        )}
      </Space>
    );

    const detailItems = [
      t.toolArgs && { key: 'args', label: '参数', children: <pre style={{ margin: 0, fontSize: 11, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{(() => { try { return JSON.stringify(JSON.parse(t.toolArgs), null, 2); } catch { return t.toolArgs; } })()}</pre> },
      t.toolError && { key: 'err', label: '错误', children: <Text type="danger" style={{ fontSize: 11 }}>{t.toolError}</Text> },
      t.messageSummary && { key: 'msg', label: 'LLM 输出', children: <Text style={{ fontSize: 11 }}>{t.messageSummary}</Text> },
    ].filter(Boolean) as { key: string; label: string; children: React.ReactNode }[];

    return {
      key: String(t.round),
      label,
      children: detailItems.length > 0
        ? <Descriptions column={1} size="small" items={detailItems} />
        : <Text type="secondary" style={{ fontSize: 11 }}>无详情</Text>,
    };
  });

  return (
    <div style={{ maxWidth: '72%', marginBottom: 4 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
        <ToolOutlined style={{ color: '#faad14', fontSize: 13 }} />
        <Text type="secondary" style={{ fontSize: 11 }}>
          Agent 思考过程 · {totalRounds} 轮推理 · {toolTurns.length} 次工具调用
        </Text>
      </div>
      <Collapse
        size="small"
        ghost
        style={{ background: '#fffbe6', border: '1px solid #ffe58f', borderRadius: 6, fontSize: 12 }}
        items={items}
      />
    </div>
  );
};

const ChatTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const { message } = App.useApp();
  const [agents, setAgents] = useState<WorkspaceAgentDto[]>([]);
  const [selectedAgentId, setSelectedAgentId] = useState<string | undefined>(undefined);
  const [chatMessages, setChatMessages] = useState<ChatMessage[]>([]);
  const [inputText, setInputText] = useState('');
  const [sessionId, setSessionId] = useState<string | undefined>(undefined);
  const [sending, setSending] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    listWorkspaceAgents(workspaceId).then(setAgents).catch(() => {});
  }, [workspaceId]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [chatMessages]);

  const handleSend = async () => {
    const text = inputText.trim();
    if (!text) return;
    setInputText('');
    setChatMessages((prev) => [
      ...prev,
      { role: 'user', content: text, timestamp: new Date().toISOString() },
    ]);
    setSending(true);
    try {
      const req: AdminChatRequest = {
        messageText: text,
        sessionId,
        agentId: selectedAgentId,
      };
      const resp = await sendAdminChatMessage(workspaceId, req);
      setSessionId(resp.sessionId);
      if (resp.isSuccess && resp.reply) {
        setChatMessages((prev) => {
          const next: ChatMessage[] = [...prev];
          // 如果有工具调用步骤，先插入 trace 卡片，再插入 assistant 回复
          if (resp.turnSteps && resp.turnSteps.length > 0) {
            next.push({
              role: 'tool-trace',
              content: '',
              timestamp: new Date().toISOString(),
              turns: resp.turnSteps,
            });
          }
          next.push({ role: 'assistant', content: resp.reply!, timestamp: new Date().toISOString() });
          return next;
        });
      } else {
        message.error(resp.errorMessage ?? '发送失败，Agent 未返回回复');
      }
    } catch {
      message.error('发送请求失败，请检查 Controller 服务状态');
    } finally {
      setSending(false);
    }
  };

  const handleClearSession = () => {
    setChatMessages([]);
    setSessionId(undefined);
  };

  const getAgentOptionLabel = (agent: WorkspaceAgentDto) => {
    const agentName = agent.name?.trim() || 'Agent';
    const avatarEmoji = agent.avatarEmoji?.trim();
    if (avatarEmoji) {
      return `${avatarEmoji} ${agentName}`;
    }

    return (
      <Space size={6}>
        <RobotOutlined />
        <span>{agentName}</span>
      </Space>
    );
  };

  // 全局 Ctrl+Enter 快捷键：监听 pudding:chat:send 自定义事件触发发送
  const handleSendRef = useRef(handleSend);
  handleSendRef.current = handleSend;

  useEffect(() => {
    const handler = () => handleSendRef.current();
    window.addEventListener('pudding:chat:send', handler);
    return () => window.removeEventListener('pudding:chat:send', handler);
  }, []);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '520px', background: '#fff', border: '1px solid #f0f0f0', borderRadius: 8 }}>
      {/* Toolbar */}
      <div style={{ padding: '8px 16px', borderBottom: '1px solid #f0f0f0', display: 'flex', alignItems: 'center', gap: 12 }}>
        <Text type="secondary" style={{ flexShrink: 0 }}>对话 Agent：</Text>
        <Select
          style={{ width: 220 }}
          placeholder="选择 Agent（不选则使用场景默认）"
          allowClear
          value={selectedAgentId}
          onChange={(v) => { setSelectedAgentId(v); handleClearSession(); }}
          options={agents.filter((a) => !a.isFrozen && a.isEnabled).map((a) => ({
            label: getAgentOptionLabel(a), value: a.agentId,
          }))}
        />
        {sessionId && (
          <Text type="secondary" style={{ fontSize: 12, flex: 1 }}>
            会话 ID：<Text code style={{ fontSize: 11 }}>{sessionId}</Text>
          </Text>
        )}
        <Button size="small" onClick={handleClearSession}>清空会话</Button>
      </div>

      {/* Messages */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '16px', display: 'flex', flexDirection: 'column', gap: 12 }}>
        {chatMessages.length === 0 && (
          <div style={{ textAlign: 'center', marginTop: 60 }}>
            <MessageOutlined style={{ fontSize: 36, color: '#d9d9d9' }} />
            <br />
            <Text type="secondary" style={{ marginTop: 8, display: 'block' }}>
              选择 Agent 后开始对话
            </Text>
          </div>
        )}
        {chatMessages.map((m, idx) => {
          // tool-trace 渲染工具调用卡片
          if (m.role === 'tool-trace') {
            return (
              <div key={idx} style={{ display: 'flex', justifyContent: 'flex-start' }}>
                {m.turns && <ToolTraceCard turns={m.turns} />}
              </div>
            );
          }
          return (
            <div key={idx} style={{ display: 'flex', justifyContent: m.role === 'user' ? 'flex-end' : 'flex-start' }}>
              <div
                style={{
                  maxWidth: '72%',
                  padding: '8px 12px',
                  borderRadius: 8,
                  background: m.role === 'user' ? '#1677ff' : '#f5f5f5',
                  color: m.role === 'user' ? '#fff' : '#000',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-word',
                }}
              >
                {m.content}
              </div>
            </div>
          );
        })}
        {sending && (
          <div style={{ display: 'flex', justifyContent: 'flex-start' }}>
            <div style={{ padding: '8px 12px', borderRadius: 8, background: '#f5f5f5' }}>
              <Spin size="small" /> <Text type="secondary" style={{ marginLeft: 8 }}>Agent 思考中…</Text>
            </div>
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div style={{ padding: '8px 16px', borderTop: '1px solid #f0f0f0', display: 'flex', gap: 8 }}>
        <Input.TextArea
          value={inputText}
          onChange={(e) => setInputText(e.target.value)}
          placeholder="输入消息，Ctrl+Enter 发送"
          autoSize={{ minRows: 1, maxRows: 4 }}
          onKeyDown={(e) => {
            if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); handleSend(); }
          }}
          style={{ flex: 1 }}
        />
        <Button
          type="primary"
          icon={<SendOutlined />}
          loading={sending}
          onClick={handleSend}
          style={{ alignSelf: 'flex-end' }}
        >
          发送
        </Button>
      </div>
    </div>
  );
};

// ─── 知识库管理 Tab ───────────────────────────────────────────────────────────

const KB_TYPES = [
  { label: '向量存储 (VectorStore)', value: 'VectorStore' },
  { label: '知识图谱 (Graph)', value: 'Graph' },
  { label: '文件索引 (FileIndex)', value: 'FileIndex' },
];

const KB_TYPE_COLORS: Record<string, string> = {
  VectorStore: 'blue', Graph: 'purple', FileIndex: 'cyan',
};

const KnowledgeBasesTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const isLimited = true;
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<KnowledgeBaseDto | null>(null);
  const [form] = Form.useForm<UpsertKnowledgeBaseRequest>();

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ kbType: 'VectorStore', isEnabled: true });
    setDrawerOpen(true);
  };

  const openEdit = (item: KnowledgeBaseDto) => {
    setEditItem(item);
    form.setFieldsValue(item);
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateKnowledgeBase(workspaceId, editItem.kbId, values);
        message.success('知识库已更新');
      } else {
        await createKnowledgeBase(workspaceId, values);
        message.success('知识库已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败');
    }
  };

  const handleDelete = async (kbId: string) => {
    await deleteKnowledgeBase(workspaceId, kbId);
    message.success('知识库已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<KnowledgeBaseDto>[] = [
    { title: '名称', dataIndex: 'name', width: 160 },
    {
      title: '类型',
      dataIndex: 'kbType',
      width: 120,
      render: (_, r) => <Tag color={KB_TYPE_COLORS[r.kbType] ?? 'default'}>{r.kbType}</Tag>,
    },
    { title: '文档数', dataIndex: 'documentCount', width: 90 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '启用',
      width: 80,
      render: (_, r) => r.isEnabled
        ? <Badge status="processing" text="启用" />
        : <Badge status="default" text="停用" />,
    },
  ];

  if (!isLimited) {
    columns.push({
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除知识库？" onConfirm={() => handleDelete(r.kbId)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    });
  }

  return (
    <>
      <Alert
        showIcon
        type="info"
        message="知识库功能将在后续版本开放"
        description="V1 阶段暂不支持在场景内创建和编辑知识库。"
        style={{ marginBottom: 12 }}
      />
      <ProTable<KnowledgeBaseDto>
        actionRef={tableRef}
        rowKey="kbId"
        columns={columns}
        request={async () => {
          const data = await listKnowledgeBases(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无知识库，功能将在后续版本开放"
            />
          ),
        }}
        toolBarRender={isLimited ? false : () => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>新建知识库</Button>,
        ]}
      />
      {!isLimited && (
        <Drawer
          title={editItem ? '编辑知识库' : '新建知识库'}
          open={drawerOpen}
          width={480}
          onClose={() => setDrawerOpen(false)}
          extra={<Button type="primary" onClick={handleSave}>保存</Button>}
        >
          <ProForm form={form} submitter={false} layout="vertical">
            <ProFormText name="name" label="名称" rules={[{ required: true }]} />
            <ProFormSelect name="kbType" label="知识库类型" options={KB_TYPES} rules={[{ required: true }]} />
            <ProFormTextArea name="description" label="描述" rows={2} />
            <ProFormSwitch name="isEnabled" label="启用" />
          </ProForm>
        </Drawer>
      )}
    </>
  );
};

// ─── SKILL 库管理 Tab ──────────────────────────────────────────────────────────

const SKILL_TYPES = [
  { label: 'MCP Server', value: 'MCP' },
  { label: '内置工具 (BuiltIn)', value: 'BuiltIn' },
  { label: '自定义脚本 (CustomScript)', value: 'CustomScript' },
  { label: 'HTTP 工具 (HttpTool)', value: 'HttpTool' },
];

const SKILL_TYPE_COLORS: Record<string, string> = {
  MCP: 'magenta', BuiltIn: 'green', CustomScript: 'orange', HttpTool: 'blue',
};

const SkillsTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<WorkspaceSkillDto | null>(null);
  const [skillType, setSkillType] = useState<string>('BuiltIn');
  const [form] = Form.useForm<UpsertWorkspaceSkillRequest>();

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ skillType: 'BuiltIn', isEnabled: true });
    setSkillType('BuiltIn');
    setDrawerOpen(true);
  };

  const openEdit = (item: WorkspaceSkillDto) => {
    setEditItem(item);
    form.setFieldsValue(item);
    setSkillType(item.skillType);
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateWorkspaceSkill(workspaceId, editItem.skillId, values);
        message.success('Skill 已更新');
      } else {
        await createWorkspaceSkill(workspaceId, values);
        message.success('Skill 已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败');
    }
  };

  const handleDelete = async (skillId: string) => {
    await deleteWorkspaceSkill(workspaceId, skillId);
    message.success('Skill 已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkspaceSkillDto>[] = [
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '类型',
      dataIndex: 'skillType',
      width: 120,
      render: (_, r) => <Tag color={SKILL_TYPE_COLORS[r.skillType] ?? 'default'}>{r.skillType}</Tag>,
    },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '启用',
      width: 80,
      render: (_, r) => r.isEnabled
        ? <Badge status="processing" text="启用" />
        : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除 Skill？" onConfirm={() => handleDelete(r.skillId)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <>
      <ProTable<WorkspaceSkillDto>
        actionRef={tableRef}
        rowKey="skillId"
        columns={columns}
        request={async () => {
          const data = await listWorkspaceSkills(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无 Skill，点击上方按钮注册"
            />
          ),
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>注册 Skill</Button>,
        ]}
      />
      <Drawer
        title={editItem ? '编辑 Skill' : '注册 Skill'}
        open={drawerOpen}
        width={500}
        onClose={() => setDrawerOpen(false)}
        extra={<Button type="primary" onClick={handleSave}>保存</Button>}
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormSelect
            name="skillType"
            label="技能类型"
            options={SKILL_TYPES}
            rules={[{ required: true }]}
            fieldProps={{ onChange: (v: string) => { setSkillType(v); form.setFieldValue('configJson', undefined); } }}
          />
          <ProFormTextArea name="description" label="描述" rows={2} />
          <ProFormTextArea
            name="configJson"
            label={skillType === 'MCP' ? 'MCP Server 配置 JSON' : skillType === 'HttpTool' ? 'HTTP 工具配置 JSON' : '配置 JSON（可选）'}
            rows={5}
            placeholder={
              skillType === 'MCP'
                ? '{"serverUrl": "http://localhost:3100/sse", "transport": "sse"}'
                : skillType === 'HttpTool'
                  ? '{"endpoint": "https://api.example.com/tool", "method": "POST"}'
                  : '可选的配置参数 JSON'
            }
          />
          <ProFormSwitch name="isEnabled" label="启用" />
        </ProForm>
      </Drawer>
    </>
  );
};

// ─── 对等节点 Tab ────────────────────────────────────────────────────────────

const PeersTab: React.FC = () => {
  const { message } = App.useApp();
  const [loading, setLoading] = useState(false);
  const [peers, setPeers] = useState<PeerNodeDto[]>([]);

  const loadPeers = async (showError = false) => {
    setLoading(true);
    try {
      const data = await listP2pPeers();
      const sorted = [...data].sort(
        (a, b) => new Date(b.lastSeen).getTime() - new Date(a.lastSeen).getTime(),
      );
      setPeers(sorted);
    } catch {
      if (showError) {
        message.error('加载对等节点失败，请检查 P2P 服务状态');
      }
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadPeers(false);
    const timer = window.setInterval(() => {
      void loadPeers(false);
    }, 5000);

    return () => {
      window.clearInterval(timer);
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <>
      <div style={{ marginBottom: 12, textAlign: 'right' }}>
        <Button onClick={() => loadPeers(true)} loading={loading}>刷新节点</Button>
      </div>
      <Table<PeerNodeDto>
        rowKey="nodeId"
        loading={loading}
        dataSource={peers}
        pagination={false}
        size="small"
        bordered
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无已发现对等节点"
            />
          ),
        }}
        columns={[
          {
            title: '节点名',
            dataIndex: 'displayName',
            render: (value: string, record: PeerNodeDto) => (
              <Space direction="vertical" size={0}>
                <Text>{value || record.nodeId}</Text>
                <Text type="secondary" style={{ fontSize: 12 }}>
                  {record.nodeId}
                </Text>
              </Space>
            ),
          },
          {
            title: '主机',
            dataIndex: 'host',
          },
          {
            title: '端口',
            dataIndex: 'port',
            width: 100,
          },
          {
            title: '最后心跳',
            dataIndex: 'lastSeen',
            render: (value: string) => new Date(value).toLocaleString('zh-CN'),
            width: 200,
          },
        ]}
      />
    </>
  );
};

// ─── 渠道管道 Tab ─────────────────────────────────────────────────────────────

const CHANNEL_TYPES = [
  { label: 'HTTP (Webhook)', value: 'HTTP' },
  { label: 'RabbitMQ', value: 'RabbitMQ' },
  { label: 'WebSocket', value: 'WebSocket' },
  { label: 'CLI', value: 'CLI' },
  { label: 'Telegram', value: 'Telegram' },
  { label: 'Email', value: 'Email' },
];

const CHANNEL_COLORS: Record<string, string> = {
  HTTP: 'blue', RabbitMQ: 'orange', WebSocket: 'purple', CLI: 'default', Telegram: 'geekblue', Email: 'cyan',
};

const ChannelsTab: React.FC<{ workspaceId: string }> = ({ workspaceId }) => {
  const { message } = App.useApp();
  const tableRef = useRef<ActionType>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editItem, setEditItem] = useState<WorkspaceChannelDto | null>(null);
  const [workspaceAgents, setWorkspaceAgents] = useState<WorkspaceAgentDto[]>([]);
  const [form] = Form.useForm<UpsertWorkspaceChannelRequest>();

  useEffect(() => {
    listWorkspaceAgents(workspaceId).then(setWorkspaceAgents).catch(() => {});
  }, [workspaceId]);

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({ channelType: 'HTTP', isEnabled: true });
    setDrawerOpen(true);
  };

  const openEdit = (item: WorkspaceChannelDto) => {
    setEditItem(item);
    form.setFieldsValue(item);
    setDrawerOpen(true);
  };

  const handleSave = async () => {
    try {
      const values = await form.validateFields();
      if (editItem) {
        await updateWorkspaceChannel(workspaceId, editItem.channelId, values);
        message.success('渠道已更新');
      } else {
        await createWorkspaceChannel(workspaceId, values);
        message.success('渠道已创建');
      }
      setDrawerOpen(false);
      tableRef.current?.reload();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('保存失败');
    }
  };

  const handleDelete = async (channelId: string) => {
    await deleteWorkspaceChannel(workspaceId, channelId);
    message.success('渠道已删除');
    tableRef.current?.reload();
  };

  const columns: ProColumns<WorkspaceChannelDto>[] = [
    { title: '名称', dataIndex: 'name', width: 140 },
    {
      title: '类型',
      dataIndex: 'channelType',
      width: 110,
      render: (_, r) => <Tag color={CHANNEL_COLORS[r.channelType] ?? 'default'}>{r.channelType}</Tag>,
    },
    {
      title: '默认 Agent',
      width: 160,
      ellipsis: true,
      render: (_, r) => r.defaultAgentId
        ? <Text code style={{ fontSize: 12 }}>{r.defaultAgentId}</Text>
        : <Text type="secondary">场景默认</Text>,
    },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '启用',
      width: 80,
      render: (_, r) => r.isEnabled
        ? <Badge status="processing" text="启用" />
        : <Badge status="default" text="停用" />,
    },
    {
      title: '操作',
      width: 100,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除渠道？" onConfirm={() => handleDelete(r.channelId)} okButtonProps={{ danger: true }}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <>
      <ProTable<WorkspaceChannelDto>
        actionRef={tableRef}
        rowKey="channelId"
        columns={columns}
        request={async () => {
          const data = await listWorkspaceChannels(workspaceId);
          return { data, success: true };
        }}
        search={false}
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无渠道，点击上方按钮添加"
            />
          ),
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>添加渠道</Button>,
        ]}
      />
      <Drawer
        title={editItem ? '编辑渠道' : '添加渠道'}
        open={drawerOpen}
        width={500}
        onClose={() => setDrawerOpen(false)}
        extra={<Button type="primary" onClick={handleSave}>保存</Button>}
      >
        <ProForm form={form} submitter={false} layout="vertical">
          <ProFormText name="name" label="渠道名称" rules={[{ required: true }]} />
          <ProFormSelect name="channelType" label="渠道类型" options={CHANNEL_TYPES} rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
          <ProFormSelect
            name="defaultAgentId"
            label="默认路由 Agent"
            options={workspaceAgents.filter((a) => !a.isFrozen && a.isEnabled).map((a) => ({
              label: a.name, value: a.agentId,
            }))}
            placeholder="不选则使用场景默认路由策略"
            fieldProps={{ allowClear: true }}
          />
          <ProFormTextArea name="configJson" label="连接配置 JSON" rows={4} placeholder='{"endpoint": "...", "authRef": "secret-name"}' />
          <ProFormSwitch name="isEnabled" label="启用" />
        </ProForm>
      </Drawer>
    </>
  );
};

// ─── 场景详情页 ────────────────────────────────────────────────────────────────

const WorkspaceDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const location = useLocation();
  const { message } = App.useApp();

  const [workspace, setWorkspace] = useState<WorkspaceWithPermDto | null>(null);
  const [loading, setLoading] = useState(true);

  // Members tab
  const [members, setMembers] = useState<WorkspaceMemberDto[]>([]);
  const [membersLoading, setMembersLoading] = useState(false);

  // Add member modal
  const [addMemberOpen, setAddMemberOpen] = useState(false);
  const [addMemberLoading, setAddMemberLoading] = useState(false);
  const [allUsers, setAllUsers] = useState<AppUserDto[]>([]);
  const [memberForm] = Form.useForm<AddWorkspaceMemberRequest>();

  // Edit settings modal
  const [editOpen, setEditOpen] = useState(false);
  const [editLoading, setEditLoading] = useState(false);
  const [editForm] = Form.useForm<UpdateWorkspaceRequest>();

  const workspaceId = id ?? '';
  const tabFromQuery = new URLSearchParams(location.search).get('tab');
  const defaultTabKey = [
    'overview',
    'workspace-agents',
    'agent-templates',
    'chat',
    'workflows',
    'knowledge-bases',
    'skills',
    'peers',
    'channels',
    'members',
  ].includes(tabFromQuery ?? '')
    ? (tabFromQuery as string)
    : 'overview';

  const loadWorkspace = async () => {
    setLoading(true);
    try {
      const ws = await getWorkspace(workspaceId);
      setWorkspace(ws);
    } catch {
      message.error('加载场景失败');
    } finally {
      setLoading(false);
    }
  };

  const loadMembers = async () => {
    setMembersLoading(true);
    try {
      const data = await listWorkspaceMembers(workspaceId);
      setMembers(data);
    } catch {
      message.error('加载成员列表失败');
    } finally {
      setMembersLoading(false);
    }
  };

  useEffect(() => {
    if (workspaceId) {
      loadWorkspace();
      loadMembers();
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [workspaceId]);

  const openAddMember = async () => {
    try {
      const users = await listUsers();
      setAllUsers(users);
    } catch {
      message.error('加载用户列表失败');
    }
    memberForm.resetFields();
    memberForm.setFieldsValue({ accessLevel: 'Write' });
    setAddMemberOpen(true);
  };

  const handleAddMember = async () => {
    try {
      const values = await memberForm.validateFields();
      setAddMemberLoading(true);
      await addWorkspaceMember(workspaceId, values);
      message.success('成员已添加');
      setAddMemberOpen(false);
      loadMembers();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('添加失败');
    } finally {
      setAddMemberLoading(false);
    }
  };

  const handleRemoveMember = async (memberId: number) => {
    try {
      await removeWorkspaceMember(workspaceId, memberId);
      message.success('成员已移除');
      loadMembers();
    } catch {
      message.error('移除失败');
    }
  };

  const openEdit = () => {
    if (!workspace) return;
    editForm.setFieldsValue({
      name: workspace.name,
      description: workspace.description,
      userProfile: workspace.userProfile,
      isEnabled: workspace.isEnabled,
    });
    setEditOpen(true);
  };

  const handleEdit = async () => {
    if (!workspace) return;
    try {
      const values = await editForm.validateFields();
      setEditLoading(true);
      const request: UpdateWorkspaceRequest = {
        name: values.name,
        description: values.description,
        userProfile: values.userProfile,
        isEnabled: values.isEnabled,
        teamAccessPolicy: workspace.teamAccessPolicy,
        companyAccessPolicy: workspace.companyAccessPolicy,
      };
      await updateWorkspace(workspaceId, request);
      message.success('场景已更新');
      setEditOpen(false);
      loadWorkspace();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('更新失败');
    } finally {
      setEditLoading(false);
    }
  };

  const memberColumns = [
    {
      title: '用户 ID',
      dataIndex: 'userId',
      width: 160,
    },
    {
      title: '用户名',
      dataIndex: 'username',
    },
    {
      title: '显示名称',
      dataIndex: 'displayName',
      render: (v: string | undefined) => v ?? <Text type="secondary">—</Text>,
    },
    {
      title: '访问级别',
      dataIndex: 'accessLevel',
      render: (v: WorkspaceAccessPolicy) => (
        <Tag color={POLICY_COLOURS[v]}>{v}</Tag>
      ),
    },
    {
      title: '操作',
      key: 'action',
      width: 100,
      render: (_: unknown, record: WorkspaceMemberDto) => (
        <Popconfirm
          title="确认移除该成员？"
          onConfirm={() => handleRemoveMember(record.id)}
          okText="移除"
          cancelText="取消"
        >
          <Button type="link" size="small" danger>移除</Button>
        </Popconfirm>
      ),
    },
  ];

  if (loading) {
    return (
      <PageContainer>
        <Spin size="large" style={{ display: 'block', margin: '80px auto' }} />
      </PageContainer>
    );
  }

  if (!workspace) {
    return (
      <PageContainer>
        <div style={{ textAlign: 'center', padding: 80 }}>
          <Text type="secondary">场景不存在或已被删除</Text>
          <br />
          <Button style={{ marginTop: 16 }} onClick={() => history.push('/workspace')}>
            返回列表
          </Button>
        </div>
      </PageContainer>
    );
  }

  const statusBadge = workspace.isFrozen
    ? <Badge status="error" text="已冻结" />
    : workspace.isEnabled
      ? <Badge status="success" text="正常" />
      : <Badge status="default" text="已停用" />;

  return (
    <PageContainer
      header={{
        title: (
          <Space>
            <Button
              type="text"
              icon={<ArrowLeftOutlined />}
              onClick={() => history.push('/workspace')}
            />
            {workspace.name}
            {workspace.workspaceId === 'default' && <Tag color="blue">内置</Tag>}
          </Space>
        ),
        subTitle: `场景 ID：${workspace.workspaceId}`,
        extra: [
          <Button key="edit" icon={<SettingOutlined />} onClick={openEdit}>
            编辑设置
          </Button>,
        ],
      }}
    >
      <Tabs
        defaultActiveKey={defaultTabKey}
        items={[
          {
            key: 'overview',
            label: '概览',
            children: (
              <Descriptions bordered column={2} style={{ background: '#fff', padding: 16 }}>
                <Descriptions.Item label="场景 ID">{workspace.workspaceId}</Descriptions.Item>
                <Descriptions.Item label="状态">{statusBadge}</Descriptions.Item>
                <Descriptions.Item label="名称">{workspace.name}</Descriptions.Item>
                <Descriptions.Item label="成员数">{workspace.memberCount}</Descriptions.Item>
                <Descriptions.Item label="描述" span={2}>
                  {workspace.description ?? <Text type="secondary">暂无描述</Text>}
                </Descriptions.Item>
                <Descriptions.Item label="创建时间">
                  {new Date(workspace.createdAt).toLocaleString('zh-CN')}
                </Descriptions.Item>
              </Descriptions>
            ),
          },
          {
            key: 'workspace-agents',
            label: (
              <Space>
                <RobotOutlined />
                Agent 列表
              </Space>
            ),
            children: <WorkspaceAgentsTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'agent-templates',
            label: (
              <Space>
                <RobotOutlined />
                模板管理
              </Space>
            ),
            children: <AgentTemplatesTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'chat',
            label: (
              <Space>
                <MessageOutlined />
                Chat
              </Space>
            ),
            children: <ChatTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'workflows',
            label: (
              <Space>
                <ThunderboltOutlined />
                工作流
              </Space>
            ),
            children: <WorkflowsTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'knowledge-bases',
            label: '知识库',
            children: <KnowledgeBasesTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'skills',
            label: (
              <Space>
                <ToolOutlined />
                SKILL 库
              </Space>
            ),
            children: <SkillsTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'peers',
            label: (
              <Space>
                <CodeOutlined />
                对等节点
              </Space>
            ),
            children: <PeersTab />,
          },
          {
            key: 'channels',
            label: '渠道管道',
            children: <ChannelsTab workspaceId={workspace.workspaceId} />,
          },
          {
            key: 'members',
            label: (
              <Space>
                <TeamOutlined />
                成员
                <Tag>{workspace.memberCount}</Tag>
              </Space>
            ),
            children: (
              <>
                <div style={{ marginBottom: 12, textAlign: 'right' }}>
                  <Button
                    type="primary"
                    icon={<UserAddOutlined />}
                    onClick={openAddMember}
                  >
                    添加成员
                  </Button>
                </div>
                <Table
                  rowKey="id"
                  loading={membersLoading}
                  dataSource={members}
                  columns={memberColumns}
                  pagination={false}
                  size="small"
                  bordered
                />
              </>
            ),
          },
        ]}
      />

      {/* Add member modal */}
      <Modal
        title="添加白名单成员"
        open={addMemberOpen}
        onOk={handleAddMember}
        onCancel={() => setAddMemberOpen(false)}
        confirmLoading={addMemberLoading}
        okText="添加"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={memberForm} layout="vertical" style={{ marginTop: 16 }}>
          <Form.Item
            name="userId"
            label="用户"
            rules={[{ required: true, message: '请选择用户' }]}
          >
            <Select
              placeholder="选择用户"
              showSearch
              optionFilterProp="label"
              options={allUsers.map((u) => ({
                label: `${u.username}${u.displayName ? ` (${u.displayName})` : ''}`,
                value: u.userId,
              }))}
            />
          </Form.Item>
          <Form.Item
            name="accessLevel"
            label="访问级别"
            rules={[{ required: true }]}
          >
            <Select options={ACCESS_OPTIONS} />
          </Form.Item>
        </Form>
      </Modal>

      {/* Edit workspace modal */}
      <Modal
        title="编辑场景设置"
        open={editOpen}
        onOk={handleEdit}
        onCancel={() => setEditOpen(false)}
        confirmLoading={editLoading}
        okText="保存"
        cancelText="取消"
        destroyOnClose
        width={520}
      >
        <Form form={editForm} layout="vertical" style={{ marginTop: 16 }}>
          <Form.Item
            name="name"
            label="名称"
            rules={[{ required: true, message: '请输入名称' }, { max: 128 }]}
          >
            <Input placeholder="场景显示名称" />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input.TextArea rows={2} maxLength={512} placeholder="可选描述" />
          </Form.Item>
          <Form.Item name="userProfile" label="用户画像">
            <Input.TextArea
              rows={6}
              maxLength={2000}
              placeholder="描述该场景下的用户偏好、背景、习惯..."
            />
          </Form.Item>
          <Form.Item name="isEnabled" label="启用状态">
            <Select
              options={[
                { label: '启用', value: true },
                { label: '停用', value: false },
              ]}
            />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
};

export default function WorkspaceDetailPageWrapper() {
  return (
    <App>
      <WorkspaceDetailPage />
    </App>
  );
}
