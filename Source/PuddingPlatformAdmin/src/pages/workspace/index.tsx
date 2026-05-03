import {
  DeleteOutlined,
  EnterOutlined,
  PlusOutlined,
} from '@ant-design/icons';
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import {
  App,
  Badge,
  Button,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Tag,
  Tooltip,
} from 'antd';
import React, { useRef, useState } from 'react';
import { history } from '@umijs/max';
import {
  createWorkspace,
  deleteWorkspace,
  listTeams,
  listWorkspaces,
  type CreateWorkspaceRequest,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';

interface CreateSceneFormValues {
  name: string;
  description?: string;
}

const WorkspaceTable: React.FC = () => {
  const { message } = App.useApp();
  const actionRef = useRef<ActionType | null>(null);

  const [createOpen, setCreateOpen] = useState(false);
  const [createLoading, setCreateLoading] = useState(false);
  const [form] = Form.useForm<CreateSceneFormValues>();

  const refreshTable = () => actionRef.current?.reload();

  const openCreateModal = () => {
    form.resetFields();
    setCreateOpen(true);
  };

  const handleCreate = async () => {
    try {
      const values = await form.validateFields();
      setCreateLoading(true);

      const teams = await listTeams();
      const defaultTeamId = teams[0]?.teamId;
      if (!defaultTeamId) {
        message.error('创建失败：系统尚未初始化可用分组');
        return;
      }

      const slugBase = values.name
        .trim()
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-+|-+$/g, '')
        .slice(0, 48) || 'scene';

      const request: CreateWorkspaceRequest = {
        workspaceId: `${slugBase}-${Date.now().toString().slice(-6)}`,
        teamId: defaultTeamId,
        name: values.name,
        description: values.description,
        teamAccessPolicy: 'Write',
        companyAccessPolicy: 'None',
      };

      await createWorkspace(request);
      message.success(`场景 "${values.name}" 创建成功`);
      setCreateOpen(false);
      refreshTable();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return; // validation
      message.error('创建失败，请检查输入');
    } finally {
      setCreateLoading(false);
    }
  };

  const handleDelete = async (ws: WorkspaceWithPermDto) => {
    try {
      await deleteWorkspace(ws.workspaceId);
      message.success(`场景 "${ws.name}" 已删除`);
      refreshTable();
    } catch {
      message.error('删除失败，请稍后重试');
    }
  };

  const columns: ProColumns<WorkspaceWithPermDto>[] = [
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
      title: '场景 ID',
      dataIndex: 'workspaceId',
      copyable: true,
      width: 180,
      ellipsis: true,
      search: false,
    },
    {
      title: '操作',
      valueType: 'option',
      width: 160,
      render: (_, record) => [
        <Tooltip title="进入场景" key="enter">
          <Button
            type="link"
            icon={<EnterOutlined />}
            size="small"
            onClick={() => history.push(`/workspace/${record.workspaceId}`)}
          >
            进入
          </Button>
        </Tooltip>,
        <Popconfirm
          key="delete"
          title="确认删除此场景？"
          description="此操作不可恢复，内置场景无法删除。"
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
        title: '场景管理',
        subTitle: '管理你的 AI 助手场景',
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
            新建场景
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
        options={{ reload: true, density: true }}
        cardBordered
        locale={{
          emptyText: (
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description="暂无场景，点击上方「新建场景」按钮创建"
            />
          ),
        }}
      />

      <Modal
        title="新建场景"
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
            name="name"
            label="名称"
            rules={[
              { required: true, message: '请输入场景名称' },
              { max: 128, message: '最多 128 个字符' },
            ]}
          >
            <Input placeholder="场景显示名称" />
          </Form.Item>

          <Form.Item name="description" label="描述">
            <Input.TextArea placeholder="可选，场景用途说明" rows={2} maxLength={512} />
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

