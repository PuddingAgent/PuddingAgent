import {
  PageContainer,
  ProTable,
  ProForm,
  ProFormText,
  ProFormSelect,
} from '@ant-design/pro-components';
import type { ProColumns, ActionType } from '@ant-design/pro-components';
import {
  Alert,
  Button,
  Checkbox,
  Drawer,
  Descriptions,
  Form,
  Input,
  Modal,
  Select,
  Space,
  Tag,
  Typography,
  message,
} from 'antd';
import {
  PlusOutlined,
  EditOutlined,
  StopOutlined,
  EyeOutlined,
} from '@ant-design/icons';
import React, { useRef, useState, useEffect } from 'react';
import { SecretOnceModal } from './components/SecretOnceModal';
import {
  getExternalApiStatus,
  listAccessTokens,
  createAccessToken,
  renameAccessToken,
  revokeAccessToken,
  listWorkspaces,
  type ExternalAccessTokenDto,
  type CreatedAccessTokenDto,
  type ExternalAccessTokenStatusWire,
  type ExternalApiStatusDto,
} from '@/services/platform/api';

/**
 * ADR-075: 第三方任务看板 Access Token 管理。
 * 明文 Secret 只在创建成功后的一次性 Modal 中出现，关闭/刷新后不可恢复；
 * 不提供 reveal/unrevoke/硬删除；scope 与 workspace 创建后不可扩大。
 */

const SCOPE_OPTIONS: { value: string; label: string; risk: 'low' | 'medium' | 'high' }[] = [
  { value: 'tasks.read', label: 'tasks.read — 读取任务/评论/评价/Watch', risk: 'low' },
  { value: 'tasks.write', label: 'tasks.write — 创建任务、修改元数据、导入', risk: 'medium' },
  { value: 'tasks.comment', label: 'tasks.comment — 追加评论', risk: 'medium' },
  { value: 'tasks.evaluate', label: 'tasks.evaluate — 追加结构化评价（不改任务状态）', risk: 'medium' },
  { value: 'tasks.command', label: 'tasks.command — 状态/执行命令（高风险）', risk: 'high' },
];

const STATUS_META: Record<
  ExternalAccessTokenStatusWire,
  { color: string; text: string }
> = {
  Active: { color: 'green', text: 'Active' },
  Expired: { color: 'orange', text: 'Expired' },
  Revoked: { color: 'red', text: 'Revoked' },
  OwnerDisabled: { color: 'default', text: 'OwnerDisabled' },
};

function formatUtc(value?: string | null): string {
  if (!value) return '-';
  return new Date(value).toLocaleString('zh-CN');
}

function statusTag(status: ExternalAccessTokenStatusWire) {
  const meta = STATUS_META[status] ?? { color: 'default', text: status };
  return <Tag color={meta.color}>{meta.text}</Tag>;
}

export default function AccessTokenManagementPage() {
  const actionRef = useRef<ActionType>(null);
  const [statusFilter, setStatusFilter] = useState<ExternalAccessTokenStatusWire | undefined>();

  const [apiStatus, setApiStatus] = useState<ExternalApiStatusDto | null>(null);
  const [workspaceOptions, setWorkspaceOptions] = useState<{ label: string; value: string }[]>([]);

  // 创建 Drawer
  const [createOpen, setCreateOpen] = useState(false);
  const [createForm] = Form.useForm();
  const [creating, setCreating] = useState(false);

  // 一次性 Secret Modal（确认态由 SecretOnceModal 内部管理）
  const [secretToken, setSecretToken] = useState<CreatedAccessTokenDto | null>(null);

  // 详情 Drawer
  const [detailToken, setDetailToken] = useState<ExternalAccessTokenDto | null>(null);

  // 重命名 / 撤销
  const [renaming, setRenaming] = useState<ExternalAccessTokenDto | null>(null);
  const [renameForm] = Form.useForm();
  const [revoking, setRevoking] = useState<ExternalAccessTokenDto | null>(null);
  const [revokeForm] = Form.useForm();
  const [mutationLoading, setMutationLoading] = useState(false);

  useEffect(() => {
    getExternalApiStatus()
      .then((status) => {
        setApiStatus(status);
        return status;
      })
      .catch(() => {});
    listWorkspaces()
      .then((workspaces) =>
        setWorkspaceOptions(
          workspaces.map((w) => ({ label: w.workspaceId, value: w.workspaceId })),
        ),
      )
      .catch(() => {});
  }, []);

  useEffect(() => {
    if (createOpen) {
      createForm.resetFields();
      const defaultLifetime = apiStatus?.defaultTokenLifetimeDays ?? 90;
      createForm.setFieldsValue({
        name: '',
        workspaceIds: [],
        scopes: ['tasks.read'],
        lifetimeDays: defaultLifetime,
      });
    }
  }, [createOpen, createForm, apiStatus]);

  useEffect(() => {
    if (renaming) {
      renameForm.setFieldsValue({ name: renaming.name });
    }
  }, [renaming, renameForm]);

  useEffect(() => {
    if (revoking) {
      revokeForm.setFieldsValue({ reason: '' });
    }
  }, [revoking, revokeForm]);

  async function handleCreate() {
    let values: { name: string; workspaceIds: string[]; scopes: string[]; lifetimeDays: number };
    try {
      values = await createForm.validateFields();
    } catch {
      // 校验失败：antd Form 已就地展示字段错误，无需额外提示。
      return;
    }
    setCreating(true);
    try {
      const created = await createAccessToken({
        name: values.name,
        workspaceIds: values.workspaceIds,
        scopes: values.scopes,
        lifetimeDays: values.lifetimeDays,
      });
      setCreateOpen(false);
      setSecretToken(created);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '创建失败');
    } finally {
      setCreating(false);
    }
  }

  async function handleRename() {
    if (!renaming) return;
    let values: { name: string };
    try {
      values = await renameForm.validateFields();
    } catch {
      return;
    }
    setMutationLoading(true);
    try {
      await renameAccessToken(renaming.tokenId, {
        name: values.name,
        expectedVersion: renaming.version,
      });
      message.success('已重命名');
      setRenaming(null);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '重命名失败');
    } finally {
      setMutationLoading(false);
    }
  }

  async function handleRevoke() {
    if (!revoking) return;
    const values = await revokeForm.validateFields();
    setMutationLoading(true);
    try {
      await revokeAccessToken(revoking.tokenId, {
        expectedVersion: revoking.version,
        reason: values.reason || undefined,
      });
      message.success('已撤销，立即生效');
      setRevoking(null);
      actionRef.current?.reload();
    } catch (e: any) {
      message.error(e?.message ?? '撤销失败');
    } finally {
      setMutationLoading(false);
    }
  }

  const columns: ProColumns<ExternalAccessTokenDto>[] = [
    { title: '名称', dataIndex: 'name', width: 160, copyable: false },
    {
      title: '前缀',
      dataIndex: 'displayPrefix',
      width: 150,
      copyable: true,
      render: (_, record) => (
        <Typography.Text code>{record.displayPrefix}</Typography.Text>
      ),
    },
    {
      title: 'Workspaces',
      dataIndex: 'workspaces',
      width: 140,
      render: (_, record) => (
        <Space wrap size={4}>
          {record.workspaces.map((w) => (
            <Tag key={w}>{w}</Tag>
          ))}
        </Space>
      ),
    },
    {
      title: 'Scopes',
      dataIndex: 'scopes',
      width: 180,
      render: (_, record) => (
        <Space wrap size={4}>
          {record.scopes.map((s) => (
            <Tag key={s} color={s === 'tasks.command' ? 'volcano' : 'blue'}>
              {s}
            </Tag>
          ))}
        </Space>
      ),
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 110,
      render: (_, record) => statusTag(record.status),
    },
    {
      title: '到期时间',
      dataIndex: 'expiresAtUtc',
      width: 160,
      render: (_, record) => formatUtc(record.expiresAtUtc),
    },
    {
      title: '最后使用',
      dataIndex: 'lastUsedAtUtc',
      width: 160,
      render: (_, record) => formatUtc(record.lastUsedAtUtc),
    },
    {
      title: '创建者/创建时间',
      dataIndex: 'ownerUserId',
      width: 180,
      render: (_, record) => (
        <div>
          <div>{record.ownerUserId}</div>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {formatUtc(record.createdAtUtc)}
          </Typography.Text>
        </div>
      ),
    },
    {
      title: '操作',
      valueType: 'option',
      width: 200,
      render: (_, record) => (
        <Space size={0}>
          <Button type="link" size="small" icon={<EyeOutlined />} onClick={() => setDetailToken(record)}>
            详情
          </Button>
          <Button type="link" size="small" icon={<EditOutlined />} onClick={() => setRenaming(record)}>
            重命名
          </Button>
          {record.status === 'Active' && (
            <Button type="link" size="small" danger icon={<StopOutlined />} onClick={() => setRevoking(record)}>
              撤销
            </Button>
          )}
        </Space>
      ),
    },
  ];

  const externalApiAlert = apiStatus ? (
    <Alert
      type={apiStatus.enabled ? 'success' : 'warning'}
      showIcon
      style={{ marginBottom: 12 }}
      message={
        apiStatus.enabled
          ? `External API 已启用（${apiStatus.publicBaseUrl ?? apiStatus.boundBaseUrl}）`
          : 'External API 当前未启用（system.json externalTaskApi.enabled=false）：令牌可创建，外部端点暂不可调用'
      }
    />
  ) : null;

  return (
    <PageContainer>
      {externalApiAlert}
      <ProTable<ExternalAccessTokenDto>
        actionRef={actionRef}
        rowKey="tokenId"
        columns={columns}
        request={async (params) => {
          const result = await listAccessTokens({
            status: statusFilter,
            page: params.current ?? 1,
            pageSize: params.pageSize ?? 20,
          });
          return { data: result.items, success: true, total: result.total };
        }}
        toolBarRender={() => [
          <Select
            key="status-filter"
            allowClear
            placeholder="状态过滤"
            style={{ width: 160 }}
            value={statusFilter}
            onChange={(value) => {
              setStatusFilter(value);
              actionRef.current?.reload();
            }}
            options={Object.entries(STATUS_META).map(([value, meta]) => ({
              value,
              label: meta.text,
            }))}
          />,
          <Button key="create" type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
            新建 Access Token
          </Button>,
        ]}
        search={false}
        pagination={{ pageSize: 20 }}
      />

      {/* ── 创建 Drawer ─────────────────────────────────────── */}
      <Drawer
        title="新建 Access Token"
        open={createOpen}
        onClose={() => setCreateOpen(false)}
        width={520}
        extra={
          <Space>
            <Button onClick={() => setCreateOpen(false)}>取消</Button>
            <Button type="primary" loading={creating} onClick={handleCreate}>
              创建
            </Button>
          </Space>
        }
      >
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          message="Secret 只在创建成功后显示一次，之后无法找回；请立即保存到安全位置。"
        />
        {apiStatus && (
          <Descriptions size="small" column={1} style={{ marginBottom: 16 }}>
            <Descriptions.Item label="有效期上限">
              {apiStatus.maxTokenLifetimeDays} 天（默认 {apiStatus.defaultTokenLifetimeDays} 天）
            </Descriptions.Item>
            <Descriptions.Item label="每人 Active 上限">{apiStatus.maxActiveTokensPerOwner} 个</Descriptions.Item>
          </Descriptions>
        )}
        <ProForm form={createForm} submitter={false} layout="vertical">
          <ProFormText
            name="name"
            label="名称"
            rules={[
              { required: true, message: '请输入名称' },
              { max: 100, message: '最长 100 字符' },
            ]}
            placeholder="如 codex-readonly"
          />
          <ProFormSelect
            name="workspaceIds"
            label="Workspace 允许清单（多选，至少一项，创建后不可扩大）"
            mode="multiple"
            options={workspaceOptions}
            rules={[{ required: true, message: '至少选择一个 workspace' }]}
          />
          <Form.Item
            name="scopes"
            label="Scope（默认仅最小只读权限；创建后不可扩大）"
            rules={[{ required: true, message: '至少选择一个 scope' }]}
          >
            <Checkbox.Group
              style={{ display: 'flex', flexDirection: 'column', gap: 8 }}
              options={SCOPE_OPTIONS.map((s) => ({
                value: s.value,
                label: (
                  <span>
                    <Typography.Text code>{s.value}</Typography.Text>
                    <Typography.Text type="secondary"> — {s.label.split(' — ')[1]}</Typography.Text>
                    {s.risk === 'high' && <Tag color="volcano" style={{ marginLeft: 8 }}>高风险</Tag>}
                  </span>
                ),
              }))}
            />
          </Form.Item>
          <ProFormSelect
            name="lifetimeDays"
            label="有效期（天）"
            options={[30, 60, 90, 180, 365]
              .filter((d) => !apiStatus || d <= apiStatus.maxTokenLifetimeDays)
              .map((d) => ({ value: d, label: `${d} 天` }))}
            rules={[{ required: true }]}
          />
        </ProForm>
      </Drawer>

      {/* ── 一次性 Secret Modal ─────────────────────────────── */}
      <SecretOnceModal
        token={secretToken}
        apiStatus={apiStatus}
        onClose={() => setSecretToken(null)}
      />

      {/* ── 详情 Drawer ─────────────────────────────────────── */}
      <Drawer
        title={detailToken ? `Token 详情：${detailToken.name}` : ''}
        open={detailToken !== null}
        onClose={() => setDetailToken(null)}
        width={480}
      >
        {detailToken && (
          <Descriptions column={1} size="small" bordered>
            <Descriptions.Item label="TokenId">{detailToken.tokenId}</Descriptions.Item>
            <Descriptions.Item label="KeyId">{detailToken.keyId}</Descriptions.Item>
            <Descriptions.Item label="前缀">
              <Typography.Text code>{detailToken.displayPrefix}</Typography.Text>
            </Descriptions.Item>
            <Descriptions.Item label="状态">{statusTag(detailToken.status)}</Descriptions.Item>
            <Descriptions.Item label="Scope">{detailToken.scopes.join(', ')}</Descriptions.Item>
            <Descriptions.Item label="Workspaces">{detailToken.workspaces.join(', ')}</Descriptions.Item>
            <Descriptions.Item label="创建者">{detailToken.ownerUserId}</Descriptions.Item>
            <Descriptions.Item label="创建时间">{formatUtc(detailToken.createdAtUtc)}</Descriptions.Item>
            <Descriptions.Item label="到期时间">{formatUtc(detailToken.expiresAtUtc)}</Descriptions.Item>
            <Descriptions.Item label="最后使用">{formatUtc(detailToken.lastUsedAtUtc)}</Descriptions.Item>
            <Descriptions.Item label="管理版本">v{detailToken.version}</Descriptions.Item>
            {detailToken.revokedAtUtc && (
              <>
                <Descriptions.Item label="撤销时间">{formatUtc(detailToken.revokedAtUtc)}</Descriptions.Item>
                <Descriptions.Item label="撤销人">{detailToken.revokedByUserId ?? '-'}</Descriptions.Item>
                <Descriptions.Item label="撤销原因">{detailToken.revocationReason ?? '-'}</Descriptions.Item>
              </>
            )}
          </Descriptions>
        )}
      </Drawer>

      {/* ── 重命名 Modal ────────────────────────────────────── */}
      <Modal
        open={renaming !== null}
        title={renaming ? `重命名：${renaming.name}` : ''}
        okText="保存"
        confirmLoading={mutationLoading}
        onOk={handleRename}
        onCancel={() => setRenaming(null)}
        destroyOnClose
      >
        <Form form={renameForm} layout="vertical">
          <Form.Item
            name="name"
            label="新名称（不改变任何安全事实）"
            rules={[
              { required: true, message: '请输入名称' },
              { max: 100, message: '最长 100 字符' },
            ]}
          >
            <Input maxLength={100} />
          </Form.Item>
        </Form>
      </Modal>

      {/* ── 撤销 Modal ──────────────────────────────────────── */}
      <Modal
        open={revoking !== null}
        title={revoking ? `撤销 Token：${revoking.name}` : ''}
        okText="确认撤销"
        okButtonProps={{ danger: true }}
        confirmLoading={mutationLoading}
        onOk={handleRevoke}
        onCancel={() => setRevoking(null)}
        destroyOnClose
      >
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 16 }}
          message="撤销立即生效且不可恢复：正在使用此 Token 的第三方调用将立刻收到 401。"
        />
        <Form form={revokeForm} layout="vertical">
          <Form.Item
            name="reason"
            label="撤销原因（可选，最长 500 字符）"
            rules={[{ max: 500, message: '最长 500 字符' }]}
          >
            <Input.TextArea rows={3} maxLength={500} placeholder="如：密钥疑似泄漏 / 集成下线" />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
}
