import {
  PageContainer,
  ProForm,
  ProFormSelect,
  ProFormText,
  ProFormTextArea,
  ProTable,
} from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import {
  Button,
  Drawer,
  Form,
  Modal,
  Space,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd';
import {
  CopyOutlined,
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
} from '@ant-design/icons';
import React, { useRef, useState } from 'react';
import {
  createKeyVaultSecret,
  deleteKeyVaultSecret,
  listKeyVaultSecrets,
  updateKeyVaultSecret,
  type KeyVaultSecretDto,
} from '@/services/platform/api';

const { Text } = Typography;

type KeyVaultFormValues = {
  name: string;
  description?: string;
  category: string;
  value?: string;
  tagsInput?: string;
};

const parseTags = (raw?: string): string[] => {
  if (!raw) return [];
  return raw
    .split(',')
    .map((x) => x.trim())
    .filter((x) => x.length > 0);
};

/**
 * 分类单一事实来源：表格 Tag 与表单下拉共用同一份定义，
 * 避免此前「展示端按枚举上色、录入端却是自由文本」导致拼写漂移、颜色退化。
 */
const CATEGORY_META: Record<string, { label: string; color: string }> = {
  general: { label: '通用', color: 'blue' },
  api: { label: 'API Key', color: 'red' },
  token: { label: 'Token', color: 'orange' },
};

const CATEGORY_OPTIONS = Object.entries(CATEGORY_META).map(([value, meta]) => ({
  value,
  label: meta.label,
}));

const KeyVaultPage: React.FC = () => {
  const tableRef = useRef<ActionType | undefined>(undefined);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [editing, setEditing] = useState<KeyVaultSecretDto | null>(null);
  const [form] = Form.useForm<KeyVaultFormValues>();
  const openCreate = () => {
    setEditing(null);
    form.resetFields();
    form.setFieldsValue({ category: 'general' });
    setDrawerOpen(true);
  };

  const openEdit = (record: KeyVaultSecretDto) => {
    setEditing(record);
    form.setFieldsValue({
      name: record.name,
      description: record.description,
      category: record.category,
      tagsInput: record.tags?.join(', '),
      value: undefined,
    });
    setDrawerOpen(true);
  };

  const handleSubmit = async () => {
    const values = await form.validateFields();
    const payload = {
      name: values.name,
      description: values.description,
      category: values.category || 'general',
      tags: parseTags(values.tagsInput),
    };

    if (editing) {
      await updateKeyVaultSecret(editing.keyVaultId, {
        ...payload,
        value: values.value && values.value.length > 0 ? values.value : undefined,
      });
      message.success('密钥已更新');
    } else {
      if (!values.value) {
        message.error('请填写密钥值');
        return;
      }
      await createKeyVaultSecret({
        ...payload,
        value: values.value,
      });
      message.success('密钥已创建');
    }

    setDrawerOpen(false);
    tableRef.current?.reload();
  };

  /** 删除：危险操作，使用 Modal.confirm 二次确认 */
  const handleDelete = (record: KeyVaultSecretDto) => {
    Modal.confirm({
      title: `确认删除密钥「${record.name}」？`,
      icon: <DeleteOutlined />,
      content: (
        <Typography.Text type="danger">
          此操作不可恢复。删除后所有引用 <Typography.Text code>{`{{vault:${record.name}}}`}</Typography.Text> 的配置将失效。
        </Typography.Text>
      ),
      okText: '确认删除',
      okType: 'danger',
      cancelText: '取消',
      onOk: async () => {
        await deleteKeyVaultSecret(record.keyVaultId);
        message.success('密钥已删除');
        tableRef.current?.reload();
      },
    });
  };

  /** 轮换密钥：警告操作，需二次确认 */
  const handleRotate = (record: KeyVaultSecretDto) => {
    Modal.confirm({
      title: `轮换密钥「${record.name}」？`,
      icon: <ReloadOutlined />,
      content: '请准备好新密钥值。轮换后将立即生效，旧值将被替换。',
      okText: '进入编辑',
      cancelText: '取消',
      onOk: () => openEdit(record),
    });
  };

  const handleCopyPlaceholder = async (record: KeyVaultSecretDto) => {
    const placeholder = `{{vault:${record.name}}}`;
    try {
      await navigator.clipboard.writeText(placeholder);
      message.success('占位符已复制');
    } catch {
      message.error('复制失败，请手动复制占位符');
    }
  };

  const columns: ProColumns<KeyVaultSecretDto>[] = [
    {
      title: '名称',
      dataIndex: 'name',
      width: 180,
      render: (_, record) => (
        <Space>
          <SafetyCertificateOutlined style={{ color: '#7c3aed', fontSize: 14 }} />
          <Typography.Text code>{record.name}</Typography.Text>
        </Space>
      ),
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true,
      render: (_, record) => <Typography.Text type="secondary">{record.description || '—'}</Typography.Text>,
    },
    {
      title: '分类',
      dataIndex: 'category',
      width: 110,
      render: (_, record) => {
        const meta = CATEGORY_META[record.category ?? 'general'] ?? CATEGORY_META.general;
        return <Tag color={meta.color}>{meta.label}</Tag>;
      },
    },
    {
      title: '密钥值',
      width: 120,
      // 列表接口不返回明文（安全设计）。此前此处提供「眼睛」按钮声称可揭示，
      // 实际只把文案从 '••••••••' 换成 '•••• 已揭示'，是**假功能**——已移除，
      // 改为静态遮蔽 + 说明；真正的明文查看需后端 includePlainText + 审计（见重设计文档 S3）。
      render: () => (
        <Tooltip title="安全设计：列表不返回明文。如需更换密钥值，请使用「轮换」（旧值将被替换）">
          <Typography.Text type="secondary" style={{ fontFamily: 'monospace', letterSpacing: 1 }}>
            ••••••••
          </Typography.Text>
        </Tooltip>
      ),
    },
    {
      title: '占位符',
      width: 200,
      render: (_, record) => (
        <Space>
          <Typography.Text code style={{ fontSize: 12 }}>{`{{vault:${record.name}}}`}</Typography.Text>
          <Tooltip title="复制占位符">
            <Button size="small" icon={<CopyOutlined />} onClick={() => handleCopyPlaceholder(record)} type="text" />
          </Tooltip>
        </Space>
      ),
    },
    {
      title: '标签',
      width: 150,
      ellipsis: true,
      render: (_, record) =>
        record.tags?.length ? (
          <Space size={2} wrap>
            {record.tags.map((t) => (
              <Tag key={t} color="purple" style={{ fontSize: 10 }}>{t}</Tag>
            ))}
          </Space>
        ) : <Typography.Text type="secondary">—</Typography.Text>,
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      width: 170,
      render: (_, record) => new Date(record.createdAt).toLocaleString('zh-CN'),
    },
    {
      title: '操作',
      width: 230,
      render: (_, record) => (
        <Space size={4}>
          <Tooltip title="编辑元数据">
            <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} type="text" />
          </Tooltip>
          <Tooltip title="轮换密钥（需确认）">
            <Button
              size="small"
              icon={<ReloadOutlined />}
              onClick={() => handleRotate(record)}
              type="text"
              style={{ color: '#f97316' }}
            />
          </Tooltip>
          <Tooltip title="删除密钥（危险操作）">
            <Button
              size="small"
              danger
              icon={<DeleteOutlined />}
              onClick={() => handleDelete(record)}
              type="text"
            />
          </Tooltip>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer
      title="密钥保管箱"
      subTitle="安全存储 API Key / Token；列表不展示明文，配置中以 {{vault:name}} 占位符引用"
      // ⚠️ 此前此处硬编码 style={{ background: '#1a1a1e' }}（深色底），
      // 而应用为浅色主题 ⇒ 页头呈「深灰标题压深色底」，标题几乎不可读，
      // 且迫使 ProTable 用 transparent 打补丁。已移除硬编码色，交回主题令牌。
    >
      <ProTable<KeyVaultSecretDto>
        actionRef={tableRef}
        rowKey="keyVaultId"
        columns={columns}
        search={false}
        request={async () => {
          const data = await listKeyVaultSecrets();
          return { data, success: true };
        }}
        toolBarRender={() => [
          <Button key="add" type="primary" icon={<PlusOutlined />} onClick={openCreate}>
            新建密钥
          </Button>,
        ]}
        cardBordered
      />

      <Drawer
        title={editing ? '编辑密钥' : '新建密钥'}
        open={drawerOpen}
        width={520}
        onClose={() => setDrawerOpen(false)}
        extra={
          <Button type="primary" onClick={handleSubmit}>
            保存
          </Button>
        }
      >
        <ProForm<KeyVaultFormValues>
          form={form}
          layout="vertical"
          submitter={false}
        >
          <ProFormText
            name="name"
            label="密钥名称"
            rules={[
              { required: true, message: '请输入密钥名称' },
              { pattern: /^[a-zA-Z0-9._-]+$/, message: '仅允许字母、数字、点、下划线、短横线' },
            ]}
            placeholder="例如 openai-api-key"
          />

          <ProFormTextArea
            name="description"
            label="描述"
            rows={3}
            placeholder="可选，简要描述该密钥用途"
          />

          <ProFormSelect
            name="category"
            label="分类"
            initialValue="general"
            rules={[{ required: true, message: '请选择分类' }]}
            options={CATEGORY_OPTIONS}
          />

          <ProFormText
            name="tagsInput"
            label="标签（逗号分隔）"
            placeholder="例如 production, openai, billing"
          />

          <ProFormText.Password
            name="value"
            label="密钥值"
            rules={editing ? [] : [{ required: true, message: '请输入密钥值' }]}
            fieldProps={{ autoComplete: 'new-password' }}
            placeholder={editing ? '留空则保持原值不变' : '请输入密钥值（仅创建/更新时提交）'}
          />
        </ProForm>
      </Drawer>
    </PageContainer>
  );
};

export default KeyVaultPage;
