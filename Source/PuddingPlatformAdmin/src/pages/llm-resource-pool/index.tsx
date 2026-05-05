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
  Drawer,
  Form,
  Modal,
  Popconfirm,
  Space,
  Tag,
  Tabs,
  Typography,
  Badge,
  Statistic,
  Progress,
  Divider,
  Input,
  message,
  Tooltip,
} from 'antd';
import { PlusOutlined, EditOutlined, DeleteOutlined, KeyOutlined, BarChartOutlined } from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  listLlmProviders,
  getLlmProvider,
  createLlmProvider,
  updateLlmProvider,
  deleteLlmProvider,
  updateLlmProviderQuota,
  resetDailyQuota,
  listLlmModels,
  createLlmModel,
  updateLlmModel,
  deleteLlmModel,
  type LlmProviderDto,
  type LlmProviderDetailDto,
  type LlmModelDto,
  type UpsertLlmProviderRequest,
  type UpsertLlmModelRequest,
  type UpdateQuotaRequest,
} from '@/services/platform/api';

const { Text } = Typography;

const PROTOCOLS = [
  { label: 'OpenAI 协议', value: 'openai' },
  { label: 'Anthropic 协议', value: 'anthropic' },
  { label: 'Azure OpenAI', value: 'azure-openai' },
  { label: '自定义协议', value: 'custom' },
];

const CAPABILITY_TAGS = [
  { label: '文本', value: 'text' },
  { label: '视觉', value: 'vision' },
  { label: '函数调用', value: 'function-calling' },
  { label: 'JSON 模式', value: 'json-mode' },
  { label: '流式输出', value: 'streaming' },
  { label: '长文本', value: 'long-context' },
  { label: '代码', value: 'code' },
  { label: '推理', value: 'reasoning' },
];

const tagColorMap: Record<string, string> = {
  text: 'blue',
  vision: 'purple',
  'function-calling': 'green',
  'json-mode': 'orange',
  streaming: 'cyan',
  'long-context': 'geekblue',
  code: 'volcano',
  reasoning: 'magenta',
};

// ── 模型子表格 ────────────────────────────────────────────────────
const ModelTable: React.FC<{ provider: LlmProviderDetailDto; onRefresh: () => void }> = ({
  provider,
  onRefresh,
}) => {
  const [editModel, setEditModel] = useState<LlmModelDto | null>(null);
  const [modelDrawer, setModelDrawer] = useState(false);
  const [modelForm] = Form.useForm<UpsertLlmModelRequest>();

  const openCreate = () => {
    setEditModel(null);
    modelForm.resetFields();
    modelForm.setFieldsValue({
      sortOrder: 100,
      maxContextTokens: 8192,
      maxOutputTokens: 2048,
      isDefault: false,
      isDeprecated: false,
    });
    setModelDrawer(true);
  };

  const openEdit = (m: LlmModelDto) => {
    setEditModel(m);
    modelForm.setFieldsValue({ ...m, capabilityTags: m.capabilityTags });
    setModelDrawer(true);
  };

  const handleSave = async () => {
    const values = await modelForm.validateFields();
    if (editModel) {
      await updateLlmModel(provider.providerId, editModel.modelId, values);
      message.success('模型已更新');
    } else {
      await createLlmModel(provider.providerId, values);
      message.success('模型已创建');
    }
    setModelDrawer(false);
    onRefresh();
  };

  const handleDelete = async (m: LlmModelDto) => {
    await deleteLlmModel(provider.providerId, m.modelId);
    message.success('模型已删除');
    onRefresh();
  };

  const columns: ProColumns<LlmModelDto>[] = [
    {
      title: '模型 ID',
      dataIndex: 'modelId',
      copyable: true,
      width: 180,
      render: (_, r) => (
        <Space size={4}>
          <Text code>{r.modelId}</Text>
          {r.isDefault && <Tag color="gold">默认</Tag>}
          {r.isDeprecated && <Tag color="default">已弃用</Tag>}
        </Space>
      ),
    },
    { title: '名称', dataIndex: 'name', width: 160 },
    {
      title: '上下文长度',
      dataIndex: 'maxContextTokens',
      width: 120,
      render: (_, r) => `${(r.maxContextTokens / 1000).toFixed(0)}K tokens`,
    },
    {
      title: '输出长度',
      dataIndex: 'maxOutputTokens',
      width: 120,
      render: (_, r) => `${r.maxOutputTokens.toLocaleString()} tokens`,
    },
    {
      title: '输入价格 ($/1M)',
      dataIndex: 'inputPricePer1MTokens',
      width: 130,
      render: (_, r) => `$${r.inputPricePer1MTokens.toFixed(4)}`,
    },
    {
      title: '输出价格 ($/1M)',
      dataIndex: 'outputPricePer1MTokens',
      width: 130,
      render: (_, r) => `$${r.outputPricePer1MTokens.toFixed(4)}`,
    },
    {
      title: '能力标签',
      dataIndex: 'capabilityTags',
      width: 220,
      render: (_, r) =>
        r.capabilityTags.map((t) => (
          <Tag key={t} color={tagColorMap[t] ?? 'default'} style={{ marginBottom: 2 }}>
            {CAPABILITY_TAGS.find((c) => c.value === t)?.label ?? t}
          </Tag>
        )),
    },
    {
      title: '操作',
      width: 120,
      render: (_, r) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除该模型？" onConfirm={() => handleDelete(r)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <>
      <ProTable<LlmModelDto>
        dataSource={provider.models}
        rowKey="id"
        columns={columns}
        search={false}
        options={false}
        size="small"
        pagination={false}
        toolBarRender={() => [
          <Button key="add" type="primary" size="small" icon={<PlusOutlined />} onClick={openCreate}>
            添加模型
          </Button>,
        ]}
      />

      <Drawer
        title={editModel ? '编辑模型' : '添加模型'}
        open={modelDrawer}
        width={540}
        onClose={() => setModelDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSave}>
            保存
          </Button>
        }
      >
        <ProForm form={modelForm} submitter={false} layout="vertical">
          <ProFormText name="modelId" label="模型 ID" rules={[{ required: true }]}
            disabled={!!editModel} placeholder="如 gpt-4o-mini" />
          <ProFormText name="name" label="显示名称" rules={[{ required: true }]} />
          <ProFormTextArea name="description" label="描述" rows={3} />
          <ProFormDigit name="maxContextTokens" label="上下文长度 (tokens)"
            rules={[{ required: true }]} min={1024} />
          <ProFormDigit name="maxOutputTokens" label="输出长度 (tokens)"
            rules={[{ required: true }]} min={1} />
          <ProFormDigit name="inputPricePer1MTokens" label="输入价格 ($/1M tokens)"
            fieldProps={{ precision: 6 }} />
          <ProFormDigit name="outputPricePer1MTokens" label="输出价格 ($/1M tokens)"
            fieldProps={{ precision: 6 }} />
          <ProFormSelect
            name="capabilityTags"
            label="能力标签"
            mode="multiple"
            options={CAPABILITY_TAGS}
          />
          <Space>
            <ProFormSwitch name="isDefault" label="设为默认" />
            <ProFormSwitch name="isDeprecated" label="标记已弃用" />
          </Space>
          <ProFormDigit name="sortOrder" label="排序权重" min={0} />
        </ProForm>
      </Drawer>
    </>
  );
};

// ── 主页面 ────────────────────────────────────────────────────────
const LlmResourcePoolPage: React.FC = () => {
  const tableRef = useRef<ActionType | null>(null);
  const [detailDrawer, setDetailDrawer] = useState(false);
  const [detailProvider, setDetailProvider] = useState<LlmProviderDetailDto | null>(null);
  const [providerDrawer, setProviderDrawer] = useState(false);
  const [editProvider, setEditProvider] = useState<LlmProviderDto | null>(null);
  const [providerForm] = Form.useForm<UpsertLlmProviderRequest>();
  const [quotaModal, setQuotaModal] = useState(false);
  const [quotaProviderId, setQuotaProviderId] = useState('');
  const [quotaForm] = Form.useForm<UpdateQuotaRequest>();
  const [apiKeyVisible, setApiKeyVisible] = useState(false);

  const openDetail = async (provider: LlmProviderDto) => {
    const detail = await getLlmProvider(provider.providerId);
    setDetailProvider(detail);
    setDetailDrawer(true);
  };

  const openCreate = () => {
    setEditProvider(null);
    providerForm.resetFields();
    providerForm.setFieldsValue({ protocol: 'openai', isEnabled: true });
    setApiKeyVisible(false);
    setProviderDrawer(true);
  };

  const openEdit = (p: LlmProviderDto) => {
    setEditProvider(p);
    providerForm.setFieldsValue({ ...p, apiKey: undefined });
    setApiKeyVisible(false);
    setProviderDrawer(true);
  };

  const handleSaveProvider = async () => {
    const values = await providerForm.validateFields();
    if (editProvider) {
      await updateLlmProvider(editProvider.providerId, values);
      message.success('服务商已更新');
    } else {
      await createLlmProvider(values);
      message.success('服务商已创建');
    }
    setProviderDrawer(false);
    tableRef.current?.reload();
  };

  const handleDelete = async (providerId: string) => {
    await deleteLlmProvider(providerId);
    message.success('服务商已删除');
    tableRef.current?.reload();
  };

  const openQuota = (providerId: string) => {
    setQuotaProviderId(providerId);
    quotaForm.resetFields();
    setQuotaModal(true);
  };

  const handleSaveQuota = async () => {
    const values = await quotaForm.validateFields();
    await updateLlmProviderQuota(quotaProviderId, values);
    message.success('配额已更新');
    setQuotaModal(false);
  };

  const handleResetDaily = async (providerId: string) => {
    await resetDailyQuota(providerId);
    message.success('今日配额已重置');
  };

  const refreshDetail = async () => {
    if (detailProvider) {
      const updated = await getLlmProvider(detailProvider.providerId);
      setDetailProvider(updated);
    }
    tableRef.current?.reload();
  };

  const columns: ProColumns<LlmProviderDto>[] = [
    {
      title: '服务商',
      dataIndex: 'name',
      render: (_, r) => (
        <Space>
          <span style={{ fontWeight: 500 }}>{r.name}</span>
          <Text type="secondary" style={{ fontSize: 12 }}>
            {r.providerId}
          </Text>
        </Space>
      ),
    },
    {
      title: '协议',
      dataIndex: 'protocol',
      width: 130,
      render: (_, r) => {
        const p = PROTOCOLS.find((x) => x.value === r.protocol);
        return <Tag color="blue">{p?.label ?? r.protocol}</Tag>;
      },
    },
    {
      title: 'API 地址',
      dataIndex: 'baseUrl',
      ellipsis: true,
      render: (_, r) => <Text type="secondary">{r.baseUrl}</Text>,
    },
    {
      title: 'API Key',
      width: 100,
      render: (_, r) =>
        r.hasApiKey ? <Badge status="success" text="已配置" /> : <Badge status="default" text="未配置" />,
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
      width: 200,
      render: (_, r) => (
        <Space size={4}>
          <Button size="small" onClick={() => openDetail(r)}>
            模型
          </Button>
          <Tooltip title="配置配额">
            <Button size="small" icon={<BarChartOutlined />} onClick={() => openQuota(r.providerId)} />
          </Tooltip>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(r)} />
          <Popconfirm title="确认删除该服务商及其所有模型？" onConfirm={() => handleDelete(r.providerId)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer title="LLM 资源池" subTitle="管理 LLM 服务商、模型配置与 API 配额">
      <ProTable<LlmProviderDto>
        actionRef={tableRef}
        rowKey="id"
        columns={columns}
        request={async () => {
          const data = await listLlmProviders();
          return { data, success: true };
        }}
        search={false}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            添加服务商
          </Button>,
        ]}
      />

      {/* ── 服务商表单 Drawer ── */}
      <Drawer
        title={editProvider ? '编辑服务商' : '添加服务商'}
        open={providerDrawer}
        width={520}
        onClose={() => setProviderDrawer(false)}
        extra={
          <Button type="primary" onClick={handleSaveProvider}>
            保存
          </Button>
        }
      >
        <ProForm form={providerForm} submitter={false} layout="vertical">
          <ProFormText
            name="providerId"
            label="服务商标识 (ID)"
            rules={[{ required: true }, { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' }]}
            disabled={!!editProvider}
            placeholder="如 openai、deepseek、my-provider"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormSelect name="protocol" label="协议类型" options={PROTOCOLS} rules={[{ required: true }]} />
          <ProFormText name="baseUrl" label="API 基础地址" rules={[{ required: true }]}
            placeholder="如 https://api.openai.com/v1" />
          <Form.Item label="API Key" name="apiKey">
            <Input.Password
              placeholder={editProvider ? '留空表示不修改现有 Key' : '输入 API Key'}
              visibilityToggle={{ visible: apiKeyVisible, onVisibleChange: setApiKeyVisible }}
              prefix={<KeyOutlined />}
            />
          </Form.Item>
          <ProFormTextArea name="description" label="描述" rows={3} />
          <ProFormSwitch name="isEnabled" label="启用" />
        </ProForm>
      </Drawer>

      {/* ── 服务商详情（含模型列表）Drawer ── */}
      <Drawer
        title={detailProvider ? `${detailProvider.name} — 模型管理` : '模型管理'}
        open={detailDrawer}
        width={900}
        onClose={() => setDetailDrawer(false)}
      >
        {detailProvider && (
          <Tabs
            items={[
              {
                key: 'models',
                label: '模型列表',
                children: <ModelTable provider={detailProvider} onRefresh={refreshDetail} />,
              },
              {
                key: 'quota',
                label: '配额状态',
                children: (
                  <div style={{ padding: '16px 0' }}>
                    {detailProvider.quota ? (
                      <>
                        <Space size="large" style={{ marginBottom: 24 }}>
                          <Statistic
                            title="今日已用 tokens"
                            value={detailProvider.quota.dailyTokensUsed.toLocaleString()}
                            suffix={
                              detailProvider.quota.dailyTokenLimit
                                ? `/ ${detailProvider.quota.dailyTokenLimit.toLocaleString()}`
                                : '（无限制）'
                            }
                          />
                          <Statistic
                            title="本月已用 tokens"
                            value={detailProvider.quota.monthlyTokensUsed.toLocaleString()}
                            suffix={
                              detailProvider.quota.monthlyTokenLimit
                                ? `/ ${detailProvider.quota.monthlyTokenLimit.toLocaleString()}`
                                : '（无限制）'
                            }
                          />
                        </Space>
                        {detailProvider.quota.dailyTokenLimit && (
                          <>
                            <Text type="secondary">今日配额使用率</Text>
                            <Progress
                              percent={Math.min(
                                100,
                                Math.round(
                                  (detailProvider.quota.dailyTokensUsed /
                                    detailProvider.quota.dailyTokenLimit) *
                                    100,
                                ),
                              )}
                              status={detailProvider.quota.isSuspended ? 'exception' : 'active'}
                            />
                          </>
                        )}
                        <Divider />
                        <Space>
                          <Button onClick={() => handleResetDaily(detailProvider.providerId)}>
                            重置今日配额
                          </Button>
                          <Button type="primary" onClick={() => openQuota(detailProvider.providerId)}>
                            修改配额限制
                          </Button>
                        </Space>
                      </>
                    ) : (
                      <Text type="secondary">尚未配置配额限制（不限量）</Text>
                    )}
                  </div>
                ),
              },
            ]}
          />
        )}
      </Drawer>

      {/* ── 配额设置 Modal ── */}
      <Modal
        title="配置配额限制"
        open={quotaModal}
        onOk={handleSaveQuota}
        onCancel={() => setQuotaModal(false)}
        okText="保存"
      >
        <ProForm form={quotaForm} submitter={false} layout="vertical">
          <ProFormDigit
            name="dailyTokenLimit"
            label="每日 Token 限额"
            min={0}
            placeholder="留空表示不限制"
            fieldProps={{ precision: 0 }}
          />
          <ProFormDigit
            name="monthlyTokenLimit"
            label="每月 Token 限额"
            min={0}
            placeholder="留空表示不限制"
            fieldProps={{ precision: 0 }}
          />
        </ProForm>
      </Modal>
    </PageContainer>
  );
};

export default LlmResourcePoolPage;
