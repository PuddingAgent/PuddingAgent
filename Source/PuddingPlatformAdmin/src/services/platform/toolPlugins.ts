import { request } from '@umijs/max';

export interface PluginScope { workspaceId: string; agentId: string; sessionId: string }
export interface PluginFailure { code: string; stage: string; errorId: string; message: string; effectOutcome: string }
export interface ExecutablePluginPackage {
  id: string; version: string; contentHash: string; status: string; failure?: PluginFailure;
  manifest?: { name: string; description: string; requestedCapabilities: string[];
    tools: { id: string; name: string; description: string; effects: string[] }[];
    hooks: { id: string; event: string }[] };
}
export interface PluginActivation {
  activationId: string; pluginId: string; version: string; contentHash: string;
  scope: PluginScope; state: string; revision: number; processId?: number;
  toolNames: string[]; hooksEnabled: boolean; failure?: PluginFailure; stopWasForced: boolean;
}
export interface ExecutablePluginCatalog {
  catalog: { packages: ExecutablePluginPackage[]; complete: boolean };
  activations: PluginActivation[]; revision: number; coreProcessId: number; workerInstalled: boolean; sdkDirectory: string;
}
export interface PluginDiagnostic { at: string; activationId: string; scope: PluginScope; failure: PluginFailure }
export const getExecutablePlugins = () => request<ExecutablePluginCatalog>('/api/tool-plugins');
export const getExecutablePluginDiagnostics = () => request<PluginDiagnostic[]>('/api/tool-plugins/diagnostics');
export const importExecutablePlugin = (file: File) => {
  const body = new FormData(); body.append('file', file);
  return request('/api/tool-plugins/import', { method: 'POST', data: body });
};
export const loadExecutablePlugin = (data: { contentHash: string; operationId: string; scope: PluginScope;
  executeApproved: boolean; approvedCapabilities: string[]; enableHooks: boolean; expectedRevision: number }) =>
  request<PluginActivation>('/api/tool-plugins/load', { method: 'POST', data });
export const unloadExecutablePlugin = (data: { activationId: string; scope: PluginScope; expectedRevision: number }) =>
  request<PluginActivation>('/api/tool-plugins/unload', { method: 'POST', data });
