import { request } from '@umijs/max';

// ─── 类型定义（与 C# 模型对齐）───────────────────────────────────

export type SessionStatus = 'Active' | 'Idle' | 'Completed' | 'Failed' | 'Frozen';
export type SessionType = 'ServiceSession' | 'TaskSession' | 'AuditSession';
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
  title?: string;
  createdAt: string;
  lastActiveAt: string;
}

// ─── Workspace API ────────────────────────────────────────────────

export async function listWorkspaces(): Promise<WorkspaceWithPermDto[]> {
  return request('/api/workspaces', { method: 'GET' });
}

export async function getWorkspace(id: string): Promise<WorkspaceWithPermDto> {
  return request(`/api/workspaces/${encodeURIComponent(id)}`, { method: 'GET' });
}

export async function createWorkspace(req: CreateWorkspaceRequest): Promise<WorkspaceWithPermDto> {
  return request('/api/workspaces', { method: 'POST', data: req });
}

export async function updateWorkspace(
  id: string,
  req: UpdateWorkspaceRequest,
): Promise<WorkspaceWithPermDto> {
  return request(`/api/workspaces/${encodeURIComponent(id)}`, {
    method: 'PUT',
    data: req,
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

export async function createSession(
  workspaceId: string,
  agentTemplateId: string,
  title?: string,
): Promise<SessionRecord> {
  return request('/api/sessions', {
    method: 'POST',
    data: { workspaceId, agentTemplateId, title },
  });
}

export async function deleteSession(sessionId: string): Promise<void> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' });
}

export async function renameSession(sessionId: string, title: string): Promise<SessionRecord> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/title`, {
    method: 'PUT',
    data: { title },
  });
}

export async function archiveSession(sessionId: string): Promise<SessionRecord> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/archive`, {
    method: 'POST',
  });
}

// ─── Chat Message (history) API ────────────────────────────────

export interface ChatMessageDto {
  id: number;
  role: 'user' | 'agent';
  content: string;
  thinking?: ThinkingChunkDto[];
  usage?: TokenUsageDto;
  createdAt: number;
}

export interface ThinkingChunkDto {
  text: string;
  timestamp: number;
}

export interface MessageListResponse {
  items: ChatMessageDto[];
  hasMore: boolean;
  oldestCreatedAt: number | null;
}

export async function listSessionMessages(
  sessionId: string,
  before?: number,
  limit?: number,
): Promise<MessageListResponse> {
  const params = new URLSearchParams();
  if (before) params.set('before', String(before));
  if (limit) params.set('limit', String(limit));
  const qs = params.toString();
  return request(
    `/api/sessions/${encodeURIComponent(sessionId)}/messages${qs ? `?${qs}` : ''}`,
    { method: 'GET' },
  );
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
  maxOutputTokens: number;
  inputPricePer1MTokens: number;
  outputPricePer1MTokens: number;
  cacheHitPricePer1MTokens: number;
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
  maxOutputTokens: number;
  inputPricePer1MTokens: number;
  outputPricePer1MTokens: number;
  cacheHitPricePer1MTokens?: number;
  capabilityTags?: string[];
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export interface CapabilityDto {
  id: number;
  capabilityId: string;
  name: string;
  description?: string;
  toolName: string;
  toolDescription?: string;
  toolParametersJson?: string;
  requiresShellExecution: boolean;
  requiresFileWrite: boolean;
  requiresNetworkAccess: boolean;
  isEnabled: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertCapabilityRequest {
  capabilityId: string;
  name: string;
  description?: string;
  toolName: string;
  toolDescription?: string;
  toolParametersJson?: string;
  requiresShellExecution: boolean;
  requiresFileWrite: boolean;
  requiresNetworkAccess: boolean;
  isEnabled: boolean;
  sortOrder: number;
}

export interface UpdateQuotaRequest {
  dailyTokenLimit?: number;
  monthlyTokenLimit?: number;
}

// ─── Skill Package Types ──────────────────────────────────────────

export interface SkillPackageDto {
  id: number;
  skillPackageId: string;
  name: string;
  description?: string;
  version: string;
  fileName: string;
  fileSizeBytes: number;
  contentType: string;
  isEnabled: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}

export interface UpdateSkillPackageRequest {
  name: string;
  description?: string;
  isEnabled: boolean;
  sortOrder: number;
}

// ─── KeyVault Types ──────────────────────────────────────────────

export interface KeyVaultSecretDto {
  id: number;
  keyVaultId: string;
  name: string;
  description?: string;
  category: string;
  tags: string[];
  createdAt: string;
  updatedAt?: string;
}

export interface KeyVaultSecretDetailDto extends KeyVaultSecretDto {
  value?: string;
}

export interface CreateKeyVaultSecretRequest {
  name: string;
  value: string;
  description?: string;
  category: string;
  tags?: string[];
}

export interface UpdateKeyVaultSecretRequest {
  name: string;
  value?: string;
  description?: string;
  category: string;
  tags?: string[];
}

export interface KeyVaultTextTransformRequest {
  text: string;
}

export interface KeyVaultTextTransformResponse {
  text: string;
}

// ─── Agent Avatar Types (ADR-034) ──────────────────────────────

export interface AgentAvatarDto {
  avatarId: string;
  name: string;
  url: string;
  personality?: string;
  hairColor?: string;
  expression?: string;
  visualTraits: string[];
  recommendedUse?: string;
  isBuiltIn: boolean;
  isEnabled: boolean;
  sortOrder: number;
}

export async function listAgentAvatars(enabledOnly = true): Promise<AgentAvatarDto[]> {
  return request('/api/agent-avatars', { method: 'GET', params: { enabledOnly } });
}

// ─── Global Agent Template Types ────────────────────────────────

export interface GlobalAgentTemplateDto {
  id: number;
  templateId: string;
  name: string;
  description?: string;
  avatarEmoji?: string;
  avatarId?: string;
  avatarUrl?: string;
  avatarName?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  personaPrompt?: string;
  toolsDescription?: string;
  bootstrapTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  memoryLlmProviderId?: string;
  memoryLlmModelId?: string;
  memorySearchMode?: string;
  reasoningEffort?: string;
  consciousProfileId?: string;
  subconsciousProfileId?: string;
  maxRounds?: number;
  maxElapsedSeconds?: number;
  maxToolCallsTotal?: number;
  maxContextTokens: number;
  maxReplyTokens: number;
  containerImage?: string;
  selectedCapabilityIds: string[];
  selectedSkillPackageIds: string[];
  agentsPrompt?: string;
  memoryPrompt?: string;
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
  avatarEmoji?: string;
  avatarId?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  personaPrompt?: string;
  toolsDescription?: string;
  bootstrapTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  memoryLlmProviderId?: string;
  memoryLlmModelId?: string;
  memorySearchMode?: string;
  reasoningEffort?: string;
  consciousProfileId?: string;
  subconsciousProfileId?: string;
  maxRounds?: number;
  maxElapsedSeconds?: number;
  maxToolCallsTotal?: number;
  maxContextTokens: number;
  maxReplyTokens: number;
  containerImage?: string;
  selectedCapabilityIds?: string[];
  selectedSkillPackageIds?: string[];
  agentsPrompt?: string;
  memoryPrompt?: string;
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
  avatarEmoji?: string;
  avatarId?: string;
  avatarUrl?: string;
  avatarName?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  personaPrompt?: string;
  toolsDescription?: string;
  bootstrapTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  memoryLlmProviderId?: string;
  memoryLlmModelId?: string;
  memorySearchMode?: string;
  reasoningEffort?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  containerImage?: string;
  baseGlobalTemplateId?: string;
  selectedCapabilityIds: string[];
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
  avatarEmoji?: string;
  avatarId?: string;
  role: string;
  systemPrompt?: string;
  userPromptTemplate?: string;
  personaPrompt?: string;
  toolsDescription?: string;
  bootstrapTemplate?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  memoryLlmProviderId?: string;
  memoryLlmModelId?: string;
  memorySearchMode?: string;
  reasoningEffort?: string;
  maxContextTokens: number;
  maxReplyTokens: number;
  containerImage?: string;
  baseGlobalTemplateId?: string;
  selectedCapabilityIds?: string[];
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

// ─── Skill Package API ────────────────────────────────────────────

export async function listSkillPackages(enabledOnly?: boolean): Promise<SkillPackageDto[]> {
  const url = enabledOnly ? '/api/skill-packages?enabledOnly=true' : '/api/skill-packages';
  return request(url, { method: 'GET' });
}

export async function getSkillPackage(skillPackageId: string): Promise<SkillPackageDto> {
  return request(`/api/skill-packages/${encodeURIComponent(skillPackageId)}`, { method: 'GET' });
}

export async function createSkillPackage(formData: FormData): Promise<SkillPackageDto> {
  return request('/api/skill-packages', { method: 'POST', data: formData, requestType: 'form' });
}

export async function updateSkillPackage(
  skillPackageId: string,
  req: UpdateSkillPackageRequest,
): Promise<SkillPackageDto> {
  return request(`/api/skill-packages/${encodeURIComponent(skillPackageId)}`, {
    method: 'PUT',
    data: req,
  });
}

export async function updateSkillPackageFile(
  skillPackageId: string,
  formData: FormData,
): Promise<SkillPackageDto> {
  return request(`/api/skill-packages/${encodeURIComponent(skillPackageId)}/file`, {
    method: 'PUT',
    data: formData,
    requestType: 'form',
  });
}

export async function deleteSkillPackage(skillPackageId: string): Promise<void> {
  return request(`/api/skill-packages/${encodeURIComponent(skillPackageId)}`, { method: 'DELETE' });
}

export async function getSkillPackageDownloadUrl(skillPackageId: string): Promise<{ url: string }> {
  return request(`/api/skill-packages/${encodeURIComponent(skillPackageId)}/download-url`, { method: 'GET' });
}

// ─── KeyVault API ────────────────────────────────────────────────

export async function listKeyVaultSecrets(): Promise<KeyVaultSecretDto[]> {
  return request('/api/keyvault/secrets', { method: 'GET' });
}

export async function createKeyVaultSecret(
  req: CreateKeyVaultSecretRequest,
): Promise<KeyVaultSecretDto> {
  return request('/api/keyvault/secrets', { method: 'POST', data: req });
}

export async function updateKeyVaultSecret(
  keyVaultId: string,
  req: UpdateKeyVaultSecretRequest,
): Promise<KeyVaultSecretDto> {
  return request(`/api/keyvault/secrets/${encodeURIComponent(keyVaultId)}`, {
    method: 'PUT',
    data: req,
  });
}

export async function getKeyVaultSecret(
  keyVaultId: string,
  confirm = true,
): Promise<KeyVaultSecretDetailDto> {
  const qs = `?confirm=${confirm ? 'true' : 'false'}`;
  return request(`/api/keyvault/secrets/${encodeURIComponent(keyVaultId)}${qs}`, { method: 'GET' });
}

export async function deleteKeyVaultSecret(keyVaultId: string): Promise<void> {
  return request(`/api/keyvault/secrets/${encodeURIComponent(keyVaultId)}`, { method: 'DELETE' });
}

export async function injectKeyVaultText(
  req: KeyVaultTextTransformRequest,
): Promise<KeyVaultTextTransformResponse> {
  return request('/api/keyvault/inject', { method: 'POST', data: req });
}

export async function stripKeyVaultText(
  req: KeyVaultTextTransformRequest,
): Promise<KeyVaultTextTransformResponse> {
  return request('/api/keyvault/strip', { method: 'POST', data: req });
}

// ─── Capability API ─────────────────────────────────────────────

export async function listCapabilities(enabledOnly?: boolean): Promise<CapabilityDto[]> {
  const url = enabledOnly ? '/api/capabilities?enabledOnly=true' : '/api/capabilities';
  return request(url, { method: 'GET' });
}

export async function getCapability(capabilityId: string): Promise<CapabilityDto> {
  return request(`/api/capabilities/${encodeURIComponent(capabilityId)}`, { method: 'GET' });
}

export async function createCapability(req: UpsertCapabilityRequest): Promise<CapabilityDto> {
  return request('/api/capabilities', { method: 'POST', data: req });
}

export async function updateCapability(
  capabilityId: string,
  req: UpsertCapabilityRequest,
): Promise<CapabilityDto> {
  return request(`/api/capabilities/${encodeURIComponent(capabilityId)}`, {
    method: 'PUT',
    data: req,
  });
}

export async function deleteCapability(capabilityId: string): Promise<void> {
  return request(`/api/capabilities/${encodeURIComponent(capabilityId)}`, { method: 'DELETE' });
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
  userProfile?: string;
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
  userProfile?: string;
  teamAccessPolicy: WorkspaceAccessPolicy;
  companyAccessPolicy: WorkspaceAccessPolicy;
}

export interface UpdateWorkspaceRequest {
  name: string;
  description?: string;
  userProfile?: string;
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

// ─── Workspace-scoped Member API (via /api/workspaces) ──────────
// Used by the workspace detail page; routes are on WorkspaceApiController.

export async function listWorkspaceMembersDirect(workspaceId: string): Promise<WorkspaceMemberDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/members`, { method: 'GET' });
}

export async function addWorkspaceMemberDirect(
  workspaceId: string,
  req: AddWorkspaceMemberRequest,
): Promise<WorkspaceMemberDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/members`, {
    method: 'POST',
    data: req,
  });
}

export async function removeWorkspaceMemberDirect(workspaceId: string, memberId: number): Promise<void> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/members/${memberId}`,
    { method: 'DELETE' },
  );
}

// ─── WorkspaceAgent types ─────────────────────────────────────

export interface WorkspaceAgentDto {
  agentId: string;
  name: string;
  description?: string;
  displayName?: string;
  avatarId?: string;
  avatarEmoji?: string;
  avatarUrl?: string;
  sourceTemplateId?: string;
  systemPromptOverride?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  isEnabled: boolean;
  isFrozen: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateWorkspaceAgentRequest {
  name: string;
  description?: string;
  displayName?: string;
  avatarId?: string;
  avatarEmoji?: string;
  avatarUrl?: string;
  sourceTemplateId?: string;
  systemPromptOverride?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
}

export interface UpdateWorkspaceAgentRequest {
  name: string;
  description?: string;
  displayName?: string;
  avatarId?: string;
  avatarEmoji?: string;
  avatarUrl?: string;
  sourceTemplateId?: string;
  systemPromptOverride?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
  isEnabled: boolean;
}

// ─── WorkspaceAgent API ───────────────────────────────────────

export async function listWorkspaceAgents(workspaceId: string): Promise<WorkspaceAgentDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents`, { method: 'GET' });
}

export async function createWorkspaceAgent(
  workspaceId: string, req: CreateWorkspaceAgentRequest,
): Promise<WorkspaceAgentDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents`, { method: 'POST', data: req });
}

export async function updateWorkspaceAgent(
  workspaceId: string, agentId: string, req: UpdateWorkspaceAgentRequest,
): Promise<WorkspaceAgentDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}`, {
    method: 'PUT', data: req,
  });
}

export async function freezeWorkspaceAgent(workspaceId: string, agentId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/freeze`, { method: 'POST' });
}

export async function unfreezeWorkspaceAgent(workspaceId: string, agentId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/unfreeze`, { method: 'POST' });
}

export async function deleteWorkspaceAgent(workspaceId: string, agentId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}`, { method: 'DELETE' });
}

// ─── Workflow types ───────────────────────────────────────────

export interface WorkflowDto {
  workflowId: string;
  name: string;
  description?: string;
  definitionJson?: string;
  status: string;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertWorkflowRequest {
  name: string;
  description?: string;
  definitionJson?: string;
  status: string;
  isEnabled: boolean;
}

// ─── Workflow API ─────────────────────────────────────────────

export async function listWorkflows(workspaceId: string): Promise<WorkflowDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/workflows`, { method: 'GET' });
}

export async function createWorkflow(workspaceId: string, req: UpsertWorkflowRequest): Promise<WorkflowDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/workflows`, { method: 'POST', data: req });
}

export async function updateWorkflow(workspaceId: string, workflowId: string, req: UpsertWorkflowRequest): Promise<WorkflowDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/workflows/${encodeURIComponent(workflowId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteWorkflow(workspaceId: string, workflowId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/workflows/${encodeURIComponent(workflowId)}`, { method: 'DELETE' });
}

// ─── KnowledgeBase types ──────────────────────────────────────

export interface KnowledgeBaseDto {
  kbId: string;
  name: string;
  description?: string;
  kbType: string;
  documentCount: number;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertKnowledgeBaseRequest {
  name: string;
  description?: string;
  kbType: string;
  isEnabled: boolean;
}

// ─── KnowledgeBase API ────────────────────────────────────────

export async function listKnowledgeBases(workspaceId: string): Promise<KnowledgeBaseDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/knowledge-bases`, { method: 'GET' });
}

export async function createKnowledgeBase(workspaceId: string, req: UpsertKnowledgeBaseRequest): Promise<KnowledgeBaseDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/knowledge-bases`, { method: 'POST', data: req });
}

export async function updateKnowledgeBase(workspaceId: string, kbId: string, req: UpsertKnowledgeBaseRequest): Promise<KnowledgeBaseDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/knowledge-bases/${encodeURIComponent(kbId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteKnowledgeBase(workspaceId: string, kbId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/knowledge-bases/${encodeURIComponent(kbId)}`, { method: 'DELETE' });
}

// ─── WorkspaceSkill types ─────────────────────────────────────

export interface WorkspaceSkillDto {
  skillId: string;
  name: string;
  description?: string;
  skillType: string;
  configJson?: string;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertWorkspaceSkillRequest {
  name: string;
  description?: string;
  skillType: string;
  configJson?: string;
  isEnabled: boolean;
}

// ─── WorkspaceSkill API ───────────────────────────────────────

export async function listWorkspaceSkills(workspaceId: string): Promise<WorkspaceSkillDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/skills`, { method: 'GET' });
}

export async function createWorkspaceSkill(workspaceId: string, req: UpsertWorkspaceSkillRequest): Promise<WorkspaceSkillDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/skills`, { method: 'POST', data: req });
}

export async function updateWorkspaceSkill(workspaceId: string, skillId: string, req: UpsertWorkspaceSkillRequest): Promise<WorkspaceSkillDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/skills/${encodeURIComponent(skillId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteWorkspaceSkill(workspaceId: string, skillId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/skills/${encodeURIComponent(skillId)}`, { method: 'DELETE' });
}

// ─── WorkspaceChannel types ───────────────────────────────────

export interface WorkspaceChannelDto {
  channelId: string;
  name: string;
  description?: string;
  channelType: string;
  defaultAgentId?: string;
  configJson?: string;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface UpsertWorkspaceChannelRequest {
  name: string;
  description?: string;
  channelType: string;
  defaultAgentId?: string;
  configJson?: string;
  isEnabled: boolean;
}

// ─── WorkspaceChannel API ─────────────────────────────────────

export async function listWorkspaceChannels(workspaceId: string): Promise<WorkspaceChannelDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/channels`, { method: 'GET' });
}

export async function createWorkspaceChannel(workspaceId: string, req: UpsertWorkspaceChannelRequest): Promise<WorkspaceChannelDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/channels`, { method: 'POST', data: req });
}

export async function updateWorkspaceChannel(workspaceId: string, channelId: string, req: UpsertWorkspaceChannelRequest): Promise<WorkspaceChannelDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/channels/${encodeURIComponent(channelId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteWorkspaceChannel(workspaceId: string, channelId: string): Promise<void> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/channels/${encodeURIComponent(channelId)}`, { method: 'DELETE' });
}

// ─── Sub-Agent API (ADR-016) ──────────────────────────────────────

export interface SubAgentStatusDto {
  subSessionId: string;
  status: string;       // 'running' | 'completed' | 'failed'
  templateId?: string;
  modelId?: string;
  taskSummary: string;
  spawnedAt: string;
  completedAt?: string;
  resultSummary?: string;
  success?: boolean;
}

export interface SubAgentStatsDto {
  total: number;
  running: number;
  completed: number;
  failed: number;
  lastCompletedId?: string;
  lastFailedId?: string;
}

/** 获取会话的所有子代理状态 */
export async function getSessionSubAgents(sessionId: string): Promise<SubAgentStatusDto[]> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/sub-agents`, { method: 'GET' });
}

/** SSE 订阅会话事件流（含 subagent.spawned / subagent.completed） */
export function subscribeSessionEvents(
  sessionId: string,
  onEvent: (ev: AdminChatStreamEvent) => void,
  signal?: AbortSignal,
): void {
  const url = `/api/sessions/${encodeURIComponent(sessionId)}/events/stream`;
  const token = localStorage.getItem('pudding_token');
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;

  fetch(url, { headers, signal })
    .then(async (resp) => {
      if (!resp.ok || !resp.body) return;
      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split('\n');
        buf = lines.pop() || '';
        let eventType = '';
        for (const line of lines) {
          if (line.startsWith('event: ')) eventType = line.slice(7).trim();
          else if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              onEvent({ type: eventType as any, ...data });
            } catch { /* skip malformed */ }
          }
        }
      }
    })
    .catch(() => { /* connection closed */ });
}

// ─── Workspace Notifications SSE ─────────────────────────────

/** 工作区通知事件（页面级，独立于会话 SSE） */
export interface WorkspaceNotification {
  type: string;           // 'notification.sub_agent_completed' 等
  sessionId: string;
  workspaceId: string;
  agentId?: string;
  sessionTitle?: string;
  data?: Record<string, unknown>;
  timestamp: string;
}

/** SSE 订阅工作区通知流 */
export function subscribeWorkspaceNotifications(
  workspaceId: string,
  onNotification: (event: WorkspaceNotification) => void,
  signal?: AbortSignal,
): void {
  const url = `/api/workspaces/${encodeURIComponent(workspaceId)}/notifications/stream`;
  const token = localStorage.getItem('pudding_token');
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;

  fetch(url, { headers, signal })
    .then(async (resp) => {
      if (!resp.ok || !resp.body) return;
      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split('\n');
        buf = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              onNotification(data as WorkspaceNotification);
            } catch { /* skip malformed */ }
          }
        }
      }
    })
    .catch(() => { /* connection closed */ });
}

// ─── Chat types ───────────────────────────────────────────────

export interface AdminChatRequest {
  messageText: string;
  sessionId?: string;
  agentId?: string;
  forceNewSession?: boolean;
  reasoningEffort?: string;
}

export interface TokenUsageDto {
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
  contextWindowTokens?: number;
  promptCacheHitTokens?: number;
  promptCacheMissTokens?: number;
}

export type ContextHealthState = 'Healthy' | 'Warning' | 'Unhealthy' | 'Critical' | 'Blocking';
export type ContextCompactionLevel = 'Micro' | 'SessionMemory' | 'Full';
export type ContextCompactionMode = 'Manual' | 'Auto';

export interface ContextHealthSnapshot {
  sessionId: string;
  usedTokens: number;
  contextWindowTokens: number;
  effectiveWindowTokens: number;
  remainingTokens: number;
  usageRatio: number;
  state: ContextHealthState;
  shouldSuggestCompact: boolean;
  shouldAutoCompact: boolean;
  shouldBlockSend: boolean;
}

export interface CompactSessionRequest {
  workspaceId?: string;
  agentId?: string;
  level?: ContextCompactionLevel;
  reason?: string;
}

export interface ContextCompactionResult {
  sessionId: string;
  summaryMessageId: string;
  mode: ContextCompactionMode;
  level: ContextCompactionLevel;
  beforeTokens: number;
  afterTokens: number;
  compactedMessageCount: number;
  summaryPreview: string;
}

/** 单轮工具调用步骤摘要（对应后端 TurnStepDto）。 */
export interface TurnStep {
  round: number;
  /** CONTINUE / DONE / WAIT / FAILED / CANCELLED */
  status: string;
  messageSummary?: string;
  toolName?: string;
  toolArgs?: string;
  toolSuccess?: boolean;
  toolError?: string;
  durationMs?: number;
}

export interface AdminChatResponse {
  messageId: string;
  sessionId: string;
  reply?: string;
  isSuccess: boolean;
  errorMessage?: string;
  usage?: TokenUsageDto;
  /** 本次执行产生的逐轮步骤（包含工具调用记录）。 */
  turnSteps?: TurnStep[];
}

export type AdminChatStreamEvent =
  | { type: 'metadata'; messageId: string; sessionId: string; routeDecisionId?: string; source_type?: string; source_id?: string; source_name?: string }
  | { type: 'delta'; delta: string }
  | { type: 'thinking'; delta: string }
  | { type: 'tool_call'; name: string; arguments: string }
  | { type: 'tool_result'; name: string; exitCode: number; output: string; error?: string }
  | { type: 'step'; status?: string; message?: string; [key: string]: unknown }
  | { type: 'usage'; usage: TokenUsageDto }
  | { type: 'context.health'; state?: ContextHealthState; usedTokens?: number; effectiveWindowTokens?: number; usageRatio?: number; [key: string]: unknown }
  | { type: 'context.compaction.started'; sessionId?: string; mode?: ContextCompactionMode; level?: ContextCompactionLevel; reason?: string; [key: string]: unknown }
  | { type: 'context.compaction.completed'; sessionId?: string; compactedMessageCount?: number; beforeTokens?: number; afterTokens?: number; summaryMessageId?: string; [key: string]: unknown }
  | { type: 'context.compaction.failed'; sessionId?: string; error?: string; [key: string]: unknown }
  | { type: 'done'; reply?: string; usage?: TokenUsageDto; traceId?: string; sessionId?: string }
  | { type: 'error'; message: string }
  | { type: 'cancelled'; message?: string }
  | { type: 'subconscious_step'; status: 'loading' | 'thinking' | 'done'; message: string; [key: string]: unknown }
  // T-102: 子代理事件（ADR-016）
  | { type: 'subagent.spawned'; sub_agent_id: string; template?: string; model?: string; task?: string; [key: string]: unknown }
  | { type: 'subagent.delta'; sub_agent_id?: string; delta?: string; data?: string; [key: string]: unknown }
  | { type: 'subagent.thinking'; sub_agent_id?: string; delta?: string; [key: string]: unknown }
  | { type: 'subagent.tool_call'; sub_agent_id?: string; name?: string; arguments?: string; [key: string]: unknown }
  | { type: 'subagent.tool_result'; sub_agent_id?: string; name?: string; exitCode?: number; output?: string; error?: string; [key: string]: unknown }
  | { type: 'subagent.completed'; sub_agent_id: string; success?: boolean; reply?: string; error?: string; result_summary?: string; [key: string]: unknown }
  // T-102: 会话关闭事件（ADR-016）
  | { type: 'session.closed'; sessionId: string };

// ─── Chat API ─────────────────────────────────────────────────

export async function sendAdminChatMessage(
  workspaceId: string, req: AdminChatRequest,
): Promise<AdminChatResponse> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/chat/message`, {
    method: 'POST', data: req,
  });
}

/** T-102: 非流式消息提交 — POST 返回 { messageId, sessionId }，帧通过持久 SSE 接收 */
export async function sendChatMessage(
  workspaceId: string,
  req: AdminChatRequest,
  signal?: AbortSignal,
): Promise<{ messageId: string; sessionId: string }> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/chat/message`, {
    method: 'POST',
    data: req,
    signal,
  }) as Promise<{ messageId: string; sessionId: string }>;
}

export async function getContextHealth(sessionId: string): Promise<ContextHealthSnapshot> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/context-health`, {
    method: 'GET',
  });
}

export async function compactSession(
  sessionId: string,
  req: CompactSessionRequest,
): Promise<ContextCompactionResult> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/compact`, {
    method: 'POST',
    data: req,
  });
}


// ─── P2P Peer Discovery API ────────────────────────────────────

export interface PeerNodeDto {
  nodeId: string;
  displayName: string;
  host: string;
  port: number;
  lastSeen: string;
}

export async function listP2pPeers(): Promise<PeerNodeDto[]> {
  return request('/api/p2p/peers', { method: 'GET' });
}

// ─── Runtime 节点管理类型（对齐 PuddingCore.Platform.RuntimeModels）──

export type RuntimeNodeStatus = 'Online' | 'Offline' | 'Degraded';

export type NativeCapabilityCategory =
  | 'QueryState'
  | 'RunTest'
  | 'ReadResult'
  | 'ExecuteCommand'
  | 'Custom';

export interface NativeCapabilityDescriptor {
  capabilityId: string;
  name: string;
  description?: string;
  category: NativeCapabilityCategory;
  requiresApproval: boolean;
}

export interface RuntimeNodeInfo {
  nodeId: string;
  endpoint: string;
  status: RuntimeNodeStatus;
  lastHeartbeat: string;
  activeSessionCount: number;
  embeddedMode: boolean;
  hostType?: string;
  nativeCapabilities: NativeCapabilityDescriptor[];
  isFrozen: boolean;
}

// ─── Runtime Registry API（优先 /api，兼容旧部署 /ingress）───

async function requestRuntimeRegistry<T>(path: string, options: { method: string; data?: unknown }): Promise<T> {
  try {
    return await request(`/api/runtime-registry${path}`, options);
  } catch {
    // 兼容旧部署：某些网关通过 /ingress 暴露 RuntimeRegistry。
    return request(`/ingress/runtime-registry${path}`, options);
  }
}

/** 列出所有已注册的 Runtime 节点（含离线节点）。 */
export async function listRuntimeNodes(): Promise<RuntimeNodeInfo[]> {
  return requestRuntimeRegistry<RuntimeNodeInfo[]>('/nodes', { method: 'GET' });
}

/** 列出所有嵌入式宿主节点。 */
export async function listEmbeddedRuntimeNodes(): Promise<RuntimeNodeInfo[]> {
  return requestRuntimeRegistry<RuntimeNodeInfo[]>('/embedded', { method: 'GET' });
}

/** 获取指定节点的原生能力列表。 */
export async function getRuntimeNodeCapabilities(
  nodeId: string,
): Promise<NativeCapabilityDescriptor[]> {
  return requestRuntimeRegistry<NativeCapabilityDescriptor[]>(`/${encodeURIComponent(nodeId)}/capabilities`, {
    method: 'GET',
  });
}

/** 冻结指定 Runtime 节点（嵌入式专用）。 */
export async function freezeRuntimeNode(nodeId: string, reason: string): Promise<void> {
  return requestRuntimeRegistry<void>(`/${encodeURIComponent(nodeId)}/freeze`, {
    method: 'POST',
    data: { reason, operatorId: 'admin' },
  });
}

/** 解除指定 Runtime 节点冻结。 */
export async function unfreezeRuntimeNode(nodeId: string): Promise<void> {
  return requestRuntimeRegistry<void>(`/${encodeURIComponent(nodeId)}/unfreeze`, {
    method: 'POST',
    data: { reason: 'admin unfreeze', operatorId: 'admin' },
  });
}

// ─── Token Stats API (ADR-018) ───────────────────────────────────

export interface MonthlyTokenStatsResponse {
  yearMonth: string;
  totalPromptTokens: number;
  totalCompletionTokens: number;
  totalCacheHitTokens: number;
  totalCacheMissTokens: number;
  cacheHitRate: number;
  totalCost: number;
  totalRequests: number;
  byProvider: MonthlyProviderStats[];
}

export interface MonthlyProviderStats {
  providerId: string;
  promptTokens: number;
  completionTokens: number;
  cacheHitTokens: number;
  cacheMissTokens: number;
  cacheHitRate: number;
  totalCost: number;
  requestCount: number;
  models: MonthlyModelStats[];
}

export interface MonthlyModelStats {
  modelId: string;
  promptTokens: number;
  completionTokens: number;
  cacheHitTokens: number;
  cacheMissTokens: number;
  cacheHitRate: number;
  totalCost: number;
  requestCount: number;
}

/** 获取月度 Token 统计（ADR-018 StatsApiController） */
export async function getMonthlyTokenStats(
  yearMonth: string,
  providerId?: string,
  modelId?: string,
): Promise<MonthlyTokenStatsResponse> {
  const params = new URLSearchParams({ yearMonth });
  if (providerId) params.set('providerId', providerId);
  if (modelId) params.set('modelId', modelId);
  return request(`/api/stats/tokens/monthly?${params.toString()}`, { method: 'GET' });
}

/** 获取会话 Token 使用明细（ADR-018 MessageApiController token-stats） */
export async function getSessionTokenStats(
  sessionId: string,
): Promise<{ sessionId: string; messages: any[]; aggregates: any }> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/token-stats`, { method: 'GET' });
}

// ─── Token Events API (ADR-043 缓存统计闭环) ─────────────────────

export interface TokenUsageEventResponse {
  id: number;
  sourceType: string;
  sourceId: string;
  workspaceId?: string;
  sessionId?: string;
  providerId?: string;
  modelId?: string;
  occurredAtUtc: string;
  yearMonth: string;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  cacheHitTokens: number;
  cacheMissTokens: number;
  cacheEligibleTokens: number;
  cacheHitRate?: number;
  inputCost: number;
  outputCost: number;
  cacheHitCost: number;
  totalCost: number;
}

export interface TokenEventsPageResponse {
  total: number;
  page: number;
  pageSize: number;
  events: TokenUsageEventResponse[];
}

/** 查询 Token 使用事件明细（ADR-043） */
export async function getTokenEvents(
  params: {
    from?: string;
    to?: string;
    providerId?: string;
    modelId?: string;
    sessionId?: string;
    page?: number;
    pageSize?: number;
  },
): Promise<TokenEventsPageResponse> {
  const searchParams = new URLSearchParams();
  if (params.from) searchParams.set('from', params.from);
  if (params.to) searchParams.set('to', params.to);
  if (params.providerId) searchParams.set('providerId', params.providerId);
  if (params.modelId) searchParams.set('modelId', params.modelId);
  if (params.sessionId) searchParams.set('sessionId', params.sessionId);
  if (params.page) searchParams.set('page', String(params.page));
  if (params.pageSize) searchParams.set('pageSize', String(params.pageSize));
  return request(`/api/stats/tokens/events?${searchParams.toString()}`, { method: 'GET' });
}

/** 触发 Token 统计重建（仅管理员，ADR-043） */
export async function rebuildTokenEvents(
  yearMonth?: string,
): Promise<{ eventsCreated: number; messagesScanned: number; skippedDuplicates: number; errors: number; errorDetails: string[] }> {
  const params = yearMonth ? `?yearMonth=${yearMonth}` : '';
  return request(`/api/stats/tokens/rebuild${params}`, { method: 'POST' });
}

// ─── Memory Library Admin API (ADR-030) ──────────────────────────

export async function getMemoryLibraryOverview(workspaceId: string): Promise<{
  workspaceId: string;
  libraryCount: number;
  bookCount: number;
  treeNodeCount: number;
}> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/overview`);
}

export async function listMemoryLibraries(workspaceId: string): Promise<
  { libraryId: string; workspaceId: string; agentId?: string; name: string; description?: string; createdAt: number; updatedAt: number }[]
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/libraries`);
}

export async function listAgentMemoryLibraries(workspaceId: string, agentId: string): Promise<
  { libraryId: string; workspaceId: string; agentId?: string; name: string; description?: string; createdAt: number; updatedAt: number }[]
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/libraries`);
}

export async function ensureAgentDefaultMemoryLibrary(workspaceId: string, agentId: string): Promise<
  { libraryId: string; workspaceId: string; agentId?: string; name: string; description?: string; createdAt: number; updatedAt: number }
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/libraries/default`, {
    method: 'POST',
  });
}

export async function getMemoryLibraryTree(
  workspaceId: string,
  libraryId: string,
): Promise<
  { id: string; parentId: string | null; type: string; title: string; summary?: string; status: string; bookId?: string; children: any[] }[]
> {
  return request(`/api/admin/memory-library/libraries/${encodeURIComponent(libraryId)}/tree`, {
    params: { workspaceId },
  });
}

export async function getAgentMemoryLibraryTree(
  workspaceId: string,
  agentId: string,
  libraryId: string,
): Promise<
  { id: string; parentId: string | null; type: string; title: string; summary?: string; status: string; bookId?: string; children: any[] }[]
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/libraries/${encodeURIComponent(libraryId)}/tree`);
}

export async function getMemoryBookPage(
  workspaceId: string,
  bookId: string,
): Promise<{
  workspaceId: string;
  libraryId: string;
  bookId: string;
  title: string;
  summary?: string;
  status: string;
  chapters: { chapterId: string; bookId: string; title: string; content: string; contentType: string; importance: number; createdAt: number; updatedAt: number }[];
}> {
  return request(`/api/admin/memory-library/books/${encodeURIComponent(bookId)}`, {
    params: { workspaceId },
  });
}

export async function getAgentMemoryBookPage(
  workspaceId: string,
  agentId: string,
  bookId: string,
): Promise<{
  workspaceId: string;
  libraryId: string;
  bookId: string;
  title: string;
  summary?: string;
  status: string;
  chapters: { chapterId: string; bookId: string; title: string; content: string; contentType: string; importance: number; createdAt: number; updatedAt: number }[];
}> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/books/${encodeURIComponent(bookId)}`);
}

export async function searchMemoryLibrary(
  workspaceId: string,
  query: string,
  topK = 20,
): Promise<
  { bookId: string; chapterId: string; bookTitle: string; snippet: string; score: number }[]
> {
  return request('/api/admin/memory-library/search', {
    params: { workspaceId, query, topK },
  });
}

export async function searchAgentMemoryLibrary(
  workspaceId: string,
  agentId: string,
  query: string,
  topK = 20,
): Promise<
  { bookId: string; chapterId: string; bookTitle: string; snippet: string; score: number }[]
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/search`, {
    params: { query, topK },
  });
}

// ─── Memory Library Write API ──────────────────────────────────

export async function createMemoryTreeNode(req: {
  workspaceId: string;
  libraryId: string;
  parentNodeId?: string;
  name: string;
  summary?: string;
  nodeType: string;
}): Promise<any> {
  return request('/api/admin/memory-library/tree-nodes', { method: 'POST', data: req });
}

export async function createAgentMemoryTreeNode(workspaceId: string, agentId: string, req: {
  libraryId: string;
  parentNodeId?: string;
  name: string;
  summary?: string;
  nodeType: string;
}): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/tree-nodes`, {
    method: 'POST',
    data: { ...req, workspaceId },
  });
}

export async function createMemoryBook(req: {
  workspaceId: string;
  libraryId: string;
  nodeId?: string;
  title: string;
  summary?: string;
}): Promise<any> {
  return request('/api/admin/memory-library/books', { method: 'POST', data: req });
}

export async function createAgentMemoryBook(workspaceId: string, agentId: string, req: {
  libraryId: string;
  nodeId?: string;
  title: string;
  summary?: string;
}): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/books`, {
    method: 'POST',
    data: { ...req, workspaceId },
  });
}

export async function updateMemoryBook(
  workspaceId: string,
  bookId: string,
  req: { title: string; summary?: string },
): Promise<any> {
  return request(`/api/admin/memory-library/books/${encodeURIComponent(bookId)}`, {
    method: 'PUT', data: req, params: { workspaceId },
  });
}

export async function updateAgentMemoryBook(
  workspaceId: string,
  agentId: string,
  bookId: string,
  req: { title: string; summary?: string },
): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/books/${encodeURIComponent(bookId)}`, {
    method: 'PUT', data: req,
  });
}

export async function createMemoryChapter(req: {
  bookId: string;
  title: string;
  content: string;
  importance: number;
}): Promise<any> {
  return request('/api/admin/memory-library/chapters', { method: 'POST', data: req });
}

export async function createAgentMemoryChapter(workspaceId: string, agentId: string, req: {
  bookId: string;
  title: string;
  content: string;
  importance: number;
}): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/chapters`, {
    method: 'POST', data: req,
  });
}

export async function updateMemoryChapter(
  workspaceId: string,
  chapterId: string,
  req: { title: string; content: string; importance: number },
): Promise<any> {
  return request(`/api/admin/memory-library/chapters/${encodeURIComponent(chapterId)}`, {
    method: 'PUT', data: req, params: { workspaceId },
  });
}

export async function updateAgentMemoryChapter(
  workspaceId: string,
  agentId: string,
  chapterId: string,
  req: { title: string; content: string; importance: number },
): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/chapters/${encodeURIComponent(chapterId)}`, {
    method: 'PUT', data: req,
  });
}

export async function archiveMemoryBook(workspaceId: string, bookId: string): Promise<any> {
  return request(`/api/admin/memory-library/books/${encodeURIComponent(bookId)}/archive`, {
    method: 'POST', params: { workspaceId },
  });
}

export async function archiveAgentMemoryBook(workspaceId: string, agentId: string, bookId: string): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/books/${encodeURIComponent(bookId)}/archive`, {
    method: 'POST',
  });
}

export async function archiveMemoryChapter(workspaceId: string, chapterId: string): Promise<any> {
  return request(`/api/admin/memory-library/chapters/${encodeURIComponent(chapterId)}/archive`, {
    method: 'POST', params: { workspaceId },
  });
}

export async function archiveAgentMemoryChapter(workspaceId: string, agentId: string, chapterId: string): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/chapters/${encodeURIComponent(chapterId)}/archive`, {
    method: 'POST',
  });
}

// ─── Memory Library Sources & Pointers ──────────────────────────

export async function listMemorySources(
  ownerType: string,
  ownerId: string,
): Promise<{ sourceReferenceId: string; ownerType: string; ownerId: string; targetType: string; targetId: string; targetRange?: string; label?: string; createdAt: number }[]> {
  return request('/api/admin/memory-library/sources', {
    params: { ownerType, ownerId },
  });
}

export async function listAgentMemorySources(
  workspaceId: string,
  agentId: string,
  ownerType: string,
  ownerId: string,
): Promise<{ sourceReferenceId: string; ownerType: string; ownerId: string; targetType: string; targetId: string; targetRange?: string; label?: string; createdAt: number }[]> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/sources`, {
    params: { ownerType, ownerId },
  });
}

export async function listMemoryPointers(
  workspaceId: string,
  sourceType: string,
  sourceId: string,
): Promise<{
  outgoing: { pointerId: string; targetType: string; targetId: string; targetLabel?: string; description?: string }[];
  backlinks: { pointerId: string; targetType: string; targetId: string; targetLabel?: string; description?: string }[];
}> {
  return request('/api/admin/memory-library/pointers', {
    params: { workspaceId, sourceType, sourceId },
  });
}

export async function listAgentMemoryPointers(
  workspaceId: string,
  agentId: string,
  sourceType: string,
  sourceId: string,
): Promise<{
  outgoing: { pointerId: string; targetType: string; targetId: string; targetLabel?: string; description?: string }[];
  backlinks: { pointerId: string; targetType: string; targetId: string; targetLabel?: string; description?: string }[];
}> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/pointers`, {
    params: { sourceType, sourceId },
  });
}

