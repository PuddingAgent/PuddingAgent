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
  Input,
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
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  createKeyVaultSecret,
  deleteKeyVaultSecret,
  getKeyVaultUsage,
  listKeyVaultSecrets,
  updateKeyVaultSecret,
  type KeyVaultSecretDto,
  type KeyVaultUsageEntryDto,
} from '@/services/platform/api';
import { formatCreatedAt } from './createdAt';
import { describeUsage } from './usageView';

const { Text } = Typography;

type KeyVaultFormValues = {
  name: string;
  description?: string;
  category: string;
  value?: string;
  tagsInput?: string;
};

/**
 * 后端 CreatedAt 用 DateTime.UtcNow 写入，但实体列是 SQLite TEXT，
 * 读回后 DateTimeKind 丢失，序列化不带时区后缀 ⇒ 浏览器当本地时间解析，
 * 显示时间会比真实时间早 8 小时。这里显式补 UTC 后缀（值的语义一直是 UTC）。
 */
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

  // 列表一次性取回后在前端过滤：后端 list 接口不接受任何查询参数，
  // 每敲一个字就往返一次接口既慢又无谓（当前条目量级为几十条）。
  const [keyword, setKeyword] = useState('');
  const [rows, setRows] = useState<KeyVaultSecretDto[]>([]);
  const [usage, setUsage] = useState<Record<string, KeyVaultUsageEntryDto>>({});
  const [usageSummary, setUsageSummary] = useState({ orphan: 0, duplicate: 0 });
  const [loading, setLoading] = useState(false);

  const reloadRows = useCallback(async () => {
    setLoading(true);
    try {
      const [list, report] = await Promise.all([
        listKeyVaultSecrets(),
        // 使用情况是附加信息：拉不到不应该让整个列表变成空白。
        getKeyVaultUsage().catch(() => null),
      ]);
      setRows(list);
      if (report) {
        setUsage(Object.fromEntries(report.entries.map((e) => [e.name, e])));
        setUsageSummary({ orphan: report.orphanCount, duplicate: report.duplicateGroupCount });
      }
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reloadRows();
  }, [reloadRows]);

  const filteredRows = useMemo(() => {
    const kw = keyword.trim().toLowerCase();
    if (!kw) return rows;
    return rows.filter((r) =>
      [r.name, r.description ?? '', r.category ?? '', ...(r.tags ?? [])]
        .join(' ')
        .toLowerCase()
        .includes(kw),
    );
  }, [rows, keyword]);

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
    void reloadRows();
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
        void reloadRows();
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
      width: 320,
      render: (_, record) => (
        <Space>
          <SafetyCertificateOutlined style={{ color: '#7c3aed', fontSize: 14 }} />
          <Tooltip title={record.name}>
            <Typography.Text code style={{ fontSize: 12 }}>
              {record.name}
            </Typography.Text>
          </Tooltip>
          <Tooltip title="复制占位符">
            <Button
              size="small"
              icon={<CopyOutlined />}
              onClick={() => handleCopyPlaceholder(record)}
              type="text"
            />
          </Tooltip>
        </Space>
      ),
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true,
      render: (_, record) => {
        const desc = record.description ?? '';
        // 迁移脚本写的描述与原名称同义（Migrated from xxx），是纯噪声；
        // 收成一个小标记，原文件路径仍可从名称推出，完整描述进 tooltip。
        if (desc.startsWith('Migrated from')) {
          return (
            <Tooltip title={desc}>
              <Tag color="default" style={{ fontSize: 10 }}>
                迁移导入
              </Tag>
            </Tooltip>
          );
        }
        return <Typography.Text type="secondary">{desc || '—'}</Typography.Text>;
      },
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
    // 「密钥值」整列只是八个圆点（列表接口本就不返回明文，页头已说明），
    // 「占位符」与「名称」是同一段文字套壳 ⇒ 两列都撤掉，复制按钮并入名称列。
    {
      title: '被引用',
      width: 140,
      render: (_, record) => {
        const badges = describeUsage(usage[record.name]);
        if (!badges) return <Typography.Text type="secondary">—</Typography.Text>;
        return (
          <Space size={4} wrap>
            {badges.map((b) => (
              <Tooltip key={b.key} title={b.tooltip}>
                <Tag color={b.color} style={{ fontSize: 10 }}>{b.label}</Tag>
              </Tooltip>
            ))}
          </Space>
        );
      },
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
      render: (_, record) => formatCreatedAt(record.createdAt),
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
        loading={loading}
        dataSource={filteredRows}
        pagination={{ pageSize: 20, showSizeChanger: false, hideOnSinglePage: true }}
        toolBarRender={() => [
          usageSummary.orphan > 0 ? (
            <Tooltip key="orphan" title="没有任何配置引用的条目：改了也不会生效，可以清理。">
              <Tag color="red">孤儿 {usageSummary.orphan}</Tag>
            </Tooltip>
          ) : null,
          usageSummary.duplicate > 0 ? (
            <Tooltip key="dup" title="有两条以上密钥值完全相同，但它们彼此独立，容易漂移。">
              <Tag color="purple">同值重复 {usageSummary.duplicate} 组</Tag>
            </Tooltip>
          ) : null,
          <Input.Search
            key="search"
            allowClear
            placeholder="搜索名称 / 描述 / 标签"
            style={{ width: 280 }}
            onChange={(e) => setKeyword(e.target.value)}
          />,
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
