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
  ImportOutlined,
} from '@ant-design/icons';
import React, { useRef, useState, useEffect, useCallback } from 'react';
import {
  listGlobalAgentTemplates,
  createGlobalAgentTemplate,
  updateGlobalAgentTemplate,
  deleteGlobalAgentTemplate,
  listGlobalAgentTemplatePresets,
  importGlobalAgentTemplatePreset,
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

import AgentTemplateSettingsDrawer from '@/pages/agent-template-settings/AgentTemplateSettingsDrawer';
import { collectErrorSections, findSectionByField } from '@/pages/agent-template-settings/types';

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
type PresetTemplateItem = GlobalAgentTemplateDto & { imported: boolean };

export interface GlobalAgentTemplateRequestDefaults {
  defaultCapIds: string[];
  grantTargetKeys: string[];
  skillTargetKeys: string[];
  /**
   * 兼容当前后端 Upsert DTO 的必填字段。
   * 模板页不再让用户编辑上下文/回复 token，真实窗口应来自 LLM 服务商与模型配置。
   */
  legacyMaxContextTokens?: number;
  legacyMaxReplyTokens?: number;
}

function uniqueStrings(values: (string | undefined | null)[]): string[] {
  const seen = new Set<string>();
  for (const value of values) {
    if (!value) continue;
    seen.add(value);
  }
  return Array.from(seen);
}

export function buildGlobalAgentTemplateRequest(
  values: UpsertGlobalAgentTemplateRequest,
  defaults: GlobalAgentTemplateRequestDefaults,
): UpsertGlobalAgentTemplateRequest {
  return {
    ...values,
    selectedCapabilityIds: uniqueStrings([...defaults.defaultCapIds, ...defaults.grantTargetKeys]),
    selectedSkillPackageIds: uniqueStrings(defaults.skillTargetKeys),
    memorySearchMode: values.memorySearchMode || 'deep',
    maxRounds: values.maxRounds ?? 200,
    maxElapsedSeconds: values.maxElapsedSeconds ?? 1200,
    maxToolCallsTotal: values.maxToolCallsTotal ?? 100,
    maxContextTokens: values.maxContextTokens ?? defaults.legacyMaxContextTokens ?? 8192,
    maxReplyTokens: values.maxReplyTokens ?? defaults.legacyMaxReplyTokens ?? 2048,
    isEnabled: values.isEnabled ?? true,
    sortOrder: values.sortOrder ?? 100,
  };
}

export function resolvePresetImportStates(
  presets: GlobalAgentTemplateDto[],
  templates: GlobalAgentTemplateDto[],
): PresetTemplateItem[] {
  const importedTemplateIds = new Set(templates.map((item) => item.templateId));
  return presets.map((preset) => ({
    ...preset,
    imported: importedTemplateIds.has(preset.templateId),
  }));
}

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
  const [embeddingModels, setEmbeddingModels] = useState<LlmModelDto[]>([]);
  const [loadingEmbeddingModels, setLoadingEmbeddingModels] = useState(false);
  const [data, setData] = useState<GlobalAgentTemplateDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [presetDrawer, setPresetDrawer] = useState(false);
  const [presets, setPresets] = useState<GlobalAgentTemplateDto[]>([]);
  const [loadingPresets, setLoadingPresets] = useState(false);
  const [importingPresetId, setImportingPresetId] = useState<string | null>(null);

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

  const fetchPresets = useCallback(async () => {
    setLoadingPresets(true);
    try {
      const result = await listGlobalAgentTemplatePresets();
      setPresets(result);
    } finally {
      setLoadingPresets(false);
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

  const handleEmbeddingProviderChange = async (providerId: string) => {
    form.setFieldValue('embeddingModelId', undefined);
    if (!providerId) {
      setEmbeddingModels([]);
      return;
    }
    setLoadingEmbeddingModels(true);
    try {
      const ms = await listLlmModels(providerId);
      setEmbeddingModels(ms.filter((m) => !m.isDeprecated && m.isEmbedding));
    } finally {
      setLoadingEmbeddingModels(false);
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
      avatarId: avatars[0]?.avatarId,
      selectedCapabilityIds: defaultCapIds,
      selectedSkillPackageIds: [],
    });
    setModels([]);
    setMemoryModels([]);
    setEmbeddingModels([]);
    setFormDrawer(true);
  };

  const openEdit = async (item: GlobalAgentTemplateDto) => {
    setEditItem(item);
    form.resetFields();
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
    if (item.embeddingProviderId) {
      const ms = await listLlmModels(item.embeddingProviderId);
      setEmbeddingModels(ms.filter((m) => !m.isDeprecated && m.isEmbedding));
    } else {
      setEmbeddingModels([]);
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
    const request = buildGlobalAgentTemplateRequest(values, {
      defaultCapIds,
      grantTargetKeys,
      skillTargetKeys,
      legacyMaxContextTokens: editItem?.maxContextTokens,
      legacyMaxReplyTokens: editItem?.maxReplyTokens,
    });
    if (editItem) {
      await updateGlobalAgentTemplate(editItem.templateId, request);
      message.success('模板已更新');
    } else {
      await createGlobalAgentTemplate(request);
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

  const openPresetDrawer = () => {
    setPresetDrawer(true);
    fetchPresets();
  };

  const handleImportPreset = async (templateId: string) => {
    setImportingPresetId(templateId);
    try {
      await importGlobalAgentTemplatePreset(templateId);
      message.success('预制模板已导入');
      await fetchData();
      await fetchPresets();
      tableRef.current?.reload();
    } finally {
      setImportingPresetId(null);
    }
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
      title: '运行环境',
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
              styles={{ body: { padding: '16px' } }}
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

  const renderPresetCards = () => {
    const items = resolvePresetImportStates(presets, data);
    if (!items.length && !loadingPresets) {
      return <div style={{ textAlign: 'center', padding: 40, color: '#999' }}>暂无系统预制模板</div>;
    }

    return (
      <Row gutter={[12, 12]}>
        {items.map((item) => {
          const roleColor = roleColorMap[item.role] ?? 'default';
          return (
            <Col xs={24} sm={12} key={item.templateId}>
              <Card size="small" styles={{ body: { padding: 16 } }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: 12 }}>
                  <Space align="start">
                    <Avatar size={30} src={item.avatarUrl || undefined}>
                      {item.avatarEmoji || <RobotOutlined />}
                    </Avatar>
                    <div>
                      <Space size={6} wrap>
                        <Text strong>{item.name}</Text>
                        <Tag color={roleColor}>{item.role}</Tag>
                      </Space>
                      <div style={{ marginTop: 6, color: '#666', lineHeight: 1.55 }}>
                        {item.description || '系统预制 Agent 模板'}
                      </div>
                    </div>
                  </Space>
                  <Button
                    type={item.imported ? 'default' : 'primary'}
                    size="small"
                    disabled={item.imported}
                    loading={importingPresetId === item.templateId}
                    onClick={() => handleImportPreset(item.templateId)}
                  >
                    {item.imported ? '已导入' : '导入'}
                  </Button>
                </div>
              </Card>
            </Col>
          );
        })}
      </Row>
    );
  };

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
      <Button icon={<ImportOutlined />} onClick={openPresetDrawer}>
        导入预制
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
          rowKey="templateId"
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
            <Button key="import" icon={<ImportOutlined />} onClick={openPresetDrawer}>
              导入预制
            </Button>,
          ]}
        />
      )}

      <AgentTemplateSettingsDrawer
        scope="global"
        open={formDrawer}
        mode={editItem ? 'edit' : 'create'}
        title={editItem ? '编辑 Agent 模板' : '创建 Agent 模板'}
        builtIn={editItem?.isBuiltIn}
        form={form}
        onClose={() => setFormDrawer(false)}
        onSave={handleSave}
        providers={providers}
        models={models}
        memoryModels={memoryModels}
        loadingModels={loadingModels}
        loadingMemoryModels={loadingMemoryModels}
        embeddingModels={embeddingModels}
        loadingEmbeddingModels={loadingEmbeddingModels}
        capabilities={capabilities}
        skillPackages={skillPackages}
        avatars={avatars}
        grantTargetKeys={grantTargetKeys}
        skillTargetKeys={skillTargetKeys}
        setGrantTargetKeys={setGrantTargetKeys}
        setSkillTargetKeys={setSkillTargetKeys}
        onProviderChange={handleProviderChange}
        onMemoryProviderChange={handleMemoryProviderChange}
        onEmbeddingProviderChange={handleEmbeddingProviderChange}
        defaultCapIds={defaultCapIds}
        grantCapabilities={grantCapabilities}
      />

      <Drawer
        title="导入系统预制模板"
        width={720}
        open={presetDrawer}
        onClose={() => setPresetDrawer(false)}
      >
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          message="系统预制模板来自软件随包 JSON，导入后会复制为可编辑的全局 Agent 模板。"
        />
        {loadingPresets ? <div style={{ textAlign: 'center', padding: 40 }}>加载中...</div> : renderPresetCards()}
      </Drawer>
    </PageContainer>
  );
};

export default GlobalAgentTemplatePage;
