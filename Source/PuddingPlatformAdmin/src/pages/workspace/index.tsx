/**
 * Workspace 页面 — 去 Pro 模板化样板页
 *
 * 使用 Pudding wrapper 组件：PuddingPageHeader、PuddingToolbar、PuddingDataTable、PuddingStatusBadge、PuddingEntityCard。
 * 桌面端默认表格视图，卡片视图作为辅助。
 * 不使用 PageContainer、ProTable、DefaultFooter。
 */
import {
  DeleteOutlined,
  EnterOutlined,
  HomeOutlined,
  PlusOutlined,
  SettingOutlined,
  AppstoreOutlined,
  TableOutlined,
} from '@ant-design/icons';
import {
  App,
  Button,
  Col,
  Form,
  Input,
  Modal,
  Popconfirm,
  Row,
  Segmented,
  Select,
  Space,
  Tooltip,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import dayjs from 'dayjs';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { history } from '@umijs/max';
import {
  createWorkspace,
  deleteWorkspace,
  listWorkspaceAgents,
  listTeams,
  listWorkspaces,
  type CreateWorkspaceRequest,
  type WorkspaceWithPermDto,
} from '@/services/platform/api';
import {
  PuddingPageHeader,
  PuddingToolbar,
  PuddingDataTable,
  PuddingStatusBadge,
  PuddingEntityCard,
  WorkspaceNavigationHeader,
} from '@/components';
import type { PuddingStatusTone } from '@/components';
import {
  buildChatPath,
  buildWorkspaceSettingsPath,
  buildWorkspaceStudioPath,
  clearRecentWorkspaceVisit,
  readRecentWorkspaceVisit,
} from '@/utils/workspaceNavigation';
import styles from './styles';

const { Text } = Typography;

interface CreateSceneFormValues {
  name: string;
  description?: string;
}

type ViewMode = 'table' | 'card';

interface WorkspaceStatus {
  tone: PuddingStatusTone;
  label: string;
}

/** 获取工作空间状态映射 */
const getWorkspaceStatus = (workspace: WorkspaceWithPermDto): WorkspaceStatus => {
  if (workspace.isFrozen) return { tone: 'danger', label: '已冻结' };
  if (!workspace.isEnabled) return { tone: 'neutral', label: '已停用' };
  return { tone: 'success', label: '运行中' };
};

/** 桌面端默认表格，移动端默认卡片 */
const getInitialViewMode = (): ViewMode => {
  if (typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches) {
    return 'card';
  }
  return 'table';
};

const statusOptions = [
  { value: 'all', label: '全部状态' },
  { value: 'success', label: '运行中' },
  { value: 'danger', label: '已冻结' },
  { value: 'neutral', label: '已停用' },
];

const WorkspacePage: React.FC = () => {
  const { message } = App.useApp();
  const [workspaces, setWorkspaces] = useState<WorkspaceWithPermDto[]>([]);
  const [agentCounts, setAgentCounts] = useState<Record<string, number | undefined>>({});
  const [loading, setLoading] = useState(false);
  const [viewMode, setViewMode] = useState<ViewMode>(getInitialViewMode);
  const [query, setQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState('all');

  const [createOpen, setCreateOpen] = useState(false);
  const [createLoading, setCreateLoading] = useState(false);
  const [form] = Form.useForm<CreateSceneFormValues>();

  const fetchData = useCallback(async () => {
    setLoading(true);
    try {
      const data = await listWorkspaces();
      setWorkspaces(data);
      const counts = await Promise.all(data.map(async (workspace) => {
        try {
          const agents = await listWorkspaceAgents(workspace.workspaceId);
          return [workspace.workspaceId, agents.length] as const;
        } catch {
          return [workspace.workspaceId, undefined] as const;
        }
      }));
      setAgentCounts(Object.fromEntries(counts));
    } catch {
      message.error('加载工作空间列表失败');
    } finally {
      setLoading(false);
    }
  }, [message]);

  useEffect(() => { fetchData(); }, [fetchData]);

  /** 搜索与筛选 */
  const filteredWorkspaces = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase();
    return workspaces.filter((workspace) => {
      const matchesQuery =
        !normalizedQuery ||
        workspace.name?.toLowerCase().includes(normalizedQuery) ||
        workspace.workspaceId?.toLowerCase().includes(normalizedQuery);

      const status = getWorkspaceStatus(workspace).tone;
      const matchesStatus = statusFilter === 'all' || statusFilter === status;

      return matchesQuery && matchesStatus;
    });
  }, [query, statusFilter, workspaces]);

  const openCreateModal = useCallback(() => {
    form.resetFields();
    setCreateOpen(true);
  }, [form]);

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
        .slice(0, 48) || 'workspace';

      const request: CreateWorkspaceRequest = {
        workspaceId: `${slugBase}-${Date.now().toString().slice(-6)}`,
        teamId: defaultTeamId,
        name: values.name,
        description: values.description,
        teamAccessPolicy: 'Write',
        companyAccessPolicy: 'None',
      };

      await createWorkspace(request);
      message.success(`工作空间 "${values.name}" 创建成功`);
      setCreateOpen(false);
      history.push(buildWorkspaceStudioPath({ workspaceId: request.workspaceId }));
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
      const recentVisit = readRecentWorkspaceVisit();
      if (recentVisit?.workspaceId === ws.workspaceId) clearRecentWorkspaceVisit();
      message.success(`工作空间 "${ws.name}" 已删除`);
      fetchData();
    } catch {
      message.error('删除失败，请稍后重试');
    }
  };

  /** 表格列定义 */
  const columns: ColumnsType<WorkspaceWithPermDto> = useMemo(() => [
    {
      title: '工作空间',
      dataIndex: 'name',
      render: (_, record) => (
        <div style={styles.nameCell}>
          <Space>
            <span style={styles.name}>{record.name}</span>
            {record.workspaceId === 'default' && (
              <PuddingStatusBadge tone="accent">内置</PuddingStatusBadge>
            )}
          </Space>
          {record.description && (
            <span style={styles.nameDescription}>{record.description}</span>
          )}
        </div>
      ),
    },
    {
      title: '状态',
      width: 112,
      render: (_, record) => {
        const status = getWorkspaceStatus(record);
        return <PuddingStatusBadge tone={status.tone}>{status.label}</PuddingStatusBadge>;
      },
    },
    {
      title: 'Agent',
      width: 88,
      align: 'right',
      render: (_, record) => agentCounts[record.workspaceId] ?? '—',
    },
    {
      title: 'Workspace ID',
      dataIndex: 'workspaceId',
      width: 220,
      render: (value) => <Text code copyable={{ text: value }}>{value}</Text>,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      width: 160,
      render: (value) => dayjs(value).format('YYYY-MM-DD'),
    },
    {
      title: '操作',
      key: 'actions',
      width: 176,
      render: (_, record) => (
        <Space size={4}>
          <Tooltip title={`打开 ${record.name} 工作室`}>
            <Button
              aria-label={`打开 ${record.name} 工作室`}
              icon={<HomeOutlined />}
              onClick={() => history.push(buildWorkspaceStudioPath({ workspaceId: record.workspaceId }))}
            />
          </Tooltip>
          <Tooltip title={`进入 ${record.name} 对话`}>
            <Button
              aria-label={`进入 ${record.name} 对话`}
              icon={<EnterOutlined />}
              onClick={() => history.push(buildChatPath({ workspaceId: record.workspaceId }))}
            />
          </Tooltip>
          <Tooltip title={`设置 ${record.name}`}>
            <Button
              aria-label={`设置 ${record.name}`}
              icon={<SettingOutlined />}
              onClick={() => history.push(buildWorkspaceSettingsPath(record.workspaceId))}
            />
          </Tooltip>
          <Popconfirm
            title="确认删除此工作空间？"
            description="此操作不可恢复，内置工作空间无法删除。"
            onConfirm={() => handleDelete(record)}
            okText="删除"
            cancelText="取消"
            okButtonProps={{ danger: true }}
          >
            <Tooltip title={record.workspaceId === 'default' ? '内置工作空间不可删除' : '删除'}>
              <Button
                aria-label={`删除 ${record.name}`}
                danger
                icon={<DeleteOutlined />}
                disabled={record.workspaceId === 'default'}
              />
            </Tooltip>
          </Popconfirm>
        </Space>
      ),
    },
  ], [agentCounts]);

  const emptyText = useMemo(() => (
    <div style={styles.emptyState}>
      <div style={styles.emptyTitle}>暂无工作空间</div>
      <div style={styles.emptyDescription}>
        创建一个工作空间后，就可以组织多个 Agent 一起处理任务。
      </div>
      <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
        新建工作空间
      </Button>
    </div>
  ), [openCreateModal]);

  const renderTable = () => (
    <PuddingDataTable<WorkspaceWithPermDto>
      rowKey="workspaceId"
      columns={columns}
      dataSource={filteredWorkspaces}
      loading={loading}
      emptyText={emptyText}
      pagination={{ pageSize: 20, showSizeChanger: false }}
    />
  );

  const renderCards = () => {
    if (filteredWorkspaces.length === 0 && !loading) {
      return (
        <div style={styles.emptyState}>
          <div style={styles.emptyTitle}>暂无工作空间</div>
          <div style={styles.emptyDescription}>
            创建一个工作空间后，就可以组织多个 Agent 一起处理任务。
          </div>
          <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
            新建工作空间
          </Button>
        </div>
      );
    }

    return (
      <div style={styles.cardGrid}>
        <Row gutter={[16, 16]}>
          {filteredWorkspaces.map((ws) => {
            const status = getWorkspaceStatus(ws);
            return (
              <Col xs={24} sm={12} lg={8} xl={6} key={ws.workspaceId}>
                <PuddingEntityCard
                  title={ws.name}
                  status={<PuddingStatusBadge tone={status.tone}>{status.label}</PuddingStatusBadge>}
                  description={ws.description}
                  meta={[
                    { label: 'Agent', value: agentCounts[ws.workspaceId] ?? '—' },
                    { label: '成员', value: ws.memberCount ?? 0 },
                    { label: '创建', value: dayjs(ws.createdAt).format('YYYY-MM-DD') },
                  ]}
                  actions={
                    <>
                      <Button
                        type="primary"
                        icon={<HomeOutlined />}
                        block
                        onClick={() => history.push(buildWorkspaceStudioPath({ workspaceId: ws.workspaceId }))}
                      >
                        进入工作室
                      </Button>
                      <Button
                        icon={<EnterOutlined />}
                        block
                        onClick={() => history.push(buildChatPath({ workspaceId: ws.workspaceId }))}
                      >
                        进入对话
                      </Button>
                      <Button
                        icon={<SettingOutlined />}
                        block
                        onClick={() => history.push(buildWorkspaceSettingsPath(ws.workspaceId))}
                      >
                        设置
                      </Button>
                      <Popconfirm
                        title="确认删除此工作空间？"
                        description="此操作不可恢复，内置工作空间无法删除。"
                        onConfirm={() => handleDelete(ws)}
                        okText="删除"
                        cancelText="取消"
                        okButtonProps={{ danger: true }}
                      >
                        <Button
                          danger
                          icon={<DeleteOutlined />}
                          disabled={ws.workspaceId === 'default'}
                          aria-label={`删除 ${ws.name}`}
                        />
                      </Popconfirm>
                    </>
                  }
                />
              </Col>
            );
          })}
        </Row>
      </div>
    );
  };

  return (
    <div style={styles.shell}>
      <WorkspaceNavigationHeader crumbs={[{ label: '工作空间', title: '工作空间' }]} />

      <main style={styles.content}>
        <section style={styles.page}>
          <PuddingPageHeader
            title="工作空间"
            description="选择一个工作场所，查看其中的 Agent 状态并进入工作室。"
            actions={
              <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
                新建工作空间
              </Button>
            }
          />

          <PuddingToolbar
            leading={
              <Input.Search
                allowClear
                placeholder="搜索名称或 Workspace ID"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
              />
            }
            filters={
              <Select
                value={statusFilter}
                onChange={setStatusFilter}
                options={statusOptions}
                aria-label="按状态筛选工作空间"
                style={{ minWidth: 120 }}
              />
            }
            actions={
              <Segmented
                value={viewMode}
                onChange={(value) => setViewMode(value as ViewMode)}
                options={[
                  { label: '表格', value: 'table', icon: <TableOutlined /> },
                  { label: '卡片', value: 'card', icon: <AppstoreOutlined /> },
                ]}
              />
            }
          />

          {viewMode === 'table' ? renderTable() : renderCards()}
        </section>
      </main>

      <Modal
        title="新建工作空间"
        open={createOpen}
        onOk={handleCreate}
        onCancel={() => setCreateOpen(false)}
        confirmLoading={createLoading}
        okText="创建"
        cancelText="取消"
        width={520}
        destroyOnHidden
      >
        <Form form={form} layout="vertical" style={styles.createForm}>
          <Form.Item
            name="name"
            label="名称"
            rules={[
              { required: true, message: '请输入工作空间名称' },
              { max: 128, message: '最多 128 个字符' },
            ]}
          >
            <Input autoFocus placeholder="例如：默认工作空间" />
          </Form.Item>

          <Form.Item
            name="description"
            label="描述"
            extra="用于帮助成员理解这个工作空间的用途。"
          >
            <Input.TextArea placeholder="可选" rows={3} maxLength={512} showCount />
          </Form.Item>
        </Form>
      </Modal>
    </div>
  );
};

const WorkspacePageWrapper: React.FC = () => (
  <App>
    <WorkspacePage />
  </App>
);

export default WorkspacePageWrapper;

