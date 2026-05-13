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
  Card,
  Col,
  Drawer,
  Form,
  Popconfirm,
  Divider,
  Row,
  Space,
  Tag,
  Badge,
  Typography,
  message,
  Alert,
  Radio,
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
import React, { useRef, useState, useEffect } from 'react';
import {
  listGlobalAgentTemplates,
  createGlobalAgentTemplate,
  updateGlobalAgentTemplate,
  deleteGlobalAgentTemplate,
  listLlmProviders,
  listLlmModels,
  listCapabilities,
  listSkillPackages,
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
  const [capabilities, setCapabilities] = useState<CapabilityDto[]>([]);
  const [skillPackages, setSkillPackages] = useState<SkillPackageDto[]>([]);
  const [loadingModels, setLoadingModels] = useState(false);

  useEffect(() => {
    listLlmProviders().then(setProviders).catch(() => {});
    listCapabilities(true).then(setCapabilities).catch(() => {});
    listSkillPackages(true).then(setSkillPackages).catch(() => {});
  }, []);

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

  const openCreate = () => {
    setEditItem(null);
    form.resetFields();
    form.setFieldsValue({
      role: 'Service',
      isEnabled: true,
      sortOrder: 100,
      maxRounds: 200,
      maxElapsedSeconds: 1200,
      maxToolCallsTotal: 100,
      maxContextTokens: 8192,
      maxReplyTokens: 2048,
      selectedCapabilityIds: [],
      selectedSkillPackageIds: [],
    });
    setModels([]);
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
    form.setFieldsValue(item);
    setFormDrawer(true);
  };

  const handleSave = async () => {
    const values = await form.validateFields();
    if (editItem) {
      await updateGlobalAgentTemplate(editItem.templateId, values);
      message.success('模板已更新');
    } else {
      await createGlobalAgentTemplate(values);
      message.success('模板已创建');
    }
    setFormDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (templateId: string) => {
    await deleteGlobalAgentTemplate(templateId);
    message.success('模板已删除');
    tableRef.current?.reload();
  };

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
    <ProTable<GlobalAgentTemplateDto>
      actionRef={tableRef}
      rowKey="id"
      columns={[]}
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
      tableRender={(_, tableProps) => {
        const items = (tableProps?.dataSource ?? []) as GlobalAgentTemplateDto[];
        return (
          <Row gutter={[16, 16]} style={{ padding: '0 0 16px' }}>
            {items.map((item) => {
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
                        <Text style={{ fontSize: 18 }}>{item.avatarEmoji || '🤖'}</Text>
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
      }}
    />
  );

  return (
    <PageContainer
      title="全局 Agent 模板"
      subTitle="配置系统内置的 Agent 提示词与首选模型"
    >
      {viewMode === 'card' ? renderCards() : (
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

          <ProFormSelect
            name="selectedCapabilityIds"
            label="能力选择（多选）"
            mode="multiple"
            options={capabilities.map((c) => ({ label: `${c.name} (${c.toolName})`, value: c.capabilityId }))}
            placeholder="选择能力后，Runtime 会在上下文注入对应工具"
          />

          <ProFormSelect
            name="selectedSkillPackageIds"
            label="SKILL 包选择（多选）"
            mode="multiple"
            options={skillPackages.map((s) => ({ label: `${s.name} v${s.version}`, value: s.skillPackageId }))}
            placeholder="选择 SKILL 后，Runtime 会下载并挂载到容器内"
          />

          <ProFormTextArea
            name="systemPrompt"
            label="系统 Prompt"
            rows={6}
            placeholder="输入 Agent 的角色定义和行为准则…"
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
