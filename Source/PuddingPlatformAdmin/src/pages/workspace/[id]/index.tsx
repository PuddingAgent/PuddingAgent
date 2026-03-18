import {
  ArrowLeftOutlined,
  DeleteOutlined,
  EditOutlined,
  LockOutlined,
  PlusOutlined,
  RobotOutlined,
  SettingOutlined,
  TeamOutlined,
  UnlockOutlined,
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
  App,
  Badge,
  Button,
  Descriptions,
  Drawer,
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
import { history, useParams } from '@umijs/max';
import {
  addWorkspaceMember,
  createWorkspaceAgentTemplate,
  deleteWorkspaceAgentTemplate,
  freezeWorkspace,
  getWorkspace,
  listGlobalAgentTemplates,
  listLlmModels,
  listLlmProviders,
  listUsers,
  listWorkspaceAgentTemplates,
  listWorkspaceMembers,
  removeWorkspaceMember,
  unfreezeWorkspace,
  updateWorkspace,
  updateWorkspaceAgentTemplate,
  type AddWorkspaceMemberRequest,
  type AppUserDto,
  type GlobalAgentTemplateDto,
  type LlmModelDto,
  type LlmProviderDto,
  type UpdateWorkspaceRequest,
  type UpsertWorkspaceAgentTemplateRequest,
  type WorkspaceAccessPolicy,
  type WorkspaceAgentTemplateDto,
  type WorkspaceMemberDto,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

const { Text } = Typography;

const POLICY_COLOURS: Record<WorkspaceAccessPolicy, string> = {
  None: 'default',
  ReadOnly: 'blue',
  Write: 'green',
  Manage: 'red',
};

const POLICY_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
  { label: '无访问（白名单模式）', value: 'None' },
  { label: '只读', value: 'ReadOnly' },
  { label: '可读写', value: 'Write' },
  { label: '可管理', value: 'Manage' },
];

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
      role: tpl.role,
      systemPrompt: tpl.systemPrompt,
      userPromptTemplate: tpl.userPromptTemplate,
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

// ─── 工作空间详情页 ────────────────────────────────────────────────────────────

const WorkspaceDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
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

  const loadWorkspace = async () => {
    setLoading(true);
    try {
      const ws = await getWorkspace(workspaceId);
      setWorkspace(ws);
    } catch {
      message.error('加载工作空间失败');
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

  const handleFreeze = async () => {
    if (!workspace) return;
    try {
      await freezeWorkspace(workspaceId);
      message.success('工作空间已冻结');
      loadWorkspace();
    } catch {
      message.error('操作失败');
    }
  };

  const handleUnfreeze = async () => {
    if (!workspace) return;
    try {
      await unfreezeWorkspace(workspaceId);
      message.success('工作空间已解冻');
      loadWorkspace();
    } catch {
      message.error('操作失败');
    }
  };

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
      teamAccessPolicy: workspace.teamAccessPolicy,
      companyAccessPolicy: workspace.companyAccessPolicy,
      isEnabled: workspace.isEnabled,
    });
    setEditOpen(true);
  };

  const handleEdit = async () => {
    try {
      const values = await editForm.validateFields();
      setEditLoading(true);
      await updateWorkspace(workspaceId, values);
      message.success('工作空间已更新');
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
          <Text type="secondary">工作空间不存在或已被删除</Text>
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
        subTitle: workspace.workspaceId,
        extra: [
          <Button key="edit" icon={<SettingOutlined />} onClick={openEdit}>
            编辑设置
          </Button>,
          workspace.isFrozen ? (
            <Button key="unfreeze" icon={<UnlockOutlined />} onClick={handleUnfreeze}>
              解冻
            </Button>
          ) : (
            <Button key="freeze" icon={<LockOutlined />} danger onClick={handleFreeze}>
              冻结
            </Button>
          ),
        ],
      }}
    >
      <Tabs
        defaultActiveKey="overview"
        items={[
          {
            key: 'overview',
            label: '概览',
            children: (
              <Descriptions bordered column={2} style={{ background: '#fff', padding: 16 }}>
                <Descriptions.Item label="Workspace ID">{workspace.workspaceId}</Descriptions.Item>
                <Descriptions.Item label="状态">{statusBadge}</Descriptions.Item>
                <Descriptions.Item label="名称">{workspace.name}</Descriptions.Item>
                <Descriptions.Item label="所属团队">
                  <Tag color="geekblue">{workspace.teamName}</Tag>
                </Descriptions.Item>
                <Descriptions.Item label="描述" span={2}>
                  {workspace.description ?? <Text type="secondary">暂无描述</Text>}
                </Descriptions.Item>
                <Descriptions.Item label="团队访问策略">
                  <Tag color={POLICY_COLOURS[workspace.teamAccessPolicy]}>
                    {workspace.teamAccessPolicy}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label="全公司访问策略">
                  <Tag color={POLICY_COLOURS[workspace.companyAccessPolicy]}>
                    {workspace.companyAccessPolicy}
                  </Tag>
                </Descriptions.Item>
                <Descriptions.Item label="成员数">{workspace.memberCount}</Descriptions.Item>
                <Descriptions.Item label="创建时间">
                  {new Date(workspace.createdAt).toLocaleString('zh-CN')}
                </Descriptions.Item>
              </Descriptions>
            ),
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
          {
            key: 'agent-templates',
            label: (
              <Space>
                <RobotOutlined />
                Agent 模板
              </Space>
            ),
            children: <AgentTemplatesTab workspaceId={workspace.workspaceId} />,
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
        title="编辑工作空间设置"
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
            <Input placeholder="工作空间显示名称" />
          </Form.Item>
          <Form.Item name="description" label="描述">
            <Input.TextArea rows={2} maxLength={512} placeholder="可选描述" />
          </Form.Item>
          <Form.Item name="teamAccessPolicy" label="团队成员默认权限">
            <Select options={POLICY_OPTIONS} />
          </Form.Item>
          <Form.Item name="companyAccessPolicy" label="全公司默认权限">
            <Select options={POLICY_OPTIONS} />
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
