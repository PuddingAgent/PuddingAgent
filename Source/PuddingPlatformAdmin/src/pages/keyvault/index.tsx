import { PageContainer, ProForm, ProFormText, ProFormTextArea, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { Button, Drawer, Form, Popconfirm, Space, Tag, Typography, message } from 'antd';
import { CopyOutlined, DeleteOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons';
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

  const handleDelete = async (record: KeyVaultSecretDto) => {
    await deleteKeyVaultSecret(record.keyVaultId);
    message.success('密钥已删除');
    tableRef.current?.reload();
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
      render: (_, record) => <Text code>{record.name}</Text>,
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true,
      render: (_, record) => <Text type="secondary">{record.description || '—'}</Text>,
    },
    {
      title: '分类',
      dataIndex: 'category',
      width: 120,
      render: (_, record) => <Tag color="blue">{record.category || 'general'}</Tag>,
    },
    {
      title: '占位符',
      width: 220,
      render: (_, record) => (
        <Space>
          <Text code>{`{{vault:${record.name}}}`}</Text>
          <Button
            size="small"
            icon={<CopyOutlined />}
            onClick={() => handleCopyPlaceholder(record)}
          >
            复制
          </Button>
        </Space>
      ),
    },
    {
      title: '创建时间',
      dataIndex: 'createdAt',
      width: 180,
      render: (_, record) => new Date(record.createdAt).toLocaleString('zh-CN'),
    },
    {
      title: '操作',
      width: 140,
      render: (_, record) => (
        <Space size={4}>
          <Button size="small" icon={<EditOutlined />} onClick={() => openEdit(record)} />
          <Popconfirm title="确认删除该密钥？" onConfirm={() => handleDelete(record)}>
            <Button size="small" danger icon={<DeleteOutlined />} />
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer
      title="KeyVault 密钥保管箱"
      subTitle="安全存储 API Key / Token；列表不展示明文，使用 {{vault:name}} 占位符引用"
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

          <ProFormText
            name="category"
            label="分类"
            initialValue="general"
            rules={[{ required: true, message: '请输入分类' }]}
            placeholder="例如 general / api / token"
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
