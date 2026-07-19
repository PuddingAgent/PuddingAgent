import type { SubAgentCardMap, SubAgentCardStatus } from '../types';

export type SubAgentRunStatus =
  | 'created'
  | 'running'
  | 'completed'
  | 'failed'
  | 'cancelled'
  | 'timed_out'
  | 'interrupted';

export interface SubAgentToolRun {
  toolCallId: string;
  toolName: string;
  status: 'running' | 'completed' | 'failed';
  round?: number;
  startedAt: number;
  completedAt?: number;
  durationMs?: number;
  outputLength?: number;
  error?: string;
}

export interface SubAgentRunView {
  runId: string;
  invocationId?: string;
  batchId?: string;
  subSessionId: string;
  parentSessionId?: string;
  parentTurnId?: string;
  parentRunId?: string;
  parentToolCallId?: string;
  originToolId?: string;
  role?: string;
  templateId?: string;
  providerId?: string;
  profileId?: string;
  modelId?: string;
  taskSummary: string;
  status: SubAgentRunStatus;
  phase:
    | 'created'
    | 'starting'
    | 'context'
    | 'round'
    | 'llm'
    | 'tool'
    | 'completed';
  currentRound: number;
  maxRounds?: number;
  timeoutSeconds?: number;
  startedAt: number;
  completedAt?: number;
  lastActivityAt: number;
  llmDurationMs: number;
  toolDurationMs: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  cacheHitTokens: number;
  cacheMissTokens: number;
  tools: SubAgentToolRun[];
  /** Conversation event ids already folded into this run. Prevents bootstrap/replay/live overlap from double-counting usage. */
  appliedEventIds: string[];
  output?: string;
  error?: string;
}

export type SubAgentRunMap = Record<string, SubAgentRunView>;

export interface SubAgentConversationEvent {
  type: string;
  occurredAt?: unknown;
  recordedAt?: unknown;
  timestamp?: unknown;
  payload?: unknown;
  [key: string]: unknown;
}

const read = (
  event: SubAgentConversationEvent,
  ...keys: string[]
): unknown => {
  const payload =
    event.payload && typeof event.payload === 'object' && !Array.isArray(event.payload)
      ? (event.payload as Record<string, unknown>)
      : undefined;
  for (const key of keys) {
    if (event[key] !== undefined) return event[key];
    if (payload?.[key] !== undefined) return payload[key];
  }
  return undefined;
};

const text = (
  event: SubAgentConversationEvent,
  ...keys: string[]
): string | undefined => {
  const value = read(event, ...keys);
  return typeof value === 'string' && value.trim() ? value : undefined;
};

const number = (
  event: SubAgentConversationEvent,
  ...keys: string[]
): number | undefined => {
  const value = read(event, ...keys);
  const parsed = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(parsed) ? parsed : undefined;
};

const timestamp = (event: SubAgentConversationEvent): number => {
  const raw =
    event.occurredAt ??
    event.recordedAt ??
    event.timestamp ??
    read(event, 'occurred_at', 'recorded_at', 'timestamp');
  if (typeof raw === 'number' && Number.isFinite(raw))
    return raw > 10_000_000_000 ? raw : raw * 1000;
  if (typeof raw === 'string') {
    const parsed = Date.parse(raw);
    if (Number.isFinite(parsed)) return parsed;
  }
  return Date.now();
};

const canonicalSubAgentEventPrefixes = [
  'subagent.run.',
  'subagent.round.',
  'subagent.llm.',
  'subagent.tool.',
];

const isCanonicalSubAgentEvent = (type: string): boolean =>
  canonicalSubAgentEventPrefixes.some((prefix) => type.startsWith(prefix));

const isTerminalType = (type: string): boolean =>
  type === 'subagent.run.completed' ||
  type === 'subagent.run.failed' ||
  type === 'subagent.run.cancelled' ||
  type === 'subagent.run.timed_out' ||
  type === 'subagent.run.interrupted';

const terminalStatus = (
  event: SubAgentConversationEvent,
): SubAgentRunStatus => {
  switch (event.type) {
    case 'subagent.run.completed':
      return 'completed';
    case 'subagent.run.cancelled':
      return 'cancelled';
    case 'subagent.run.timed_out':
      return 'timed_out';
    case 'subagent.run.interrupted':
      return 'interrupted';
    default:
      return 'failed';
  }
};

const createRun = (
  event: SubAgentConversationEvent,
  runId: string,
  at: number,
): SubAgentRunView => ({
  runId,
  invocationId: text(event, 'invocation_id', 'invocationId'),
  batchId: text(event, 'batch_id', 'batchId'),
  subSessionId:
    text(event, 'sub_agent_id', 'subAgentId') ?? runId,
  parentSessionId: text(event, 'parent_session_id', 'parentSessionId'),
  parentTurnId: text(event, 'parent_turn_id', 'parentTurnId', 'turnId'),
  parentRunId: text(event, 'parent_run_id', 'parentRunId'),
  parentToolCallId: text(
    event,
    'parent_tool_call_id',
    'parentToolCallId',
  ),
  originToolId: text(event, 'origin_tool_id', 'originToolId'),
  role: text(event, 'role'),
  templateId: text(event, 'template', 'template_id', 'templateId'),
  providerId: text(event, 'provider_id', 'providerId'),
  profileId: text(event, 'profile_id', 'profileId'),
  modelId: text(event, 'model_id', 'modelId', 'model'),
  taskSummary:
    text(event, 'task_summary', 'taskSummary', 'task') ?? '子代理任务',
  status: event.type === 'subagent.run.created' ? 'created' : 'running',
  phase: event.type === 'subagent.run.created' ? 'created' : 'starting',
  currentRound: number(event, 'round') ?? 0,
  maxRounds: number(event, 'max_rounds', 'maxRounds'),
  timeoutSeconds: number(event, 'timeout_seconds', 'timeoutSeconds'),
  startedAt: at,
  lastActivityAt: at,
  llmDurationMs: 0,
  toolDurationMs: 0,
  promptTokens: 0,
  completionTokens: 0,
  totalTokens: 0,
  cacheHitTokens: 0,
  cacheMissTokens: 0,
  tools: [],
  appliedEventIds: [],
});

const mergeIdentity = (
  current: SubAgentRunView,
  event: SubAgentConversationEvent,
): SubAgentRunView => ({
  ...current,
  invocationId:
    text(event, 'invocation_id', 'invocationId') ?? current.invocationId,
  batchId: text(event, 'batch_id', 'batchId') ?? current.batchId,
  subSessionId:
    text(event, 'sub_agent_id', 'subAgentId') ?? current.subSessionId,
  parentSessionId:
    text(event, 'parent_session_id', 'parentSessionId') ??
    current.parentSessionId,
  parentTurnId:
    text(event, 'parent_turn_id', 'parentTurnId', 'turnId') ??
    current.parentTurnId,
  parentRunId:
    text(event, 'parent_run_id', 'parentRunId') ?? current.parentRunId,
  parentToolCallId:
    text(event, 'parent_tool_call_id', 'parentToolCallId') ??
    current.parentToolCallId,
  originToolId:
    text(event, 'origin_tool_id', 'originToolId') ?? current.originToolId,
  role: text(event, 'role') ?? current.role,
  templateId:
    text(event, 'template', 'template_id', 'templateId') ??
    current.templateId,
  providerId:
    text(event, 'provider_id', 'providerId') ?? current.providerId,
  profileId: text(event, 'profile_id', 'profileId') ?? current.profileId,
  modelId:
    text(event, 'model_id', 'modelId', 'model') ?? current.modelId,
  taskSummary:
    text(event, 'task_summary', 'taskSummary', 'task') ??
    current.taskSummary,
  maxRounds:
    number(event, 'max_rounds', 'maxRounds') ?? current.maxRounds,
  timeoutSeconds:
    number(event, 'timeout_seconds', 'timeoutSeconds') ??
    current.timeoutSeconds,
});

export function reduceSubAgentRunEvent(
  state: SubAgentRunMap,
  event: SubAgentConversationEvent,
): SubAgentRunMap {
  // ADR-060 events are the only UI authority. Older subagent.spawned/delta/
  // completed frames did not carry a stable runId or a reliable terminal
  // state, so replaying them can resurrect historical runs as permanently
  // active after a reconnect.
  if (!isCanonicalSubAgentEvent(event.type)) return state;
  const explicitRunId = text(event, 'run_id', 'runId');
  if (!explicitRunId) return state;
  const eventId = text(event, 'event_id', 'eventId');
  const subSessionId = text(event, 'sub_agent_id', 'subAgentId', 'id');
  const existingEntry = subSessionId
    ? Object.entries(state).find(([, run]) => run.subSessionId === subSessionId)
    : undefined;
  const runId = explicitRunId;
  const current = state[runId] ?? existingEntry?.[1];
  if (eventId && current?.appliedEventIds.includes(eventId)) return state;

  const at = timestamp(event);
  let next = mergeIdentity(
    current ?? createRun(event, runId, at),
    event,
  );
  if (next.runId !== runId) next = { ...next, runId };
  next = { ...next, lastActivityAt: at };

  switch (event.type) {
    case 'subagent.run.started':
      next = { ...next, status: 'running', phase: 'starting' };
      break;
    case 'subagent.run.context_assembled':
      next = { ...next, status: 'running', phase: 'context' };
      break;
    case 'subagent.round.started':
    case 'subagent.round.completed':
      next = {
        ...next,
        status: 'running',
        phase: 'round',
        currentRound: number(event, 'round') ?? next.currentRound,
      };
      break;
    case 'subagent.llm.started':
      next = {
        ...next,
        status: 'running',
        phase: 'llm',
        currentRound: number(event, 'round') ?? next.currentRound,
      };
      break;
    case 'subagent.llm.completed':
      next = {
        ...next,
        status: 'running',
        phase: 'round',
        currentRound: number(event, 'round') ?? next.currentRound,
        llmDurationMs:
          next.llmDurationMs + (number(event, 'duration_ms', 'durationMs') ?? 0),
        promptTokens:
          next.promptTokens + (number(event, 'prompt_tokens', 'promptTokens') ?? 0),
        completionTokens:
          next.completionTokens +
          (number(event, 'completion_tokens', 'completionTokens') ?? 0),
        totalTokens:
          next.totalTokens + (number(event, 'total_tokens', 'totalTokens') ?? 0),
        cacheHitTokens:
          next.cacheHitTokens +
          (number(event, 'cache_hit_tokens', 'cacheHitTokens') ?? 0),
        cacheMissTokens:
          next.cacheMissTokens +
          (number(event, 'cache_miss_tokens', 'cacheMissTokens') ?? 0),
      };
      break;
    case 'subagent.llm.failed':
      next = {
        ...next,
        status: 'running',
        phase: 'llm',
        error: text(event, 'error', 'error_message', 'errorMessage'),
        llmDurationMs:
          next.llmDurationMs + (number(event, 'duration_ms', 'durationMs') ?? 0),
      };
      break;
    case 'subagent.tool.started': {
      const toolCallId =
        text(event, 'tool_call_id', 'toolCallId') ??
        `${runId}:tool:${next.tools.length + 1}`;
      const toolName = text(event, 'tool_name', 'toolName') ?? 'unknown';
      const runningTool: SubAgentToolRun = {
        toolCallId,
        toolName,
        status: 'running',
        round: number(event, 'round'),
        startedAt: at,
      };
      next = {
        ...next,
        status: 'running',
        phase: 'tool',
        currentRound: number(event, 'round') ?? next.currentRound,
        tools: [
          ...next.tools.filter((tool) => tool.toolCallId !== toolCallId),
          runningTool,
        ].slice(-50),
      };
      break;
    }
    case 'subagent.tool.completed':
    case 'subagent.tool.failed': {
      const toolCallId =
        text(event, 'tool_call_id', 'toolCallId') ??
        `${runId}:tool:${next.tools.length + 1}`;
      const existing = next.tools.find(
        (tool) => tool.toolCallId === toolCallId,
      );
      const durationMs = number(event, 'duration_ms', 'durationMs') ?? 0;
      const failed = event.type === 'subagent.tool.failed';
      const tool: SubAgentToolRun = {
        toolCallId,
        toolName:
          text(event, 'tool_name', 'toolName') ??
          existing?.toolName ??
          'unknown',
        status: failed ? 'failed' : 'completed',
        round: number(event, 'round') ?? existing?.round,
        startedAt: existing?.startedAt ?? Math.max(0, at - durationMs),
        completedAt: at,
        durationMs,
        outputLength: number(event, 'output_length', 'outputLength'),
        error: text(event, 'error', 'error_message', 'errorMessage'),
      };
      next = {
        ...next,
        status: 'running',
        phase: 'round',
        toolDurationMs: next.toolDurationMs + durationMs,
        error: failed ? tool.error ?? next.error : next.error,
        tools: [
          ...next.tools.filter((item) => item.toolCallId !== toolCallId),
          tool,
        ].slice(-50),
      };
      break;
    }
    default:
      if (isTerminalType(event.type)) {
        const status = terminalStatus(event);
        next = {
          ...next,
          status,
          phase: 'completed',
          completedAt: at,
          output:
            text(event, 'reply', 'output', 'result_summary') ?? next.output,
          error:
            text(event, 'error', 'error_message', 'errorMessage') ??
            next.error,
          currentRound:
            number(event, 'total_rounds', 'totalRounds') ??
            next.currentRound,
        };
      }
      break;
  }

  if (eventId) {
    next = {
      ...next,
      appliedEventIds: [...next.appliedEventIds, eventId].slice(-5000),
    };
  }

  const output = { ...state, [runId]: next };
  if (existingEntry && existingEntry[0] !== runId)
    delete output[existingEntry[0]];
  return output;
}

const toCardStatus = (status: SubAgentRunStatus): SubAgentCardStatus =>
  status === 'created' ? 'spawning' : status;

export function projectSubAgentRunsToCards(
  runs: SubAgentRunMap,
): SubAgentCardMap {
  const cards: SubAgentCardMap = {};
  for (const run of Object.values(runs)) {
    const activeTool = [...run.tools]
      .reverse()
      .find((tool) => tool.status === 'running');
    const lastTool = run.tools[run.tools.length - 1];
    cards[`sa-${run.runId}`] = {
      turnId: `sa-${run.runId}`,
      runId: run.runId,
      invocationId: run.invocationId,
      batchId: run.batchId,
      subSessionId: run.subSessionId,
      parentSessionId: run.parentSessionId,
      templateId: run.templateId,
      modelId: run.modelId,
      providerId: run.providerId,
      profileId: run.profileId,
      originToolId: run.originToolId,
      role: run.role,
      taskSummary: run.taskSummary,
      status: toCardStatus(run.status),
      phase: run.phase,
      currentRound: run.currentRound,
      maxRounds: run.maxRounds,
      timeoutSeconds: run.timeoutSeconds,
      spawnedAt: run.startedAt,
      completedAt: run.completedAt,
      lastActivityAt: run.lastActivityAt,
      promptTokens: run.promptTokens,
      completionTokens: run.completionTokens,
      totalTokens: run.totalTokens,
      llmDurationMs: run.llmDurationMs,
      toolDurationMs: run.toolDurationMs,
      toolCount: run.tools.length,
      failedToolCount: run.tools.filter((tool) => tool.status === 'failed')
        .length,
      activeToolName: activeTool?.toolName,
      lastToolName: lastTool?.toolName,
      output: run.output,
      success: run.status === 'completed',
      error: run.error,
    };
  }
  return cards;
}
