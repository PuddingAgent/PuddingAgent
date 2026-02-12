import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormSelect,
  ProFormSwitch,
  ProFormTextArea,
} from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Popconfirm,
  Space,
  Tag,
  Typography,
  Tabs,
  Form,
  Select,
  Descriptions,
  Empty,
  Tooltip,
  message,
  Modal,
  Table,
} from 'antd';
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  TeamOutlined,
  AppstoreOutlined,
  UserAddOutlined,
} from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  listTeams,
  getTeam,
  createTeam,
  updateTeam,
  deleteTeam,
  addTeamMember,
  removeTeamMember,
  listTeamWorkspaces,
  createTeamWorkspace,
  updateWorkspacePerm,
  deleteWorkspacePerm,
  listWorkspaceMembers,
  addWorkspaceMember,
  removeWorkspaceMember,
  listUsers,
  type TeamDto,
  type TeamDetailDto,
  type TeamMemberDto,
  type WorkspaceWithPermDto,
  type WorkspaceMemberDto,
  type AppUserDto,
  type UpsertTeamRequest,
  type CreateWorkspaceRequest,
  type UpdateWorkspaceRequest,
  type AddTeamMemberRequest,
  type AddWorkspaceMemberRequest,
  type WorkspaceAccessPolicy,
} from '@/services/platform/api';

const { Text } = Typography;

const POLICY_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
  { label: '无访问（白名单模式）', value: 'None' },
  { label: '只读', value: 'ReadOnly' },
  { label: '可读写', value: 'Write' },
  { label: '可管理', value: 'Manage' },
];

const POLICY_COLORS: Record<WorkspaceAccessPolicy, string> = {
  None: 'default',
  ReadOnly: 'blue',
  Write: 'green',
  Manage: 'red',
};

export default function TeamManagementPage() {
  const actionRef = useRef<ActionType>(null);

  // 团队 Drawer
  const [teamDrawerOpen, setTeamDrawerOpen] = useState(false);
  const [editingTeam, setEditingTeam] = useState<TeamDto | null>(null);
  const [teamForm] = Form.useForm();
  const [teamSaving, setTeamSaving] = useState(false);

  // 团队详情 Drawer（成员 + 场景）
  const [detailDrawerOpen, setDetailDrawerOpen] = useState(false);
  const [teamDetail, setTeamDetail] = useState<TeamDetailDto | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);

  // 添加成员 Modal
  const [addMemberOpen, setAddMemberOpen] = useState(false);
  const [memberForm] = Form.useForm();
  const [allUsers, setAllUsers] = useState<AppUserDto[]>([]);

  // 场景 Drawer
  const [wsDrawerOpen, setWsDrawerOpen] = useState(false);
  const [editingWs, setEditingWs] = useState<WorkspaceWithPermDto | null>(null);
  const [wsForm] = Form.useForm();
  const [wsSaving, setWsSaving] = useState(false);

  // 场景成员
  const [wsMembersOpen, setWsMembersOpen] = useState(false);
  const [wsMembers, setWsMembers] = useState<WorkspaceMemberDto[]>([]);
  const [addWsMemberOpen, setAddWsMemberOpen] = useState(false);
  const [wsMemberForm] = Form.useForm();
  const [currentWsId, setCurrentWsId] = useState<string>('');

  const teamColumns: ProColumns<TeamDto>[] = [
    { title: 'TeamId', dataIndex: 'teamId', width: 140, copyable: true },
    { title: '名称', dataIndex: 'name', width: 140 },
    { title: '描述', dataIndex: 'description', ellipsis: true },
    {
      title: '成员数', dataIndex: 'memberCount', width: 80,
      render: (v) => <Tag icon={<TeamOutlined />}>{v as number}</Tag>,
    },
    {
      title: '场景数', dataIndex: 'workspaceCount', width: 90,
      render: (v) => <Tag icon={<AppstoreOutlined />}>{v as number}</Tag>,
    },
    {
      title: '状态', dataIndex: 'isEnabled', width: 80,
      render: (_, r) => <Tag color={r.isEnabled ? 'success' : 'default'}>{r.isEnabled ? '启用' : '禁用'}</Tag>,
    },
    {
      title: '操作', key: 'action', width: 200,
      render: (_, r) => (
        <Space>
          <Button size="small" onClick={() => openDetail(r.teamId)}>详情</Button>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEditTeam(r)}>编辑</Button>
          <Popconfirm
            title={`确认删除团队 "${r.name}"？（需先删除所有场景）`}
            onConfirm={() => handleDeleteTeam(r.teamId)}
            okText="删除" cancelText="取消"
          >
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  async function openDetail(teamId: string) {
    setDetailLoading(true);
    setDetailDrawerOpen(true);
    setTeamDetail(null);
    try {
      const detail = await getTeam(teamId);
      setTeamDetail(detail);
    } catch {
      message.error('加载团队详情失败');
    } finally {
      setDetailLoading(false);
    }
  }

  function openCreateTeam() {
    setEditingTeam(null);
    teamForm.resetFields();
    setTeamDrawerOpen(true);
  }

  function openEditTeam(team: TeamDto) {
    setEditingTeam(team);
    teamForm.setFieldsValue({
      teamId: team.teamId, name: team.name,
      description: team.description, isEnabled: team.isEnabled,
    });
    setTeamDrawerOpen(true);
  }

  async function handleSaveTeam() {
    const values = await teamForm.validateFields();
    setTeamSaving(true);
    try {
      const req = values as UpsertTeamRequest;
      if (editingTeam) {
        await updateTeam(editingTeam.teamId, req);
        message.success('团队已更新');
      } else {
        await createTeam(req);
        message.success('团队已创建');
      }
      setTeamDrawerOpen(false);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    } finally {
      setTeamSaving(false);
    }
  }

  async function handleDeleteTeam(teamId: string) {
    try {
      await deleteTeam(teamId);
      message.success('团队已删除');
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '删除失败');
    }
  }

  // ── 成员操作 ────────────────────────────────────────────────────
  async function openAddMember() {
    const users = await listUsers().catch(() => []);
    setAllUsers(users);
    memberForm.resetFields();
    setAddMemberOpen(true);
  }

  async function handleAddMember() {
    if (!teamDetail) return;
    const values = await memberForm.validateFields();
    try {
      await addTeamMember(teamDetail.teamId, values as AddTeamMemberRequest);
      message.success('成员已添加');
      setAddMemberOpen(false);
      const detail = await getTeam(teamDetail.teamId);
      setTeamDetail(detail);
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    }
  }

  async function handleRemoveMember(userId: string) {
    if (!teamDetail) return;
    try {
      await removeTeamMember(teamDetail.teamId, userId);
      message.success('成员已移除');
      const detail = await getTeam(teamDetail.teamId);
      setTeamDetail(detail);
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    }
  }

  // ── 场景操作 ───────────────────────────────────────────────────
  function openCreateWs() {
    setEditingWs(null);
    wsForm.resetFields();
    wsForm.setFieldsValue({ teamAccessPolicy: 'ReadOnly', companyAccessPolicy: 'None' });
    setWsDrawerOpen(true);
  }

  function openEditWs(ws: WorkspaceWithPermDto) {
    setEditingWs(ws);
    wsForm.setFieldsValue({
      name: ws.name, description: ws.description,
      teamAccessPolicy: ws.teamAccessPolicy, companyAccessPolicy: ws.companyAccessPolicy,
      isEnabled: ws.isEnabled,
    });
    setWsDrawerOpen(true);
  }

  async function handleSaveWs() {
    if (!teamDetail) return;
    const values = await wsForm.validateFields();
    setWsSaving(true);
    try {
      if (editingWs) {
        await updateWorkspacePerm(editingWs.workspaceId, values as UpdateWorkspaceRequest);
        message.success('场景已更新');
      } else {
        await createTeamWorkspace(teamDetail.teamId, {
          ...(values as CreateWorkspaceRequest),
          teamId: teamDetail.teamId,
        });
        message.success('场景已创建');
      }
      setWsDrawerOpen(false);
      const detail = await getTeam(teamDetail.teamId);
      setTeamDetail(detail);
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    } finally {
      setWsSaving(false);
    }
  }

  async function handleDeleteWs(workspaceId: string) {
    if (!teamDetail) return;
    try {
      await deleteWorkspacePerm(workspaceId);
      message.success('场景已删除');
      const detail = await getTeam(teamDetail.teamId);
      setTeamDetail(detail);
    } catch (e: any) {
      message.error(e?.message ?? '删除失败');
    }
  }

  async function openWsMembers(wsId: string) {
    setCurrentWsId(wsId);
    const members = await listWorkspaceMembers(wsId).catch(() => []);
    setWsMembers(members);
    setWsMembersOpen(true);
  }

  async function handleAddWsMember() {
    const values = await wsMemberForm.validateFields();
    try {
      await addWorkspaceMember(currentWsId, values as AddWorkspaceMemberRequest);
      message.success('成员已添加');
      const members = await listWorkspaceMembers(currentWsId);
      setWsMembers(members);
      setAddWsMemberOpen(false);
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    }
  }

  async function handleRemoveWsMember(id: number) {
    try {
      await removeWorkspaceMember(currentWsId, id);
      message.success('成员已移除');
      const members = await listWorkspaceMembers(currentWsId);
      setWsMembers(members);
    } catch (e: any) {
      message.error(e?.message ?? '操作失败');
    }
  }

  return (
    <PageContainer title="团队管理" subTitle="管理平台团队、场景及访问权限">
      <ProTable<TeamDto>
        actionRef={actionRef}
        rowKey="teamId"
        columns={teamColumns}
        request={async () => {
          const data = await listTeams();
          return { data, success: true, total: data.length };
        }}
        toolBarRender={() => [
          <Button key="create" type="primary" icon={<PlusOutlined />} onClick={openCreateTeam}>
            新建团队
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
      />

      {/* ── 新建/编辑团队 Drawer ──────────────────────────────── */}
      <Drawer
        title={editingTeam ? `编辑团队：${editingTeam.name}` : '新建团队'}
        open={teamDrawerOpen}
        onClose={() => setTeamDrawerOpen(false)}
        width={480}
        extra={
          <Space>
            <Button onClick={() => setTeamDrawerOpen(false)}>取消</Button>
            <Button type="primary" loading={teamSaving} onClick={handleSaveTeam}>保存</Button>
          </Space>
        }
      >
        <ProForm form={teamForm} submitter={false} layout="vertical">
          {!editingTeam && (
            <ProFormText
              name="teamId"
              label="TeamId（英文 slug）"
              rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字和连字符' }]}
              placeholder="e.g. platform-team"
            />
          )}
          <ProFormText name="name" label="团队名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
          <ProFormSwitch name="isEnabled" label="启用" initialValue />
        </ProForm>
      </Drawer>

      {/* ── 团队详情 Drawer ───────────────────────────────────── */}
      <Drawer
        title={teamDetail ? `团队：${teamDetail.name}` : '团队详情'}
        open={detailDrawerOpen}
        onClose={() => setDetailDrawerOpen(false)}
        width={820}
        loading={detailLoading}
      >
        {teamDetail && (
          <Tabs
            items={[
              {
                key: 'members',
                label: <><TeamOutlined /> 成员（{teamDetail.members.length}）</>,
                children: (
                  <>
                    <div style={{ marginBottom: 12 }}>
                      <Button
                        icon={<UserAddOutlined />}
                        onClick={openAddMember}
                        size="small"
                      >
                        添加成员
                      </Button>
                    </div>
                    {teamDetail.members.length === 0
                      ? <Empty description="暂无成员" />
                      : (
                        <Table
                          rowKey="userId"
                          size="small"
                          pagination={false}
                          dataSource={teamDetail.members}
                          columns={[
                            { title: 'UserId', dataIndex: 'userId', width: 130 },
                            { title: '用户名', dataIndex: 'username', width: 120 },
                            { title: '显示名', dataIndex: 'displayName' },
                            {
                              title: '角色', dataIndex: 'role', width: 90,
                              render: (v: string) => (
                                <Tag color={v === 'Admin' ? 'red' : 'blue'}>{v}</Tag>
                              ),
                            },
                            {
                              title: '操作', width: 80,
                              render: (_: any, m: TeamMemberDto) => (
                                <Popconfirm
                                  title="确认移除此成员？"
                                  onConfirm={() => handleRemoveMember(m.userId)}
                                  okText="移除" cancelText="取消"
                                >
                                  <Button size="small" danger>移除</Button>
                                </Popconfirm>
                              ),
                            },
                          ]}
                        />
                      )}
                  </>
                ),
              },
              {
                key: 'workspaces',
                label: <><AppstoreOutlined /> 场景（{teamDetail.workspaces.length}）</>,
                children: (
                  <>
                    <div style={{ marginBottom: 12 }}>
                      <Button icon={<PlusOutlined />} onClick={openCreateWs} size="small">
                        新建场景
                      </Button>
                    </div>
                    {teamDetail.workspaces.length === 0
                      ? <Empty description="暂无场景" />
                      : (
                        <Table
                          rowKey="workspaceId"
                          size="small"
                          pagination={false}
                          dataSource={teamDetail.workspaces}
                          columns={[
                            {
                              title: '场景', dataIndex: 'name', width: 140,
                              render: (v: string, r: WorkspaceWithPermDto) => (
                                <div>
                                  <div>{v}</div>
                                  <Text type="secondary" style={{ fontSize: 11 }}>
                                    /workspace/{r.teamId}/{r.slug}
                                  </Text>
                                </div>
                              ),
                            },
                            {
                              title: '团队权限', dataIndex: 'teamAccessPolicy', width: 110,
                              render: (v: WorkspaceAccessPolicy) => (
                                <Tag color={POLICY_COLORS[v]}>{v}</Tag>
                              ),
                            },
                            {
                              title: '全公司权限', dataIndex: 'companyAccessPolicy', width: 110,
                              render: (v: WorkspaceAccessPolicy) => (
                                <Tag color={POLICY_COLORS[v]}>{v}</Tag>
                              ),
                            },
                            {
                              title: '白名单', dataIndex: 'memberCount', width: 80,
                              render: (v: number) => <Tag>{v} 人</Tag>,
                            },
                            {
                              title: '状态', dataIndex: 'isEnabled', width: 70,
                              render: (v: boolean) => (
                                <Tag color={v ? 'success' : 'default'}>{v ? '启用' : '禁用'}</Tag>
                              ),
                            },
                            {
                              title: '操作', width: 190,
                              render: (_: any, r: WorkspaceWithPermDto) => (
                                <Space size={4}>
                                  <Button size="small" onClick={() => openEditWs(r)}>编辑</Button>
                                  <Button size="small" onClick={() => openWsMembers(r.workspaceId)}>
                                    白名单
                                  </Button>
                                  <Popconfirm
                                    title="确认删除此场景？"
                                    onConfirm={() => handleDeleteWs(r.workspaceId)}
                                    okText="删除" cancelText="取消"
                                  >
                                    <Button size="small" danger>删除</Button>
                                  </Popconfirm>
                                </Space>
                              ),
                            },
                          ]}
                        />
                      )}
                  </>
                ),
              },
            ]}
          />
        )}
      </Drawer>

      {/* ── 新建/编辑场景 Drawer ────────────────────────────── */}
      <Drawer
        title={editingWs ? `编辑场景：${editingWs.name}` : '新建场景'}
        open={wsDrawerOpen}
        onClose={() => setWsDrawerOpen(false)}
        width={480}
        extra={
          <Space>
            <Button onClick={() => setWsDrawerOpen(false)}>取消</Button>
            <Button type="primary" loading={wsSaving} onClick={handleSaveWs}>保存</Button>
          </Space>
        }
      >
        <ProForm form={wsForm} submitter={false} layout="vertical">
          {!editingWs && (
            <ProFormText
              name="workspaceId"
              label="WorkspaceId（全局唯一 slug）"
              rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字和连字符' }]}
              placeholder="e.g. my-workspace"
            />
          )}
          <ProFormText name="name" label="场景名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={2} />
          <ProFormSelect
            name="teamAccessPolicy"
            label="团队默认权限"
            options={POLICY_OPTIONS}
            initialValue="ReadOnly"
            tooltip="团队内所有成员的默认访问级别"
            rules={[{ required: true }]}
          />
          <ProFormSelect
            name="companyAccessPolicy"
            label="全公司默认权限"
            options={POLICY_OPTIONS}
            initialValue="None"
            tooltip="非团队成员的默认访问级别（None = 仅白名单）"
            rules={[{ required: true }]}
          />
          {editingWs && <ProFormSwitch name="isEnabled" label="启用" initialValue />}
        </ProForm>
      </Drawer>

      {/* ── 添加团队成员 Modal ────────────────────────────────── */}
      <Modal
        title="添加团队成员"
        open={addMemberOpen}
        onCancel={() => setAddMemberOpen(false)}
        onOk={handleAddMember}
        okText="添加"
        destroyOnClose
      >
        <Form form={memberForm} layout="vertical">
          <Form.Item name="userId" label="选择用户" rules={[{ required: true }]}>
            <Select
              showSearch
              placeholder="搜索用户"
              options={allUsers.map((u) => ({
                label: `${u.username}（${u.userId}）`,
                value: u.userId,
              }))}
              filterOption={(input, opt) =>
                (opt?.label as string ?? '').toLowerCase().includes(input.toLowerCase())
              }
            />
          </Form.Item>
          <Form.Item name="role" label="团队角色" initialValue="Member" rules={[{ required: true }]}>
            <Select options={[
              { label: 'Member（普通成员）', value: 'Member' },
              { label: 'Admin（团队管理员）', value: 'Admin' },
            ]} />
          </Form.Item>
        </Form>
      </Modal>

      {/* ── 场景白名单成员 Modal ────────────────────────────── */}
      <Modal
        title="场景白名单成员"
        open={wsMembersOpen}
        onCancel={() => setWsMembersOpen(false)}
        footer={null}
        width={600}
        destroyOnClose
      >
        <Button
          icon={<UserAddOutlined />}
          style={{ marginBottom: 12 }}
          onClick={() => { wsMemberForm.resetFields(); setAddWsMemberOpen(true); }}
          size="small"
        >
          添加成员
        </Button>
        <Table
          rowKey="id"
          size="small"
          pagination={false}
          dataSource={wsMembers}
          columns={[
            { title: 'UserId', dataIndex: 'userId', width: 120 },
            { title: '用户名', dataIndex: 'username', width: 110 },
            {
              title: '权限', dataIndex: 'accessLevel', width: 100,
              render: (v: WorkspaceAccessPolicy) => <Tag color={POLICY_COLORS[v]}>{v}</Tag>,
            },
            {
              title: '操作', width: 80,
              render: (_: any, r: WorkspaceMemberDto) => (
                <Popconfirm
                  title="确认移除？"
                  onConfirm={() => handleRemoveWsMember(r.id)}
                  okText="移除" cancelText="取消"
                >
                  <Button size="small" danger>移除</Button>
                </Popconfirm>
              ),
            },
          ]}
        />
      </Modal>

      {/* ── 添加场景白名单成员 Modal ────────────────────────── */}
      <Modal
        title="添加白名单成员"
        open={addWsMemberOpen}
        onCancel={() => setAddWsMemberOpen(false)}
        onOk={handleAddWsMember}
        okText="添加"
        destroyOnClose
      >
        <Form form={wsMemberForm} layout="vertical">
          <Form.Item name="userId" label="选择用户" rules={[{ required: true }]}>
            <Select
              showSearch
              placeholder="搜索用户"
              options={allUsers.map((u) => ({
                label: `${u.username}（${u.userId}）`,
                value: u.userId,
              }))}
              filterOption={(input, opt) =>
                (opt?.label as string ?? '').toLowerCase().includes(input.toLowerCase())
              }
            />
          </Form.Item>
          <Form.Item name="accessLevel" label="权限级别" initialValue="ReadOnly" rules={[{ required: true }]}>
            <Select options={POLICY_OPTIONS.filter((o) => o.value !== 'None')} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
}
