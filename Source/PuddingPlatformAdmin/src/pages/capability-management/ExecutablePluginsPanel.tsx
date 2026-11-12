import * as React from 'react';
import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Card, Checkbox, Empty, Modal, Select, Space, Table, Tag, Typography, Upload, message } from 'antd';
import { listSessions, type SessionRecord } from '@/services/platform/api';
import { getExecutablePlugins, getExecutablePluginDiagnostics, importExecutablePlugin, loadExecutablePlugin, unloadExecutablePlugin,
  type ExecutablePluginCatalog, type ExecutablePluginPackage, type PluginActivation, type PluginDiagnostic } from '@/services/platform/toolPlugins';

const { Text, Paragraph } = Typography;
export function activationLabel(state: string): string {
  return ({ Loading: '正在加载', Active: '已加载', Stopping: '正在卸载', Unloaded: '已卸载', Failed: '失败', StopFailed: '退出未确认' } as Record<string, string>)[state] ?? `未知状态：${state}`;
}
export default function ExecutablePluginsPanel() {
  const [catalog, setCatalog] = useState<ExecutablePluginCatalog>();
  const [diagnostics, setDiagnostics] = useState<PluginDiagnostic[]>([]);
  const [sessions, setSessions] = useState<SessionRecord[]>([]);
  const [sessionId, setSessionId] = useState<string>();
  const [selected, setSelected] = useState<ExecutablePluginPackage>();
  const [approved, setApproved] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const refresh = useCallback(async () => {
    try {
      const [next, failures, activeSessions] = await Promise.all([getExecutablePlugins(), getExecutablePluginDiagnostics(), listSessions()]);
      setCatalog(next); setDiagnostics(failures); setSessions(activeSessions.filter(s => s.agentInstanceId && !['Completed', 'Failed'].includes(s.status)));
      setError('');
    } catch (e) { setError(e instanceof Error ? e.message : '无法读取插件目录'); }
  }, []);
  useEffect(() => { void refresh(); }, [refresh]);
  const operation = async (action: () => Promise<void>) => {
    setBusy(true);
    try { await action(); } catch (e) { message.error(e instanceof Error ? e.message : '操作失败，请查看诊断'); }
    finally { await refresh(); setBusy(false); }
  };
  const renderState = (activation: PluginActivation) => <Space direction="vertical" size={2}>
    <Tag color={activation.state === 'Active' ? 'success' : ['Failed', 'StopFailed'].includes(activation.state) ? 'error' : 'default'}>{activationLabel(activation.state)}</Tag>
    {activation.stopWasForced && <Text type="secondary">已强制回收进程</Text>}
    {activation.failure && <Text type="danger">{activation.failure.code} · {activation.failure.message}</Text>}
  </Space>;
  const load = () => operation(async () => {
    const session = sessions.find(s => s.sessionId === sessionId);
    if (!selected?.manifest || !session?.agentInstanceId || !approved || !catalog) return;
    const result = await loadExecutablePlugin({ contentHash: selected.contentHash, operationId: crypto.randomUUID(),
      scope: { workspaceId: session.workspaceId, agentId: session.agentInstanceId, sessionId: session.sessionId },
      executeApproved: approved, approvedCapabilities: selected.manifest.requestedCapabilities,
      enableHooks: selected.manifest.hooks.length > 0, expectedRevision: catalog.revision });
    if (result.state === 'Active') { message.success('已加载；目标会话下一次模型调用可发现工具'); setSelected(undefined); }
    else message.error(`${activationLabel(result.state)}：${result.failure?.message ?? '请查看诊断'}`);
  });
  return <Space direction="vertical" size="middle" style={{ width: '100%' }}>
    <Alert type="info" showIcon message="按会话加载，即时生效" description="Agent 可以编写并打包自己的工具，通过 plugin_import → plugin_load 使用，通过 plugin_unload 回收。加载与卸载不重启 Core；已安装的包可在其他会话重新加载。" />
    {error && <Alert type="error" showIcon message={error} />}
    {catalog && !catalog.workerInstalled && <Alert type="warning" message="当前部署缺少插件运行器，请部署包含运行器的新版本。" />}
    {catalog && !catalog.catalog.complete && <Alert type="warning" message="包目录扫描未完成；当前列表不能证明其他包不存在。" />}
    <Space wrap>
      <Upload accept=".zip" showUploadList={false} disabled={busy} beforeUpload={file => { void operation(async () => { await importExecutablePlugin(file); message.success('已导入，尚未执行'); }); return false; }}>
        <Button type="primary" disabled={busy}>导入插件 ZIP</Button>
      </Upload>
      <Button onClick={() => void refresh()} disabled={busy}>刷新状态</Button>
      {catalog && <Text type="secondary">Core PID {catalog.coreProcessId} · 目录版本 {catalog.revision}</Text>}
    </Space>
    <Card title="已安装的包">
      <Table rowKey="contentHash" pagination={{ pageSize: 8 }} dataSource={catalog?.catalog.packages ?? []} columns={[
        { title: '插件', render: (_, p) => <><Text strong>{p.manifest?.name ?? p.id}</Text><Paragraph type="secondary">{p.manifest?.description ?? p.failure?.message}</Paragraph><Text code>{p.id} · {p.version}</Text></> },
        { title: '能力', render: (_, p) => <Space direction="vertical"><Text>{p.manifest?.tools.length ?? 0} 个工具 · {p.manifest?.hooks.length ?? 0} 个建议 Hook</Text><Text type="secondary">{p.manifest?.requestedCapabilities.join('、') || '无 Core 服务请求'}</Text></Space> },
        { title: '内容指纹', render: (_, p) => <Text copyable={{ text: p.contentHash }} code>{p.contentHash.slice(0, 12)}</Text> },
        { title: '操作', render: (_, p) => <Button disabled={busy || !p.manifest || !catalog?.workerInstalled} onClick={() => { setSelected(p); setApproved(false); }}>加载到会话</Button> },
      ]} />
    </Card>
    <Card title="运行中的实例与最近状态">
      <Table rowKey="activationId" pagination={{ pageSize: 8 }} dataSource={catalog?.activations ?? []} expandable={{ expandedRowRender: a => <><Paragraph copyable>{a.activationId}</Paragraph>{a.toolNames.map(t => <Paragraph code copyable key={t}>{t}</Paragraph>)}</> }} columns={[
        { title: '插件', render: (_, a) => <>{a.pluginId} <Tag>{a.version}</Tag></> },
        { title: '绑定会话', render: (_, a) => <Space direction="vertical" size={0}><Text>{sessions.find(s => s.sessionId === a.scope.sessionId)?.title ?? a.scope.sessionId}</Text><Text type="secondary">Agent {a.scope.agentId}</Text></Space> },
        { title: '状态', render: (_, a) => renderState(a) },
        { title: '进程', dataIndex: 'processId' },
        { title: '操作', render: (_, a) => <Button disabled={busy || a.state === 'Unloaded'} onClick={() => void operation(async () => {
          const result = await unloadExecutablePlugin({ activationId: a.activationId, scope: a.scope, expectedRevision: catalog!.revision });
          if (result.state === 'Unloaded') message.success('工具已撤销，进程退出已确认');
          else message.error('进程退出尚未确认，请查看诊断');
        })}>卸载</Button> },
      ]} />
    </Card>
    <Card title="诊断">
      {diagnostics.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="当前 Core 进程尚无插件失败记录" /> : diagnostics.slice(-20).reverse().map((d, i) =>
        <Paragraph key={`${d.failure.errorId}-${i}`}><Text code>{d.failure.code}</Text> {d.failure.message}<br /><Text type="secondary">{d.at} · {d.failure.stage} · {d.failure.errorId} · 副作用 {d.failure.effectOutcome}</Text></Paragraph>)}
    </Card>
    {catalog && <Paragraph type="secondary">SDK 目录：<Text copyable>{catalog.sdkDirectory}</Text></Paragraph>}
    <Modal title={`加载 ${selected?.manifest?.name ?? ''}`} open={!!selected} onCancel={() => setSelected(undefined)} onOk={() => void load()} confirmLoading={busy} okText="授权并加载" okButtonProps={{ disabled: !approved || !sessions.some(s => s.sessionId === sessionId) }}>
      <Paragraph>插件在独立进程中以当前 Windows 用户身份运行。进程隔离用于超时和故障回收，不是恶意代码沙箱。仅加载你信任的代码。</Paragraph>
      <Select aria-label="目标会话" placeholder="选择目标 Agent 的会话" style={{ width: '100%', marginBottom: 16 }} value={sessionId} onChange={setSessionId} showSearch optionFilterProp="label" options={sessions.map(s => ({ value: s.sessionId, label: `${s.title || s.sessionId} · ${s.agentInstanceId}` }))} />
      <Paragraph>Core 服务：{selected?.manifest?.requestedCapabilities.join('、') || '无'}<br />建议 Hook：{selected?.manifest?.hooks.map(h => h.id).join('、') || '无'}</Paragraph>
      <Checkbox checked={approved} onChange={e => setApproved(e.target.checked)}>允许此包执行代码，并启用以上 Core 服务和 Hook</Checkbox>
    </Modal>
  </Space>;
}
