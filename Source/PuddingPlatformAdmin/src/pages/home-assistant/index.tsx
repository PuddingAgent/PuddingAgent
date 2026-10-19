import React, { useCallback, useEffect, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Form,
  Input,
  Modal,
  Popconfirm,
  Space,
  Table,
  Tag,
  Typography,
  message,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { PageContainer } from '@ant-design/pro-components';
import {
  deleteInstance,
  getDomainCatalog,
  listInstances,
  testInstance,
  upsertInstance,
} from './api';
import type {
  HaConnectionTestResult,
  HaDomainCatalog,
  HaInstanceAdminView,
} from './api';

const AGENT_STORAGE_KEY = 'pudding.ha.agentId';
const DEFAULT_AGENT_ID = 'default.global_general-assistant.6a8';

interface InstanceFormValues {
  alias: string;
  baseUrl: string;
  token?: string;
  optInDomains?: string[];
}

/**
 * Home Assistant 管理页。
 *
 * <p>安全口径（与后端一致）：令牌**只写不读**——列表只显示 hasToken 与预览，
 * 编辑时留空表示保留原值；永久黑名单域不提供勾选项，只在下方只读展示。</p>
 */
export default function HomeAssistantPage() {
  const [agentId, setAgentId] = useState(
    () => localStorage.getItem(AGENT_STORAGE_KEY) || DEFAULT_AGENT_ID,
  );
  const [instances, setInstances] = useState<HaInstanceAdminView[]>([]);
  const [domains, setDomains] = useState<HaDomainCatalog | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState<HaInstanceAdminView | null>(null);
  const [testResult, setTestResult] = useState<HaConnectionTestResult | null>(null);
  const [form] = Form.useForm<InstanceFormValues>();

  const reload = useCallback(async (id: string) => {
    if (!id.trim()) {
      setInstances([]);
      return;
    }
    setLoading(true);
    try {
      const [list, catalog] = await Promise.all([listInstances(id), getDomainCatalog()]);
      setInstances(list);
      setDomains(catalog);
    } catch (error) {
      message.error('加载 Home Assistant 配置失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void reload(agentId);
  }, [agentId, reload]);

  const openCreate = () => {
    setEditing(null);
    setTestResult(null);
    form.setFieldsValue({ alias: '', baseUrl: 'http://', token: '', optInDomains: [] });
    setModalOpen(true);
  };

  const openEdit = (row: HaInstanceAdminView) => {
    setEditing(row);
    setTestResult(null);
    form.setFieldsValue({
      alias: row.alias,
      baseUrl: row.baseUrl,
      token: '',
      optInDomains: row.optInDomains,
    });
    setModalOpen(true);
  };

  const submit = async () => {
    const values = await form.validateFields();
    const alias = (editing?.alias ?? values.alias).trim();
    if (!alias) {
      message.error('请填写实例别名');
      return;
    }

    setSaving(true);
    try {
      await upsertInstance(agentId, alias, {
        baseUrl: values.baseUrl.trim(),
        // 留空 = 保留原令牌（后端语义），新建时必须提供
        token: values.token?.trim() ? values.token.trim() : undefined,
        optInDomains: values.optInDomains ?? [],
      });
      message.success('已保存');
      setModalOpen(false);
      await reload(agentId);
    } catch {
      message.error('保存失败，请检查 baseUrl 与令牌');
    } finally {
      setSaving(false);
    }
  };

  const runTest = async (row: HaInstanceAdminView) => {
    setTestResult(null);
    try {
      const result = await testInstance(agentId, row.alias);
      setTestResult(result);
      if (result.ok) {
        message.success(`连接成功：${result.version ?? ''}`);
      } else {
        message.warning(`连接失败：${result.errorCode ?? 'unknown'}`);
      }
    } catch {
      message.error('测试请求失败');
    }
  };

  const columns: ColumnsType<HaInstanceAdminView> = [
    {
      title: '别名',
      dataIndex: 'alias',
      width: 140,
      render: (value: string) => <Tag color="blue">{value}</Tag>,
    },
    {
      title: '地址',
      dataIndex: 'baseUrl',
      ellipsis: true,
    },
    {
      title: '令牌',
      dataIndex: 'tokenPreview',
      width: 180,
      render: (value: string | null | undefined, row) =>
        row.hasToken ? <Tag color="green">{value ?? '已配置'}</Tag> : <Tag>未配置</Tag>,
    },
    {
      title: '已放开域',
      dataIndex: 'optInDomains',
      render: (value: string[]) =>
        value.length === 0 ? (
          <Typography.Text type="secondary">无（media_player / fan / climate 默认关闭）</Typography.Text>
        ) : (
          <Space wrap>
            {value.map((domain) => (
              <Tag key={domain}>{domain}</Tag>
            ))}
          </Space>
        ),
    },
    {
      title: '操作',
      width: 220,
      render: (_, row) => (
        <Space>
          <Button size="small" onClick={() => void runTest(row)}>
            测试
          </Button>
          <Button size="small" onClick={() => openEdit(row)}>
            编辑
          </Button>
          <Popconfirm
            title="删除该实例？"
            okText="删除"
            cancelText="取消"
            onConfirm={async () => {
              await deleteInstance(agentId, row.alias);
              message.success('已删除');
              await reload(agentId);
            }}
          >
            <Button size="small" danger>
              删除
            </Button>
          </Popconfirm>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer
      title="Home Assistant"
      subTitle="实例凭据与域权限（令牌只写不读）"
      extra={[
        <Button key="create" type="primary" onClick={openCreate}>
          新增实例
        </Button>,
      ]}
    >
      <Card style={{ marginBottom: 16 }}>
        <Space>
          <Typography.Text>Agent 实例 ID：</Typography.Text>
          <Input
            style={{ width: 420 }}
            value={agentId}
            onChange={(event) => {
              const next = event.target.value;
              setAgentId(next);
              localStorage.setItem(AGENT_STORAGE_KEY, next);
            }}
            placeholder={DEFAULT_AGENT_ID}
          />
          <Button onClick={() => void reload(agentId)} loading={loading}>
            刷新
          </Button>
        </Space>
        <Typography.Paragraph type="secondary" style={{ marginTop: 8, marginBottom: 0 }}>
          配置保存在该 Agent 私有目录的 <code>secrets/home-assistant.json</code>，不会写入仓库。
        </Typography.Paragraph>
      </Card>

      {testResult ? (
        <Alert
          style={{ marginBottom: 16 }}
          type={testResult.ok ? 'success' : 'warning'}
          showIcon
          message={
            testResult.ok
              ? `连接成功：${testResult.apiMessage ?? ''} · Core ${testResult.version ?? '?'} · ${testResult.timeZone ?? ''}`
              : `连接失败：${testResult.errorCode ?? 'unknown'} — ${testResult.message ?? ''}`
          }
          closable
          onClose={() => setTestResult(null)}
        />
      ) : null}

      <Card title="实例">
        <Table<HaInstanceAdminView>
          rowKey="alias"
          loading={loading}
          columns={columns}
          dataSource={instances}
          pagination={false}
          locale={{ emptyText: '尚未配置实例' }}
        />
      </Card>

      <Card title="域权限说明" style={{ marginTop: 16 }}>
        <Typography.Paragraph>
          <Tag color="green">默认允许</Tag>{' '}
          {(domains?.defaultAllowed ?? []).join('、') || '—'}
        </Typography.Paragraph>
        <Typography.Paragraph>
          <Tag color="orange">可在上方勾选放开</Tag>{' '}
          {(domains?.optInCandidates ?? []).join('、') || '—'}
        </Typography.Paragraph>
        <Typography.Paragraph style={{ marginBottom: 0 }}>
          <Tag color="red">永久拒绝（不可配置）</Tag>{' '}
          {(domains?.alwaysBlocked ?? []).join('、') || '—'}
        </Typography.Paragraph>
        <Alert
          style={{ marginTop: 12 }}
          type="info"
          showIcon
          message="永久拒绝的域可在 Home Assistant 内执行任意逻辑或触碰宿主机（如 shell_command / hassio / homeassistant / script / lock / alarm_control_panel / camera），因此不提供放开入口。"
        />
      </Card>

      <Modal
        title={editing ? `编辑实例：${editing.alias}` : '新增实例'}
        open={modalOpen}
        onCancel={() => setModalOpen(false)}
        onOk={() => void submit()}
        confirmLoading={saving}
        okText="保存"
        cancelText="取消"
        destroyOnClose
      >
        <Form form={form} layout="vertical">
          <Form.Item
            name="alias"
            label="别名"
            rules={[{ required: true, message: '请填写别名，例如 asus' }]}
          >
            <Input disabled={!!editing} placeholder="asus" />
          </Form.Item>
          <Form.Item
            name="baseUrl"
            label="地址"
            rules={[
              { required: true, message: '请填写 http/https 地址' },
              { pattern: /^https?:\/\/.+/i, message: '必须是 http:// 或 https:// 开头' },
            ]}
          >
            <Input placeholder="http://192.168.100.10:8123/" />
          </Form.Item>
          <Form.Item
            name="token"
            label="长期访问令牌"
            extra={
              editing
                ? `留空表示保留原令牌（当前：${editing.tokenPreview ?? '已配置'}）`
                : 'HA → 用户 → 长期访问令牌'
            }
            rules={editing ? [] : [{ required: true, message: '新建实例必须提供令牌' }]}
          >
            <Input.Password placeholder={editing ? '留空不修改' : 'eyJ...'} autoComplete="new-password" />
          </Form.Item>
          <Form.Item name="optInDomains" label="放开以下域（默认全部关闭）">
            <Checkbox.Group
              options={(domains?.optInCandidates ?? []).map((domain) => ({
                label: domain,
                value: domain,
              }))}
            />
          </Form.Item>
        </Form>
      </Modal>
    </PageContainer>
  );
}
