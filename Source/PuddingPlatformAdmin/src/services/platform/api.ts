import { request } from '@umijs/max';
import { recordPerfEvent } from '@/utils/debug';

// ─── 类型定义（与 C# 模型对齐）───────────────────────────────────

export type SessionStatus = 'Active' | 'Idle' | 'Completed' | 'Failed' | 'Frozen';
export type SessionType = 'ServiceSession' | 'TaskSession' | 'AuditSession';
export type SessionRole = 'Main' | 'Task' | 'Branch' | 'Audit';
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
  sessionRole?: SessionRole;
  status: SessionStatus;
  runtimeNodeId?: string;
  agentInstanceId?: string;
  parentSessionId?: string;
  rootSessionId?: string;
  principalKind?: 'agent' | 'group';
  principalId?: string;
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
  defaultTitleBase?: string,
): Promise<SessionRecord> {
  return request('/api/sessions', {
    method: 'POST',
    data: { workspaceId, agentTemplateId, title, defaultTitleBase },
  });
}

export interface EnsureMainSessionRequest {
  workspaceId: string;
  principalKind: 'agent' | 'group';
  principalId: string;
  agentTemplateId: string;
  title?: string;
}

export async function ensureMainSession(req: EnsureMainSessionRequest): Promise<SessionRecord> {
  return request('/api/sessions/main', { method: 'POST', data: req });
}

export interface AgentGroupContactDto {
  groupId: string;
  name: string;
  description?: string;
  avatarUrl?: string;
  memberAgentIds: string[];
  mainSessionId?: string;
  status?: 'idle' | 'working' | 'waiting' | 'blocked' | 'offline';
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

// ─── Hermes Benchmark Cases ─────────────────────────────────────

export interface BenchmarkCaseSummaryDto {
  id: string;
  title: string;
  category: string;
  coverage: string[];
  difficulty: 'easy' | 'medium' | 'hard' | 'extreme';
  estimatedRounds?: string;
  seedId?: string;
  capabilityTargets: string[];
  sortOrder: number;
}

export interface BenchmarkCaseDetailDto {
  id: string;
  title: string;
  prompt: string;
}

export async function listBenchmarkCases(): Promise<BenchmarkCaseSummaryDto[]> {
  return request('/api/benchmark-cases', { method: 'GET' });
}

export async function getBenchmarkCase(caseId: string): Promise<BenchmarkCaseDetailDto> {
  return request(`/api/benchmark-cases/${encodeURIComponent(caseId)}`, { method: 'GET' });
}

export interface BenchmarkPrepareResultDto {
  runId: string;
  caseId: string;
  workspaceId: string;
  sessionId?: string;
  seed: {
    seedId?: string | null;
    files: Array<{
      path: string;
      bytes: number;
    }>;
  };
}

export async function prepareBenchmarkCase(
  caseId: string,
  workspaceId: string,
  sessionId?: string | null,
): Promise<BenchmarkPrepareResultDto> {
  return request(`/api/benchmark-cases/${encodeURIComponent(caseId)}/prepare`, {
    method: 'POST',
    data: { workspaceId, sessionId },
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
  messageId?: string | null;
  turnId?: string | null;
  commandId?: string | null;
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

// ─── Message Search API (历史消息搜索) ──────────────────────────

export interface MessageSearchMatch {
  sessionId: string;
  workspaceId: string;
  day: string;
  sequenceNum: number;
  eventType: string;
  recordedAt: string;
  snippet: string;
  evidenceRef: string;
  fullContent?: string;
  kind: 'message';
}

export interface TopicSearchMatch {
  messageId: number;
  topicTitle: string;
  sessionId: string;
  workspaceId: string;
  createdAt: string;
  kind: 'topic';
}

export interface MessageSearchResponse {
  matches: MessageSearchMatch[];
  topics: TopicSearchMatch[];
  hasMore: boolean;
}

export async function searchMessages(
  workspaceId: string,
  query: string,
  options?: { limit?: number; fromDay?: string; toDay?: string },
): Promise<MessageSearchResponse> {
  return request('/api/messages/search', {
    method: 'POST',
    data: {
      workspaceId,
      query,
      limit: options?.limit ?? 20,
      searchTopics: false,
      fromDay: options?.fromDay ?? null,
      toDay: options?.toDay ?? null,
    },
  });
}

/** 仅搜索话题标题（不搜消息内容） */
export interface TopicSearchOnlyResponse {
  topics: TopicSearchMatch[];
}

export async function searchTopicsOnly(
  workspaceId: string,
  query: string,
  limit: number = 20,
): Promise<TopicSearchOnlyResponse> {
  const params = new URLSearchParams({ workspaceId, q: query, limit: String(limit) });
  return request(`/api/topics/search?${params}`, { method: 'GET' });
}

export interface MessageByIdResponse {
  messageId: string;
  sessionId: string;
  workspaceId: string;
  role: string;
  content: string;
  createdAt: string;
  evidenceRef: string;
}

export async function getMessageById(
  sessionId: string,
  messageId: number,
): Promise<MessageByIdResponse> {
  return request(
    `/api/sessions/${encodeURIComponent(sessionId)}/messages/by-id/${messageId}`,
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
  maxConcurrentRequests?: number;
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
  isEmbedding: boolean;
  sortOrder: number;
  maxConcurrentRequests?: number;
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
  maxConcurrentRequests?: number;
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
  isEmbedding?: boolean;
  sortOrder: number;
  maxConcurrentRequests?: number;
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
  sourceKind: string;
  sourceId?: string;
  runtimeStatus: string;
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

export interface PluginToolItemDto {
  toolId: string;
  name: string;
  description: string;
  category: string;
  permissionLevel: string;
  safety: string;
  runtimeStatus: string;
  isEnabledByDefault: boolean;
  sortOrder: number;
  parameters: unknown;
}

export interface PluginCatalogItemDto {
  pluginId: string;
  name: string;
  version: string;
  status: string;
  statusReason: string;
  toolCount: number;
  tools: PluginToolItemDto[];
}

export interface PluginReloadResultDto {
  pluginId: string;
  requiresRestart: boolean;
  message: string;
}

export interface PluginCatalogReloadResultDto {
  pluginCount: number;
  message: string;
}

export interface PluginPackageInstallResultDto {
  pluginId: string;
  name: string;
  version: string;
  requiresRestart: boolean;
  message: string;
}

export interface PluginDiagnosticEventDto {
  eventId: string;
  occurredAtUtc: string;
  eventType: string;
  pluginId?: string;
  pluginVersion?: string;
  status?: string;
  message?: string;
  durationMs?: number;
  details: Record<string, string>;
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
  embeddingProviderId?: string;
  embeddingModelId?: string;
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
  embeddingProviderId?: string;
  embeddingModelId?: string;
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
  embeddingProviderId?: string;
  embeddingModelId?: string;
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
  embeddingProviderId?: string;
  embeddingModelId?: string;
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

// ─── TTS/ASR Voice Provider API ──────────────────────────────────

export interface VoiceProviderDto {
  providerId: string;
  name: string;
  endpoint: string;
  hasApiKey: boolean;
  description?: string;
  isEnabled: boolean;
  ttsModelCount: number;
  asrModelCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface VoiceProviderDetailDto {
  providerId: string;
  name: string;
  endpoint: string;
  hasApiKey: boolean;
  description?: string;
  isEnabled: boolean;
  ttsModels: TtsModelDto[];
  asrModels: AsrModelDto[];
  createdAt: string;
  updatedAt: string;
}

export interface UpsertVoiceProviderRequest {
  providerId: string;
  name: string;
  endpoint: string;
  apiKey?: string;
  description?: string;
  isEnabled: boolean;
}

export interface TtsModelDto {
  modelId: string;
  name: string;
  path?: string;
  voices: string[];
  audioFormats: string[];
  sampleRates: number[];
  supportsStreaming: boolean;
  supportsInstructions: boolean;
  supportsVoiceCloning: boolean;
  supportsVoiceDesign: boolean;
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export interface UpsertTtsModelRequest {
  modelId: string;
  name: string;
  path?: string;
  voices?: string[];
  audioFormats?: string[];
  sampleRates?: number[];
  supportsStreaming: boolean;
  supportsInstructions: boolean;
  supportsVoiceCloning: boolean;
  supportsVoiceDesign: boolean;
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export interface AsrModelDto {
  modelId: string;
  name: string;
  path?: string;
  languages: string[];
  sampleRates: number[];
  supportsEmotion: boolean;
  supportsTimestamps: boolean;
  supportsHotWords: boolean;
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export interface UpsertAsrModelRequest {
  modelId: string;
  name: string;
  path?: string;
  languages?: string[];
  sampleRates?: number[];
  supportsEmotion: boolean;
  supportsTimestamps: boolean;
  supportsHotWords: boolean;
  isDeprecated: boolean;
  isDefault: boolean;
  sortOrder: number;
}

export async function listVoiceProviders(): Promise<VoiceProviderDto[]> {
  return request('/api/voice-providers', { method: 'GET' });
}

export async function getVoiceProvider(providerId: string): Promise<VoiceProviderDetailDto> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}`, { method: 'GET' });
}

export async function createVoiceProvider(req: UpsertVoiceProviderRequest): Promise<VoiceProviderDto> {
  return request('/api/voice-providers', { method: 'POST', data: req });
}

export async function updateVoiceProvider(
  providerId: string, req: UpsertVoiceProviderRequest,
): Promise<VoiceProviderDto> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}`, {
    method: 'PUT', data: req,
  });
}

export async function deleteVoiceProvider(providerId: string): Promise<void> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}`, { method: 'DELETE' });
}

// ─── TTS Model API ───────────────────────────────────────────────

export async function listTtsModels(providerId: string): Promise<TtsModelDto[]> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}/tts-models`, {
    method: 'GET',
  });
}

export async function createTtsModel(
  providerId: string, req: UpsertTtsModelRequest,
): Promise<TtsModelDto> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}/tts-models`, {
    method: 'POST', data: req,
  });
}

export async function updateTtsModel(
  providerId: string, modelId: string, req: UpsertTtsModelRequest,
): Promise<TtsModelDto> {
  return request(
    `/api/voice-providers/${encodeURIComponent(providerId)}/tts-models/${encodeURIComponent(modelId)}`,
    { method: 'PUT', data: req },
  );
}

export async function deleteTtsModel(providerId: string, modelId: string): Promise<void> {
  return request(
    `/api/voice-providers/${encodeURIComponent(providerId)}/tts-models/${encodeURIComponent(modelId)}`,
    { method: 'DELETE' },
  );
}

// ─── ASR Model API ───────────────────────────────────────────────

export async function listAsrModels(providerId: string): Promise<AsrModelDto[]> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}/asr-models`, {
    method: 'GET',
  });
}

export async function createAsrModel(
  providerId: string, req: UpsertAsrModelRequest,
): Promise<AsrModelDto> {
  return request(`/api/voice-providers/${encodeURIComponent(providerId)}/asr-models`, {
    method: 'POST', data: req,
  });
}

export async function updateAsrModel(
  providerId: string, modelId: string, req: UpsertAsrModelRequest,
): Promise<AsrModelDto> {
  return request(
    `/api/voice-providers/${encodeURIComponent(providerId)}/asr-models/${encodeURIComponent(modelId)}`,
    { method: 'PUT', data: req },
  );
}

export async function deleteAsrModel(providerId: string, modelId: string): Promise<void> {
  return request(
    `/api/voice-providers/${encodeURIComponent(providerId)}/asr-models/${encodeURIComponent(modelId)}`,
    { method: 'DELETE' },
  );
}

// ─── TTS Voice Synthesis API ─────────────────────────────────────

export async function synthesizeTts(params: {
  text: string;
  voice?: string;
  format?: string;
}): Promise<Blob> {
  const response = await request<Blob>('/api/voice/tts/synthesize', {
    method: 'POST',
    data: params,
    responseType: 'blob',
  });
  return response;
}

// ─── ASR Voice Recognition API ────────────────────────────────────

export async function recognizeAsr(audioBlob: Blob): Promise<{ text: string; emotion?: string }> {
  const form = new FormData();
  form.append('audio', audioBlob, 'recording.webm');
  return request('/api/voice/asr/recognize', {
    method: 'POST',
    data: form,
    headers: { 'Content-Type': 'multipart/form-data' },
  });
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

// ─── Plugin Catalog API ─────────────────────────────────────────

export async function listPlugins(): Promise<PluginCatalogItemDto[]> {
  return request('/api/plugins', { method: 'GET' });
}

export async function listPluginDiagnostics(params?: {
  pluginId?: string;
  limit?: number;
}): Promise<PluginDiagnosticEventDto[]> {
  return request('/api/plugins/diagnostics', { method: 'GET', params });
}

export async function getPlugin(pluginId: string): Promise<PluginCatalogItemDto> {
  return request(`/api/plugins/${encodeURIComponent(pluginId)}`, { method: 'GET' });
}

export async function reloadPlugin(pluginId: string): Promise<PluginReloadResultDto> {
  return request(`/api/plugins/${encodeURIComponent(pluginId)}/reload`, { method: 'POST' });
}

export async function reloadPluginCatalog(): Promise<PluginCatalogReloadResultDto> {
  return request('/api/plugins/reload', { method: 'POST' });
}

export async function uploadPluginPackage(formData: FormData): Promise<PluginPackageInstallResultDto> {
  return request('/api/plugins/upload', {
    method: 'POST',
    data: formData,
    requestType: 'form',
  });
}

// ─── Global Agent Template API ───────────────────────────────────

export async function listGlobalAgentTemplates(enabledOnly?: boolean): Promise<GlobalAgentTemplateDto[]> {
  const url = enabledOnly ? '/api/global-agent-templates?enabledOnly=true' : '/api/global-agent-templates';
  return request(url, { method: 'GET' });
}

export async function getGlobalAgentTemplate(templateId: string): Promise<GlobalAgentTemplateDto> {
  return request(`/api/global-agent-templates/${encodeURIComponent(templateId)}`, { method: 'GET' });
}

export async function listGlobalAgentTemplatePresets(): Promise<GlobalAgentTemplateDto[]> {
  return request('/api/global-agent-templates/presets', { method: 'GET' });
}

export async function importGlobalAgentTemplatePreset(templateId: string): Promise<GlobalAgentTemplateDto> {
  return request(`/api/global-agent-templates/presets/${encodeURIComponent(templateId)}/import`, { method: 'POST' });
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
  mainSessionId?: string;
  systemPromptOverride?: string;
  preferredProviderId?: string;
  preferredModelId?: string;
    heartbeatPrompt?: string;
  isEnabled: boolean;
  isFrozen: boolean;
  createdAt: string;
  updatedAt: string;
  // 嵌入的模板配置
  systemPrompt?: string;
  role?: string;
  memorySearchMode?: string;
  maxContextTokens?: number;
  maxRounds?: number;
  maxElapsedSeconds?: number;
  allowFileWrite?: boolean;
  allowShellExecution?: boolean;
  allowNetworkAccess?: boolean;
  selectedCapabilityIds?: string[];
  skillPackageIds?: string[];
  allowedToolNames?: string[];
  // Markdown 文件内容
  soulMdContent?: string;
  agentsMdContent?: string;
  toolsMdContent?: string;
  bootstrapMdContent?: string;
  memoryMdContent?: string;
}

export interface CreateWorkspaceAgentRequest {
  name: string;
  description?: string;
  displayName?: string;
  avatarId?: string;
  avatarEmoji?: string;
  avatarUrl?: string;
  sourceTemplateId?: string;
  heartbeatPrompt?: string;
}

export interface UpdateWorkspaceAgentRequest {
  name: string;
  description?: string;
  displayName?: string;
  avatarId?: string;
  avatarEmoji?: string;
  avatarUrl?: string;
  sourceTemplateId?: string;
  heartbeatPrompt?: string;
  isEnabled: boolean;
  // Agent 自身配置
  systemPrompt?: string;
  memorySearchMode?: string;
  maxContextTokens?: number;
  maxRounds?: number;
  maxElapsedSeconds?: number;
  allowFileWrite?: boolean;
  allowShellExecution?: boolean;
  allowNetworkAccess?: boolean;
  selectedCapabilityIds?: string[];
  skillPackageIds?: string[];
  allowedToolNames?: string[];
  soulMdContent?: string;
  agentsMdContent?: string;
  toolsMdContent?: string;
  bootstrapMdContent?: string;
  memoryMdContent?: string;
}

// ─── WorkspaceAgent API ───────────────────────────────────────

export async function listWorkspaceAgents(workspaceId: string): Promise<WorkspaceAgentDto[]> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents`, { method: 'GET' });
}

export async function getWorkspaceAgent(
  workspaceId: string, agentId: string,
): Promise<WorkspaceAgentDto> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}`, { method: 'GET' });
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

// ── ADR-056: projected cursor ──────────────────────────────────

export interface ProjectedCursor {
  sessionId: string;
  projectedThroughSequence: number;
}

/** 获取会话的投影游标（ChatMessages 已物化到该 Sequence）。 */
export async function getProjectedCursor(sessionId: string): Promise<ProjectedCursor> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/projected-cursor`, {
    method: 'GET',
  });
}

// ── P0: Conversation Bootstrap ─────────────────────────────────

export interface ConversationBootstrapResponse {
  conversation: { conversationId: string; sessionId: string };
  turns: Array<{
    turnId: string;
    status: 'active' | 'completed' | 'failed' | 'cancelled';
    userMessageId: string;
    assistantMessageId: string;
    createdAt: number;
    terminalSequence?: number | null;
    errorCode?: string | null;
    errorMessage?: string | null;
  }>;
  messages: Array<{
    id: number;
    role: string;
    content: string;
    createdAt: number;
  }>;
  lifecycleEvents: unknown[];
  snapshotCursor: number;
  hasMoreHistory: boolean;
  historyCursor: string | null;
}

/** P0: 初始化加载 — 获取 conversation 快照、turns、messages、cursor。 */
export async function getConversationBootstrap(
  conversationId: string,
  messageLimit?: number,
): Promise<ConversationBootstrapResponse> {
  const params = new URLSearchParams();
  if (messageLimit) params.set('messageLimit', String(messageLimit));
  const qs = params.toString();
  return request(`/api/conversations/${encodeURIComponent(conversationId)}/bootstrap${qs ? `?${qs}` : ''}`, {
    method: 'GET',
  });
}

/** ADR-057: 查询会话事件（用于 gap recovery）。 */
export async function getSessionEvents(
  sessionId: string,
  options?: { afterSequence?: number; limit?: number },
): Promise<{ events: unknown[]; hasMore: boolean }> {
  const params = new URLSearchParams();
  if (options?.afterSequence != null) params.set('from', String(options.afterSequence));
  if (options?.limit) params.set('limit', String(options.limit));
  const qs = params.toString();
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/events${qs ? `?${qs}` : ''}`, {
    method: 'GET',
  });
}

// ── ADR-056-E: normalize new server event names to legacy names ──
// Backend emits new names (assistant.content.delta etc.) via MapLegacy.
// Frontend internally uses legacy names for backward compatibility.
const NEW_TO_LEGACY_EVENT: Record<string, string> = {
  'assistant.content.delta': 'delta',
  'assistant.thinking.delta': 'thinking',
  'turn.completed': 'done',
  'turn.failed': 'error',
  'turn.cancelled': 'cancelled',
  'usage.recorded': 'usage',
  'tool.call.started': 'tool_call',
  'tool.call.completed': 'tool_result',
  'tool.call.failed': 'tool_error',
};

export function normalizeConversationEventType(rawType: string): string {
  return NEW_TO_LEGACY_EVENT[rawType] ?? rawType;
}

export function projectConversationEventEnvelope(
  data: Record<string, unknown>,
  rawType: string,
  sequenceNum?: number,
): AdminChatStreamEvent {
  const payload =
    data.payload &&
    typeof data.payload === 'object' &&
    !Array.isArray(data.payload)
      ? (data.payload as Record<string, unknown>)
      : {};
  const normalizedType = normalizeConversationEventType(rawType);
  const terminalMessage =
    normalizedType === 'error' &&
    typeof payload.errorMessage === 'string'
      ? payload.errorMessage
      : undefined;
  return {
    ...data,
    ...payload,
    type: normalizedType as AdminChatStreamEvent['type'],
    ...(terminalMessage ? { message: terminalMessage } : {}),
    ...(sequenceNum !== undefined ? { sequenceNum } : {}),
  } as AdminChatStreamEvent;
}

/** SSE 订阅会话事件流（含 subagent.spawned / subagent.completed）。
 * P0 v3: 支持 Last-Event-ID 重连、generation token、cursor 校验。
 */
export function subscribeSessionEvents(
  sessionId: string,
  onEvent: (ev: AdminChatStreamEvent) => void,
  signal?: AbortSignal,
  options?: {
    onError?: (error: Error, httpStatus?: number) => void;
    afterSequence?: number;
    generation?: number;
  },
): void {
  const onError = options?.onError;
  const generation = options?.generation;
  const url = `/api/sessions/${encodeURIComponent(sessionId)}/events/stream`;
  const token = localStorage.getItem('pudding_token');
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;

  // P0: Send Last-Event-ID on first connect and reconnects
  if (options?.afterSequence != null && options.afterSequence > 0) {
    headers['Last-Event-ID'] = String(options.afterSequence);
  }

  const startedAt = performance.now();
  recordPerfEvent('chat.sse.fetchStart', { sessionId, url, afterSequence: options?.afterSequence, generation });

  fetch(url, { headers, signal })
    .then(async (resp) => {
      recordPerfEvent('chat.sse.response', {
        sessionId,
        status: resp.status,
        ok: resp.ok,
        hasBody: Boolean(resp.body),
        contentType: resp.headers.get('content-type') ?? undefined,
        elapsedMs: Math.round(performance.now() - startedAt),
      });
      if (!resp.ok || !resp.body) {
        const httpStatus = resp.status;
        const msg = `SSE stream failed: HTTP ${httpStatus} for session ${sessionId}`;
        onError?.(new Error(msg), httpStatus);
        return;
      }

      const reader = resp.body.getReader();
      const decoder = new TextDecoder();
      let buf = '';
      let chunkCount = 0;
      let eventCount = 0;
      let lastEventType = '';
      let lastEventId = '';
      let lastSequenceNum = options?.afterSequence ?? 0;

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        chunkCount++;
        const chunkText = decoder.decode(value, { stream: true });
        buf += chunkText;
        recordPerfEvent('chat.sse.chunk', {
          sessionId,
          chunkCount,
          bytes: value.byteLength,
          chars: chunkText.length,
          bufferedChars: buf.length,
        });
        const lines = buf.split('\n');
        buf = lines.pop() || '';
        for (const line of lines) {
          if (line.startsWith('id: ')) {
            // ADR-056: SSE id field carries the committed sequence number.
            lastEventId = line.slice(4).trim();
          } else if (line.startsWith('event: ')) {
            lastEventType = line.slice(7).trim();
          } else if (line.startsWith('data: ')) {
            try {
              const data = JSON.parse(line.slice(6));
              eventCount++;

              // P0: Extract sequence from SSE id (primary) or data payload (fallback)
              const seqNum = /^\d+$/.test(lastEventId) ? Number(lastEventId) : (data.sequenceNum ?? data.sequence ?? undefined);
              const seq = typeof seqNum === 'number' && Number.isFinite(seqNum) ? seqNum : undefined;

              // P0: Validate SSE id == envelope sequence (if both present)
              if (seq !== undefined && data.sequence !== undefined && String(seq) !== String(data.sequence)) {
                recordPerfEvent('chat.sse.sequenceMismatch', {
                  sessionId,
                  sseId: lastEventId,
                  dataSequence: data.sequence,
                  eventType: lastEventType,
                });
              }

              // P0: Track cursor for reconnection
              if (seq !== undefined && seq > lastSequenceNum) {
                lastSequenceNum = seq;
              }

              recordPerfEvent('chat.sse.event', {
                sessionId,
                eventType: lastEventType,
                eventCount,
                sequenceNum: seq,
                sseId: lastEventId || undefined,
                dataChars: line.length,
              });
              // ADR-056: normalize event names (new → legacy) then pass to callback
              // type must come AFTER ...data so it is not overwritten by data.type
              onEvent(projectConversationEventEnvelope(data, lastEventType, seq));
            } catch (error) {
              recordPerfEvent('chat.sse.parseError', {
                sessionId,
                eventType: lastEventType,
                sseId: lastEventId || undefined,
                lineChars: line.length,
                error: error instanceof Error ? error.message : String(error),
              });
            }
          }
        }
      }
      recordPerfEvent('chat.sse.complete', {
        sessionId,
        chunkCount,
        eventCount,
        lastSequenceNum,
        bufferedChars: buf.length,
        elapsedMs: Math.round(performance.now() - startedAt),
      });
    })
    .catch((error) => {
      recordPerfEvent('chat.sse.error', {
        sessionId,
        error: error instanceof Error ? error.message : String(error),
        aborted: signal?.aborted === true,
        elapsedMs: Math.round(performance.now() - startedAt),
      });
      if (!signal?.aborted) {
        onError?.(error instanceof Error ? error : new Error(String(error)));
      }
    });
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

export interface AdminChatSteeringRequest {
  messageText: string;
  agentId?: string;
  sourceQueueItemId?: string;
  priority?: number;
}

export interface AdminChatSteeringResponse {
  steeringId: string;
  sessionId: string;
  workspaceId: string;
  agentId?: string;
  status: string;
  createdAt: number;
}

export interface MessageAddressDto {
  kind: string;
  id: string;
  workspaceId?: string;
  displayName?: string;
}

export interface WorkspaceMessageSendRequest {
  content: string;
  roomId?: string;
  conversationId?: string;
  replyToMessageId?: string;
  targetAgentIds?: string[];
  audience?: 'direct' | 'room' | 'broadcast' | string;
  visibility?: 'public' | 'private' | 'system' | string;
  intent?: string;
  priority?: number;
  metadata?: Record<string, string>;
}

export interface MessageSendResult {
  messageId: string;
  roomId?: string;
  deliveryIds: string[];
}

export interface AgentMessageQueueItem {
  deliveryId: string;
  messageId: string;
  workspaceId: string;
  roomId?: string;
  from: MessageAddressDto;
  target: MessageAddressDto;
  content: string;
  status: 'queued' | 'delivering' | 'retrying' | 'delivered' | 'dead_letter' | 'failed' | 'cancelled' | 'expired' | string;
  priority: number;
  attemptCount: number;
  createdAt: number;
  availableAt?: number;
  leaseUntil?: number;
  readAt?: number;
  ackAt?: number;
  claimedByExecutionId?: string;
  lastError?: string;
}

export interface AgentMessageQueueSnapshot {
  workspaceId: string;
  agentId: string;
  roomId?: string;
  items: AgentMessageQueueItem[];
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
  compactionId?: string;
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
  summaryMarkdown?: string;
  diagnostics?: ContextCompactionDiagnostics;
}

export interface ContextCompactionDiagnostics {
  compactionId: string;
  previousSessionId: string;
  newSessionId?: string | null;
  newSessionTitle?: string | null;
  previousLastMessageId?: string | null;
  previousLastMessageSequence?: number | null;
  activeMessageCountBefore: number;
  compactedMessageCount: number;
  keptRecentMessageCount: number;
  beforeTokens: number;
  afterTokens: number;
  summaryMessageId: string;
  summaryCharacterCount: number;
  summaryEstimatedTokens: number;
  summaryGenerator?: string;
  completedAtUtc: string;
  durationMs: number;
}

/// 压缩会话的完整响应，包含压缩结果和可选的新会话。
export interface CompactSessionResponse {
  compactionId: string;
  compaction: ContextCompactionResult;
  newSessionId: string;
  newSessionTitle: string | null;
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

export interface ConversationRecipientRequest {
  type: 'agent';
  agentIds: string[];
}

export interface ConversationContentPart {
  type: 'text';
  text: string;
}

export interface SubmitConversationTurnRequest {
  clientRequestId: string;
  clientMessageId: string;
  recipients: ConversationRecipientRequest;
  content: ConversationContentPart[];
}

export interface ConversationAcceptanceResult {
  conversationId: string;
  messageId: string;
  turnIds: string[];
  commandIds: string[];
  acceptedSequence: number;
}

export interface VisionArtifactUploadResponse {
  artifactId: string;
  mimeType: string;
  width?: number;
  height?: number;
  capturedAt: number;
}

export type AdminChatStreamEvent =
  | {
      type: 'metadata';
      messageId: string;
      sessionId: string;
      routeDecisionId?: string;
      source_type?: string;
      source_id?: string;
      source_name?: string;
      agent_id?: string;
      audience?: 'agent' | 'all';
      target_agent_ids?: string;
      avatar_url?: string;
      fanout_index?: string;
      fanout_count?: string;
      inputMode?: string;
      cameraSessionId?: string;
      visionArtifactId?: string;
      visualProvider?: string;
      visualModel?: string;
      omniSessionId?: string;
      omniProvider?: string;
      omniModel?: string;
    }
  | { type: 'delta'; delta: string }
  | { type: 'thinking'; delta: string }
  | { type: 'voice_capture_status'; messageId?: string; sessionId?: string; voiceSessionId?: string; status: string; text?: string; transcript?: string; error?: string; [key: string]: unknown }
  | { type: 'voice_playback_status'; messageId?: string; sessionId?: string; voiceSessionId?: string; status: string; audioBase64?: string; sampleRate?: number; error?: string; [key: string]: unknown }
  | { type: 'camera_capture_status'; sessionId?: string; status: string; artifactId?: string; error?: string; [key: string]: unknown }
  | { type: 'visual_reasoning_status'; sessionId?: string; status: string; artifactId?: string; error?: string; [key: string]: unknown }
  | { type: 'tool_call'; name: string; arguments: string }
  | { type: 'tool_result'; name: string; exitCode: number; output: string; error?: string }
  | { type: 'step'; status?: string; message?: string; [key: string]: unknown }
  | { type: 'usage'; usage: TokenUsageDto }
  | { type: 'context.health'; state?: ContextHealthState; usedTokens?: number; effectiveWindowTokens?: number; usageRatio?: number; [key: string]: unknown }
  | { type: 'context.compaction.started'; compactionId?: string; sessionId?: string; mode?: ContextCompactionMode; level?: ContextCompactionLevel; reason?: string; [key: string]: unknown }
  | { type: 'context.compaction.completed'; compactionId?: string; sessionId?: string; sourceSessionId?: string; newSessionId?: string; newSessionTitle?: string; compaction?: ContextCompactionResult; [key: string]: unknown }
  | { type: 'context.compaction.failed'; compactionId?: string; sessionId?: string; error?: string; errorType?: string; [key: string]: unknown }
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

export async function sendWorkspaceMessage(
  workspaceId: string,
  req: WorkspaceMessageSendRequest,
): Promise<MessageSendResult> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/messages`, {
    method: 'POST',
    data: req,
  });
}

export async function getAgentMessageQueue(
  workspaceId: string,
  agentId: string,
  params?: { roomId?: string; limit?: number; includeTerminal?: boolean },
): Promise<AgentMessageQueueSnapshot> {
  return request(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/message-queue`,
    {
      method: 'GET',
      params,
    },
  );
}

/** Canonical Conversation command endpoint. HTTP 202 means accepted, never completed. */
export async function submitConversationTurn(
  workspaceId: string,
  conversationId: string,
  req: SubmitConversationTurnRequest,
  signal?: AbortSignal,
): Promise<ConversationAcceptanceResult> {
  return request(
    `/api/v1/conversations/${encodeURIComponent(conversationId)}/turns`,
    {
      method: 'POST',
      data: req,
      headers: { 'X-Workspace-Id': workspaceId },
      signal,
    },
  );
}

export async function awaitConversationTurn(
  conversationId: string,
  turnId: string,
  afterSequence: number,
  timeoutMs = 120_000,
): Promise<{ reply: string }> {
  const controller = new AbortController();
  let accumulatedReply = '';

  return new Promise((resolve, reject) => {
    const finish = (action: () => void) => {
      window.clearTimeout(timeout);
      controller.abort();
      action();
    };
    const timeout = window.setTimeout(
      () => finish(() => reject(new Error('等待 Agent 回复超时'))),
      timeoutMs,
    );

    subscribeSessionEvents(
      conversationId,
      (event) => {
        const eventTurnId = 'turnId' in event ? event.turnId : undefined;
        if (eventTurnId && eventTurnId !== turnId) return;

        if (event.type === 'delta') {
          accumulatedReply += event.delta;
          return;
        }
        if (event.type === 'done') {
          finish(() => resolve({
            reply: event.reply?.trim() || accumulatedReply.trim(),
          }));
          return;
        }
        if (event.type === 'error') {
          finish(() => reject(new Error(event.message || 'Agent 执行失败')));
          return;
        }
        if (event.type === 'cancelled') {
          finish(() => reject(new Error(event.message || 'Agent 执行已取消')));
        }
      },
      controller.signal,
      {
        afterSequence,
        onError: (error) => finish(() => reject(error)),
      },
    );
  });
}

export async function createChatSteeringMessage(
  workspaceId: string,
  sessionId: string,
  req: AdminChatSteeringRequest,
): Promise<AdminChatSteeringResponse> {
  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/chat/sessions/${encodeURIComponent(sessionId)}/steering`, {
    method: 'POST',
    data: req,
  });
}

export async function uploadVisionArtifact(
  workspaceId: string,
  file: Blob,
  metadata?: { width?: number; height?: number; capturedAt?: number },
  signal?: AbortSignal,
): Promise<VisionArtifactUploadResponse> {
  const form = new FormData();
  form.append('file', file, 'camera-frame.jpg');
  if (metadata?.width != null) form.append('width', String(metadata.width));
  if (metadata?.height != null) form.append('height', String(metadata.height));
  if (metadata?.capturedAt != null) form.append('capturedAt', String(metadata.capturedAt));

  return request(`/api/workspaces/${encodeURIComponent(workspaceId)}/vision-artifacts`, {
    method: 'POST',
    data: form,
    signal,
  });
}

export async function getContextHealth(sessionId: string): Promise<ContextHealthSnapshot> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/context-health`, {
    method: 'GET',
  });
}

export interface CacheDiagnosticsReport {
  sessionId: string;
  analyzedEventCount: number;
  distinctPrefixHashCount: number;
  status: string;
  averageCacheHitRate?: number | null;
  cacheHitTokens: number;
  cacheMissTokens: number;
  cacheEligibleTokens: number;
  firstChurnAtUtc?: string | null;
  firstChurnReason?: string | null;
  firstChurnSource?: string | null;
}

export async function getCacheDiagnostics(
  sessionId: string,
  limit = 50,
): Promise<CacheDiagnosticsReport> {
  return request(`/api/sessions/${encodeURIComponent(sessionId)}/cache-diagnostics`, {
    method: 'GET',
    params: { limit },
  });
}

export async function compactSession(
  sessionId: string,
  req: CompactSessionRequest,
): Promise<CompactSessionResponse> {
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

export interface TokenSeriesPoint {
  period: string;
  cacheMissTokens: number;
  cacheHitTokens: number;
  completionTokens: number;
  requestCount: number;
  totalCost: number;
}

export interface TokenStatsSeriesResponse {
  yearMonth: string;
  year: number;
  monthly: TokenSeriesPoint[];
  daily: TokenSeriesPoint[];
}

export interface ContextLayerTokenStatsLayer {
  layerName: string;
  layerOrder: number;
  layerRole?: string;
  calls: number;
  tokenCount: number;
  tokenShare?: number;
  avgTokens?: number;
  medianTokens?: number;
  p95Tokens?: number;
  estimatedHitTokens: number;
  estimatedMissTokens: number;
  avgCacheHitRate?: number;
  medianCacheHitRate?: number;
  changeCount: number;
  changeRate?: number;
  distinctHashes: number;
  changeReasons: Array<{ reason: string; count: number }> | Record<string, number>;
}

export interface ContextLayerTokenStatsResponse {
  from?: string;
  to?: string;
  providerId?: string;
  modelId?: string;
  sessionId?: string;
  totalEvents: number;
  totalLayerTokens: number;
  layers: ContextLayerTokenStatsLayer[];
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

/** 获取 Token 用量图表序列（按月 + 按日） */
export async function getTokenStatsSeries(
  yearMonth: string,
  providerId?: string,
  modelId?: string,
): Promise<TokenStatsSeriesResponse> {
  const params = new URLSearchParams({ yearMonth });
  if (providerId) params.set('providerId', providerId);
  if (modelId) params.set('modelId', modelId);
  return request(`/api/stats/tokens/series?${params.toString()}`, { method: 'GET' });
}

/** 获取上下文分层 Token 与缓存命中统计 */
export async function getContextLayerTokenStats(
  params: {
    from?: string;
    to?: string;
    providerId?: string;
    modelId?: string;
    sessionId?: string;
  },
): Promise<ContextLayerTokenStatsResponse> {
  const searchParams = new URLSearchParams();
  if (params.from) searchParams.set('from', params.from);
  if (params.to) searchParams.set('to', params.to);
  if (params.providerId) searchParams.set('providerId', params.providerId);
  if (params.modelId) searchParams.set('modelId', params.modelId);
  if (params.sessionId) searchParams.set('sessionId', params.sessionId);
  return request(`/api/stats/tokens/context-layers?${searchParams.toString()}`, { method: 'GET' });
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
): Promise<{
  eventsCreated: number;
  eventsDeleted?: number;
  messagesScanned: number;
  skippedDuplicates: number;
  statsRowsRebuilt?: number;
  errors: number;
  errorDetails: string[];
}> {
  const params = yearMonth ? `?yearMonth=${yearMonth}` : '';
  return request(`/api/stats/tokens/rebuild${params}`, { method: 'POST' });
}

// ─── Memory Library Admin API (ADR-030) ──────────────────────────

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

export async function getAgentMemoryLibraryTree(
  workspaceId: string,
  agentId: string,
  libraryId: string,
): Promise<
  { id: string; parentId: string | null; type: string; title: string; summary?: string; status: string; bookId?: string; children: any[] }[]
> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/libraries/${encodeURIComponent(libraryId)}/tree`);
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

export async function archiveAgentMemoryBook(workspaceId: string, agentId: string, bookId: string): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/books/${encodeURIComponent(bookId)}/archive`, {
    method: 'POST',
  });
}

export async function archiveAgentMemoryChapter(workspaceId: string, agentId: string, chapterId: string): Promise<any> {
  return request(`/api/admin/memory-library/workspaces/${encodeURIComponent(workspaceId)}/agents/${encodeURIComponent(agentId)}/chapters/${encodeURIComponent(chapterId)}/archive`, {
    method: 'POST',
  });
}

// ─── Memory Library Sources & Pointers ──────────────────────────

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

// ─── Tool Approval Governance ───────────────────────────────────

export type ToolApprovalAllowlistSource = 'built_in' | 'audit_agent' | 'human';
export type ToolApprovalAllowlistStatus = 'enabled' | 'disabled';

export interface ToolApprovalAllowlistRuleDto {
  ruleId: string;
  workspaceId?: string;
  toolId: string;
  command?: string;
  argumentsJson?: string;
  source: ToolApprovalAllowlistSource;
  status: ToolApprovalAllowlistStatus;
  approvedByAgentInstanceId?: string;
  approvedByUserId?: string;
  approvalTicketId?: string;
  reason?: string;
  createdAtUtc: string;
  updatedAtUtc?: string;
  disabledAtUtc?: string;
  hitCount: number;
  lastHitAtUtc?: string;
}

export interface ToolApprovalAllowlistRuleMutation {
  workspaceId?: string;
  toolId: string;
  command?: string;
  argumentsJson?: string;
  source: ToolApprovalAllowlistSource;
  status: ToolApprovalAllowlistStatus;
  approvedByAgentInstanceId?: string;
  approvedByUserId?: string;
  approvalTicketId?: string;
  reason?: string;
}

export interface ToolApprovalAuditEventDto {
  eventId: string;
  eventType: string;
  workspaceId?: string;
  sessionId?: string;
  agentInstanceId?: string;
  userId?: string;
  toolId?: string;
  command?: string;
  argumentsJson?: string;
  originalCommand?: string;
  originalArgumentsJson?: string;
  ticketId?: string;
  allowlistRuleId?: string;
  allowlistRuleCommand?: string;
  allowlistRuleArgumentsJson?: string;
  allowlistRuleHitCount?: number;
  decision?: string;
  source?: ToolApprovalAllowlistSource;
  reviewerModel?: string;
  reason?: string;
  createdAtUtc: string;
}

export interface ToolApprovalStatsDto {
  ticketSubmittedCount: number;
  ticketApprovedCount: number;
  ticketDeniedCount: number;
  ticketNeedHumanCount: number;
  ticketMatchedCount?: number;
  ticketConsumedCount?: number;
  ticketMismatchCount?: number;
  implicitApprovedCount?: number;
  implicitDeniedCount?: number;
  allowlistHitCount: number;
  allowlistRuleCount: number;
  enabledAllowlistRuleCount: number;
  builtInAllowlistRuleCount: number;
  dynamicAllowlistRuleCount: number;
}

export async function listToolApprovalAllowlist(params?: {
  workspaceId?: string;
  toolId?: string;
  status?: ToolApprovalAllowlistStatus;
}): Promise<{ items: ToolApprovalAllowlistRuleDto[] }> {
  return request('/api/tool-approval/allowlist', { method: 'GET', params });
}

export async function createToolApprovalAllowlistRule(
  req: ToolApprovalAllowlistRuleMutation,
): Promise<ToolApprovalAllowlistRuleDto> {
  return request('/api/tool-approval/allowlist', { method: 'POST', data: req });
}

export async function updateToolApprovalAllowlistRule(
  ruleId: string,
  req: ToolApprovalAllowlistRuleMutation,
): Promise<ToolApprovalAllowlistRuleDto> {
  return request(`/api/tool-approval/allowlist/${encodeURIComponent(ruleId)}`, {
    method: 'PUT',
    data: req,
  });
}

export async function disableToolApprovalAllowlistRule(ruleId: string): Promise<void> {
  return request(`/api/tool-approval/allowlist/${encodeURIComponent(ruleId)}`, {
    method: 'DELETE',
  });
}

export async function listToolApprovalAuditEvents(params?: {
  workspaceId?: string;
  toolId?: string;
  eventType?: string;
  limit?: number;
}): Promise<{ items: ToolApprovalAuditEventDto[] }> {
  return request('/api/tool-approval/audit-events', { method: 'GET', params });
}

export async function getToolApprovalStats(): Promise<ToolApprovalStatsDto> {
  return request('/api/tool-approval/stats', { method: 'GET' });
}

export interface SessionBenchmarkReportDto {
  sessionId: string;
  hasJsonl: boolean;
  paths: SessionBenchmarkPathsDto;
  usage: SessionBenchmarkUsageDto;
  counts: SessionBenchmarkCountsDto;
  approvalStats: SessionBenchmarkApprovalStatsDto;
  toolOutputStats: SessionBenchmarkToolOutputStatsDto[];
  failures: SessionBenchmarkToolResultDto[];
  approvalTimeline: SessionBenchmarkApprovalEventDto[];
  tickets: SessionBenchmarkTicketDto[];
  timelineErrors: SessionBenchmarkTimelineErrorDto[];
  sessionLogFindings: string[];
  frictionPoints: SessionBenchmarkFrictionPointDto[];
  scores: SessionBenchmarkScoresDto;
}

export interface SessionBenchmarkPathsDto {
  jsonl?: string;
  timeline?: string;
  sessionLog?: string;
}

export interface SessionBenchmarkUsageDto {
  promptTokens?: number;
  completionTokens?: number;
  totalTokens?: number;
  contextWindowTokens?: number;
  promptCacheHitTokens?: number;
  promptCacheMissTokens?: number;
}

export interface SessionBenchmarkCountsDto {
  messages: Record<string, number>;
  events: Record<string, number>;
  toolCalls: Record<string, number>;
  toolResults: Record<string, number>;
  failedToolResults: number;
  approvalEvents: Record<string, number>;
  tickets: number;
  timeline: Record<string, number>;
}

export interface SessionBenchmarkApprovalStatsDto {
  implicitApproved: number;
  implicitDenied: number;
  implicitDecisions: number;
  implicitApprovals: number;
  explicitTickets: number;
  ticketApprovals: number;
  allowlistHits: number;
  approvalMismatches: number;
  approvalDecisionAttempts: number;
  implicitCoveragePercent: number;
  implicitLatencySamples: number;
  implicitLatencyAvgMs?: number;
  implicitLatencyP50Ms?: number;
  implicitLatencyP95Ms?: number;
  implicitLatencyMaxMs?: number;
}

export interface SessionBenchmarkToolResultDto {
  seq: number;
  name: string;
  exitCode?: number;
  error?: string;
  output?: string;
  recordedAt?: string;
  pairedCommand?: string;
  category: string;
  durationMs?: number;
  outputLineCount: number;
  outputCharCount: number;
  errorLineCount: number;
  errorCharCount: number;
  totalTextLineCount: number;
  totalTextCharCount: number;
}

export interface SessionBenchmarkToolOutputStatsDto {
  toolName: string;
  resultCount: number;
  outputLineTotal: number;
  outputCharTotal: number;
  errorLineTotal: number;
  errorCharTotal: number;
  totalTextLineTotal: number;
  totalTextCharTotal: number;
  maxTotalTextCharCount: number;
  avgTotalTextCharCount: number;
  durationSamples: number;
  durationAvgMs?: number;
  durationP50Ms?: number;
  durationP95Ms?: number;
  durationMaxMs?: number;
}

export interface SessionBenchmarkApprovalEventDto {
  eventType: string;
  toolId?: string;
  command?: string;
  ticketId?: string;
  allowlistRuleId?: string;
  decision?: string;
  reason?: string;
  createdAtUtc?: string;
}

export interface SessionBenchmarkTicketDto {
  ticketId: string;
  toolId: string;
  status: string;
  scope: string;
  remainingUses?: number;
  command?: string;
}

export interface SessionBenchmarkTimelineErrorDto {
  recordedAtUtc?: string;
  component?: string;
  operation?: string;
  status?: string;
  errorMessage?: string;
}

export interface SessionBenchmarkFrictionPointDto {
  severity: string;
  category: string;
  evidence: string;
  impact: string;
  recommendation: string;
}

export interface SessionBenchmarkScoresDto {
  completion: number;
  toolExecution: number;
  approvalFlow: number;
  diagnosability: number;
  governance: number;
  overall: number;
  grade: string;
}

export async function getSessionBenchmarkDiagnostics(
  sessionId: string,
  maxFindings = 20,
): Promise<SessionBenchmarkReportDto> {
  return request(`/api/diagnostics/sessions/${encodeURIComponent(sessionId)}/benchmark`, {
    method: 'GET',
    params: { maxFindings },
  });
}

