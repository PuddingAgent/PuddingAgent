import * as React from 'react';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { message } from 'antd';
import ExecutablePluginsPanel, { activationLabel } from './ExecutablePluginsPanel';
import { listSessions } from '@/services/platform/api';
import { getExecutablePlugins, getExecutablePluginDiagnostics, loadExecutablePlugin, unloadExecutablePlugin } from '@/services/platform/toolPlugins';

jest.mock('@/services/platform/api', () => ({ listSessions: jest.fn() }));
jest.mock('@/services/platform/toolPlugins', () => ({ getExecutablePlugins: jest.fn(), getExecutablePluginDiagnostics: jest.fn(), loadExecutablePlugin: jest.fn(), unloadExecutablePlugin: jest.fn(), importExecutablePlugin: jest.fn() }));
const scope = { workspaceId: 'w1', agentId: 'a1', sessionId: 's1' };
beforeEach(() => {
  jest.clearAllMocks();
  Object.defineProperty(globalThis.crypto, 'randomUUID', { configurable: true, value: () => 'operation-1' });
  (listSessions as jest.Mock).mockResolvedValue([{ sessionId: 's1', workspaceId: 'w1', agentInstanceId: 'a1', title: '测试会话', status: 'Active' }]);
  (getExecutablePluginDiagnostics as jest.Mock).mockResolvedValue([]);
  (getExecutablePlugins as jest.Mock).mockResolvedValue({ revision: 7, workerInstalled: true, coreProcessId: 123, sdkDirectory: 'sdk',
    catalog: { complete: true, packages: [{ id: 'sample.echo', version: '1.0.0', contentHash: 'hash', manifest: { name: '示例', description: '说明', requestedCapabilities: ['workspace.read'], tools: [], hooks: [] } }] },
    activations: [{ activationId: 'activation', pluginId: 'sample.echo', version: '1.0.0', state: 'Active', scope, toolNames: [], processId: 456 }] });
});
it('requires execution acknowledgement and binds loading to the selected real session', async () => {
  (loadExecutablePlugin as jest.Mock).mockResolvedValue({ state: 'Active' });
  render(<ExecutablePluginsPanel />);
  fireEvent.click(await screen.findByRole('button', { name: '加载到会话' }));
  const submit = screen.getByRole('button', { name: '授权并加载' });
  expect((submit as HTMLButtonElement).disabled).toBe(true);
  fireEvent.mouseDown(screen.getByRole('combobox', { name: '目标会话' }));
  fireEvent.click(await screen.findByText('测试会话 · a1'));
  expect((submit as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.click(submit);
  await waitFor(() => expect(loadExecutablePlugin).toHaveBeenCalledWith({ contentHash: 'hash', operationId: 'operation-1', scope,
    executeApproved: true, approvedCapabilities: ['workspace.read'], enableHooks: false, expectedRevision: 7 }));
});
it('never reports unconfirmed worker exit as unloaded', async () => {
  const success = jest.spyOn(message, 'success').mockImplementation(() => (() => {}) as any);
  const failure = jest.spyOn(message, 'error').mockImplementation(() => (() => {}) as any);
  (unloadExecutablePlugin as jest.Mock).mockResolvedValue({ state: 'StopFailed' });
  render(<ExecutablePluginsPanel />);
  fireEvent.click(await screen.findByRole('button', { name: /卸\s*载/ }));
  await waitFor(() => expect(failure).toHaveBeenCalledWith('进程退出尚未确认，请查看诊断'));
  expect(success).not.toHaveBeenCalled();
  expect(activationLabel('StopFailed')).toBe('退出未确认');
  expect(activationLabel('Unexpected')).toContain('未知');
  success.mockRestore(); failure.mockRestore();
});
