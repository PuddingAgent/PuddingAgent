import {
  DeleteOutlined,
  EnterOutlined,
  PlusOutlined,
  AppstoreOutlined,
  TableOutlined,
} from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import {
  App,
  Badge,
  Button,
  Card,
  Col,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Row,
  Space,
  Tag,
  Tooltip,
  Typography,
  Radio,
} from 'antd';
import React, { useEffect, useRef, useState } from 'react';
import { history } from '@umijs/max';
import {
  createWorkspace,
  deleteWorkspace,
  listTeams,
  listWorkspaces,
  type CreateWorkspaceRequest,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import { ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';

const { Text } = Typography;

interface CreateSceneFormValues {
  name: string;
  description?: string;
}

type ViewMode = 'card' | 'table';

/** 获取场景状态对应的 Badge 配置 */
const getStatusBadge = (r: WorkspaceWithPermDto) => {
  if (r.isFrozen) return { status: 'error' as const, text: '已冻结', color: '#ef4444' };
  if (!r.isEnabled) return { status: 'default' as const, text: '已停用', color: '#94a3b8' };
  return { status: 'success' as const, text: '运行中', color: '#22c55e' };
};

// 表格列定义
const tableColumns: ProColumns<WorkspaceWithPermDto>[] = [
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
      const s = getStatusBadge(record);
      return <Badge status={s.status} text={s.text} />;
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
    title: '成员数',
    dataIndex: 'memberCount',
    width: 80,
    search: false,
  },
  {
    title: '创建时间',
    dataIndex: 'createdAt',
    valueType: 'dateTime',
    width: 170,
    search: false,
    render: (_, r) => new Date(r.createdAt).toLocaleString('zh-CN'),
  },
];

const WorkspaceTable: React.FC = () => {
  const { message } = App.useApp();
  const [viewMode, setViewMode] = useState<ViewMode>('card');
  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [loading, setLoading] = useState(false);

  const [createOpen, setCreateOpen] = useState(false);
  const [createLoading, setCreateLoading] = useState(false);
  const [form] = Form.useForm<CreateSceneFormValues>();

  const fetchData = async () => {
    setLoading(true);
    try {
      const data = await listWorkspaces();
      setWorkspaces(data);
    } catch {
      message.error('加载场景列表失败');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { fetchData(); }, []);

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
      fetchData();
    } catch (err: unknown) {
      if (err && typeof err === 'object' && 'errorFields' in err) return;
      message.error('创建失败，请检查输入');
    } finally {
      setCreateLoading(false);
    }
  };

  const handleDelete = async (ws: WorkspaceWithPermDto) => {
    try {
      await deleteWorkspace(ws.workspaceId);
      message.success(`场景 "${ws.name}" 已删除`);
      fetchData();
    } catch {
      message.error('删除失败，请稍后重试');
    }
  };

  return (
    <PageContainer
      header={{
        title: '场景管理',
        subTitle: '管理你的 AI 助手场景',
        extra: [
          <Radio.Group
            key="viewToggle"
            value={viewMode}
            onChange={(e) => setViewMode(e.target.value)}
            optionType="button"
            buttonStyle="solid"
            size="small"
          >
            <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
            <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
          </Radio.Group>,
          <Button
            key="create"
            type="primary"
            icon={<PlusOutlined />}
            onClick={openCreateModal}
            style={{ marginLeft: 8 }}
          >
            新建场景
          </Button>,
        ],
      }}
    >
      {viewMode === 'card' ? (
        workspaces.length === 0 && !loading ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="暂无场景，点击右上角「新建场景」创建你的第一个 AI 助手场景"
          >
            <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
              创建场景
            </Button>
          </Empty>
        ) : (
          <Row gutter={[16, 16]}>
            {workspaces.map((ws) => {
              const status = getStatusBadge(ws);
              return (
                <Col xs={24} sm={12} lg={8} xl={6} key={ws.workspaceId}>
                  <Card
                    hoverable
                    loading={loading}
                    style={{
                      borderRadius: 16,
                      background: 'rgba(250,250,247,0.72)',
                      backdropFilter: 'blur(8px)',
                      border: '1px solid rgba(124,58,237,0.18)',
                      boxShadow: '0 4px 24px rgba(124,58,237,0.06)',
                      height: '100%',
                    }}
                    bodyStyle={{ padding: '20px 20px 16px' }}
                  >
                    {/* Header: name + status */}
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 12 }}>
                      <Text strong style={{ fontSize: 16, maxWidth: '70%', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {ws.name}
                      </Text>
                      <Tag color={status.color}>{status.text}</Tag>
                    </div>

                    {/* Description */}
                    {ws.description && (
                      <Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 12, lineHeight: '18px' }}>
                        {ws.description.length > 60 ? `${ws.description.slice(0, 60)}…` : ws.description}
                      </Text>
                    )}

                    {/* Meta info */}
                    <div style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
                      <div>
                        <Text type="secondary" style={{ fontSize: 11 }}>成员</Text>
                        <div><Text strong style={{ fontSize: 14 }}>{ws.memberCount ?? 0}</Text></div>
                      </div>
                      <div>
                        <Text type="secondary" style={{ fontSize: 11 }}>场景 ID</Text>
                        <div><Text code style={{ fontSize: 11 }}>{ws.workspaceId.slice(0, 12)}…</Text></div>
                      </div>
                      <div>
                        <Text type="secondary" style={{ fontSize: 11 }}>创建</Text>
                        <div><Text style={{ fontSize: 12 }}>{new Date(ws.createdAt).toLocaleDateString('zh-CN')}</Text></div>
                      </div>
                    </div>

                    {/* Actions */}
                    <div style={{ display: 'flex', gap: 8, borderTop: '1px solid rgba(124,58,237,0.1)', paddingTop: 12 }}>
                      <Button
                        type="primary"
                        icon={<EnterOutlined />}
                        size="middle"
                        block
                        style={{
                          background: '#7c3aed',
                          borderColor: '#7c3aed',
                          fontWeight: 500,
                        }}
                        onClick={() => history.push(`/workspace/${ws.workspaceId}`)}
                      >
                        进入 Chat
                      </Button>
                      <Popconfirm
                        title="确认删除此场景？"
                        description="此操作不可恢复，内置场景无法删除。"
                        onConfirm={() => handleDelete(ws)}
                        okText="删除"
                        cancelText="取消"
                        okButtonProps={{ danger: true }}
                      >
                        <Button
                          danger
                          icon={<DeleteOutlined />}
                          size="middle"
                          disabled={ws.workspaceId === 'default'}
                        />
                      </Popconfirm>
                    </div>
                  </Card>
                </Col>
              );
            })}
          </Row>
        )
      ) : (
        <ProTable<WorkspaceWithPermDto>
          rowKey="workspaceId"
          columns={[
            ...tableColumns,
            {
              title: '操作',
              valueType: 'option',
              width: 180,
              render: (_: unknown, record: WorkspaceWithPermDto) => [
                <Tooltip title="进入 Chat" key="enter">
                  <Button
                    type="link"
                    icon={<EnterOutlined />}
                    size="small"
                    onClick={() => history.push(`/workspace/${record.workspaceId}`)}
                  >
                    进入 Chat
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
          ]}
          dataSource={workspaces}
          loading={loading}
          search={false}
          pagination={{ pageSize: 20 }}
          options={{ reload: fetchData, density: true }}
          cardBordered
          locale={{
            emptyText: (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="暂无场景，点击右上角「新建场景」按钮创建"
              />
            ),
          }}
        />
      )}

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

