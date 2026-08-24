// ── MessageList：消息列表容器（虚拟滚动）───────────────────────────────
import {
  ArrowDownOutlined,
  VerticalAlignBottomOutlined,
} from '@ant-design/icons';
import { Alert, Badge, Button, Skeleton, Spin, Tooltip } from 'antd';
import React, {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type { WorkspaceAgentDto } from '@/services/platform/api';
import {
  decideSessionApproval,
  decideSessionPlan,
  type SessionApprovalDecision,
  type SessionPlanDecision,
} from '@/services/platform/api';
import { getPerfEvents } from '@/utils/perfEventRuntime';
import type {
  AgentConversationView,
  ApprovalCardData,
  ConversationMessageView,
  PlanStepData,
  ProcessSummaryItem,
} from '../client/types';
import { useChatStyles } from '../styles';
import { ChatMessageStyleProvider } from '../styles/messageStyleContext';
import type {
  AssistantStatus,
  ChatQuotedMessage,
  ChatTurn,
  MessageStatus,
  ParentDelegationActivity,
  TimelineItem,
} from '../types';
import { inboundDebug } from '../utils/inboundDebug';
import { buildVirtualMessageItems } from '../viewport/messageProjection';
import type { ScrollIntent, VirtualMessageItem } from '../viewport/types';
import { useMessageViewportRuntime } from '../viewport/useMessageViewportRuntime';
import ApprovalCard from './ApprovalCard';
import EditablePlanCard from './EditablePlanCard';
import type { ChatEmptyStateMode } from './ChatEmptyState';
import ChatEmptyState from './ChatEmptyState';
import FocusViewToggle from './FocusViewToggle';
import MessageRow from './MessageRow';
import PinnedMessageButton from './PinnedMessageButton';
import type { TranscriptMode } from './TranscriptModeSwitch';
import type { ExecutionFlowProjection } from '../projections/executionFlowProjector';

interface MessageListProps {
  turns: ChatTurn[];
  sessionId?: string | null;
  /** 当前工作空间 ID，用于用户视觉消息的图片加载 */
  workspaceId?: string;
  agentId: string | undefined;
  selectedAgent?: WorkspaceAgentDto;
  error: string | null;
  historyLoading: boolean;
  loadingMore: boolean;
  hasMoreMessages: boolean;
  onClearError: () => void;
  onLoadMore: () => void;
  formatTime: (ts: number) => string;
  onDeleteTurn: (turnId: string) => void;
  onContextMenu: (
    e: React.MouseEvent,
    turnId: string,
    role: 'user' | 'assistant',
    content: string,
  ) => void;
  onRerunTurn?: (turnId: string) => void;
  onPinTurn?: (turnId: string) => void;
  onPinnedQuote?: (quoteText: string) => void;
  messageListRef: React.RefObject<HTMLDivElement | null>;
  listEndRef: React.RefObject<HTMLDivElement | null>;
  conversationView?: AgentConversationView | null;
  /** 当前登录用户信息 */
  currentUser?: { name?: string; avatar?: string };
  viewportScrollIntent?: ScrollIntent;
  onViewportScrollIntentHandled?: () => void;
  /** 主代理对当前委派的有界摘要；子代理内部过程仍只在托盘坞展示。 */
  parentDelegationActivity?: ParentDelegationActivity;
    /** P0#2：转录视图分级（normal | verbose | summary） */
  transcriptMode?: TranscriptMode;
  onTranscriptModeChange?: (mode: TranscriptMode) => void;
  /** P2#9：用户在审批卡点击「拒绝」时通知（进入 Recently denied 面板）。 */
  onApprovalDenied?: (card: ApprovalCardData) => void;
  /** CU-11 Phase 2: per-turn 投影选择器（灰度开启时按 turnId 取 canonical 投影）。 */
  getTurnProjection?: (turnId: string) => ExecutionFlowProjection | undefined;
  /** P2#8：Focus view 单行折叠模式 */
  focusView?: boolean;
  onFocusViewChange?: (value: boolean) => void;
}

const toTimestamp = (value: string) => {
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : 0;
};

const toUserMessageStatus = (
  status: ConversationMessageView['status'],
): MessageStatus => {
  if (status === 'sending') return 'sending';
  if (status === 'failed') return 'error';
  return 'success';
};

const toAssistantStatus = (
  status: ConversationMessageView['status'],
): AssistantStatus => {
  switch (status) {
    case 'streaming':
      return 'streaming';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancelled';
    case 'sending':
      return 'thinking';
    default:
      return 'success';
  }
};

type ActiveRunView = NonNullable<AgentConversationView['activeRun']>;

const toActiveRunAssistantStatus = (
  status: ActiveRunView['status'],
): AssistantStatus => {
  switch (status) {
    case 'queued':
    case 'waiting':
      return 'thinking';
    case 'running':
      return 'streaming';
    case 'failed':
      return 'error';
    case 'cancelled':
      return 'cancelled';
    default:
      return 'success';
  }
};

const isActiveRunStreaming = (status: ActiveRunView['status']): boolean =>
  status === 'queued' || status === 'running' || status === 'waiting';

const isSubAgentProcessItem = (kind: string): boolean =>
  kind.startsWith('subagent.') || kind.startsWith('subagent_');

const toTimelineItems = (items: ProcessSummaryItem[]): TimelineItem[] =>
  items
    // 'text' 是正文增量（历史明细接口提供；由投影分段/正文气泡承载），
    // 'subagent.*' 是旧会话级事件 kind——都不进入路径 A 时间线。
    .filter(
      (item) =>
        !isSubAgentProcessItem(item.kind) && item.kind !== 'text',
    )
    .map((item) => ({
      id: item.id,
      toolCallId: item.toolCallId ?? undefined,
      type:
        item.kind === 'tool_call' ||
        item.kind === 'tool_result' ||
        item.kind === 'thinking'
          ? item.kind
          : item.kind === 'delegation'
            ? item.status === 'running'
              ? 'subagent_spawned'
              : 'subagent_completed'
            : 'subconscious_step',
      text: item.text,
      status:
        item.kind === 'delegation' && item.status !== 'running'
          ? item.status === 'success'
            ? 'completed'
            : item.status === 'error'
              ? 'failed'
              : 'cancelled'
          : item.status,
      // DelegationRow 以 name 作为委派分组键：delegationRunId（sub_agent_id）
      // 在 created/terminal 两类事件中一致，优先于展示名（template）。
      // 终态项的回复摘要走 output（buildDelegationNodesFromProcessItems 语义）。
      name:
        item.kind === 'delegation'
          ? (item.delegationRunId ?? item.name ?? undefined)
          : (item.name ?? undefined),
      arguments: item.arguments ?? undefined,
      output:
        item.kind === 'delegation' && item.status !== 'running'
          ? item.text
          : (item.output ?? undefined),
      exitCode: item.exitCode ?? undefined,
      message: item.message ?? undefined,
      timestamp: toTimestamp(item.timestamp),
      collapsed: true,
    }));

const readPuddingMessage = (
  source: string,
): Record<string, unknown> | undefined => {
  try {
    const parsed = JSON.parse(source) as Record<string, unknown>;
    return parsed?.schema === 'pudding-message' ? parsed : undefined;
  } catch {
    // Message Fabric existed before the JSON envelope and some persisted
    // cross-agent messages are still stored as XML. Normalize them here so the
    // chat renderer treats both protocol shapes as the same domain message.
    if (
      !source.trimStart().startsWith('<pudding-message') ||
      typeof DOMParser === 'undefined'
    ) {
      return undefined;
    }
    try {
      const doc = new DOMParser().parseFromString(source, 'application/xml');
      if (doc.querySelector('parsererror')) return undefined;
      const root = doc.documentElement;
      if (root?.nodeName !== 'pudding-message') return undefined;
      const from = root.querySelector('from');
      const context = root.querySelector('context');
      return {
        schema: 'pudding-message',
        message_type:
          root.querySelector('message-type')?.textContent ?? undefined,
        from: {
          kind: from?.getAttribute('kind') ?? undefined,
          id: from?.getAttribute('id') ?? undefined,
          display_name: from?.getAttribute('display-name') ?? undefined,
        },
        context: {
          text: context?.textContent ?? '',
        },
      };
    } catch {
      return undefined;
    }
  }
};

const parseAgentQuotedMessage = (
  message: ConversationMessageView,
  timestamp: number,
): ChatQuotedMessage | undefined => {
  if (message.sourceKind !== 'agent') {
    inboundDebug.log(
      'parse',
      'SKIP sourceKind=',
      message.sourceKind,
      'messageType=',
      message.messageType,
    );
    return undefined;
  }

  inboundDebug.log(
    'parse',
    'CHECK sourceKind=',
    message.sourceKind,
    'messageType=',
    message.messageType,
    'content[0:120]=',
    message.content?.substring(0, 120),
  );

  const parsed = readPuddingMessage(message.content);
  if (!parsed) {
    inboundDebug.warn(
      'parse',
      'NOT pudding-message JSON, content[0:80]=',
      message.content?.substring(0, 80),
    );
    return undefined;
  }

  const from = parsed.from as
    | { kind?: string; id?: string; display_name?: string }
    | undefined;
  inboundDebug.log('parse', 'parsed from=', from);

  if (from?.kind !== 'agent') {
    inboundDebug.log('parse', 'SKIP from.kind=', from?.kind);
    return undefined;
  }

  const context = parsed.context as { text?: string } | undefined;
  const result: ChatQuotedMessage = {
    sourceId: from.id || message.sourceId || 'agent',
    sourceName: from.display_name || message.sourceName || 'Agent',
    sourceKind: 'agent',
    messageType:
      typeof parsed.message_type === 'string' ? parsed.message_type : undefined,
    content: context?.text ?? message.content,
    createdAt: timestamp,
  };
  inboundDebug.log('parse', 'MATCHED result=', result);
  return result;
};

/** 检测是否为系统心跳消息（sourceKind='system' + sourceId='heartbeat'） */
const isHeartbeatMessage = (message: ConversationMessageView): boolean =>
  message.role === 'system' &&
  message.sourceKind === 'system' &&
  message.sourceId === 'heartbeat';

const createProjectedTurn = (
  message: ConversationMessageView,
  agentName: string,
): ChatTurn => {
  const timestamp = toTimestamp(message.createdAt);
  const turnId = message.turnId || message.runId || message.messageId;
  const isUser = message.role === 'user';
  const isHeartbeat = isHeartbeatMessage(message);
  const quotedMessage = parseAgentQuotedMessage(message, timestamp);
  const isInboundAgentMessage = Boolean(quotedMessage);
  inboundDebug.log(
    'project',
    'role=',
    message.role,
    'sourceKind=',
    message.sourceKind,
    'sourceName=',
    message.sourceName,
    'isInbound=',
    isInboundAgentMessage,
    'isHeartbeat=',
    isHeartbeat,
  );
  const sourceName = message.sourceName || agentName;

  return {
    turnId,
    source: {
      sourceId: message.sourceId || 'agent',
      sourceType: isHeartbeat
        ? 'system_command'
        : isInboundAgentMessage
          ? 'agent'
          : 'agent',
      displayName: isHeartbeat ? '系统心跳' : sourceName,
      avatarEmoji: isHeartbeat ? '💓' : '🤖',
      avatarColor: isHeartbeat ? '#1677ff' : '#7c3aed',
    },
    userMessage: {
      id: isUser ? message.messageId : `${turnId}:placeholder-user`,
      text: isUser && !isInboundAgentMessage ? message.content : '',
      timestamp,
      status: isUser ? toUserMessageStatus(message.status) : 'success',
      metadata: isUser ? message.metadata : undefined,
    },
    assistant: {
      id: isUser ? `${turnId}:placeholder-assistant` : message.messageId,
      status:
        isUser || isInboundAgentMessage || isHeartbeat
          ? 'success'
          : toAssistantStatus(message.status),
      timelineItems:
        isUser || isInboundAgentMessage || isHeartbeat
          ? []
          : toTimelineItems(message.processItems),
      processSummary:
        isUser || isInboundAgentMessage || isHeartbeat
          ? undefined
          : (message.processSummary ?? undefined),
      processMessageId:
        isUser || isInboundAgentMessage || isHeartbeat
          ? undefined
          : message.messageId,
      answerMarkdown:
        (isUser || isInboundAgentMessage) && !isHeartbeat
          ? ''
          : message.content,
      isStreaming:
        !isUser &&
        !isInboundAgentMessage &&
        !isHeartbeat &&
        message.status === 'streaming',
      renderMode: isHeartbeat
        ? ('heartbeat' as const)
        : isInboundAgentMessage
          ? ('inbound' as const)
          : 'structured',
      quotedMessage,
      approvalCard: message.approvalCard ?? undefined,
      planCard: message.planCard ?? undefined,
    },
  };
};

const createActiveRunTurn = (
  activeRun: ActiveRunView,
  agentName: string,
  answerMarkdown?: string,
): ChatTurn => {
  const timestamp = toTimestamp(activeRun.startedAt || activeRun.updatedAt);
  return {
    turnId: activeRun.runId,
    source: {
      sourceId: activeRun.agentId || 'agent',
      sourceType: 'agent',
      displayName: agentName,
      avatarEmoji: '🤖',
      avatarColor: '#7c3aed',
    },
    userMessage: {
      id: `${activeRun.runId}:active-user-placeholder`,
      text: '',
      timestamp,
      status: 'success',
    },
    assistant: {
      id: `${activeRun.runId}:active-assistant`,
      status: toActiveRunAssistantStatus(activeRun.status),
      timelineItems: toTimelineItems(
        activeRun.outputSnapshot.processItems ?? [],
      ),
      processSummary: activeRun.outputSnapshot.processSummary ?? undefined,
      answerMarkdown: answerMarkdown ?? activeRun.outputSnapshot.markdown,
      isStreaming: isActiveRunStreaming(activeRun.status),
      renderMode: 'structured',
    },
  };
};

/** @internal 导出仅供定向单测消费（同 messageId 刷新不追加第二张卡片）。 */
export const mergeProjectedMessageIntoTurns = (
  turns: ChatTurn[],
  message: ConversationMessageView,
  agentName: string,
) => {
  const projected = createProjectedTurn(message, agentName);
  if (message.role !== 'agent' || !message.runId) {
    turns.push(projected);
    return;
  }

  const previous = turns[turns.length - 1];
  if (!previous || previous.turnId !== projected.turnId) {
    turns.push(projected);
    return;
  }
  // 同一 agent 消息（messageId 稳定）经投影刷新重复到达时原地更新；
  // 追加为新 turn 会让同一逻辑回复裂成「轨迹卡 + 正文卡」两张卡片。
  if (previous.assistant.id === projected.assistant.id) {
    previous.assistant = {
      ...projected.assistant,
      quotedMessage:
        previous.assistant.quotedMessage ?? projected.assistant.quotedMessage,
    };
    previous.source = projected.source;
    return;
  }
  if (previous.assistant.answerMarkdown) {
    turns.push(projected);
    return;
  }

  previous.assistant = {
    ...projected.assistant,
    quotedMessage:
      previous.assistant.quotedMessage ?? projected.assistant.quotedMessage,
  };
  previous.source = projected.source;
};

const buildProjectedTurns = (
  conversationView: AgentConversationView | null | undefined,
  agentName: string,
): ChatTurn[] => {
  if (!conversationView?.messages.length) return [];
  const projectedTurns: ChatTurn[] = [];
  for (const message of conversationView.messages) {
    // 跳过非心跳的系统消息（心跳消息允许通过以渲染为 heartbeat role）
    if (message.role === 'system' && !isHeartbeatMessage(message)) continue;
    mergeProjectedMessageIntoTurns(projectedTurns, message, agentName);
  }
  return projectedTurns;
};

const isPendingLocalTurn = (turn: ChatTurn): boolean =>
  turn.userMessage.status === 'sending' ||
  turn.assistant.isStreaming ||
  turn.assistant.status === 'thinking' ||
  turn.assistant.status === 'executing' ||
  turn.assistant.status === 'streaming';

const SERVER_PROJECTION_CLOCK_SKEW_MS = 1_000;
const hasProjectedUserTurn = (
  projectedTurns: ChatTurn[],
  localTurn: ChatTurn,
): boolean => {
  const localText = localTurn.userMessage.text.trim();
  if (!localText) return false;
  const localTimestamp = localTurn.userMessage.timestamp;
  return projectedTurns.some(
    (turn) =>
      turn.userMessage.id === localTurn.userMessage.id ||
      // 本地 SSE turn 在 POST 确认后 turnId 迁移为服务端 turnId，与投影 user
      // turn 的 turnId（message.turnId/runId/messageId）一致——这是「同一
      // 条回复」的稳定锚点。若只按 text+timestamp 匹配（≤1s 容差），时钟偏差、
      // outbox 延迟 flush 或客户端/服务端 id 体系不同都会失配，导致本地 turn
      // 被追加到投影 turn 之后，同一条回复渲染成多张卡片（用户实证三张）。
      turn.turnId === localTurn.turnId ||
      (turn.userMessage.text.trim() === localText &&
        Math.abs(turn.userMessage.timestamp - localTimestamp) <=
          SERVER_PROJECTION_CLOCK_SKEW_MS),
  );
};

const mergeLocalTurnsAwaitingProjection = (
  projectedTurns: ChatTurn[],
  localTurns: ChatTurn[],
): ChatTurn[] => {
  if (projectedTurns.length === 0) return localTurns;
  let merged = projectedTurns;

  for (const localTurn of localTurns) {
    const projectedUserIndex = merged.findIndex((projectedTurn) =>
      hasProjectedUserTurn([projectedTurn], localTurn),
    );

    if (isPendingLocalTurn(localTurn)) {
      if (projectedUserIndex < 0) {
        merged = [...merged, localTurn];
        continue;
      }

      const projectedTurn = merged[projectedUserIndex];
      const projectedAssistantIsTerminal =
        Boolean(projectedTurn.assistant.answerMarkdown.trim()) &&
        !isPendingLocalTurn(projectedTurn);
      if (!projectedAssistantIsTerminal) {
        // Materializing the user message must not hide the live local assistant
        // state. This matters when a later system command temporarily becomes
        // the newest canonical projection and activeRun is unavailable even
        // though the original Agent Turn (and its subagents) is still running.
        merged = merged.map((candidate, index) =>
          index === projectedUserIndex
            ? {
                ...candidate,
                turnId: localTurn.turnId,
                source: localTurn.source ?? candidate.source,
                // 保留本地 userMessage.id（clientMessageId）：后续
                // mergeActiveRunIntoTurns 靠 userMessage.id ===
                // activeRun.commandClientId 把运行快照合并回本 turn，
                // 若沿用投影的 messageId 会让匹配失败，activeRun 被追加为
                // 第二张 assistant 卡（用户实证同回复多卡）。
                userMessage: {
                  ...candidate.userMessage,
                  id: localTurn.userMessage.id,
                },
                assistant: localTurn.assistant,
              }
            : candidate,
        );
      }
      continue;
    }

    const localAnswer = localTurn.assistant.answerMarkdown.trim();
    if (!localAnswer || projectedUserIndex < 0) continue;

    const terminalAlreadyProjected = merged.some(
      (projectedTurn) =>
        projectedTurn.assistant.answerMarkdown.trim() === localAnswer,
    );
    if (terminalAlreadyProjected) continue;

    // The SSE projection is authoritative for the just-completed local Turn
    // while the canonical conversation read model is still user-only. Overlay
    // the terminal assistant state on the matching projected user Turn instead
    // of hiding it until a refresh materializes the assistant message row.
    merged = merged.map((projectedTurn, index) =>
      index === projectedUserIndex
        ? {
            ...projectedTurn,
            turnId: localTurn.turnId,
            source: localTurn.source ?? projectedTurn.source,
            // 与 pending 分支同理：保持本地 userMessage.id，使 activeRun 的
            // commandClientId 匹配与 viewport 滚动定位（message:user:<id>）
            // 在后续链路中保持一致，避免同一条回复被拆成多张卡片。
            userMessage: {
              ...projectedTurn.userMessage,
              id: localTurn.userMessage.id,
            },
            assistant: localTurn.assistant,
          }
        : projectedTurn,
    );
  }

  const firstProjectedAt = projectedTurns[0]?.userMessage.timestamp;
  if (
    !Number.isFinite(firstProjectedAt) ||
    !localTurns.some((localTurn) =>
      hasProjectedUserTurn(projectedTurns, localTurn),
    )
  ) {
    return merged;
  }
  const olderHistory = localTurns.filter(
    (localTurn) =>
      !isPendingLocalTurn(localTurn) &&
      localTurn.userMessage.timestamp < firstProjectedAt &&
      !hasProjectedUserTurn(merged, localTurn),
  );
  return olderHistory.length > 0 ? [...olderHistory, ...merged] : merged;
};

const isActiveRunCoveredByLocalTerminal = (
  activeRun: ActiveRunView | null,
  localTurns: ChatTurn[],
): boolean => {
  if (!activeRun?.commandClientId) return false;
  return localTurns.some(
    (turn) =>
      turn.userMessage.id === activeRun.commandClientId &&
      !isPendingLocalTurn(turn) &&
      Boolean(turn.assistant.answerMarkdown.trim()),
  );
};

const ACTIVE_RUN_PENDING_ATTACH_SKEW_MS = 1_000;

const canAttachActiveRunToPendingTurn = (
  activeRun: ActiveRunView,
  pendingTurn: ChatTurn,
): boolean => {
  if (
    activeRun.commandClientId &&
    activeRun.commandClientId === pendingTurn.userMessage.id
  ) {
    return true;
  }
  const activeStartedAt = toTimestamp(
    activeRun.startedAt || activeRun.updatedAt,
  );
  if (activeStartedAt <= 0) return false;
  return (
    activeStartedAt >=
    pendingTurn.userMessage.timestamp - ACTIVE_RUN_PENDING_ATTACH_SKEW_MS
  );
};

const findActiveRunPendingTurnIndex = (
  turns: ChatTurn[],
  activeRun: ActiveRunView,
): number => {
  if (!activeRun.commandClientId) return -1;
  return turns.findIndex(
    (turn) =>
      isPendingLocalTurn(turn) &&
      // 不能要求本地 turn 尚未产出正文：流式过程中本地 SSE 可能已经带
      // 部分/完整 answerMarkdown（与 activeRun 是同一条回复的两份表示），
      // 若因「已有正文」而匹配失败，activeRun 会被当作新 turn 追加，
      // 导致同一条 assistant 回复渲染成多张卡片（用户实证三张）。
      // commandClientId 与 userMessage.id 相等已是「同一发送」的强约束，
      // 正文有无不影响归属判定。
      turn.userMessage.id === activeRun.commandClientId,
  );
};

const timelineInformationScore = (items: TimelineItem[]): number =>
  items.reduce(
    (score, item) =>
      score +
      16 +
      (item.text?.length ?? 0) +
      (item.name?.length ?? 0) +
      (item.arguments?.length ?? 0) +
      (item.output?.length ?? 0) +
      (item.message?.length ?? 0),
    0,
  );

const selectRicherTimelineItems = (
  localItems: TimelineItem[],
  activeItems: TimelineItem[],
): TimelineItem[] => {
  if (activeItems.length === 0) return localItems;
  if (localItems.length === 0) return activeItems;
  return timelineInformationScore(activeItems) >=
    timelineInformationScore(localItems)
    ? activeItems
    : localItems;
};

/**
 * 回答正文合并必须前缀单调：
 *  - 一方是另一方前缀 → 取更长（正常流式推进）；
 *  - 分叉（任一方含重复拼接/脏文本）→ 以服务端快照为准（与刷新后的持久化投影一致）。
 * 旧「取更长」会把直播竞态产生的重复版本保留到刷新，造成同段正文显示两遍。
 */
const selectMonotonicMarkdown = (local: string, active: string): string => {
  if (active.length === 0) return local;
  if (local.length === 0) return active;
  if (active.startsWith(local)) return active;
  if (local.startsWith(active)) return local;
  return active;
};

const isTerminalAssistantStatus = (status: ChatTurn['assistant']['status']): boolean =>
  status === 'success' || status === 'error' || status === 'cancelled';

/**
 * 查找与 activeRun 内容完全一致的已完成（terminal）turn。
 * 历史已完成消息与 activeRun 可能是同一条回复的两份表示：历史投影 turn 的
 * turnId 来自 message.turnId/runId/messageId，而 activeRun.runId 是另一个
 * 稳定 ID，二者不一致时按 turnId 匹配不到；若该 turn 已完成（非 pending），
 * 也无法通过 pending 挂载路径合并。此时按内容判重，把 activeRun 合并到最近
 * 一条内容相同的 terminal turn，避免同一条 assistant 回复渲染成两行。
 * 返回 -1 表示没有可合并的 turn（内容不同或 activeRun 尚无可比内容）。
 */
const findTerminalTurnWithSameMarkdown = (
  turns: ChatTurn[],
  activeAnswer: string,
): number => {
  const trimmed = activeAnswer.trim();
  if (!trimmed) return -1;
  for (let i = turns.length - 1; i >= 0; i--) {
    const turn = turns[i];
    if (
      !isPendingLocalTurn(turn) &&
      turn.assistant.answerMarkdown.trim() === trimmed
    ) {
      return i;
    }
  }
  return -1;
};

/** @internal 导出仅供定向单测消费（终态守卫/同 messageId 原地更新）。 */
export const mergeActiveRunAssistant = (
  local: ChatTurn['assistant'],
  active: ChatTurn['assistant'],
): ChatTurn['assistant'] => {
  // 本地 SSE 已终态时，轮询快照不得把 status/isStreaming 拉回运行态：
  // 快照滞后会让状态条与正文在两个状态间来回翻转（BUG2 双状态来源之一）。
  const localTerminal = isTerminalAssistantStatus(local.status);
  return {
    ...local,
    ...active,
    // Keep the local identity stable so a projection refresh cannot remount the
    // visible bubble while the same Turn is still streaming.
    id: local.id,
    status: localTerminal ? local.status : active.status,
    timelineItems: selectRicherTimelineItems(
      local.timelineItems,
      active.timelineItems,
    ),
    answerMarkdown: selectMonotonicMarkdown(
      local.answerMarkdown,
      active.answerMarkdown,
    ),
    isStreaming: localTerminal
      ? false
      : local.isStreaming || active.isStreaming,
  };
};

const mergeActiveRunIntoTurns = (
  turns: ChatTurn[],
  activeRun: AgentConversationView['activeRun'],
  agentName: string,
  activeRunMarkdown?: string,
): ChatTurn[] => {
  if (!activeRun) return turns;
  const activeTurn = createActiveRunTurn(
    activeRun,
    agentName,
    activeRunMarkdown,
  );
  const existingIndex = turns.findIndex(
    (turn) => turn.turnId === activeRun.runId,
  );
  if (existingIndex >= 0) {
    return turns.map((turn, index) =>
      index === existingIndex
        ? {
            ...turn,
            assistant: mergeActiveRunAssistant(
              turn.assistant,
              activeTurn.assistant,
            ),
            source: activeTurn.source,
          }
        : turn,
    );
  }

  const matchingPendingIndex = findActiveRunPendingTurnIndex(turns, activeRun);
  if (matchingPendingIndex >= 0) {
    return turns.map((turn, index) =>
      index === matchingPendingIndex
        ? {
            ...turn,
            assistant: mergeActiveRunAssistant(
              turn.assistant,
              activeTurn.assistant,
            ),
            source: activeTurn.source,
          }
        : turn,
    );
  }

  const lastTurn = turns[turns.length - 1];
  if (
    lastTurn &&
    isPendingLocalTurn(lastTurn) &&
    !lastTurn.assistant.answerMarkdown.trim() &&
    canAttachActiveRunToPendingTurn(activeRun, lastTurn)
  ) {
    return [
      ...turns.slice(0, -1),
      {
        ...lastTurn,
        assistant: mergeActiveRunAssistant(
          lastTurn.assistant,
          activeTurn.assistant,
        ),
        source: activeTurn.source,
      },
    ];
  }

  // 历史已完成消息与 activeRun 内容一致时，合并到历史 turn 而不是追加，
  // 否则同一条 assistant 回复会被渲染成两条独立消息行（用户实证）。
  // 合并复用 mergeActiveRunAssistant：保留历史 turn 的 assistant.id 以维持
  // 投影稳定，同时带上 activeRun 的实时内容（时间线/内容择优）。
  const sameMarkdownIndex = findTerminalTurnWithSameMarkdown(
    turns,
    activeTurn.assistant.answerMarkdown,
  );
  if (sameMarkdownIndex >= 0) {
    return turns.map((turn, index) =>
      index === sameMarkdownIndex
        ? {
            ...turn,
            assistant: mergeActiveRunAssistant(
              turn.assistant,
              activeTurn.assistant,
            ),
            source: activeTurn.source,
          }
        : turn,
    );
  }

  return [...turns, activeTurn];
};

const MessageList: React.FC<MessageListProps> = ({
  turns,
  sessionId,
  workspaceId,
  agentId,
  selectedAgent,
  error,
  historyLoading,
  loadingMore,
  hasMoreMessages,
  onClearError,
  onLoadMore,
  formatTime,
  onDeleteTurn,
  onContextMenu,
  onRerunTurn,
  onPinTurn,
  onPinnedQuote,
  messageListRef,
  listEndRef,
  conversationView,
  currentUser,
  viewportScrollIntent,
  onViewportScrollIntentHandled,
  parentDelegationActivity,
  transcriptMode = 'normal',
  onTranscriptModeChange,
  focusView = false,
  onFocusViewChange,
  onApprovalDenied,
  getTurnProjection,
}) => {
  const chatStyles = useChatStyles();
  const { styles } = chatStyles;
  const [diagCopied, setDiagCopied] = useState(false);
  const activeRun = conversationView?.activeRun ?? null;
  const activeRunMarkdownCacheRef = useRef<{
    runId: string;
    markdown: string;
  } | null>(null);
  const activeRunMarkdown = useMemo(() => {
    if (!activeRun) return undefined;
    const incoming = activeRun.outputSnapshot.markdown ?? '';
    const cached = activeRunMarkdownCacheRef.current;
    if (cached && cached.runId === activeRun.runId) {
      // 同一次 run 的快照缓存保持前缀单调：流式累积取推进方；
      // 分叉（快照重算/脏数据）以最新快照为准，避免缓存保留重复文本。
      const stable = selectMonotonicMarkdown(cached.markdown, incoming);
      activeRunMarkdownCacheRef.current = {
        runId: activeRun.runId,
        markdown: stable,
      };
      return stable;
    }
    // 新 run：重置缓存
    activeRunMarkdownCacheRef.current = {
      runId: activeRun.runId,
      markdown: incoming,
    };
    return incoming;
  }, [activeRun]);
  const projectedTurns = useMemo(
    () =>
      buildProjectedTurns(conversationView, selectedAgent?.name || 'Pudding'),
    [conversationView, selectedAgent?.name],
  );
  const hasProjectedConversation = Boolean(
    conversationView && (projectedTurns.length > 0 || activeRun),
  );
  const visibleTurns = useMemo(() => {
    if (!hasProjectedConversation) return turns;
    const activeRunForProjection = isActiveRunCoveredByLocalTerminal(
      activeRun,
      turns,
    )
      ? null
      : activeRun;
    const merged = mergeActiveRunIntoTurns(
      mergeLocalTurnsAwaitingProjection(projectedTurns, turns),
      activeRunForProjection,
      selectedAgent?.name || 'Pudding',
      activeRunMarkdown,
    );
    if (merged.length === 0 && turns.length > 0) return turns;
    return merged;
  }, [
    hasProjectedConversation,
    projectedTurns,
    turns,
    activeRun,
    selectedAgent?.name,
    activeRunMarkdown,
  ]);

  const projection = useMemo(
    () =>
      buildVirtualMessageItems({
        turns: visibleTurns,
        agentName: selectedAgent?.name || 'Pudding',
        sessionId,
        hasMoreBefore: hasMoreMessages,
        currentUser,
        focusView,
      }),
    [
      visibleTurns,
      selectedAgent?.name,
      sessionId,
      hasMoreMessages,
      currentUser,
      focusView,
    ],
  );
  const delegationTargetId = useMemo(() => {
    if (!parentDelegationActivity) return undefined;
    if (projection.activeItemId) return projection.activeItemId;
    return [...projection.items]
      .reverse()
      .find((item) => item.kind === 'message' && item.block.role === 'agent')
      ?.id;
  }, [parentDelegationActivity, projection.activeItemId, projection.items]);

  const viewport = useMessageViewportRuntime({
    items: projection.items,
    hasMoreBefore: hasMoreMessages,
    loadingBefore: loadingMore,
    onRequestLoadBefore: () => onLoadMore(),
  });
  const initiallyPositionedSessionRef = useRef<string | null>(null);

  // Opening or refreshing a conversation starts from its newest message.
  // The guard is scoped to the session so history prepends and live projection
  // refreshes do not repeatedly steal the reader's scroll position.
  useLayoutEffect(() => {
    if (historyLoading || !sessionId || projection.items.length === 0) return;
    if (initiallyPositionedSessionRef.current === sessionId) return;
    initiallyPositionedSessionRef.current = sessionId;
    viewport.scrollToBottom({
      behavior: 'auto',
      reason: 'initial-session-load',
    });
  }, [
    historyLoading,
    projection.items.length,
    sessionId,
    viewport.scrollToBottom,
  ]);

  useEffect(() => {
    if (viewportScrollIntent && viewportScrollIntent.type !== 'none') {
      viewport.applyIntent(viewportScrollIntent);
      onViewportScrollIntentHandled?.();
    }
  }, [viewport, viewportScrollIntent, onViewportScrollIntentHandled]);

  // P0#1: 审批决策 → POST /api/sessions/{sessionId}/decide
  const handleDecideApproval = React.useCallback(
    async (
      approvalId: string,
      decision: SessionApprovalDecision,
      reason?: string,
    ) => {
      if (!sessionId) return;
      try {
        await decideSessionApproval(sessionId, {
          approvalId,
          decision,
          reason,
        });
      } catch (error) {
        console.error('[Pudding Chat] approval decision failed', {
          approvalId,
          decision,
          error,
        });
      }
    },
    [sessionId],
  );

  // P1#5: 计划决定 → POST /api/sessions/{sessionId}/plan-decide
  const handleDecidePlan = React.useCallback(
    async (
      planId: string,
      decision: SessionPlanDecision,
      steps: PlanStepData[],
    ) => {
      if (!sessionId) return;
      try {
        await decideSessionPlan(sessionId, {
          planId,
          decision,
          steps: steps.map((step) => ({
            id: step.id,
            title: step.title,
            description: step.description,
          })),
        });
      } catch (error) {
        console.error('[Pudding Chat] plan decision failed', {
          planId,
          decision,
          error,
        });
      }
    },
    [sessionId],
  );

  const renderProjectionItem = (item: VirtualMessageItem) => {
    if (item.kind === 'loader') {
      return (
        <div
          style={{
            textAlign: 'center',
            padding: 8,
            cursor: loadingMore ? 'default' : 'pointer',
            color: 'var(--ant-color-primary)',
          }}
          onClick={loadingMore ? undefined : viewport.requestLoadBefore}
        >
          {loadingMore ? <Spin size="small" /> : '加载更多历史消息'}
        </div>
      );
    }

    // P0-1：跨天日期分隔线（今天/昨天/MM-DD），居中渲染；复用 timeDivider 样式。
    if (item.kind === 'divider') {
      return (
        <div
          className={styles.timeDivider}
          data-testid="chat-date-divider"
          data-divider-date={item.id.replace(/^divider:/, '')}
        >
          {item.label}
        </div>
      );
    }

    const approvalCardData =
      item.kind === 'message' ? item.block.approvalCard : undefined;
    const planCardData =
      item.kind === 'message' ? item.block.planCard : undefined;
    const messageRow = (
      <MessageRow
        block={item.block}
        parentDelegationActivity={
          item.id === delegationTargetId ? parentDelegationActivity : undefined
        }
        sessionId={sessionId}
        workspaceId={workspaceId}
        defaultAvatarUrl={selectedAgent?.avatarUrl}
        formatTime={formatTime}
        onContextMenu={onContextMenu}
        onRerunTurn={onRerunTurn}
        onPinTurn={onPinTurn}
        onDeleteTurn={onDeleteTurn}
        transcriptMode={transcriptMode}
        onTranscriptModeChange={onTranscriptModeChange}
        focusView={focusView}
        getTurnProjection={getTurnProjection}
      />
    );

    if (!approvalCardData && !planCardData) return messageRow;

    return (
      <div style={{ display: 'flex', flexDirection: 'column', width: '100%' }}>
        {messageRow}
                  {approvalCardData && (
            <ApprovalCard
              approvalId={approvalCardData.approvalId}
              toolName={approvalCardData.toolName}
              description={approvalCardData.description}
              riskLevel={approvalCardData.riskLevel}
              arguments={approvalCardData.arguments}
              status={approvalCardData.status}
              decision={approvalCardData.decision}
              reason={approvalCardData.reason}
              requestedAt={approvalCardData.requestedAt}
              expiresAt={approvalCardData.expiresAt}
              onDecide={(decision, reason) => {
                if (decision === 'deny') {
                  onApprovalDenied?.(approvalCardData);
                }
                handleDecideApproval(
                  approvalCardData.approvalId,
                  decision,
                  reason,
                );
              }}
            />
          )}
        {planCardData && (
          <EditablePlanCard
            planId={planCardData.planId}
            summary={planCardData.summary}
            steps={planCardData.steps}
            status={planCardData.status}
            decision={planCardData.decision}
            decidedAt={planCardData.decidedAt}
            requestedAt={planCardData.requestedAt}
            onDecide={(decision, steps) =>
              handleDecidePlan(planCardData.planId, decision, steps)
            }
          />
        )}
      </div>
    );
  };

  return (
    <ChatMessageStyleProvider value={chatStyles}>
      <div className={styles.messageListShell}>
        {projection.items.length > 0 && (
          <div className={styles.focusViewToolbar} data-testid="focus-view-toolbar">
            <FocusViewToggle
              value={Boolean(focusView)}
              onChange={onFocusViewChange ?? (() => undefined)}
            />
          </div>
        )}
        <div
          className={styles.messageList}
          ref={(node) => {
            viewport.parentRef.current = node;
            if (typeof messageListRef === 'object') {
              (
                messageListRef as React.MutableRefObject<HTMLDivElement | null>
              ).current = node;
            }
          }}
          onScroll={viewport.onScroll}
          data-testid="chat-message-list"
        >
        {(() => {
          const emptyStateMode: ChatEmptyStateMode | null = (() => {
            if (historyLoading || projection.items.length > 0 || activeRun)
              return null;
            if (error) return 'error';
            if (!agentId) return 'no-agent';
            return 'ready';
          })();
          return emptyStateMode ? (
            <ChatEmptyState
              mode={emptyStateMode}
              errorText={error ?? undefined}
              onRetry={onClearError}
              onSuggestionClick={(text) => {
                window.dispatchEvent(
                  new CustomEvent('pudding:chat:suggestion', { detail: text }),
                );
              }}
            />
          ) : null;
        })()}
        {historyLoading && turns.length === 0 && (
          <div className={styles.historyLoading}>
            <Skeleton
              active
              avatar
              paragraph={{ rows: 4 }}
              style={{ padding: 16 }}
            />
          </div>
        )}
        {historyLoading && turns.length > 0 && (
          <div className={styles.historyLoading}>
            <Spin />
          </div>
        )}
        {/* 虚拟滚动容器 */}
        {projection.items.length > 0 && (
          <div
            ref={viewport.contentRef}
            data-testid="chat-message-viewport-content"
            data-virtualized={viewport.virtualizationEnabled ? 'true' : 'false'}
            style={
              viewport.virtualizationEnabled
                ? {
                    height: `${viewport.totalSize}px`,
                    width: '100%',
                    position: 'relative',
                  }
                : { width: '100%', position: 'relative' }
            }
          >
            {viewport.virtualizationEnabled
              ? viewport.virtualRows.map((virtualRow) => {
                  const item = projection.items[virtualRow.index];
                  if (!item) return null;
                  return (
                    <div
                      key={virtualRow.key}
                      data-index={virtualRow.index}
                      data-viewport-item-id={item.id}
                      ref={viewport.virtualizer.measureElement}
                      style={{
                        position: 'absolute',
                        top: 0,
                        left: 0,
                        width: '100%',
                        transform: `translateY(${virtualRow.start}px)`,
                      }}
                    >
                      {renderProjectionItem(item)}
                    </div>
                  );
                })
              : projection.items.map((item, index) => (
                  <div
                    key={item.id}
                    data-index={index}
                    data-viewport-item-id={item.id}
                    style={{
                      width: '100%',
                      position: 'relative',
                      display: 'flow-root',
                    }}
                  >
                    {renderProjectionItem(item)}
                  </div>
                ))}
          </div>
        )}
        {error && (
          <Alert
            type="error"
            message={error}
            closable
            onClose={onClearError}
            className={styles.errorAlert}
            action={
              <Button
                size="small"
                type="link"
                onClick={() => {
                  const payload = {
                    timestamp: new Date().toISOString(),
                    userAgent: navigator.userAgent,
                    url: window.location.href,
                    sessionId: sessionId ?? null,
                    agentId: agentId ?? null,
                    turnsCount: turns.length,
                    lastTurnStatus:
                      turns.length > 0
                        ? ((turns[turns.length - 1] as { status?: string })
                            .status ?? null)
                        : null,
                    error,
                    recentPerfEvents: getPerfEvents().slice(-5),
                  };
                  navigator.clipboard
                    .writeText(JSON.stringify(payload, null, 2))
                    .then(() => {
                      setDiagCopied(true);
                      setTimeout(() => setDiagCopied(false), 2000);
                    });
                }}
              >
                {diagCopied ? '✓ 已复制' : '复制诊断信息'}
              </Button>
            }
          />
        )}
        {/* 底部滚动控制（messageViewportControls 锚定 messageListShell 右下角，
            不再使用视口 fixed 内联样式） */}
        {projection.items.length > 0 && (
          <div
            data-testid="chat-bottom-scroll-controls"
            className={styles.messageViewportControls}
          >
            {onPinnedQuote && (
              <PinnedMessageButton
                onQuote={onPinnedQuote}
                className={styles.messageViewportControlButton}
              />
            )}
            <Tooltip
              title={
                viewport.state.followMode === 'pinned'
                  ? '取消贴底跟随'
                  : '开启贴底跟随'
              }
            >
              <Button
                type={
                  viewport.state.followMode === 'pinned' ? 'primary' : 'default'
                }
                icon={<VerticalAlignBottomOutlined />}
                onClick={() =>
                  viewport.setPinnedBottom(
                    viewport.state.followMode !== 'pinned',
                  )
                }
                aria-label={
                  viewport.state.followMode === 'pinned'
                    ? '取消贴底跟随'
                    : '开启贴底跟随'
                }
                className={styles.messageViewportControlButton}
              />
            </Tooltip>
            {viewport.state.showBottomButton && (
              <Tooltip title="回到底部">
                <Badge dot offset={[-3, 3]}>
                  <Button
                    type="default"
                    icon={<ArrowDownOutlined />}
                    onClick={() =>
                      viewport.scrollToBottom({
                        behavior: 'smooth',
                        reason: 'manual-bottom',
                      })
                    }
                    aria-label="回到底部"
                    className={styles.messageViewportControlButton}
                  />
                </Badge>
              </Tooltip>
            )}
          </div>
        )}
          <div ref={listEndRef} />
        </div>
      </div>
    </ChatMessageStyleProvider>
  );
};

export default React.memo(MessageList);
