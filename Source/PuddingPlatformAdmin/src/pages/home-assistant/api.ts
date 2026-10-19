import { request } from '@umijs/max';

// Home Assistant 管理面 API（HomeAssistantAdminController）。
// 安全口径：后端**永不回传完整令牌**，只给 hasToken 与 tokenPreview；更新时 token 可省略。

export interface HaInstanceAdminView {
  alias: string;
  baseUrl: string;
  hasToken: boolean;
  tokenPreview?: string | null;
  optInDomains: string[];
}

export interface HaDomainCatalog {
  defaultAllowed: string[];
  optInCandidates: string[];
  alwaysBlocked: string[];
}

export interface HaConnectionTestResult {
  ok: boolean;
  apiMessage?: string | null;
  version?: string | null;
  timeZone?: string | null;
  errorCode?: string | null;
  message?: string | null;
}

export interface HaInstanceUpsertRequest {
  baseUrl: string;
  token?: string | null;
  optInDomains: string[];
}

const agentBase = (agentId: string) =>
  `/api/admin/home-assistant/agents/${encodeURIComponent(agentId)}`;

export function getDomainCatalog(): Promise<HaDomainCatalog> {
  return request('/api/admin/home-assistant/domains', { method: 'GET' });
}

export function listInstances(agentId: string): Promise<HaInstanceAdminView[]> {
  return request(`${agentBase(agentId)}/instances`, { method: 'GET' });
}

export function upsertInstance(
  agentId: string,
  alias: string,
  body: HaInstanceUpsertRequest,
): Promise<HaInstanceAdminView> {
  return request(`${agentBase(agentId)}/instances/${encodeURIComponent(alias)}`, {
    method: 'PUT',
    data: body,
  });
}

export function deleteInstance(agentId: string, alias: string): Promise<void> {
  return request(`${agentBase(agentId)}/instances/${encodeURIComponent(alias)}`, {
    method: 'DELETE',
  });
}

export function testInstance(
  agentId: string,
  alias: string,
): Promise<HaConnectionTestResult> {
  return request(`${agentBase(agentId)}/instances/${encodeURIComponent(alias)}/test`, {
    method: 'POST',
  });
}
