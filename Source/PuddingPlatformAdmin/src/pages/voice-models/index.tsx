import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormSwitch,
  ProFormTextArea,
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
  Select,
  message,
  Tooltip,
  Checkbox,
  InputNumber,
  Input,
} from 'antd';
import {
  PlusOutlined,
  EditOutlined,
  DeleteOutlined,
  KeyOutlined,
  AudioOutlined,
} from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  listVoiceProviders,
  getVoiceProvider,
  createVoiceProvider,
  updateVoiceProvider,
  deleteVoiceProvider,
  listTtsModels,
  createTtsModel,
  updateTtsModel,
  deleteTtsModel,
  listAsrModels,
  createAsrModel,
  updateAsrModel,
  deleteAsrModel,
  type VoiceProviderDto,
  type VoiceProviderDetailDto,
  type TtsModelDto,
  type AsrModelDto,
  type UpsertVoiceProviderRequest,
  type UpsertTtsModelRequest,
  type UpsertAsrModelRequest,
} from '@/services/platform/api';
import {
  VOICE_PROVIDER_TEMPLATES,
  getVoiceProviderTemplateProviderValues,
  getVoiceProviderTemplateTtsModels,
  getVoiceProviderTemplateAsrModels,
} from './voiceProviderTemplates';

const { Text } = Typography;

const AUDIO_FORMAT_OPTIONS = [
  { label: 'WAV', value: 'wav' },
  { label: 'MP3', value: 'mp3' },
  { label: 'PCM', value: 'pcm' },
  { label: 'OGG', value: 'ogg' },
];

const LANGUAGE_OPTIONS = [
  { label: '中文', value: 'zh-CN' },
  { label: '英文', value: 'en-US' },
  { label: '日文', value: 'ja-JP' },
  { label: '韩文', value: 'ko-KR' },
];

const VoiceProviderPage: React.FC = () => {
  const actionRef = useRef<ActionType>();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editingProvider, setEditingProvider] = useState<VoiceProviderDetailDto | null>(null);
  const [templateValue, setTemplateValue] = useState<string | undefined>();
  const [form] = Form.useForm<UpsertVoiceProviderRequest>();
  const [detailProviderId, setDetailProviderId] = useState<string | null>(null);

  // ── TTS Model form ──
  const [ttsDrawerOpen, setTtsDrawerOpen] = useState(false);
  const [editingTts, setEditingTts] = useState<TtsModelDto | null>(null);
  const [ttsModelProviderId, setTtsModelProviderId] = useState<string>('');
  const [ttsForm] = Form.useForm<UpsertTtsModelRequest>();

  // ── ASR Model form ──
  const [asrDrawerOpen, setAsrDrawerOpen] = useState(false);
  const [editingAsr, setEditingAsr] = useState<AsrModelDto | null>(null);
  const [asrModelProviderId, setAsrModelProviderId] = useState<string>('');
  const [asrForm] = Form.useForm<UpsertAsrModelRequest>();

  const columns: ProColumns<VoiceProviderDto>[] = [
    { title: '名称', dataIndex: 'name', key: 'name', width: 180 },
    {
      title: '服务商ID',
      dataIndex: 'providerId',
      key: 'providerId',
      width: 140,
      render: (_, r) => <Text copyable>{r.providerId}</Text>,
    },
    { title: '接入点', dataIndex: 'endpoint', key: 'endpoint', width: 220, ellipsis: true },
    {
      title: '密钥',
      dataIndex: 'hasApiKey',
      key: 'hasApiKey',
      width: 70,
      render: (_, r) =>
        r.hasApiKey ? <Tag color="green">已配置</Tag> : <Tag color="red">未配置</Tag>,
    },
    {
      title: 'TTS',
      dataIndex: 'ttsModelCount',
      key: 'ttsModelCount',
      width: 60,
      render: (_, r) => r.ttsModelCount,
    },
    {
      title: 'ASR',
      dataIndex: 'asrModelCount',
      key: 'asrModelCount',
      width: 60,
      render: (_, r) => r.asrModelCount,
    },
    {
      title: '状态',
      dataIndex: 'isEnabled',
      key: 'isEnabled',
      width: 70,
      render: (_, r) =>
        r.isEnabled ? <Tag color="blue">启用</Tag> : <Tag color="default">禁用</Tag>,
    },
    {
      title: '操作',
      key: 'action',
      width: 200,
      render: (_, record) => (
        <Space>
          <Button
            size="small"
            icon={<EditOutlined />}
            onClick={() => openEditProvider(record.providerId)}
          >
            编辑
          </Button>
          <Button
            size="small"
            icon={<AudioOutlined />}
            onClick={() => setDetailProviderId(record.providerId)}
          >
            模型
          </Button>
          <Popconfirm
            title="确认删除此语音服务商？"
            onConfirm={async () => {
              await deleteVoiceProvider(record.providerId);
              message.success('已删除');
              actionRef.current?.reload();
            }}
          >
            <Button size="small" danger icon={<DeleteOutlined />}>
              删除
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const openCreateProvider = () => {
    setEditingProvider(null);
    setTemplateValue(undefined);
    form.resetFields();
    form.setFieldsValue({ isEnabled: true });
    setDrawerOpen(true);
  };

  const openEditProvider = async (providerId: string) => {
    setEditingProvider(null);
    setTemplateValue(undefined);
    const detail = await getVoiceProvider(providerId);
    form.setFieldsValue({
      providerId: detail.providerId,
      name: detail.name,
      endpoint: detail.endpoint,
      apiKey: '',
      description: detail.description,
      isEnabled: detail.isEnabled,
    });
    setEditingProvider(detail);
    setDrawerOpen(true);
  };

  const handleTemplateSelect = (value: string) => {
    setTemplateValue(value);
    const tpl = VOICE_PROVIDER_TEMPLATES.find((t) => t.value === value);
    if (tpl) {
      const vals = getVoiceProviderTemplateProviderValues(tpl);
      form.setFieldsValue(vals);
    }
  };

  const handleProviderSubmit = async () => {
    const values = await form.validateFields();
    const req: UpsertVoiceProviderRequest = { ...values };

    if (editingProvider) {
      await updateVoiceProvider(editingProvider.providerId, req);
      message.success('已更新');
    } else {
      await createVoiceProvider(req);
      // If a template was selected, create its default models too
      if (templateValue) {
        const tpl = VOICE_PROVIDER_TEMPLATES.find((t) => t.value === templateValue);
        if (tpl) {
          for (const m of getVoiceProviderTemplateTtsModels(tpl)) {
            try { await createTtsModel(req.providerId, m); } catch {}
          }
          for (const m of getVoiceProviderTemplateAsrModels(tpl)) {
            try { await createAsrModel(req.providerId, m); } catch {}
          }
        }
      }
      message.success('已创建');
    }

    setDrawerOpen(false);
    setEditingProvider(null);
    setTemplateValue(undefined);
    actionRef.current?.reload();
  };

  // ── TTS Model handlers ──
  const openCreateTts = (providerId: string) => {
    setEditingTts(null);
    setTtsModelProviderId(providerId);
    ttsForm.resetFields();
    ttsForm.setFieldsValue({
      isDefault: false,
      isDeprecated: false,
      sortOrder: 1,
      voices: [],
      audioFormats: [],
      sampleRates: [],
    });
    setTtsDrawerOpen(true);
  };

  const openEditTts = async (providerId: string, modelId: string) => {
    const models = await listTtsModels(providerId);
    const m = models.find((x) => x.modelId === modelId);
    if (!m) return;
    setEditingTts(m);
    setTtsModelProviderId(providerId);
    ttsForm.setFieldsValue(m);
    setTtsDrawerOpen(true);
  };

  const handleTtsSubmit = async () => {
    const values = await ttsForm.validateFields();
    const req: UpsertTtsModelRequest = {
      ...values,
      voices: values.voices || [],
      audioFormats: values.audioFormats || [],
      sampleRates: values.sampleRates || [],
    };

    if (editingTts) {
      await updateTtsModel(ttsModelProviderId, editingTts.modelId, req);
      message.success('TTS 模型已更新');
    } else {
      await createTtsModel(ttsModelProviderId, req);
      message.success('TTS 模型已创建');
    }

    setTtsDrawerOpen(false);
    setEditingTts(null);
    if (detailProviderId) {
      const detail = await getVoiceProvider(detailProviderId);
      setEditingProvider(detail);
    }
  };

  // ── ASR Model handlers ──
  const openCreateAsr = (providerId: string) => {
    setEditingAsr(null);
    setAsrModelProviderId(providerId);
    asrForm.resetFields();
    asrForm.setFieldsValue({
      isDefault: false,
      isDeprecated: false,
      sortOrder: 1,
      languages: [],
      sampleRates: [],
    });
    setAsrDrawerOpen(true);
  };

  const openEditAsr = async (providerId: string, modelId: string) => {
    const models = await listAsrModels(providerId);
    const m = models.find((x) => x.modelId === modelId);
    if (!m) return;
    setEditingAsr(m);
    setAsrModelProviderId(providerId);
    asrForm.setFieldsValue(m);
    setAsrDrawerOpen(true);
  };

  const handleAsrSubmit = async () => {
    const values = await asrForm.validateFields();
    const req: UpsertAsrModelRequest = {
      ...values,
      languages: values.languages || [],
      sampleRates: values.sampleRates || [],
    };

    if (editingAsr) {
      await updateAsrModel(asrModelProviderId, editingAsr.modelId, req);
      message.success('ASR 模型已更新');
    } else {
      await createAsrModel(asrModelProviderId, req);
      message.success('ASR 模型已创建');
    }

    setAsrDrawerOpen(false);
    setEditingAsr(null);
    if (detailProviderId) {
      const detail = await getVoiceProvider(detailProviderId);
      setEditingProvider(detail);
    }
  };

  return (
    <PageContainer>
      <ProTable<VoiceProviderDto>
        headerTitle="语音服务商"
        actionRef={actionRef}
        rowKey="providerId"
        search={false}
        columns={columns}
        request={async () => {
          const data = await listVoiceProviders();
          return { data, success: true, total: data.length };
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreateProvider}>
            新增服务商
          </Button>,
        ]}
      />

      {/* ── Provider Drawer ── */}
      <Drawer
        title={editingProvider ? '编辑语音服务商' : '新增语音服务商'}
        open={drawerOpen}
        onClose={() => {
          setDrawerOpen(false);
          setEditingProvider(null);
          setTemplateValue(undefined);
        }}
        width={520}
        extra={
          <Space>
            <Button onClick={() => { setDrawerOpen(false); setEditingProvider(null); }}>
              取消
            </Button>
            <Button type="primary" onClick={handleProviderSubmit}>
              {editingProvider ? '更新' : '创建'}
            </Button>
          </Space>
        }
      >
        {/* Template selector — only when creating */}
        {!editingProvider && (
          <Form.Item label="预设模板" style={{ marginBottom: 16 }}>
            <Select
              allowClear
              placeholder="选择预设模板（可选）"
              value={templateValue}
              onChange={handleTemplateSelect}
              style={{ width: '100%' }}
              options={VOICE_PROVIDER_TEMPLATES.map((t) => ({
                label: t.label,
                value: t.value,
              }))}
            />
          </Form.Item>
        )}

        <ProForm<UpsertVoiceProviderRequest>
          form={form}
          layout="vertical"
          submitter={false}
          initialValues={{ isEnabled: true }}
        >
          <ProFormText
            name="providerId"
            label="服务商ID"
            rules={[{ required: true, message: '请输入服务商ID' }]}
            disabled={!!editingProvider}
            placeholder="例如: dashscope"
          />
          <ProFormText
            name="name"
            label="名称"
            rules={[{ required: true, message: '请输入名称' }]}
            placeholder="例如: 阿里云百炼-语音"
          />
          <ProFormText
            name="endpoint"
            label="接入点 URL"
            rules={[{ required: true, message: '请输入接入点 URL' }]}
            placeholder="https://dashscope.aliyuncs.com"
          />
          <ProFormText
            name="apiKey"
            label={
              <Space>
                <KeyOutlined />
                API Key
              </Space>
            }
            placeholder="输入 API Key（留空则不修改）"
          />
          <ProFormTextArea name="description" label="描述" placeholder="服务商描述（可选）" />
          <ProFormSwitch name="isEnabled" label="启用" />
        </ProForm>
      </Drawer>

      {/* ── Detail Modal (TTS + ASR models) ── */}
      <Modal
        title="语音模型管理"
        open={!!detailProviderId}
        onCancel={() => setDetailProviderId(null)}
        footer={null}
        width={900}
        destroyOnClose
      >
        {detailProviderId && (
          <ProviderModelDetail
            providerId={detailProviderId}
            onCreateTts={openCreateTts}
            onEditTts={openEditTts}
            onCreateAsr={openCreateAsr}
            onEditAsr={openEditAsr}
            onReload={async () => {
              const detail = await getVoiceProvider(detailProviderId);
              actionRef.current?.reload();
            }}
          />
        )}
      </Modal>

      {/* ── TTS Model Drawer ── */}
      <Drawer
        title={editingTts ? '编辑 TTS 模型' : '新增 TTS 模型'}
        open={ttsDrawerOpen}
        onClose={() => { setTtsDrawerOpen(false); setEditingTts(null); }}
        width={520}
        extra={
          <Space>
            <Button onClick={() => { setTtsDrawerOpen(false); setEditingTts(null); }}>取消</Button>
            <Button type="primary" onClick={handleTtsSubmit}>
              {editingTts ? '更新' : '创建'}
            </Button>
          </Space>
        }
      >
        <ProForm<UpsertTtsModelRequest> form={ttsForm} layout="vertical" submitter={false}>
          <ProFormText
            name="modelId"
            label="模型ID"
            rules={[{ required: true }]}
            disabled={!!editingTts}
            placeholder="cosyvoice-v3-flash"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormText name="path" label="API 路径" placeholder="/api/v1/services/audio/tts/SpeechSynthesizer" />
          <Form.Item name="voices" label="音色">
            <Select mode="tags" placeholder="输入音色名称后回车" />
          </Form.Item>
          <Form.Item name="audioFormats" label="音频格式">
            <Select mode="multiple" options={AUDIO_FORMAT_OPTIONS} placeholder="选择格式" />
          </Form.Item>
          <Form.Item name="sampleRates" label="采样率">
            <Select
              mode="tags"
              placeholder="输入采样率后回车（如 24000）"
              tokenSeparators={[',']}
            />
          </Form.Item>
          <ProFormSwitch name="supportsStreaming" label="支持流式" />
          <ProFormSwitch name="supportsInstructions" label="支持指令" />
          <ProFormSwitch name="supportsVoiceCloning" label="支持声音克隆" />
          <ProFormSwitch name="supportsVoiceDesign" label="支持声音设计" />
          <ProFormSwitch name="isDeprecated" label="已弃用" />
          <ProFormSwitch name="isDefault" label="默认模型" />
          <Form.Item name="sortOrder" label="排序">
            <InputNumber min={1} />
          </Form.Item>
        </ProForm>
      </Drawer>

      {/* ── ASR Model Drawer ── */}
      <Drawer
        title={editingAsr ? '编辑 ASR 模型' : '新增 ASR 模型'}
        open={asrDrawerOpen}
        onClose={() => { setAsrDrawerOpen(false); setEditingAsr(null); }}
        width={520}
        extra={
          <Space>
            <Button onClick={() => { setAsrDrawerOpen(false); setEditingAsr(null); }}>取消</Button>
            <Button type="primary" onClick={handleAsrSubmit}>
              {editingAsr ? '更新' : '创建'}
            </Button>
          </Space>
        }
      >
        <ProForm<UpsertAsrModelRequest> form={asrForm} layout="vertical" submitter={false}>
          <ProFormText
            name="modelId"
            label="模型ID"
            rules={[{ required: true }]}
            disabled={!!editingAsr}
            placeholder="qwen3-asr-flash-realtime"
          />
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
          <ProFormText name="path" label="API 路径" />
          <Form.Item name="languages" label="支持语言">
            <Select mode="multiple" options={LANGUAGE_OPTIONS} placeholder="选择语言" />
          </Form.Item>
          <Form.Item name="sampleRates" label="采样率">
            <Select
              mode="tags"
              placeholder="输入采样率后回车（如 16000）"
              tokenSeparators={[',']}
            />
          </Form.Item>
          <ProFormSwitch name="supportsEmotion" label="支持情感识别" />
          <ProFormSwitch name="supportsTimestamps" label="支持时间戳" />
          <ProFormSwitch name="supportsHotWords" label="支持热词" />
          <ProFormSwitch name="isDeprecated" label="已弃用" />
          <ProFormSwitch name="isDefault" label="默认模型" />
          <Form.Item name="sortOrder" label="排序">
            <InputNumber min={1} />
          </Form.Item>
        </ProForm>
      </Drawer>
    </PageContainer>
  );
};

// ── Provider Detail sub-component ──

const ProviderModelDetail: React.FC<{
  providerId: string;
  onCreateTts: (providerId: string) => void;
  onEditTts: (providerId: string, modelId: string) => void;
  onCreateAsr: (providerId: string) => void;
  onEditAsr: (providerId: string, modelId: string) => void;
  onReload: () => void;
}> = ({ providerId, onCreateTts, onEditTts, onCreateAsr, onEditAsr, onReload }) => {
  const ttsColumns: ProColumns<TtsModelDto>[] = [
    { title: '模型ID', dataIndex: 'modelId', key: 'modelId', width: 160, render: (_, r) => <Text copyable>{r.modelId}</Text> },
    { title: '名称', dataIndex: 'name', key: 'name', width: 140 },
    { title: '音色', dataIndex: 'voices', key: 'voices', width: 180, render: (_, r) => r.voices?.join(', ') || '-' },
    {
      title: '格式', dataIndex: 'audioFormats', key: 'audioFormats', width: 100,
      render: (_, r) => r.audioFormats?.map((f) => <Tag key={f}>{f}</Tag>),
    },
    {
      title: '采样率', dataIndex: 'sampleRates', key: 'sampleRates', width: 100,
      render: (_, r) => r.sampleRates?.join(', ') || '-',
    },
    {
      title: '特性', key: 'features', width: 120,
      render: (_, r) => (
        <Space size={4} wrap>
          {r.supportsStreaming && <Tag color="blue">流式</Tag>}
          {r.supportsInstructions && <Tag color="green">指令</Tag>}
          {r.supportsVoiceCloning && <Tag color="purple">克隆</Tag>}
          {r.supportsVoiceDesign && <Tag color="orange">设计</Tag>}
        </Space>
      ),
    },
    { title: '默认', dataIndex: 'isDefault', key: 'isDefault', width: 60, render: (_, r) => r.isDefault ? <Tag color="gold">默认</Tag> : null },
    {
      title: '操作', key: 'action', width: 140,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => onEditTts(providerId, record.modelId)}>编辑</Button>
          <Popconfirm
            title="确认删除？"
            onConfirm={async () => {
              await deleteTtsModel(providerId, record.modelId);
              message.success('TTS 模型已删除');
              onReload();
            }}
          >
            <Button size="small" danger icon={<DeleteOutlined />}>删除</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  const asrColumns: ProColumns<AsrModelDto>[] = [
    { title: '模型ID', dataIndex: 'modelId', key: 'modelId', width: 180, render: (_, r) => <Text copyable>{r.modelId}</Text> },
    { title: '名称', dataIndex: 'name', key: 'name', width: 160 },
    {
      title: '语言', dataIndex: 'languages', key: 'languages', width: 120,
      render: (_, r) => r.languages?.map((l) => <Tag key={l}>{l}</Tag>),
    },
    {
      title: '采样率', dataIndex: 'sampleRates', key: 'sampleRates', width: 100,
      render: (_, r) => r.sampleRates?.join(', ') || '-',
    },
    {
      title: '特性', key: 'features', width: 120,
      render: (_, r) => (
        <Space size={4} wrap>
          {r.supportsEmotion && <Tag color="pink">情感</Tag>}
          {r.supportsTimestamps && <Tag color="cyan">时间戳</Tag>}
          {r.supportsHotWords && <Tag color="orange">热词</Tag>}
        </Space>
      ),
    },
    { title: '默认', dataIndex: 'isDefault', key: 'isDefault', width: 60, render: (_, r) => r.isDefault ? <Tag color="gold">默认</Tag> : null },
    {
      title: '操作', key: 'action', width: 140,
      render: (_, record) => (
        <Space>
          <Button size="small" icon={<EditOutlined />} onClick={() => onEditAsr(providerId, record.modelId)}>编辑</Button>
          <Popconfirm
            title="确认删除？"
            onConfirm={async () => {
              await deleteAsrModel(providerId, record.modelId);
              message.success('ASR 模型已删除');
              onReload();
            }}
          >
            <Button size="small" danger icon={<DeleteOutlined />}>删除</Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <Tabs
      items={[
        {
          key: 'tts',
          label: 'TTS 模型',
          children: (
            <ProTable<TtsModelDto>
              headerTitle={
                <Button type="primary" size="small" icon={<PlusOutlined />} onClick={() => onCreateTts(providerId)}>
                  新增 TTS
                </Button>
              }
              rowKey="modelId"
              search={false}
              columns={ttsColumns}
              options={false}
              request={async () => {
                const data = await listTtsModels(providerId);
                return { data, success: true, total: data.length };
              }}
              pagination={false}
            />
          ),
        },
        {
          key: 'asr',
          label: 'ASR 模型',
          children: (
            <ProTable<AsrModelDto>
              headerTitle={
                <Button type="primary" size="small" icon={<PlusOutlined />} onClick={() => onCreateAsr(providerId)}>
                  新增 ASR
                </Button>
              }
              rowKey="modelId"
              search={false}
              columns={asrColumns}
              options={false}
              request={async () => {
                const data = await listAsrModels(providerId);
                return { data, success: true, total: data.length };
              }}
              pagination={false}
            />
          ),
        },
      ]}
    />
  );
};

export default VoiceProviderPage;
