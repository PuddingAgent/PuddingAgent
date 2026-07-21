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
  source?: 'backend_message_queue' | 'steering';
  metadata?: Record<string, string>;
  steeringId?: string;
  submittedAt?: number;
  injectedAt?: number;
  injectedRound?: number;
  injectionLatencyMs?: number;
  error?: string;
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
