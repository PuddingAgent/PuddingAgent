import {
  DeleteOutlined,
  LockOutlined,
  PlusOutlined,
  UnlockOutlined,
} from '@ant-design/icons';
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { App, Badge, Button, Popconfirm, Space, Tag, Tooltip } from 'antd';
import React, { useRef } from 'react';
import {
  deleteWorkspace,
  freezeWorkspace,
  listWorkspaces,
  unfreezeWorkspace,
  type WorkspaceDefinition,
} from '@/services/platform/api';

const WorkspaceTable: React.FC = () => {
  const { message } = App.useApp();
  const actionRef = useRef<ActionType | null>(null);

  const refreshTable = () => actionRef.current?.reload();

  const handleFreeze = async (ws: WorkspaceDefinition) => {
    try {
      await freezeWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已冻结`);
      refreshTable();
    } catch {
      message.error('操作失败，请稍后重试');
    }
  };

  const handleUnfreeze = async (ws: WorkspaceDefinition) => {
    try {
      await unfreezeWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已解冻`);
      refreshTable();
    } catch {
      message.error('操作失败，请稍后重试');
    }
  };

  const handleDelete = async (ws: WorkspaceDefinition) => {
    try {
      await deleteWorkspace(ws.workspaceId);
      message.success(`工作空间 "${ws.name}" 已删除`);
      refreshTable();
    } catch {
      message.error('删除失败，请稍后重试');
    }
  };

  const columns: ProColumns<WorkspaceDefinition>[] = [
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
      title: '状态',
      key: 'status',
      search: false,
      render: (_, record) => {
        if (record.isFrozen)
          return <Badge status="error" text="已冻结" />;
        if (!record.isEnabled)
          return <Badge status="default" text="已停用" />;
        return <Badge status="success" text="正常" />;
      },
    },
    {
      title: 'Agent 模板数',
      search: false,
      render: (_, record) => (
        <Tag>{record.agentTemplateIds.length}</Tag>
      ),
    },
    {
      title: '绑定渠道数',
      search: false,
      render: (_, record) => (
        <Tag color="purple">{record.channelBindings.length}</Tag>
      ),
    },
    {
      title: '操作',
      valueType: 'option',
      width: 180,
      render: (_, record) => [
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
        subTitle: '管理所有 Workspace 的生命周期、渠道绑定与智能体策略',
      }}
    >
      <ProTable<WorkspaceDefinition>
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
              onClick={() => message.info('创建工作空间功能即将上线')}
            >
              新建工作空间
            </Button>,
          ]}
          search={false}
          pagination={{ pageSize: 20 }}
          options={{ reload: true, density: true }}
          cardBordered
        />
    </PageContainer>
  );
};

const WorkspacePage: React.FC = () => (
  <App>
    <WorkspaceTable />
  </App>
);

export default WorkspacePage;
