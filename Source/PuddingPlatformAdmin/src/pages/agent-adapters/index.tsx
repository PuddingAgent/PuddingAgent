import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import { App, Button, Descriptions, Form, Input, Modal, Select, Space, Tag, Typography } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { getExternalApiStatus, listWorkspaces, revokeAccessToken } from '@/services/platform/api';
import {
  createAgentAdapter,
  listAgentAdapters,
  type AgentAdapterCreateInput,
  type AgentAdapterCredentials,
  type AgentAdapterRow,
} from '@/services/platform/agentAdapters';
import { agentAdapterPrompt, agentApiDocumentation } from './onboarding';

export default function AgentAdaptersPage() {
  const { message } = App.useApp();
  const table = useRef<ActionType>(null);
  const [form] = Form.useForm<AgentAdapterCreateInput>();
  const [createOpen, setCreateOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [secret, setSecret] = useState<AgentAdapterCredentials | null>(null);
  const [apiEnabled, setApiEnabled] = useState<boolean>();
  const [baseUrl, setBaseUrl] = useState(window.location.origin);
  const [workspaces, setWorkspaces] = useState<string[]>([]);

  useEffect(() => {
    getExternalApiStatus().then((status) => {
      setApiEnabled(status.enabled);
      setBaseUrl(status.publicBaseUrl || status.boundBaseUrl || window.location.origin);
    }).catch(() => message.error('无法加载外部 API 状态'));
    listWorkspaces().then((result) => setWorkspaces(result.map((workspace) => workspace.workspaceId)))
      .catch(() => message.error('无法加载工作区'));
  }, [message]);

  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      message.success('已复制');
    } catch {
      message.error('无法访问剪贴板');
    }
  }

  async function create(values: AgentAdapterCreateInput) {
    setSaving(true);
    try {
      const credential = await createAgentAdapter(values);
      setSecret(credential);
      setCreateOpen(false);
      form.resetFields();
      await table.current?.reload();
    } catch {
      message.error('签发失败，请检查工作区或访问令牌配额');
    } finally {
      setSaving(false);
    }
  }

  const columns: ProColumns<AgentAdapterRow>[] = [
    { title: '工具', dataIndex: 'name' },
    { title: '工作区', dataIndex: 'workspaceId' },
    { title: 'Agent ID', dataIndex: 'agentId', copyable: true, ellipsis: true },
    {
      title: '状态', key: 'status',
      render: (_, row) => {
        if (row.revokedAtUtc) return <Tag>已撤销</Tag>;
        if (!row.active) return <Tag>不可用</Tag>;
        if (row.online) return <Tag color="green">在线</Tag>;
        return <Tag>{row.registeredAtUtc ? '离线' : '待注册'}</Tag>;
      },
    },
    { title: '到期时间', dataIndex: 'expiresAtUtc', valueType: 'dateTime' },
    {
      title: '操作', valueType: 'option',
      render: (_, row) => (
        <Space>
          <Button type="link" onClick={() => void copy(agentAdapterPrompt(baseUrl, row))}
            disabled={!row.active}>复制接入提示词</Button>
          <Button type="link" danger disabled={!!row.revokedAtUtc} onClick={() => {
            Modal.confirm({
              title: `撤销 ${row.name} 的 AK/SK`,
              content: '撤销后，该工具将无法继续认证。',
              async onOk() {
                try {
                  await revokeAccessToken(row.tokenId, { expectedVersion: row.version });
                  await table.current?.reload();
                } catch (error) {
                  message.error('撤销失败，请刷新后重试');
                  throw error;
                }
              },
            });
          }}>撤销</Button>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer title="外部 Agent">
      <ProTable<AgentAdapterRow>
        rowKey="agentId" actionRef={table} columns={columns} search={false}
        request={async ({ current, pageSize }) => {
          const result = await listAgentAdapters(current, pageSize);
          return { data: result.items, total: result.total, success: true };
        }}
        toolBarRender={() => [
          <Button key="docs" href={agentApiDocumentation(baseUrl)} target="_blank"
            rel="noopener noreferrer" disabled={!apiEnabled}>OpenAPI 文档</Button>,
          <Button key="create" type="primary" onClick={() => setCreateOpen(true)}>签发 AK/SK</Button>,
        ]}
      />
      <Modal title="签发 Agent 凭据" open={createOpen} onCancel={() => setCreateOpen(false)}
        onOk={() => form.submit()} confirmLoading={saving} okText="签发">
        <Form form={form} layout="vertical" onFinish={create}>
          <Form.Item name="name" label="工具名称" rules={[{ required: true, whitespace: true, max: 100 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="workspaceId" label="工作区" rules={[{ required: true }]}>
            <Select options={workspaces.map((value) => ({ value, label: value }))} />
          </Form.Item>
        </Form>
      </Modal>
      <Modal title="保存 Agent 凭据" open={!!secret} onCancel={() => setSecret(null)}
        onOk={() => setSecret(null)} cancelButtonProps={{ style: { display: 'none' } }} okText="完成">
        {secret && <>
          <Typography.Paragraph>SK 仅显示这一次。请保存到外部工具的密钥配置。</Typography.Paragraph>
          <Descriptions column={1}>
            <Descriptions.Item label="Agent ID">{secret.agentId}</Descriptions.Item>
            <Descriptions.Item label="AK"><Typography.Text copyable>{secret.ak}</Typography.Text></Descriptions.Item>
            <Descriptions.Item label="SK">
              <Space>
                <Input.Password value={secret.sk} readOnly aria-label="SK" />
                <Button onClick={() => void copy(secret.sk)}>复制 SK</Button>
              </Space>
            </Descriptions.Item>
          </Descriptions>
          <Button onClick={() => void copy(agentAdapterPrompt(baseUrl, secret))}>复制接入提示词</Button>
        </>}
      </Modal>
    </PageContainer>
  );
}
