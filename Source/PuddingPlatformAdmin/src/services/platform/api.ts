import { request } from '@umijs/max';

// ─── 类型定义（与 C# 模型对齐）───────────────────────────────────

export type SessionStatus = 'Active' | 'Idle' | 'Completed' | 'Failed' | 'Frozen';
export type SessionType = 'ServiceSession' | 'TaskSession' | 'AuditSession';
export type AgentTemplateType = 'Service' | 'Task' | 'Audit';

export interface ChannelBinding {
  channelId: string;
  channelType: string;
  defaultAgentTemplateId?: string;
  allowedAgentTemplateIds: string[];
}

export interface WorkspaceDefinition {
  workspaceId: string;
  name: string;
  description?: string;
  isEnabled: boolean;
  isFrozen: boolean;
  channelBindings: ChannelBinding[];
  agentTemplateIds: string[];
  auditAgentTemplateIds: string[];
}

export interface AgentTemplateDefinition {
  templateId: string;
  name: string;
  description?: string;
  templateType: AgentTemplateType;
  skillIds: string[];
  systemPrompt?: string;
  runtime?: {
    preferredModel?: string;
    maxContextTokens: number;
    maxTurnsPerSession: number;
  };
  capability?: {
    allowShellExecution: boolean;
    allowFileWrite: boolean;
    allowNetworkAccess: boolean;
    allowedToolNames: string[];
  };
}

export interface SessionRecord {
  sessionId: string;
  workspaceId: string;
  agentTemplateId: string;
  channelId: string;
  ownerUserId: string;
  sessionType: SessionType;
  status: SessionStatus;
  runtimeNodeId?: string;
  agentInstanceId?: string;
  createdAt: string;
  lastActiveAt: string;
}

// ─── Workspace API ────────────────────────────────────────────────

export async function listWorkspaces(): Promise<WorkspaceDefinition[]> {
  return request('/api/workspaces', { method: 'GET' });
}

export async function getWorkspace(id: string): Promise<WorkspaceDefinition> {
  return request(`/api/workspaces/${encodeURIComponent(id)}`, { method: 'GET' });
}

export async function createWorkspace(workspace: WorkspaceDefinition): Promise<WorkspaceDefinition> {
  return request('/api/workspaces', { method: 'POST', data: workspace });
}

export async function updateWorkspace(workspace: WorkspaceDefinition): Promise<WorkspaceDefinition> {
  return request(`/api/workspaces/${encodeURIComponent(workspace.workspaceId)}`, {
    method: 'PUT',
    data: workspace,
  });
}

export async function deleteWorkspace(id: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(id)}`, { method: 'DELETE' });
}

export async function freezeWorkspace(id: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(id)}/freeze`, { method: 'POST' });
}

export async function unfreezeWorkspace(id: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(id)}/unfreeze`, { method: 'POST' });
}

// ─── AgentTemplate API ───────────────────────────────────────────

export async function listAgentTemplates(): Promise<AgentTemplateDefinition[]> {
  return request('/api/agent-templates', { method: 'GET' });
}

export async function getAgentTemplate(id: string): Promise<AgentTemplateDefinition> {
  return request(`/api/agent-templates/${encodeURIComponent(id)}`, { method: 'GET' });
}

// ─── Session API ─────────────────────────────────────────────────

export async function listSessions(workspaceId?: string): Promise<SessionRecord[]> {
  const url = workspaceId
    ? `/api/sessions?workspaceId=${encodeURIComponent(workspaceId)}`
    : '/api/sessions';
  return request(url, { method: 'GET' });
}

export async function getSession(sessionId: string): Promise<SessionRecord> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}`, { method: 'GET' });
}

// ─── LLM Resource Pool Types ─────────────────────────────────────

export interface LlmProviderDto {
  id: number;
  providerId: string;
  name: string;
  protocol: string;
  baseUrl: string;
  hasApiKey: boolean;
  description?: string;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface LlmProviderDetailDto extends LlmProviderDto {
  quota?: LlmProviderQuotaDto;
  models: LlmModelDto[];
}

export interface LlmProviderQuotaDto {
  dailyTokenLimit?: number;
  monthlyTokenLimit?: number;
  dailyTokensUsed: number;
  monthlyTokensUsed: number;
  isSuspended: boolean;
  dailyResetAt?: string;
  monthlyResetAt?: string;
  updatedAt: string;
}

export interface LlmModelDto {
  id: number;
  providerId: number;
  modelId: string;
  name: string;
  description?: string;
  maxContextTokens: number;
  inputPricePer1MTokens: number;
  outputPricePer1MTokens: number;
  capabilityTags: string[];
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertLlmProviderRequest {
  providerId: string;
  name: string;
  protocol: string;
  baseUrl: string;
  apiKey?: string;
  description?: string;
  isEnabled: boolean;
}

export interface UpsertLlmModelRequest {
  modelId: string;
  name: string;
  description?: string;
  maxContextTokens: number;
  inputPricePer1MTokens: number;
  outputPricePer1MTokens: number;
  capabilityTags?: string[];
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export interface UpdateQuotaRequest {
  dailyTokenLimit?: number;
  monthlyTokenLimit?: number;
}

// ─── Global Agent Template Types ────────────────────────────────

export interface GlobalAgentTemplateDto {
  id: number;
  templateId: string;
  name: string;
  description?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  isBuiltIn: boolean;
  isEnabled: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertGlobalAgentTemplateRequest {
  templateId: string;
  name: string;
  description?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  isEnabled: boolean;
  sortOrder: number;
}

// ─── Workspace Agent Template Types ─────────────────────────────

export interface WorkspaceAgentTemplateDto {
  id: number;
  workspaceId: string;
  templateId: string;
  name: string;
  description?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  baseGlobalTemplateId?: string;
  isEnabled: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertWorkspaceAgentTemplateRequest {
  workspaceId: string;
  templateId: string;
  name: string;
  description?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  baseGlobalTemplateId?: string;
  isEnabled: boolean;
  sortOrder: number;
}

// ─── LLM Provider API ────────────────────────────────────────────

export async function listLlmProviders(): Promise<LlmProviderDto[]> {
  return request('/api/llm/providers', { method: 'GET' });
}

export async function getLlmProvider(providerId: string): Promise<LlmProviderDetailDto> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}`, { method: 'GET' });
}

export async function createLlmProvider(req: UpsertLlmProviderRequest): Promise<LlmProviderDto> {
  return request('/api/llm/providers', { method: 'POST', data: req });
}

export async function updateLlmProvider(
  providerId: string, req: UpsertLlmProviderRequest,
): Promise<LlmProviderDto> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteLlmProvider(providerId: string): Promise<void> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}`, { method: 'DELETE' });
}

export async function updateLlmProviderQuota(
  providerId: string, req: UpdateQuotaRequest,
): Promise<LlmProviderQuotaDto> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}/quota`, {
    method: 'PUT', data: req,
  });
}

export async function resetDailyQuota(providerId: string): Promise<void> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}/quota/reset-daily`, {
    method: 'POST',
  });
}

// ─── LLM Model API ───────────────────────────────────────────────

export async function listLlmModels(providerId: string): Promise<LlmModelDto[]> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}/models`, { method: 'GET' });
}

export async function createLlmModel(
  providerId: string, req: UpsertLlmModelRequest,
): Promise<LlmModelDto> {
  return request(`/api/llm/providers/${encodeURIComponent(providerId)}/models`, {
    method: 'POST', data: req,
  });
}

export async function updateLlmModel(
  providerId: string, modelId: string, req: UpsertLlmModelRequest,
): Promise<LlmModelDto> {
  return request(
    `/api/llm/providers/${encodeURIComponent(providerId)}/models/${encodeURIComponent(modelId)}`,
    { method: 'PUT', data: req },
  );
}

export async function deleteLlmModel(providerId: string, modelId: string): Promise<void> {
  return request(
    `/api/llm/providers/${encodeURIComponent(providerId)}/models/${encodeURIComponent(modelId)}`,
    { method: 'DELETE' },
  );
}

// ─── Global Agent Template API ───────────────────────────────────

export async function listGlobalAgentTemplates(enabledOnly?: boolean): Promise<GlobalAgentTemplateDto[]> {
  const url = enabledOnly ? '/api/global-agent-templates?enabledOnly=true' : '/api/global-agent-templates';
  return request(url, { method: 'GET' });
}

export async function getGlobalAgentTemplate(templateId: string): Promise<GlobalAgentTemplateDto> {
  return request(`/api/global-agent-templates/${encodeURIComponent(templateId)}`, { method: 'GET' });
}

export async function createGlobalAgentTemplate(
  req: UpsertGlobalAgentTemplateRequest,
): Promise<GlobalAgentTemplateDto> {
  return request('/api/global-agent-templates', { method: 'POST', data: req });
}

export async function updateGlobalAgentTemplate(
  templateId: string, req: UpsertGlobalAgentTemplateRequest,
): Promise<GlobalAgentTemplateDto> {
  return request(`/api/global-agent-templates/${encodeURIComponent(templateId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteGlobalAgentTemplate(templateId: string): Promise<void> {
  return request(`/api/global-agent-templates/${encodeURIComponent(templateId)}`, { method: 'DELETE' });
}

// ─── Workspace Agent Template API ────────────────────────────────

export async function listWorkspaceAgentTemplates(
  workspaceId?: string,
): Promise<WorkspaceAgentTemplateDto[]> {
  const url = workspaceId
    ? `/api/workspace-agent-templates?workspaceId=${encodeURIComponent(workspaceId)}`
    : '/api/workspace-agent-templates';
  return request(url, { method: 'GET' });
}

export async function createWorkspaceAgentTemplate(
  req: UpsertWorkspaceAgentTemplateRequest,
): Promise<WorkspaceAgentTemplateDto> {
  return request('/api/workspace-agent-templates', { method: 'POST', data: req });
}

export async function updateWorkspaceAgentTemplate(
  id: number, req: UpsertWorkspaceAgentTemplateRequest,
): Promise<WorkspaceAgentTemplateDto> {
  return request(`/api/workspace-agent-templates/${id}`, { method: 'PUT', data: req });
}

export async function deleteWorkspaceAgentTemplate(id: number): Promise<void> {
  return request(`/api/workspace-agent-templates/${id}`, { method: 'DELETE' });
}

// ─── User & Org Types ────────────────────────────────────────────

export type UserType = 'Admin' | 'SimpleUser';
export type TeamMemberRole = 'Member' | 'Admin';
export type WorkspaceAccessPolicy = 'None' | 'ReadOnly' | 'Write' | 'Manage';

export interface AppUserDto {
  id: number;
  userId: string;
  username: string;
  email: string;
  displayName?: string;
  userType: UserType;
  isEnabled: boolean;
  roleIds: string[];
  createdAt: string;
}

export interface CreateUserRequest {
  userId: string;
  username: string;
  email: string;
  password: string;
  displayName?: string;
  userType: UserType;
}

export interface UpdateUserRequest {
  username: string;
  email: string;
  displayName?: string;
  userType: UserType;
  isEnabled: boolean;
}

export interface AppRoleDto {
  id: number;
  roleId: string;
  name: string;
  description?: string;
  permissions: string[];
  isSystemRole: boolean;
  createdAt: string;
}

export interface UpsertRoleRequest {
  roleId: string;
  name: string;
  description?: string;
  permissions: string[];
}

export interface TeamDto {
  id: number;
  teamId: string;
  name: string;
  description?: string;
  isEnabled: boolean;
  memberCount: number;
  workspaceCount: number;
  createdAt: string;
}

export interface TeamDetailDto extends TeamDto {
  members: TeamMemberDto[];
  workspaces: WorkspaceWithPermDto[];
}

export interface UpsertTeamRequest {
  teamId: string;
  name: string;
  description?: string;
  isEnabled: boolean;
}

export interface TeamMemberDto {
  userId: string;
  username: string;
  displayName?: string;
  role: TeamMemberRole;
}

export interface AddTeamMemberRequest {
  userId: string;
  role: TeamMemberRole;
}

export interface WorkspaceWithPermDto {
  id: number;
  workspaceId: string;
  slug: string;
  teamId: string;
  teamName: string;
  name: string;
  description?: string;
  teamAccessPolicy: WorkspaceAccessPolicy;
  companyAccessPolicy: WorkspaceAccessPolicy;
  isEnabled: boolean;
  isFrozen: boolean;
  memberCount: number;
  createdAt: string;
}

export interface CreateWorkspaceRequest {
  workspaceId: string;
  teamId: string;
  name: string;
  description?: string;
  teamAccessPolicy: WorkspaceAccessPolicy;
  companyAccessPolicy: WorkspaceAccessPolicy;
}

export interface UpdateWorkspaceRequest {
  name: string;
  description?: string;
  teamAccessPolicy: WorkspaceAccessPolicy;
  companyAccessPolicy: WorkspaceAccessPolicy;
  isEnabled: boolean;
}

export interface WorkspaceMemberDto {
  id: number;
  userId: string;
  username: string;
  displayName?: string;
  accessLevel: WorkspaceAccessPolicy;
}

export interface AddWorkspaceMemberRequest {
  userId: string;
  accessLevel: WorkspaceAccessPolicy;
}

// ─── User API ─────────────────────────────────────────────────────

export async function listUsers(): Promise<AppUserDto[]> {
  return request('/api/users', { method: 'GET' });
}

export async function getUser(userId: string): Promise<AppUserDto> {
  return request(`/api/users/${encodeURIComponent(userId)}`, { method: 'GET' });
}

export async function createUser(req: CreateUserRequest): Promise<AppUserDto> {
  return request('/api/users', { method: 'POST', data: req });
}

export async function updateUser(userId: string, req: UpdateUserRequest): Promise<AppUserDto> {
  return request(`/api/users/${encodeURIComponent(userId)}`, { method: 'PUT', data: req });
}

export async function changeUserPassword(userId: string, newPassword: string): Promise<void> {
  return request(`/api/users/${encodeURIComponent(userId)}/password`, {
    method: 'PUT', data: { newPassword },
  });
}

export async function assignUserRoles(userId: string, roleIds: string[]): Promise<AppUserDto> {
  return request(`/api/users/${encodeURIComponent(userId)}/roles`, {
    method: 'PUT', data: { roleIds },
  });
}

export async function deleteUser(userId: string): Promise<void> {
  return request(`/api/users/${encodeURIComponent(userId)}`, { method: 'DELETE' });
}

// ─── Role API ─────────────────────────────────────────────────────

export async function listRoles(): Promise<AppRoleDto[]> {
  return request('/api/roles', { method: 'GET' });
}

export async function createRole(req: UpsertRoleRequest): Promise<AppRoleDto> {
  return request('/api/roles', { method: 'POST', data: req });
}

export async function updateRole(roleId: string, req: UpsertRoleRequest): Promise<AppRoleDto> {
  return request(`/api/roles/${encodeURIComponent(roleId)}`, { method: 'PUT', data: req });
}

export async function deleteRole(roleId: string): Promise<void> {
  return request(`/api/roles/${encodeURIComponent(roleId)}`, { method: 'DELETE' });
}

// ─── Team API ────────────────────────────────────────────────────

export async function listTeams(): Promise<TeamDto[]> {
  return request('/api/teams', { method: 'GET' });
}

export async function getTeam(teamId: string): Promise<TeamDetailDto> {
  return request(`/api/teams/${encodeURIComponent(teamId)}`, { method: 'GET' });
}

export async function createTeam(req: UpsertTeamRequest): Promise<TeamDto> {
  return request('/api/teams', { method: 'POST', data: req });
}

export async function updateTeam(teamId: string, req: UpsertTeamRequest): Promise<TeamDto> {
  return request(`/api/teams/${encodeURIComponent(teamId)}`, { method: 'PUT', data: req });
}

export async function deleteTeam(teamId: string): Promise<void> {
  return request(`/api/teams/${encodeURIComponent(teamId)}`, { method: 'DELETE' });
}

export async function addTeamMember(teamId: string, req: AddTeamMemberRequest): Promise<TeamMemberDto> {
  return request(`/api/teams/${encodeURIComponent(teamId)}/members`, { method: 'POST', data: req });
}

export async function removeTeamMember(teamId: string, userId: string): Promise<void> {
  return request(
    `/api/teams/${encodeURIComponent(teamId)}/members/${encodeURIComponent(userId)}`,
    { method: 'DELETE' },
  );
}

// ─── Workspace (under teams) API ─────────────────────────────────

export async function listTeamWorkspaces(teamId: string): Promise<WorkspaceWithPermDto[]> {
  return request(`/api/teams/${encodeURIComponent(teamId)}/workspaces`, { method: 'GET' });
}

export async function createTeamWorkspace(
  teamId: string, req: CreateWorkspaceRequest,
): Promise<WorkspaceWithPermDto> {
  return request(`/api/teams/${encodeURIComponent(teamId)}/workspaces`, {
    method: 'POST', data: req,
  });
}

export async function getWorkspaceBySlug(workspaceId: string): Promise<WorkspaceWithPermDto> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}`, { method: 'GET' });
}

export async function updateWorkspacePerm(
  workspaceId: string, req: UpdateWorkspaceRequest,
): Promise<WorkspaceWithPermDto> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteWorkspacePerm(workspaceId: string): Promise<void> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}`, { method: 'DELETE' });
}

export async function listWorkspaceMembers(workspaceId: string): Promise<WorkspaceMemberDto[]> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}/members`, {
    method: 'GET',
  });
}

export async function addWorkspaceMember(
  workspaceId: string, req: AddWorkspaceMemberRequest,
): Promise<WorkspaceMemberDto> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}/members`, {
    method: 'POST', data: req,
  });
}

export async function removeWorkspaceMember(workspaceId: string, id: number): Promise<void> {
  return request(`/api/teams/workspaces/${encodeURIComponent(workspaceId)}/members/${id}`, {
    method: 'DELETE',
  });
}

