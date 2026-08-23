import type { FormInstance } from 'antd';
import type { KeyboardEvent, ReactNode, RefObject } from 'react';
import type {
  TokenUsageDto,
  WorkspaceAgentDto,
  WorkspaceWithPermDto,
} from '@/services/platform/api';
import type { AgentAvatarRuntimeEvent } from '../hooks/agentAvatarRuntime';
import type {
  AssistantStatus,
  ChatTurn,
  SessionGroup,
  SessionListItem,
  SubAgentCardMap,
} from '../types';
import type { ChatCheckpoint } from '../client/checkpointStore';
import type { ScrollIntent } from '../viewport/types';

export const MESSAGE_PAGE_SIZE = 20;
export const SESSION_EVENT_PAGE_SIZE = 50;
export const ACTIVE_SESSION_REPLAY_POLL_INTERVAL_MS = 900;
export const IDLE_SESSION_REPLAY_POLL_INTERVAL_MS = 8000;
export const SSE_HEALTHY_REPLAY_SUPPRESSION_MS = 2500;
export const MAX_CHAT_INTERACTION_RUNTIME_EVENTS = 16;
export const STEERING_INJECTED_QUEUE_RETENTION_MS = 8000;
export const CHAT_DIAG_STORAGE_KEY = 'pudding_chat_diag_events';
export const CHAT_DIAG_MAX_EVENTS = 200;

/** P1#4：权限模式 — 控制 Agent 执行工具/编辑/计划时的审批粒度 */
export type PermissionMode =
  | 'manual' // 每步需批：所有工具调用都需人工确认
  | 'acceptEdits' // 只批编辑：仅文件编辑类操作需确认
  | 'plan' // 先计划后执行：先展示执行计划，确认后执行
  | 'auto'; // 自动执行：不打断，自动执行

export const PERMISSION_MODES: PermissionMode[] = [
  'manual',
  'acceptEdits',
  'plan',
  'auto',
];

export const PERMISSION_MODE_LABELS: Record<PermissionMode, string> = {
  manual: '每步需批',
  acceptEdits: '只批编辑',
  plan: '先计划后执行',
  auto: '自动执行',
};

/** 权限模式的 localStorage 持久化键 */
export const PERMISSION_MODE_STORAGE_KEY = 'pudding-chat-permission-mode';

export interface SessionEventPageResponse {
  events?: unknown[];
  Events?: unknown[];
  hasMore?: boolean;
  HasMore?: boolean;
  maxSequence?: unknown;
  MaxSequence?: unknown;
  totalEventCount?: unknown;
  TotalEventCount?: unknown;
}

export interface ChatRouteSelection {
  workspaceId?: string;
  agentId?: string;
  sessionId?: string;
}

export interface ChatSendOptions {
  metadata?: Record<string, string>;
  /** ADR-077：typed 图片内容部件（已上传为 Workspace Artifact）。 */
  imageParts?: { type: 'image'; artifactId: string; detail?: 'original' | 'low' }[];
}

export type ChatInteractionQueueStatus =
  | 'queued'
  | 'delivering'
  | 'retrying'
  | 'delivered'
  | 'dead_letter'
  | 'failed'
  | 'cancelled'
  | 'expired'
  | 'steering_pending'
  | 'steering_injected'
  | 'steering_failed';

export interface ChatInteractionQueueItem {
  id: string;
  text: string;
  createdAt: number;
  status: ChatInteractionQueueStatus | string;
  source?: 'backend_message_queue' | 'steering' | 'local_pending';
  metadata?: Record<string, string>;
  steeringId?: string;
  submittedAt?: number;
  injectedAt?: number;
  injectedRound?: number;
  injectionLatencyMs?: number;
  error?: string;
  /**
   * P1#10 过渡防御：busy-wait 标记 —— status=retrying 且 lastError JSON 含
   * "executionState":"Busy" 时置为 'busy-wait'（计数归排队、不渲染错误）。
   * Phase 2 后由 substate 驱动（substate=waiting → 'busy-wait'），此字段可移除。
   */
  waitReason?: 'busy-wait' | null;
  /** Phase 2：后端投影子状态 —— fresh/waiting/retrying/delivered/dead_letter/failed/cancelled/expired */
  substate?: string;
  /** Phase 2：busy 挂起次数（state=queued 时 deferCount>0 → substate=waiting） */
  deferCount?: number;
  /** Phase 2：结构化 executionState（Busy/Failed/…），从 lastErrorState 列派生 */
  executionState?: string;
  /** Phase 2：队列序（0-based，priority desc + createdAt asc） */
  position?: number;
}

export type ChatInteractionRuntimeType =
  | 'voice_capture_status'
  | 'voice_playback_status'
  | 'camera_capture_status'
  | 'visual_reasoning_status';

export type ChatInteractionRuntimeEvent = Extract<
  AgentAvatarRuntimeEvent,
  { type: ChatInteractionRuntimeType }
>;

export const CHAT_INTERACTION_RUNTIME_EVENT_TYPES = new Set<string>([
  'voice_capture_status',
  'voice_playback_status',
  'camera_capture_status',
  'visual_reasoning_status',
]);

export type ChatDiagPayload = Record<string, unknown>;
export type ChatDiagWindow = Window & {
  __PUDDING_CHAT_DIAG__?: Array<Record<string, unknown>>;
};

export interface UseChatStateReturn {
  workspaces: WorkspaceWithPermDto[];
  workspaceId: string | undefined;
  workspaceLoading: boolean;
  setWorkspaceId: (value: string | undefined) => void;
  setWorkspaces: (value: WorkspaceWithPermDto[]) => void;
  agents: WorkspaceAgentDto[];
  agentId: string | undefined;
  agentLoading: boolean;
  setAgentId: (value: string | undefined) => void;
  selectedAgent: WorkspaceAgentDto | undefined;
  sidebarOpen: boolean;
  setSidebarOpen: (value: boolean) => void;
  sessions: SessionListItem[];
  selectedSessionId: string | null;
  sessionsLoading: boolean;
  groups: SessionGroup[];
  turns: ChatTurn[];
  chatInteractionRuntimeEvents: ChatInteractionRuntimeEvent[];
  historyLoading: boolean;
  hasMoreMessages: boolean;
  loadingMore: boolean;
  inputValue: string;
  setInputValue: (value: string) => void;
  loading: boolean;
  workingAgentIds: string[];
  interactionQueue: ChatInteractionQueueItem[];
  error: string | null;
  setError: (value: string | null) => void;
  latestUsage: TokenUsageDto | undefined;
  subAgentCards: SubAgentCardMap;
  sessionUnreadCounts: Record<string, number>;
  startWorkspaceNotificationStream: (workspaceId: string) => void;
  stopWorkspaceNotificationStream: () => void;
  clearSessionUnread: (sessionId: string) => void;
  tLimit: number;
  tUsed: number;
  tPct: number;
  mainSessionId: string | null;
  sessionCacheHitTokens: number;
  sessionCacheMissTokens: number;
  cacheHitRate?: number;
  /** 来自 useCompaction 的压缩状态文案（如 "上次压缩: 2分钟前"） */
  compactionStatus: string | null;
  /** P1#4：权限模式（manual / acceptEdits / plan / auto），全局持有 */
  permissionMode: PermissionMode;
  setPermissionMode: (value: PermissionMode) => void;
  /** P2#7：当前会话的 Checkpoint 时间线快照（最新在前） */
  checkpoints: ChatCheckpoint[];
  /** P2#7：时间线面板开关 */
  checkpointTimelineOpen: boolean;
  setCheckpointTimelineOpen: (open: boolean) => void;
  /** P2#7：Restore — 将视图 turns 还原到指定快照 */
  restoreCheckpoint: (checkpointId: string) => void;
  /** P2#7：Fork — 从指定快照分支一个新会话并切换过去 */
  forkCheckpoint: (checkpointId: string) => Promise<string | undefined>;
  /** P2#7：删除单个快照 */
  deleteCheckpoint: (checkpointId: string) => void;
  /** P2#7：清空当前会话全部快照 */
  clearAllCheckpoints: () => void;
  /** P2#7：当前处于「已还原到快照」状态（顶部提示条用） */
  restoredCheckpointId: string | null;
  clearRestoredMarker: () => void;
  handleSetMainSession: (sessionId: string) => void;
  createSceneOpen: boolean;
  setCreateSceneOpen: (value: boolean) => void;
  createSceneLoading: boolean;
  createSceneForm: FormInstance<{ name: string }>;
  renameModalOpen: boolean;
  setRenameModalOpen: (value: boolean) => void;
  renameTitle: string;
  setRenameTitle: (value: string) => void;
  renameSessionId: string | null;
  handleSelectSession: (
    sessionId: string,
    options?: { agentId?: string },
  ) => Promise<number | undefined>;
  handleDeleteSession: (sessionId: string) => Promise<void>;
  handleArchiveSession: (sessionId: string) => Promise<void>;
  handleRenameStart: (sessionId: string, title: string) => void;
  handleRenameSubmit: () => Promise<void>;
  ensureAgentMainSession: (
    nextWorkspaceId?: string,
    nextAgentId?: string,
    options?: { isCurrent?: () => boolean; selectSession?: boolean },
  ) => Promise<string | undefined>;
  sendMessage: (text: string, options?: ChatSendOptions) => Promise<void>;
  submitInteraction: (text: string, options?: ChatSendOptions) => Promise<void>;
  enqueueInteraction: (
    text: string,
    options?: ChatSendOptions,
  ) => string | null;
  updateQueuedInteraction: (id: string, text: string) => void;
  deleteQueuedInteraction: (id: string) => void;
  sendQueuedInteractionNow: (id: string) => Promise<void>;
  steerQueuedInteraction: (id: string) => Promise<void>;
  /** P1#6：本地待发队列内重排（拖拽） */
  reorderQueuedInteraction: (fromId: string, toId: string) => void;
  /** P1#6：取消全部 — 中止当前请求并清空本地待发队列 */
  stopQueue: () => void;
  handleKeyDown: (event: KeyboardEvent<HTMLTextAreaElement>) => void;
  loadMoreMessages: () => Promise<void>;
  resetConversation: (
    nextWorkspaceId?: string,
    nextAgentId?: string,
  ) => Promise<string | undefined>;
  handleExport: () => void;
  onDeleteTurn: (turnId: string) => void;
  onToggleReasoning: (turnId: string, blockId: string) => void;
  messageListRef: RefObject<HTMLDivElement | null>;
  listEndRef: RefObject<HTMLDivElement | null>;
  abortRef: RefObject<AbortController | null>;
  formatTime: (timestamp: number) => string;
  getStepTone: (status?: string) => 'executing' | 'success' | 'error';
  assistantStatusLabel: Record<AssistantStatus, string>;
  getAgentName: (agent: WorkspaceAgentDto) => string;
  stringToColor: (value: string) => string;
  wsOpts: { value: string; label: string; disabled: boolean }[];
  agOpts: { value: string; label: ReactNode; disabled: boolean }[];
  creatingSession: boolean;
  viewportScrollIntent: ScrollIntent;
  clearViewportScrollIntent: () => void;
}
