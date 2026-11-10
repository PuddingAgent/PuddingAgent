import { request, useAccess } from '@umijs/max';
import { App, Button, Form, Input, Modal, Select, Space } from 'antd';
import React, { useEffect, useState } from 'react';
import { listWorkspaceChannels, type WorkspaceAgentDto, type WorkspaceChannelDto } from '@/services/platform/api';

type Binding = { app: string; organization: string; externalUser: string; userId: string };
type Settings = { mode: 'restricted' | 'verified'; bindings: Binding[] };

const Editor: React.FC<{ url: string; web: boolean }> = ({ url, web }) => {
  const [form] = Form.useForm<Settings>();
  const { message } = App.useApp();
  const [ready, setReady] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(false);
  useEffect(() => {
    let active = true;
    request<Settings>(url).then(data => {
      if (active) { form.setFieldsValue(data); setReady(true); }
    }).catch(() => { if (active) setError(true); });
    return () => { active = false; };
  }, [form, url]);
  if (!ready) return <p role="status">{error ? '无法读取设置，请确认当前账号有管理员权限。' : '正在读取设置…'}</p>;
  return <Form form={form} layout="vertical" onFinish={async values => {
    setSaving(true);
    try {
      await request(url, { method: 'PUT', data: { ...values, bindings: values.bindings ?? [] } });
      message.success('已保存，下次执行时生效');
    } catch { message.error('保存失败，请检查账号绑定和工作区权限。'); }
    finally { setSaving(false); }
  }}>
    <Form.Item name="mode" label="渠道信任" extra="可信请求可以使用 Agent 已授权的开发工具，工具调用仍按原审批策略处理。">
      <Select options={[{ value: 'restricted', label: '受限' }, { value: 'verified', label: web ? '信任已登录且可写此工作区的用户' : '仅信任已绑定的成员' }]} />
    </Form.Item>
    {!web && <Form.List name="bindings">{(fields, { add, remove }) => <>
      <p>使用渠道提供的原始 ID。每个外部账号绑定一位有工作区写权限的 Pudding 用户。</p>
      {fields.map(({ key, name }) => <Space key={key} align="start" wrap>
        {([['app','应用 ID'], ['organization','组织 / Team / Tenant ID'], ['externalUser','外部用户 ID'], ['userId','Pudding 用户 ID']] as const)
          .map(([field, label]) => <Form.Item key={field} label={label} name={[name, field]} rules={[{ required: true, whitespace: true }]}>
            <Input />
          </Form.Item>)}
        <Button onClick={() => remove(name)}>移除绑定</Button>
      </Space>)}
      <Form.Item><Button onClick={() => add()}>添加账号绑定</Button></Form.Item>
    </>}</Form.List>}
    <Button type="primary" htmlType="submit" loading={saving}>保存信任设置</Button>
  </Form>;
};

export const ChannelTrustSettings: React.FC<{ workspace: string; agents: WorkspaceAgentDto[] }> = ({ workspace, agents }) => {
  const access = useAccess();
  const [open, setOpen] = useState(false);
  const [agent, setAgent] = useState<string>();
  const [channel, setChannel] = useState('web-chat');
  const [channels, setChannels] = useState<WorkspaceChannelDto[]>([]);
  const { message } = App.useApp();
  const url = agent && `/api/workspaces/${encodeURIComponent(workspace)}/agents/${encodeURIComponent(agent)}/channel-trust/${encodeURIComponent(channel)}`;
  if (!access.canAdmin) return null;
  return <>
    <Button onClick={async () => {
      try { setChannels(await listWorkspaceChannels(workspace)); setOpen(true); }
      catch { message.error('渠道列表读取失败'); }
    }}>渠道信任</Button>
    <Modal title="渠道信任" open={open} width={720} footer={null} onCancel={() => setOpen(false)} destroyOnHidden>
      <Form layout="vertical">
        <Form.Item label="Agent"><Select value={agent} placeholder="选择 Agent" onChange={v => { setAgent(v); setChannel('web-chat'); }}
          options={agents.map(a => ({ value: a.agentId, label: a.name }))} /></Form.Item>
        <Form.Item label="消息来源"><Select value={channel} onChange={setChannel} options={[
          { value: 'web-chat', label: 'Chat 页面' }, { value: 'dingtalk-group', label: '钉钉群聊' },
          ...channels.filter(c => c.boundAgentId === agent).map(c => ({ value: c.channelId, label: c.name })),
        ]} /></Form.Item>
      </Form>
      {url && <Editor key={url} url={url} web={channel === 'web-chat'} />}
    </Modal>
  </>;
};
