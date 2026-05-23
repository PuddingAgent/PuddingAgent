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
  Avatar,
  Button,
  Card,
  Checkbox,
  Col,
  Drawer,
  Form,
  Popconfirm,
  Divider,
  Row,
  Space,
  Tag,
  Transfer,
  Badge,
  Typography,
  message,
  Alert,
  Radio,
  Select,
} from 'antd';
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  LockOutlined,
  AppstoreOutlined,
  TableOutlined,
  RobotOutlined,
} from '@ant-design/icons';
import React, { useRef, useState, useEffect, useCallback } from 'react';
import {
  listGlobalAgentTemplates,
  createGlobalAgentTemplate,
  updateGlobalAgentTemplate,
  deleteGlobalAgentTemplate,
  listAgentAvatars,
  listLlmProviders,
  listLlmModels,
  listCapabilities,
  listSkillPackages,
  type AgentAvatarDto,
  type GlobalAgentTemplateDto,
  type UpsertGlobalAgentTemplateRequest,
  type LlmProviderDto,
  type LlmModelDto,
  type CapabilityDto,
  type SkillPackageDto,
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

/** 记忆搜索模式颜色映射 */
const memoryModeColorMap: Record<string, string> = {
  off: 'default',
  instant: 'blue',
  deep: 'purple',
};

const memoryModeLabelMap: Record<string, string> = {
  off: '关闭',
  instant: '即时',
  deep: '深度',
};

type ViewMode = 'card' | 'table';

const GlobalAgentTemplatePage: React.FC = () => {
  const tableRef = useRef<ActionType | null>(null);
  const [viewMode, setViewMode] = useState<ViewMode>('card');
  const [formDrawer, setFormDrawer] = useState(false);
  const [editItem, setEditItem] = useState<GlobalAgentTemplateDto | null>(null);
  const [form] = Form.useForm<UpsertGlobalAgentTemplateRequest>();
  const [providers, setProviders] = useState<LlmProviderDto[]>([]);
  const [models, setModels] = useState<LlmModelDto[]>([]);
  const [memoryModels, setMemoryModels] = useState<LlmModelDto[]>([]);
  const [capabilities, setCapabilities] = useState<CapabilityDto[]>([]);
  const [skillPackages, setSkillPackages] = useState<SkillPackageDto[]>([]);
  const [avatars, setAvatars] = useState<AgentAvatarDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);
  const [loadingMemoryModels, setLoadingMemoryModels] = useState(false);
  const [data, setData] = useState<GlobalAgentTemplateDto[]>([]);
  const [loading, setLoading] = useState(false);

  // Transfer 组件目标 keys（高权限能力 + SKILL 包）
  const [grantTargetKeys, setGrantTargetKeys] = useState<string[]>([]);
  const [skillTargetKeys, setSkillTargetKeys] = useState<string[]>([]);

  // 默认能力 ID 列表（始终预设，不展示在 Transfer 中）
  const defaultCapIds = React.useMemo(
    () => capabilities.filter((c) => !c.requiresShellExecution && !c.requiresFileWrite).map((c) => c.capabilityId),
    [capabilities],
  );

  // 高权限能力 ID 列表
  const grantCapabilities = React.useMemo(
    () => capabilities.filter((c) => c.requiresShellExecution || c.requiresFileWrite),
    [capabilities],
  );

  const fetchData = useCallback(async () => {
    setLoading(true);
    try {
      const result = await listGlobalAgentTemplates();
      setData(result);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    listLlmProviders().then(setProviders).catch(() => {});
    listCapabilities(true).then(setCapabilities).catch(() => {});
    listSkillPackages(true).then(setSkillPackages).catch(() => {});
    listAgentAvatars(true).then(setAvatars).catch(() => {});
    fetchData();
  }, [fetchData]);

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

  const handleMemoryProviderChange = async (providerId: string) => {
    form.setFieldValue('memoryLlmModelId', undefined);
    if (!providerId) {
      setMemoryModels([]);
      return;
    }
    setLoadingMemoryModels(true);
    try {
      const ms = await listLlmModels(providerId);
      setMemoryModels(ms.filter((m) => !m.isDeprecated));
    } finally {
      setLoadingMemoryModels(false);
    }
  };

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    setGrantTargetKeys([]);
    setSkillTargetKeys([]);
    form.setFieldsValue({
      role: 'Service',
      isEnabled: true,
      sortOrder: 100,
      maxRounds: 200,
      maxElapsedSeconds: 1200,
      maxToolCallsTotal: 100,
      maxContextTokens: 8192,
      maxReplyTokens: 2048,
      avatarId: avatars[0]?.avatarId,
      selectedCapabilityIds: defaultCapIds,
      selectedSkillPackageIds: [],
    });
    setModels([]);
    setMemoryModels([]);
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
    if (item.memoryLlmProviderId) {
      const ms = await listLlmModels(item.memoryLlmProviderId);
      setMemoryModels(ms.filter((m) => !m.isDeprecated));
    } else {
      setMemoryModels([]);
    }
    form.setFieldsValue({ ...item, avatarId: item.avatarId || avatars[0]?.avatarId });
    // 同步 Transfer 组件 — 从 selectedCapabilityIds 中提取高权限部分
    const grantIds = (item.selectedCapabilityIds ?? []).filter((id) =>
      grantCapabilities.some((gc) => gc.capabilityId === id),
    );
    setGrantTargetKeys(grantIds);
    setSkillTargetKeys(item.selectedSkillPackageIds ?? []);
    setFormDrawer(true);
  };

  const handleSave = async () => {
    const values = await form.validateFields();
    // ADR-034：如果 avatarId 为空，补默认头像
    if (!values.avatarId && avatars.length > 0) {
      values.avatarId = avatars[0].avatarId;
    }
    if (editItem) {
      await updateGlobalAgentTemplate(editItem.templateId, values);
      message.success('模板已更新');
    } else {
      await createGlobalAgentTemplate(values);
      message.success('模板已创建');
    }
    setFormDrawer(false);
    tableRef.current?.reload();
    fetchData();
  };

  const handleDelete = async (templateId: string) => {
    await deleteGlobalAgentTemplate(templateId);
    message.success('模板已删除');
    tableRef.current?.reload();
    fetchData();
  };

  const renderAvatarSelectItem = (avatar: AgentAvatarDto, compact = false) => (
    <Space size={8} align="center" style={{ minWidth: 0 }}>
      <Avatar size={compact ? 20 : 24} src={avatar.url} />
      <span style={{ fontWeight: 500, whiteSpace: 'nowrap' }}>{avatar.name}</span>
      {!compact && avatar.recommendedUse && (
        <Text type="secondary" ellipsis style={{ fontSize: 12 }}>
          {avatar.recommendedUse}
        </Text>
      )}
    </Space>
  );

  const findAvatar = (avatarId: unknown) => avatars.find((a) => a.avatarId === String(avatarId));

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
      title: '执行护栏',
      width: 200,
      render: (_, r) => (
        <Space size={4}>
          <Tag>{r.maxRounds ?? 200}R</Tag>
          <Tag>{Math.round((r.maxElapsedSeconds ?? 1200) / 60)}min</Tag>
          <Tag>{r.maxToolCallsTotal ?? 100}T</Tag>
        </Space>
      ),
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

  // 卡片视图：角色名、模型、记忆模式（颜色Tag）、工具能力数、风险级别
  // 卡片左边线用角色颜色
  const renderCards = () => (
    <Row gutter={[16, 16]} style={{ padding: '0 0 16px' }}>
      {data.map((item) => {
        const roleColor = roleColorMap[item.role] ?? 'default';
        const capCount = item.selectedCapabilityIds?.length ?? 0;
        const skillCount = item.selectedSkillPackageIds?.length ?? 0;
        const memMode = item.memorySearchMode || 'deep';
        const memColor = memoryModeColorMap[memMode] || 'default';
        const memLabel = memoryModeLabelMap[memMode] || memMode;
        return (
          <Col xs={24} sm={12} lg={8} xl={6} key={item.id}>
            <Card
              hoverable
              size="small"
              style={{
                borderRadius: 14,
                borderLeft: `4px solid ${roleColor === 'blue' ? '#3b82f6' : roleColor === 'green' ? '#22c55e' : roleColor === 'orange' ? '#f97316' : '#7c3aed'}`,
                background: 'rgba(250,250,247,0.78)',
                backdropFilter: 'blur(8px)',
                boxShadow: '0 2px 16px rgba(0,0,0,0.04)',
              }}
              bodyStyle={{ padding: '16px' }}
            >
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 }}>
                <Space>
                  <Avatar size={28} src={item.avatarUrl || undefined}>
                    {item.avatarEmoji || '🤖'}
                  </Avatar>
                  <Text strong style={{ fontSize: 15 }}>{item.name}</Text>
                  {item.isBuiltIn && <Tag color="gold" icon={<LockOutlined />} style={{ fontSize: 10 }}>内置</Tag>}
                </Space>
                <Tag color={roleColor}>{item.role}</Tag>
              </div>

              <Space size={4} wrap style={{ marginBottom: 8 }}>
                {item.preferredModelId ? (
                  <Tag color="geekblue" style={{ fontSize: 11 }}>{item.preferredModelId}</Tag>
                ) : (
                  <Tag style={{ fontSize: 11 }}>平台默认</Tag>
                )}
                <Tag color={memColor} style={{ fontSize: 11 }}>记忆: {memLabel}</Tag>
                <Tag style={{ fontSize: 11 }}>{capCount} 工具</Tag>
                <Tag style={{ fontSize: 11 }}>{skillCount} SKILL</Tag>
                <Tag style={{ fontSize: 11 }}>{(item.maxContextTokens / 1000).toFixed(0)}K</Tag>
              </Space>

              <div style={{ borderTop: '1px solid #f0f0f0', paddingTop: 10, display: 'flex', gap: 4 }}>
                <Badge status={item.isEnabled ? 'processing' : 'default'} text={item.isEnabled ? '启用' : '停用'} />
                <div style={{ flex: 1 }} />
                <Button size="small" icon={<EditOutlined />} type="text" onClick={() => openEdit(item)} />
                {!item.isBuiltIn && (
                  <Popconfirm title="确认删除该模板？" onConfirm={() => handleDelete(item.templateId)}>
                    <Button size="small" danger icon={<DeleteOutlined />} type="text" />
                  </Popconfirm>
                )}
              </div>
            </Card>
          </Col>
        );
      })}
    </Row>
  );

  const toolbar = (
    <Space>
      <Radio.Group
        value={viewMode}
        onChange={(e) => setViewMode(e.target.value)}
        optionType="button"
        buttonStyle="solid"
        size="small"
      >
        <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
        <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
      </Radio.Group>
      <Button type="primary" icon={<PlusOutlined />} onClick={openCreate}>
        创建模板
      </Button>
    </Space>
  );

  return (
    <PageContainer
      title="全局 Agent 模板"
      subTitle="配置系统内置的 Agent 提示词与首选模型"
    >
      {viewMode === 'card' ? (
        <>
          <div style={{ marginBottom: 16 }}>{toolbar}</div>
          {loading ? <div style={{ textAlign: 'center', padding: 40 }}>加载中...</div> : renderCards()}
        </>
      ) : (
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
            <Radio.Group
              key="viewToggle"
              value={viewMode}
              onChange={(e) => setViewMode(e.target.value)}
              optionType="button"
              buttonStyle="solid"
              size="small"
              style={{ marginRight: 8 }}
            >
              <Radio.Button value="card"><AppstoreOutlined /> 卡片</Radio.Button>
              <Radio.Button value="table"><TableOutlined /> 表格</Radio.Button>
            </Radio.Group>,
            <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
              创建模板
            </Button>,
          ]}
        />
      )}

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

          {/* ── 默认能力（预设勾选，只读展示）── */}
          <Form.Item label="默认能力" help="始终可用，无需授权（只读、记忆、子代理等）">
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
              {capabilities
                .filter((c) => !c.requiresShellExecution && !c.requiresFileWrite)
                .map((c) => (
                  <Tag key={c.capabilityId} color="green" style={{ fontSize: 11, opacity: 0.85 }}>
                    {c.name}
                  </Tag>
                ))}
            </div>
          </Form.Item>

          {/* ── 高权限能力（Transfer 穿梭框 + 搜索）── */}
          <Form.Item
            label="高权限能力"
            help="需要显式授权：Shell 执行、文件写入、Python 等（右侧为已授权）"
          >
            <Transfer
              dataSource={grantCapabilities.map((c) => ({
                key: c.capabilityId,
                title: c.name,
                description: c.toolName,
              }))}
              titles={['可选', '已授权']}
              targetKeys={grantTargetKeys}
              onChange={(nextKeys) => {
                setGrantTargetKeys(nextKeys as string[]);
                // 合并默认能力 + 高权限能力 → selectedCapabilityIds
                const merged = [...defaultCapIds, ...(nextKeys as string[])];
                form.setFieldsValue({ selectedCapabilityIds: merged });
              }}
              showSearch
              filterOption={(inputValue, item) =>
                item.title.toLowerCase().includes(inputValue.toLowerCase()) ||
                (item.description ?? '').toLowerCase().includes(inputValue.toLowerCase())
              }
              render={(item) => (
                <span>
                  {item.title}
                  <span style={{ fontSize: 10, color: '#888', marginLeft: 6 }}>{item.description}</span>
                </span>
              )}
              listStyle={{ width: 240, height: 280 }}
              style={{ width: '100%' }}
            />
          </Form.Item>

          {/* ── SKILL 包选择（Transfer + 搜索）── */}
          <Form.Item label="SKILL 包选择" help="选择 Agent 可用的 Skill 包（右侧为已选）">
            <Transfer
              dataSource={skillPackages.map((s) => ({
                key: s.skillPackageId,
                title: s.name,
                description: `v${s.version}`,
              }))}
              titles={['可选', '已选']}
              targetKeys={skillTargetKeys}
              onChange={(nextKeys) => {
                setSkillTargetKeys(nextKeys as string[]);
                form.setFieldsValue({ selectedSkillPackageIds: nextKeys as string[] });
              }}
              showSearch
              filterOption={(inputValue, item) =>
                item.title.toLowerCase().includes(inputValue.toLowerCase()) ||
                (item.description ?? '').toLowerCase().includes(inputValue.toLowerCase())
              }
              render={(item) => (
                <span>
                  {item.title}
                  <Tag style={{ fontSize: 10, marginLeft: 4 }}>v{item.description}</Tag>
                </span>
              )}
              listStyle={{ width: 240, height: 240 }}
              style={{ width: '100%' }}
            />
          </Form.Item>

          {/* ── SKILL 包选择 ── */}
          <Form.Item label="SKILL 包选择（多选）">
            <Form.Item name="selectedSkillPackageIds" noStyle>
              <Checkbox.Group style={{ display: 'flex', flexWrap: 'wrap', gap: '6px 20px' }}>
                {skillPackages.map((s) => (
                  <Checkbox key={s.skillPackageId} value={s.skillPackageId} style={{ marginRight: 0, fontSize: 12 }}>
                    {s.name} <Tag style={{ fontSize: 10, marginLeft: 4 }}>v{s.version}</Tag>
                  </Checkbox>
                ))}
              </Checkbox.Group>
            </Form.Item>
          </Form.Item>

          <ProFormTextArea
            name="systemPrompt"
            label="系统 Prompt"
            rows={6}
            placeholder="输入 Agent 的角色定义和行为准则…"
          />

          <Divider orientation="left">个性设置</Divider>
          <Form.Item
            name="avatarId"
            label="头像"
            rules={[{ required: true, message: '请选择头像' }]}
            initialValue={avatars[0]?.avatarId}
          >
            <Select
              placeholder="选择系统头像"
              loading={avatars.length === 0}
              options={avatars.map((a) => ({
                label: a.name,
                value: a.avatarId,
              }))}
              optionRender={(option) => {
                const avatar = findAvatar(option.value);
                if (!avatar) return option.label;
                return renderAvatarSelectItem(avatar);
              }}
              labelRender={(option) => {
                const avatar = findAvatar(option.value);
                if (!avatar) return option.label;
                return renderAvatarSelectItem(avatar, true);
              }}
            />
          </Form.Item>
          <ProFormTextArea
            name="personaPrompt"
            label="人设 / 语气 / 边界（SOUL）"
            rows={4}
            placeholder="定义该 Agent 的表达风格、价值观边界与行为准则"
          />
          <ProFormTextArea
            name="toolsDescription"
            label="工具使用约定（TOOLS）"
            rows={4}
            placeholder="约定何时调用工具、如何解释结果、失败时如何降级"
          />
          <ProFormTextArea
            name="bootstrapTemplate"
            label="首次引导模板（BOOTSTRAP）"
            rows={6}
            placeholder="定义首次对话的开场与引导模板"
          />

          <Divider orientation="left">潜意识模型（记忆探索）</Divider>
          <ProFormSelect
            name="memoryLlmProviderId"
            label="潜意识模型服务商"
            options={providers.filter((p) => p.isEnabled).map((p) => ({ label: p.name, value: p.providerId }))}
            placeholder="不选则跟随主聊天模型"
            extra="端点、Key 和供应商参数在 LLM 资源池配置，这里只选择服务商。"
            fieldProps={{ onChange: handleMemoryProviderChange, allowClear: true }}
          />
          <ProFormSelect
            name="memoryLlmModelId"
            label="潜意识模型"
            options={memoryModels.map((m) => ({
              label: `${m.name} (${m.modelId})`,
              value: m.modelId,
            }))}
            placeholder="不选则使用该服务商默认模型"
            extra="建议选择轻量模型，用于记忆深度探索。"
            fieldProps={{ loading: loadingMemoryModels, allowClear: true }}
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

          <Divider orientation="left">推理设置</Divider>
          <ProFormSelect
            name="reasoningEffort"
            label="推理深度"
            options={[
              { label: '跟随模型默认', value: '' },
              { label: '低（快速响应）', value: 'low' },
              { label: '中（平衡）', value: 'medium' },
              { label: '高（深度思考）', value: 'high' },
            ]}
          />

          <Divider orientation="left">执行护栏</Divider>
          <Space size="large">
            <ProFormDigit name="maxRounds" label="最大轮次" min={1} max={1000} initialValue={200} />
            <ProFormDigit name="maxElapsedSeconds" label="最大耗时(秒)" min={10} max={7200} initialValue={1200} />
            <ProFormDigit name="maxToolCallsTotal" label="最大工具调用" min={1} max={500} initialValue={100} />
          </Space>

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
