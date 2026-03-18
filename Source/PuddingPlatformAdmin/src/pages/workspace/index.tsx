import {
  DeleteOutlined,
  EnterOutlined,
  LockOutlined,
  PlusOutlined,
  UnlockOutlined,
} from '@ant-design/icons';
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import {
  App,
  Badge,
  Button,
  Form,
  Input,
  Modal,
  Popconfirm,
  Select,
  Space,
  Tag,
  Tooltip,
} from 'antd';
import React, { useRef, useState } from 'react';
import { history } from '@umijs/max';
import {
  createWorkspace,
  deleteWorkspace,
  freezeWorkspace,
  listTeams,
  listWorkspaces,
  unfreezeWorkspace,
  type CreateWorkspaceRequest,
  type TeamDto,
  type WorkspaceAccessPolicy,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

const POLICY_OPTIONS: { label: string; value: WorkspaceAccessPolicy }[] = [
  { label: '无访问（白名单模式）', value: 'None' },
  { label: '只读', value: 'ReadOnly' },
  { label: '可读写', value: 'Write' },
  { label: '可管理', value: 'Manage' },
];

const WorkspaceTable: React.FC = () => {
  const { message } = App.useApp();
  const actionRef = useRef<ActionType | null>(null);

  const [createOpen, setCreateOpen] = useState(false);
  const [createLoading, setCreateLoading] = useState(false);
  const [teams, setTeams] = useState<TeamDto[]>([]);
  const [form] = Form.useForm<CreateWorkspaceRequest>();

  const refreshTable = () => actionRef.current?.reload();

  const openCreateModal = async () => {
    try {
      const data = await listTeams();
      setTeams(data);
    } catch {
      message.error('加载团队列表失败');
    }
    form.resetFields();
    form.setFieldsValue({ teamAccessPolicy: 'Write', companyAccessPolicy: 'None' });
    setCreateOpen(true);
  };

  const handleCreate = async () => {
    try {
      const values = await form.validateFields();
      setCreateLoading(true);
      await createWorkspace(values);
      message.success(`工作空间 "${values.name}" 创建成功`);
      setCreateOpen(false);
      refreshTable();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return; // validation
      message.error('创建失败，请检查输入');
    } finally {
      setCreateLoading(false);
    }
  };

  const handleFreeze = async (ws: WorkspaceWithPermDto) => {
    try {
      await freezeWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已冻结`);
      refreshTable();
    } catch {
      message.error('操作失败，请稍后重试');
    }
  };

  const handleUnfreeze = async (ws: WorkspaceWithPermDto) => {
    try {
      await unfreezeWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已解冻`);
      refreshTable();
    } catch {
      message.error('操作失败，请稍后重试');
    }
  };

  const handleDelete = async (ws: WorkspaceWithPermDto) => {
    try {
      await deleteWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已删除`);
      refreshTable();
    } catch {
      message.error('删除失败，请稍后重试');
    }
  };

  const columns: ProColumns<WorkspaceWithPermDto>[] = [
    {
      title: 'Workspace ID',
      dataIndex: 'workspaceId',
      copyable: true,
      width: 160,
      ellipsis: true,
    },
    {
      title: '名称',
      dataIndex: 'name',
      render: (_, record) => (
        <Space>
          <span style={{ fontWeight: 500 }}>{record.name}</span>
          {record.workspaceId === 'default' && <Tag color="blue">内置</Tag>}
        </Space>
      ),
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true,
      search: false,
    },
    {
      title: '所属团队',
      dataIndex: 'teamName',
      search: false,
      render: (_, record) => <Tag color="geekblue">{record.teamName}</Tag>,
    },
    {
      title: '成员数',
      dataIndex: 'memberCount',
      search: false,
      width: 80,
      render: (_, record) => <Tag>{record.memberCount}</Tag>,
    },
    {
      title: '状态',
      key: 'status',
      search: false,
      width: 90,
      render: (_, record) => {
        if (record.isFrozen) return <Badge status="error" text="已冻结" />;
        if (!record.isEnabled) return <Badge status="default" text="已停用" />;
        return <Badge status="success" text="正常" />;
      },
    },
    {
      title: '操作',
      valueType: 'option',
      width: 220,
      render: (_, record) => [
        <Tooltip title="进入工作空间" key="enter">
          <Button
            type="link"
            icon={<EnterOutlined />}
            size="small"
            onClick={() => history.push(`/workspace/${record.workspaceId}`)}
          >
            进入
          </Button>
        </Tooltip>,
        record.isFrozen ? (
          <Tooltip title="解冻" key="unfreeze">
            <Button
              type="link"
              icon={<UnlockOutlined />}
              size="small"
              onClick={() => handleUnfreeze(record)}
            >
              解冻
            </Button>
          </Tooltip>
        ) : (
          <Tooltip title="冻结工作空间" key="freeze">
            <Button
              type="link"
              icon={<LockOutlined />}
              size="small"
              danger
              onClick={() => handleFreeze(record)}
            >
              冻结
            </Button>
          </Tooltip>
        ),
        <Popconfirm
          key="delete"
          title="确认删除此工作空间？"
          description="此操作不可恢复，内置工作空间无法删除。"
          onConfirm={() => handleDelete(record)}
          okText="删除"
          cancelText="取消"
          okButtonProps={{ danger: true }}
        >
          <Button
            type="link"
            icon={<DeleteOutlined />}
            size="small"
            danger
            disabled={record.workspaceId === 'default'}
          >
            删除
          </Button>
        </Popconfirm>,
      ],
    },
  ];

  return (
    <PageContainer
      header={{
        title: '工作空间管理',
        subTitle: '管理所有 Workspace 的生命周期与访问策略',
      }}
    >
      <ProTable<WorkspaceWithPermDto>
        actionRef={actionRef}
        rowKey="workspaceId"
        columns={columns}
        request={async () => {
          const data = await listWorkspaces();
          return { data, success: true, total: data.length };
        }}
        toolBarRender={() => [
          <Button
            key="create"
            type="primary"
            icon={<PlusOutlined />}
            onClick={openCreateModal}
          >
            新建工作空间
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
        options={{ reload: true, density: true }}
        cardBordered
      />

      <Modal
        title="新建工作空间"
        open={createOpen}
        onOk={handleCreate}
        onCancel={() => setCreateOpen(false)}
        confirmLoading={createLoading}
        okText="创建"
        cancelText="取消"
        width={520}
        destroyOnClose
      >
        <Form form={form} layout="vertical" style={{ marginTop: 16 }}>
          <Form.Item
            name="workspaceId"
            label="Workspace ID（唯一标识符）"
            rules={[
              { required: true, message: '请输入 Workspace ID' },
              { pattern: /^[a-z0-9-]+$/, message: '只允许小写字母、数字和连字符' },
              { max: 64, message: '最多 64 个字符' },
            ]}
          >
            <Input placeholder="例如：my-workspace" />
          </Form.Item>

          <Form.Item
            name="name"
            label="名称"
            rules={[
              { required: true, message: '请输入工作空间名称' },
              { max: 128, message: '最多 128 个字符' },
            ]}
          >
            <Input placeholder="工作空间显示名称" />
          </Form.Item>

          <Form.Item name="description" label="描述">
            <Input.TextArea placeholder="可选，工作空间用途说明" rows={2} maxLength={512} />
          </Form.Item>

          <Form.Item
            name="teamId"
            label="所属团队"
            rules={[{ required: true, message: '请选择所属团队' }]}
          >
            <Select
              placeholder="选择团队"
              options={teams.map((t) => ({ label: t.name, value: t.teamId }))}
              showSearch
              filterOption={(input, option) =>
                (option?.label as string)?.toLowerCase().includes(input.toLowerCase())
              }
            />
          </Form.Item>

          <Form.Item name="teamAccessPolicy" label="团队成员默认权限">
            <Select options={POLICY_OPTIONS} />
          </Form.Item>

          <Form.Item name="companyAccessPolicy" label="全公司默认权限">
            <Select options={POLICY_OPTIONS} />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
};

const WorkspacePage: React.FC = () => (
  <App>
    <WorkspaceTable />
  </App>
);

export default WorkspacePage;

