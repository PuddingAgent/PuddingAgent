import { Alert, Button, Form, Input, Modal, Select, Space, Spin, Table, Tag, Typography, message } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { SearchOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import type { HubSkillUpdateDto } from '@/services/platform/api';
import {
  listHubUpdates,
  publishHubSkill,
  publishHubSkillVersion,
  registerHubInstall,
} from '@/services/platform/api';
import {
  ALL_EVO_ACTIONS,
  EVO_ACTION_TEXT,
  errText,
  formatDateTime,
  HubEmpty,
} from './skillHubShared';

const { TextArea } = Input;
const { Text } = Typography;

/**
 * SKILL Hub 写路径入口（发布技能 / 发布新版本 / 登记安装 / 检查更新）。
 * 后端契约：SkillHubController.cs（类级 [Route("api/skill-hub")]）
 *   POST /api/skill-hub/skills                      → 发布技能
 *   POST /api/skill-hub/skills/{skillId}/versions    → 发布新版本（进化）
 *   POST /api/skill-hub/installs                    → 登记安装台账
 *   GET  /api/skill-hub/updates?agentInstanceId=     → 待更新清单
 * 失败路径统一 message.error，并在文案里带上端点路径（复用 api.ts 的 hubRequest 错误风格）。
 * 不新增任何 npm 依赖：仅用 antd / @ant-design/icons。
 */

/** 与后端 SkillHubService.SkillIdPattern 一致：^[a-z0-9][a-z0-9-]{1,127}$ */
const SKILL_ID_PATTERN = /^[a-z0-9][a-z0-9-]{1,127}$/;

const SKILL_ID_RULES = [
  { required: true, message: '请填写 Skill ID' },
  {
    pattern: SKILL_ID_PATTERN,
    message: '格式不合法：小写字母或数字开头，仅含小写字母、数字、连字符，长度 2~128',
  },
];

/** 标签输入：逗号（中英文）或空白分隔 */
function splitTags(input?: string): string[] {
  return (input ?? '')
    .split(/[,，\s]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

// ── ① 发布技能 ────────────────────────────────────────────────

interface PublishSkillFormValues {
  skillId: string;
  name: string;
  version: string;
  summary?: string;
  tags?: string;
  skillMarkdown: string;
}

export const PublishHubSkillModal: React.FC<{
  open: boolean;
  onClose: () => void;
  /** 发布成功后回调（父级据此刷新列表） */
  onPublished: () => void;
}> = ({ open, onClose, onPublished }) => {
  const [form] = Form.useForm<PublishSkillFormValues>();
  const [submitting, setSubmitting] = useState(false);

  const handleOk = useCallback(async (): Promise<void> => {
    let values: PublishSkillFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return; // 字段级校验失败：antd 已在字段下方给出提示
    }

    const version = (values.version ?? '').trim() || '1.0.0';
    setSubmitting(true);
    try {
      const created = await publishHubSkill({
        skillId: values.skillId.trim(),
        name: values.name.trim(),
        version,
        skillMarkdown: values.skillMarkdown,
        summary: values.summary?.trim() ? values.summary.trim() : null,
        tags: splitTags(values.tags),
      });
      message.success(`技能已发布：${created.skillId}@${created.latestVersion}`);
      form.resetFields();
      onPublished();
      onClose();
    } catch (e) {
      message.error(`发布技能失败（POST /api/skill-hub/skills）：${errText(e)}`);
    } finally {
      setSubmitting(false);
    }
  }, [form, onClose, onPublished]);

  return (
    <Modal
      title="发布技能到 SKILL Hub"
      open={open}
      width={680}
      okText="发布"
      cancelText="取消"
      confirmLoading={submitting}
      onCancel={onClose}
      onOk={() => void handleOk()}
      maskClosable={!submitting}
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message="POST /api/skill-hub/skills"
        description="同一 Skill ID 已存在时服务端返回 409（冲突）—— 后续版本请改用详情抽屉里的「发布新版本」。"
      />
      <Form form={form} layout="vertical" initialValues={{ version: '1.0.0' }}>
        <Form.Item name="skillId" label="Skill ID" rules={SKILL_ID_RULES}>
          <Input placeholder="如 my-skill（小写 + 连字符）" />
        </Form.Item>
        <Form.Item name="name" label="名称" rules={[{ required: true, message: '请填写名称' }]}>
          <Input placeholder="展示用名称" />
        </Form.Item>
        <Form.Item
          name="version"
          label="版本号"
          rules={[{ required: true, message: '请填写版本号' }]}
          tooltip="语义版本，如 1.0.0；服务端不做重排，仅做语义比较"
        >
          <Input placeholder="1.0.0" />
        </Form.Item>
        <Form.Item name="summary" label="摘要">
          <Input placeholder="一句话说明（可空）" />
        </Form.Item>
        <Form.Item name="tags" label="标签" tooltip="逗号分隔，如 a,b,c">
          <Input placeholder="逗号分隔（可空）" />
        </Form.Item>
        <Form.Item
          name="skillMarkdown"
          label="SKILL.md 内容"
          rules={[{ required: true, message: '请填写 SKILL.md 内容' }]}
        >
          <TextArea
            rows={10}
            placeholder="粘贴 SKILL.md 全文；服务端按内容计算 ContentHash（SHA256 前 16 字节）"
          />
        </Form.Item>
      </Form>
    </Modal>
  );
};

// ── ② 发布新版本（进化）──────────────────────────────────────

interface PublishVersionFormValues {
  version: string;
  skillMarkdown: string;
  evolutionAction: string;
  parentVersion?: string;
  publishNote?: string;
}

export const PublishHubSkillVersionModal: React.FC<{
  open: boolean;
  skillId: string;
  skillName: string;
  /** 当前最新版本：作为父版本默认值 */
  latestVersion: string;
  onClose: () => void;
  onPublished: () => void;
}> = ({ open, skillId, skillName, latestVersion, onClose, onPublished }) => {
  const [form] = Form.useForm<PublishVersionFormValues>();
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (open) {
      form.setFieldsValue({
        evolutionAction: 'patch',
        parentVersion: latestVersion || undefined,
      });
    }
  }, [open, latestVersion, form]);

  const handleOk = useCallback(async (): Promise<void> => {
    if (!skillId) {
      message.error('发布新版本失败：缺少 Skill ID（请先在技能列表打开详情）。');
      return;
    }
    let values: PublishVersionFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return;
    }

    setSubmitting(true);
    try {
      const published = await publishHubSkillVersion(skillId, {
        version: values.version.trim(),
        skillMarkdown: values.skillMarkdown,
        evolutionAction: values.evolutionAction,
        parentVersion: values.parentVersion?.trim() ? values.parentVersion.trim() : null,
        publishNote: values.publishNote?.trim() ? values.publishNote.trim() : null,
      });
      message.success(`新版本已发布：${published.skillId}@${published.version}`);
      form.resetFields();
      onPublished();
      onClose();
    } catch (e) {
      message.error(
        `发布新版本失败（POST /api/skill-hub/skills/${skillId}/versions）：${errText(e)}`,
      );
    } finally {
      setSubmitting(false);
    }
  }, [form, onClose, onPublished, skillId]);

  return (
    <Modal
      title={`发布新版本 — ${skillName || skillId}${latestVersion ? `（当前 v${latestVersion}）` : ''}`}
      open={open}
      width={680}
      okText="发布版本"
      cancelText="取消"
      confirmLoading={submitting}
      onCancel={onClose}
      onOk={() => void handleOk()}
      maskClosable={!submitting}
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message={`POST /api/skill-hub/skills/${skillId || '{skillId}'}/versions`}
        description="版本号已存在时服务端返回 409；进化动作构成血缘边（EVO MAP 据此渲染）。"
      />
      <Form form={form} layout="vertical">
        <Form.Item
          name="version"
          label="新版本号"
          rules={[{ required: true, message: '请填写新版本号' }]}
          tooltip="必须与既有版本号不同，如 1.1.0"
        >
          <Input placeholder="如 1.1.0" />
        </Form.Item>
        <Form.Item
          name="evolutionAction"
          label="进化动作"
          rules={[{ required: true, message: '请选择进化动作' }]}
        >
          <Select
            options={ALL_EVO_ACTIONS.map((a) => ({
              value: a,
              label: `${EVO_ACTION_TEXT[a] ?? a}（${a}）`,
            }))}
          />
        </Form.Item>
        <Form.Item name="parentVersion" label="父版本" tooltip="构成血缘边；留空表示无父版本">
          <Input placeholder={latestVersion || '如 1.0.0'} />
        </Form.Item>
        <Form.Item
          name="skillMarkdown"
          label="SKILL.md 内容"
          rules={[{ required: true, message: '请填写 SKILL.md 内容' }]}
        >
          <TextArea rows={10} placeholder="粘贴本版本的 SKILL.md 全文" />
        </Form.Item>
        <Form.Item name="publishNote" label="发布说明">
          <Input placeholder="本次变更说明（可空）" />
        </Form.Item>
      </Form>
    </Modal>
  );
};

// ── ③ 登记安装 ────────────────────────────────────────────────

interface RegisterInstallFormValues {
  skillId: string;
  agentInstanceId: string;
  installedVersion: string;
  contentHash?: string;
}

export const RegisterHubInstallModal: React.FC<{
  open: boolean;
  onClose: () => void;
  onRegistered: () => void;
}> = ({ open, onClose, onRegistered }) => {
  const [form] = Form.useForm<RegisterInstallFormValues>();
  const [submitting, setSubmitting] = useState(false);

  const handleOk = useCallback(async (): Promise<void> => {
    let values: RegisterInstallFormValues;
    try {
      values = await form.validateFields();
    } catch {
      return;
    }

    setSubmitting(true);
    try {
      const saved = await registerHubInstall({
        skillId: values.skillId.trim(),
        agentInstanceId: values.agentInstanceId.trim(),
        installedVersion: values.installedVersion.trim(),
        contentHash: values.contentHash?.trim() ? values.contentHash.trim() : null,
      });
      message.success(`安装台账已登记：${saved.skillId} → ${saved.agentInstanceId}@${saved.installedVersion}`);
      form.resetFields();
      onRegistered();
      onClose();
    } catch (e) {
      message.error(`登记安装失败（POST /api/skill-hub/installs）：${errText(e)}`);
    } finally {
      setSubmitting(false);
    }
  }, [form, onClose, onRegistered]);

  return (
    <Modal
      title="登记安装台账"
      open={open}
      width={600}
      okText="登记"
      cancelText="取消"
      confirmLoading={submitting}
      onCancel={onClose}
      onOk={() => void handleOk()}
      maskClosable={!submitting}
    >
      <Alert
        type="info"
        showIcon
        style={{ marginBottom: 12 }}
        message="POST /api/skill-hub/installs"
        description="按（Skill ID + Agent 实例 ID）upsert：已存在则更新版本与更新时间，并重算装机数；技能不存在返回 404。"
      />
      <Form form={form} layout="vertical">
        <Form.Item name="skillId" label="Skill ID" rules={SKILL_ID_RULES}>
          <Input placeholder="必须是 Hub 中已存在的技能，如 ppt-master" />
        </Form.Item>
        <Form.Item
          name="agentInstanceId"
          label="Agent 实例 ID"
          rules={[{ required: true, message: '请填写 Agent 实例 ID' }]}
        >
          <Input placeholder="如 8fd0f96f82224bc0a282b4626ea4e4f5-sub-xxxx" />
        </Form.Item>
        <Form.Item
          name="installedVersion"
          label="已安装版本"
          rules={[{ required: true, message: '请填写已安装版本' }]}
        >
          <Input placeholder="如 6.6.0" />
        </Form.Item>
        <Form.Item name="contentHash" label="ContentHash" tooltip="可空：留空表示未提供本地内容指纹">
          <Input placeholder="SHA256 前 16 字节 hex（可空）" />
        </Form.Item>
      </Form>
    </Modal>
  );
};

// ── ④ 检查更新 ────────────────────────────────────────────────

const updateColumns: ColumnsType<HubSkillUpdateDto> = [
  {
    title: '技能',
    dataIndex: 'skillId',
    render: (_: unknown, r: HubSkillUpdateDto) => (
      <Space size={6}>
        <Text strong>{r.name}</Text>
        <Text code style={{ fontSize: 11 }}>
          {r.skillId}
        </Text>
      </Space>
    ),
  },
  {
    title: '已装版本',
    dataIndex: 'installedVersion',
    width: 110,
    render: (v: string) => <Tag>v{v}</Tag>,
  },
  {
    title: '最新版本',
    dataIndex: 'latestVersion',
    width: 110,
    render: (v: string) => <Tag color="orange">v{v}</Tag>,
  },
  {
    title: '最新动作',
    dataIndex: 'latestEvolutionAction',
    width: 100,
    render: (a: string) => <Tag color="blue">{EVO_ACTION_TEXT[a] ?? a}</Tag>,
  },
  {
    title: '发布时间',
    dataIndex: 'latestPublishedAt',
    width: 180,
    render: (v: string) => <Text style={{ fontSize: 12 }}>{formatDateTime(v)}</Text>,
  },
  {
    title: '发布说明',
    dataIndex: 'publishNote',
    render: (v?: string | null) => v || <Text type="secondary">-</Text>,
  },
];

export const CheckHubUpdatesModal: React.FC<{
  open: boolean;
  onClose: () => void;
}> = ({ open, onClose }) => {
  const [agentInstanceId, setAgentInstanceId] = useState('');
  const [queriedId, setQueriedId] = useState('');
  const [loading, setLoading] = useState(false);
  const [updates, setUpdates] = useState<HubSkillUpdateDto[]>([]);

  const query = useCallback(async (): Promise<void> => {
    const id = agentInstanceId.trim();
    if (!id) {
      message.warning('请先填写 Agent 实例 ID');
      return;
    }
    setLoading(true);
    try {
      const data = await listHubUpdates(id);
      setUpdates(data ?? []);
      setQueriedId(id);
    } catch (e) {
      setUpdates([]);
      setQueriedId(id);
      message.error(
        `检查更新失败（GET /api/skill-hub/updates?agentInstanceId=${encodeURIComponent(id)}）：${errText(e)}`,
      );
    } finally {
      setLoading(false);
    }
  }, [agentInstanceId]);

  return (
    <Modal
      title="检查待更新技能（GET /api/skill-hub/updates）"
      open={open}
      width={900}
      footer={
        <Button type="primary" onClick={onClose}>
          关闭
        </Button>
      }
      onCancel={onClose}
    >
      <Space size={8} wrap style={{ marginBottom: 12 }}>
        <Input
          allowClear
          placeholder="Agent 实例 ID"
          style={{ width: 420 }}
          value={agentInstanceId}
          onChange={(e) => setAgentInstanceId(e.target.value)}
          onPressEnter={() => void query()}
        />
        <Button type="primary" icon={<SearchOutlined />} loading={loading} onClick={() => void query()}>
          检查更新
        </Button>
      </Space>

      {loading ? (
        <div style={{ textAlign: 'center', padding: '32px 0' }}>
          <Spin />
        </div>
      ) : queriedId ? (
        updates.length > 0 ? (
          <Table<HubSkillUpdateDto>
            rowKey="skillId"
            size="small"
            loading={loading}
            columns={updateColumns}
            dataSource={updates}
            pagination={false}
          />
        ) : (
          <HubEmpty
            description={`Agent ${queriedId} 没有待更新技能：未登记任何安装，或所有已装版本都已是最新`}
          />
        )
      ) : (
        <HubEmpty description="填写 Agent 实例 ID 后点击「检查更新」：仅当已装版本低于 Hub 最新版本时才会列出" />
      )}
    </Modal>
  );
};
