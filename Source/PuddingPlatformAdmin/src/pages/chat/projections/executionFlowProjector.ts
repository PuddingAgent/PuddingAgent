// ── CU-04: ExecutionFlowProjector（前端纯函数投影，TR-04a）────────────────
// 消息 UI 方案 §4.3「单一数据路径」+ §7.2；split-plan CU-04。
//
// 职责（对齐 CU-04 任务书）：
//  1. 输入冻结 canonical DTO（ExecutionEventDto / ToolExecutionEventDto，
//     见 services/platform/api.ts TR-01 冻结合同），输出有序 ViewModel 节点：
//     reasoning / message / tool / delegation / retry / terminal。
//  2. 按 toolCallId 配对 call/result；parentToolCallId 明确存在时构成调用树。
//  3. 相邻 reasoning delta 按 block/event 合并（复用 MessageProcessSummary 的
//     thinking 合并算法语义，抽成 mergeReasoningBlocks 供复用）。
//  4. result 先于 started 到达（乱序/缺 started）时创建占位调用，started 补全。
//  5. 终态单调：turn 终态（completed/failed/cancelled）落定后，迟到的 progress
//     事件不得把 terminal 降级回 running/thinking/executing；工具节点终态同样
//     不被迟到 started/progress 降级。
//  6. 不生成 eventId / 时间 / 顺序 / 业务终态——全部消费服务端事实
//     （eventId / sequence / occurredAt / type / 终态事件名）。
//
// 纯度约束：无 DOM / Store / 时间源 / 日志副作用。协议错误（缺必需字段）不
// console 输出，而是聚合到返回值 protocolErrors 中由调用方决定上报策略。
import type {
  ExecutionEventDto,
  ToolExecutionEventDto,
  ToolPresentationDto,
} from '@/services/platform/api';
import { sanitizeProcessText } from '../components/processPreview';

// ── 输入事件（冻结 DTO + 各 canonical 事件携带的 payload 字段）──────────────

/** message.thinking_summary.appended —— 推理增量。 */
export interface ReasoningDeltaEvent extends ExecutionEventDto {
  type: 'message.thinking_summary.appended';
  delta?: string;
}

/** message.content.appended —— 回答正文增量。 */
export interface ContentDeltaEvent extends ExecutionEventDto {
  type: 'message.content.appended';
  delta?: string;
}

/** message.completed —— 消息级终态（回答落定）。 */
export interface MessageCompletedEvent extends ExecutionEventDto {
  type: 'message.completed';
  reply?: string;
}

/** message.failed —— 消息级终态（生成失败）。 */
export interface MessageFailedEvent extends ExecutionEventDto {
  type: 'message.failed';
  message?: string;
  errorMessage?: string;
  errorCode?: string;
}

/** tool.call.requested —— 工具调用开始。 */
export interface ToolRequestedEvent extends ToolExecutionEventDto {
  type: 'tool.call.requested';
  name: string;
  arguments?: string;
}

/** tool.call.completed —— 工具调用成功结果。 */
export interface ToolCompletedEvent extends ToolExecutionEventDto {
  type: 'tool.call.completed';
  name: string;
  exitCode?: number;
  output?: string;
  error?: string;
}

/** tool.call.failed —— 工具调用失败结果。 */
export interface ToolFailedEvent extends ToolExecutionEventDto {
  type: 'tool.call.failed';
  name: string;
  exitCode?: number;
  output?: string;
  error?: string;
}

/** subagent.spawned —— 父级委派创建（DelegationRow 事实源）。 */
export interface SubAgentSpawnedEvent extends ExecutionEventDto {
  type: 'subagent.spawned';
  sub_agent_id?: string;
  subAgentId?: string;
  template?: string;
  model?: string;
  task?: string;
}

/** subagent.completed —— 父级委派终态。 */
export interface SubAgentCompletedEvent extends ExecutionEventDto {
  type: 'subagent.completed';
  sub_agent_id?: string;
  subAgentId?: string;
  success?: boolean;
  reply?: string;
  error?: string;
  result_summary?: string;
  resultSummary?: string;
}

/** subconscious_step —— 进度条消息；命中 LLM retry 形态时投影为 retry 节点。 */
export interface SubconsciousStepEvent extends ExecutionEventDto {
  type: 'subconscious_step';
  status?: string;
  message?: string;
}

/** turn.completed —— turn 级终态（成功）。 */
export interface TurnCompletedEvent extends ExecutionEventDto {
  type: 'turn.completed';
  reply?: string;
}

/** turn.failed —— turn 级终态（失败）。 */
export interface TurnFailedEvent extends ExecutionEventDto {
  type: 'turn.failed';
  message?: string;
  errorMessage?: string;
  errorCode?: string;
}

/** turn.cancelled —— turn 级终态（取消）。 */
export interface TurnCancelledEvent extends ExecutionEventDto {
  type: 'turn.cancelled';
  message?: string;
}

/**
 * 投影器输入事件联合（各分支均继承冻结 DTO ExecutionEventDto）。
 * 静态类型限定已知 canonical 事件；运行时未知 type 走 default 分支：
 * 被统计但不会投影为节点（保持纯函数对任意 canonical 输入的宽容）。
 */
export type ExecutionFlowEvent =
  | ReasoningDeltaEvent
  | ContentDeltaEvent
  | MessageCompletedEvent
  | MessageFailedEvent
  | ToolRequestedEvent
  | ToolCompletedEvent
  | ToolFailedEvent
  | SubAgentSpawnedEvent
  | SubAgentCompletedEvent
  | SubconsciousStepEvent
  | TurnCompletedEvent
  | TurnFailedEvent
  | TurnCancelledEvent;

// ── 输出 ViewModel 节点 ────────────────────────────────────────────────────

/** 节点公共事实（全部来自服务端 canonical 信封，不做本地生成）。 */
interface ExecutionFlowNodeBase {
  /** 稳定 key：由首个来源事件的 eventId（或 sequence）确定性派生。 */
  key: string;
  /** 首个来源事件的 canonical eventId（服务端事实）。 */
  firstEventId?: string;
  /** 首个来源事件的 canonical sequence（服务端事实；用于跨节点排序）。 */
  sequence: number;
  /** 首个来源事件的 occurredAt（ISO 字符串透传，不转换不生成）。 */
  occurredAt?: string;
  /** 贡献本节点的全部来源事件 eventId（审计 / CU-06 展开态）。 */
  sourceEventIds: string[];
}

/** reasoning 节点：一个连续推理段（相邻 delta 合并）。 */
export interface ReasoningNode extends ExecutionFlowNodeBase {
  kind: 'reasoning';
  /** 合并后的完整推理文本（保留换行；blocks 无分隔符拼接）。 */
  text: string;
  /** 分块（对齐 MessageProcessSummary 的 900 字符切块阈值）。 */
  blocks: ReasoningBlock[];
  /** 段末（最后一个 delta）事件的 occurredAt（服务端事实；段时长 = last - first）。 */
  lastOccurredAt?: string;
}

/** 单个推理块（UI 折叠摘要 / 展开审计共用）。 */
export interface ReasoningBlock {
  /** 由块内首个来源事件 eventId（或 sequence）确定性派生。 */
  id: string;
  text: string;
  sourceIds: string[];
}

/** message 节点：合并后的回答正文。 */
export interface MessageNode extends ExecutionFlowNodeBase {
  kind: 'message';
  text: string;
  /** 消息级终态；'none' = 仍在流式累积。 */
  terminal: 'none' | 'completed' | 'failed';
  terminalEventId?: string;
  terminalSequence?: number;
  errorMessage?: string;
}

export type ToolState = 'running' | 'completed' | 'failed';

/** tool 节点：按 toolCallId 精确配对 call/result 后的唯一行。 */
export interface ToolNode extends ExecutionFlowNodeBase {
  kind: 'tool';
  toolCallId: string;
  parentToolCallId?: string;
  state: ToolState;
  /** result 先于 started 到达时创建的占位调用；started 补全后清除。 */
  placeholder: boolean;
  name?: string;
  arguments?: string;
  output?: string;
  error?: string;
  exitCode?: number;
  durationMs?: number;
  presentation?: ToolPresentationDto;
  /** parentToolCallId 明确存在且父节点存在时的子调用树。 */
  children: ToolNode[];
}

export type DelegationState = 'running' | 'completed' | 'failed';

/** delegation 节点：父级委派摘要（CU-09 DelegationRow 事实源）。 */
export interface DelegationNode extends ExecutionFlowNodeBase {
  kind: 'delegation';
  subAgentId: string;
  state: DelegationState;
  template?: string;
  model?: string;
  taskSummary?: string;
  success?: boolean;
  replySummary?: string;
  error?: string;
}

/** retry 节点：由 subconscious_step 的 LLM retry 形态派生（对齐 ModelRetryRow）。 */
export interface RetryNode extends ExecutionFlowNodeBase {
  kind: 'retry';
  attempt: number;
  maxRetries: number;
  reasonSummary: string;
  reasonFull: string;
}

export type TerminalState = 'completed' | 'failed' | 'cancelled';

/** terminal 节点：turn 级终态（首个终态事件胜出，单调）。 */
export interface TerminalNode extends ExecutionFlowNodeBase {
  kind: 'terminal';
  state: TerminalState;
  message?: string;
  errorMessage?: string;
  reply?: string;
}

export type ExecutionFlowNode =
  | ReasoningNode
  | MessageNode
  | ToolNode
  | DelegationNode
  | RetryNode
  | TerminalNode;

/** 协议错误记录（纯聚合；不含 console/遥测副作用）。 */
export interface ExecutionFlowProtocolError {
  reason: 'missing-event-id' | 'invalid-occurred-at' | 'missing-tool-call-id';
  eventId?: string;
  sequence?: number;
  type?: string;
}

export interface ExecutionFlowProjectionStats {
  /** 输入事件总数（含重复/忽略）。 */
  totalEvents: number;
  /** 实际投影（进入排序流）的事件数。 */
  projectedEvents: number;
  /** 按 eventId 去重丢弃的重复事件数。 */
  duplicateEvents: number;
  /** turn 终态落定后被忽略的迟到 progress 事件数。 */
  ignoredAfterTerminal: number;
  /** 协议错误条目数（与 protocolErrors 长度一致）。 */
  protocolErrors: number;
}

export interface ExecutionFlowProjection {
  /** 有序 ViewModel 节点（按首个来源事件 sequence 升序）。 */
  nodes: ExecutionFlowNode[];
  /** turn 级终态（无终态事件时为 undefined）。 */
  terminal?: TerminalNode;
  stats: ExecutionFlowProjectionStats;
  protocolErrors: ExecutionFlowProtocolError[];
}

export interface ProjectExecutionFlowOptions {
  /** 仅投影该 turnId 的事件（CU-05 起 Chat 侧按 turn 接入）。 */
  turnId?: string;
}// ── canonical 事件名集合（镜像 utils/canonicalEvents.ts；不引入其日志副作用）──

/** Turn 级终态事件。 */
export const TURN_TERMINAL_EVENTS = new Set<string>([
  'turn.completed',
  'turn.failed',
  'turn.cancelled',
]);

/** 助手内容流事件。 */
const ASSISTANT_STREAM_EVENTS = new Set<string>([
  'message.content.appended',
  'message.thinking_summary.appended',
  'message.completed',
  'message.failed',
]);

/** 工具事件。 */
const TOOL_EVENTS = new Set<string>([
  'tool.call.requested',
  'tool.call.completed',
  'tool.call.failed',
]);

/** Turn 运行期 progress 事件（终态单调守卫：终态后迟到这些事件一律忽略）。 */
const TURN_PROGRESS_EVENTS = new Set<string>([
  ...ASSISTANT_STREAM_EVENTS,
  ...TOOL_EVENTS,
  'usage.recorded',
  'step',
  'subconscious_step',
  'metadata',
]);

// ── reasoning 合并算法（从 MessageProcessSummary.buildDisplayItems 抽取）─────

export interface ReasoningSegmentInput {
  /** 来源事件 eventId 或确定性派生键。 */
  id: string;
  /** 原始 delta 文本。 */
  text: string;
}

export interface ReasoningBlockOutput {
  id: string;
  text: string;
  sourceIds: string[];
}

/** MessageProcessSummary.buildDisplayItems 的 thinking 分块阈值。 */
const REASONING_BLOCK_MAX_CHARS = 900;

/**
 * 抽取自 MessageProcessSummary.buildDisplayItems 的 thinking 合并算法：
 * - 清洗：去除 undefined|null|NaN 与 \u0000；
 * - 空文本跳过；
 * - 相邻段合并，块上限 900 字符（buffer + text > 900 时切块）；
 * - 块文本用 sanitizeProcessText(compact:false) 收敛。
 * 纯函数、无副作用，供 projector 与后续 CU-10（MessageProcessSummary 改造）复用。
 */
export function mergeReasoningBlocks(
  segments: readonly ReasoningSegmentInput[],
): ReasoningBlockOutput[] {
  const blocks: ReasoningBlockOutput[] = [];
  let buffer = '';
  let bufferedSourceIds: string[] = [];
  let bufferedFirstId: string | null = null;

  const flush = () => {
    const text = sanitizeProcessText(buffer, { compact: false });
    if (text && bufferedFirstId !== null && bufferedSourceIds.length > 0) {
      blocks.push({
        id: `reasoning-block:${bufferedFirstId}`,
        text,
        sourceIds: [...bufferedSourceIds],
      });
    }
    buffer = '';
    bufferedSourceIds = [];
    bufferedFirstId = null;
  };

  for (const segment of segments) {
    const text =
      typeof segment.text === 'string'
        ? segment.text
            .replace(/(?:undefined|null|NaN)+/gi, '')
            .split('\u0000')
            .join('')
        : '';
    if (!text.trim()) continue;
    if (
      bufferedFirstId !== null &&
      buffer.length + text.length > REASONING_BLOCK_MAX_CHARS
    ) {
      flush();
    }
    if (bufferedFirstId === null) bufferedFirstId = segment.id;
    buffer += text;
    bufferedSourceIds.push(segment.id);
  }
  flush();
  return blocks;
}

// ── 工具调用树 ──────────────────────────────────────────────────────────────

/** 按 parentToolCallId 组装调用树：仅 parentToolCallId 明确存在且父节点存在时成树。 */
function buildToolTree(toolNodes: readonly ToolNode[]): ToolNode[] {
  const byCallId = new Map<string, ToolNode>();
  for (const node of toolNodes) byCallId.set(node.toolCallId, node);

  const roots: ToolNode[] = [];
  const childCount = new Map<ToolNode, number>();
  for (const node of toolNodes) {
    const parentId = node.parentToolCallId;
    const parent = parentId ? byCallId.get(parentId) : undefined;
    if (parentId && parent) {
      parent.children.push(node);
      childCount.set(parent, (childCount.get(parent) ?? 0) + 1);
    } else {
      roots.push(node);
    }
  }
  // 子节点按首个来源事件 sequence 排序（保持与服务端顺序一致）。
  for (const node of toolNodes) {
    if ((childCount.get(node) ?? 0) > 1) {
      node.children.sort((a, b) => a.sequence - b.sequence);
    }
  }
  return roots;
}

// ── retry 嗅探（镜像 ModelRetryRow.isModelRetryItem / parseRetryRatio 语义）──

const LLM_RETRY_RE = /LLM (call |stream )?retry/i;
const RETRY_RATIO_RE = /(\d+)\s*\/\s*(\d+)/;

function tryParseRetry(message?: string): {
  attempt: number;
  maxRetries: number;
  reasonSummary: string;
  reasonFull: string;
} | null {
  const reasonFull = sanitizeProcessText(message, { compact: false });
  const compact = sanitizeProcessText(message);
  if (!compact || !LLM_RETRY_RE.test(compact)) return null;
  const match = RETRY_RATIO_RE.exec(compact);
  let attempt = 0;
  let maxRetries = 0;
  if (match) {
    const parsedAttempt = Number(match[1]);
    const parsedMax = Number(match[2]);
    if (
      Number.isInteger(parsedAttempt) &&
      Number.isInteger(parsedMax) &&
      parsedAttempt >= 1 &&
      parsedMax >= 1
    ) {
      attempt = parsedAttempt;
      maxRetries = parsedMax;
    }
  }
  return { attempt, maxRetries, reasonSummary: compact, reasonFull };
}
// ── 主投影函数 ──────────────────────────────────────────────────────────────

interface NormalizedEvent {
  event: ExecutionFlowEvent;
  index: number;
}

function deriveKey(prefix: string, event: ExecutionFlowEvent): string {
  if (typeof event.eventId === 'string' && event.eventId.trim()) {
    return `${prefix}:${event.eventId}`;
  }
  return `${prefix}:seq:${event.sequence}`;
}

function readString(
  event: ExecutionFlowEvent,
  keys: string[],
): string | undefined {
  for (const key of keys) {
    const value = (event as unknown as Record<string, unknown>)[key];
    if (typeof value === 'string' && value.trim()) return value;
  }
  return undefined;
}

function readBoolean(
  event: ExecutionFlowEvent,
  keys: string[],
): boolean | undefined {
  for (const key of keys) {
    const value = (event as unknown as Record<string, unknown>)[key];
    if (typeof value === 'boolean') return value;
  }
  return undefined;
}

/**
 * 将冻结 canonical 事件集投影为有序 ExecutionFlow 节点。
 * 纯函数：同事件集无论以 bootstrap / gap replay / live 累积何种路径与到达顺序
 * 输入，只要 canonical 事实一致，输出即深度一致（重放等价）。
 */
export function projectExecutionFlow(
  events: ReadonlyArray<ExecutionFlowEvent>,
  options?: ProjectExecutionFlowOptions,
): ExecutionFlowProjection {
  const stats: ExecutionFlowProjectionStats = {
    totalEvents: events.length,
    projectedEvents: 0,
    duplicateEvents: 0,
    ignoredAfterTerminal: 0,
    protocolErrors: 0,
  };
  const protocolErrors: ExecutionFlowProtocolError[] = [];
  const recordProtocolError = (error: ExecutionFlowProtocolError) => {
    protocolErrors.push(error);
    stats.protocolErrors += 1;
  };

  // 1) turn 过滤（可选）。
  const turnFiltered = options?.turnId
    ? events.filter((event) => event.turnId === options.turnId)
    : events;

  // 2) 按 eventId 去重（首个到达者胜出；缺 eventId 不参与去重，仅记协议错误）。
  const seenEventIds = new Set<string>();
  const deduped: NormalizedEvent[] = [];
  for (let i = 0; i < turnFiltered.length; i++) {
    const event = turnFiltered[i];
    const eventId = event.eventId;
    if (typeof eventId === 'string' && eventId.trim()) {
      if (seenEventIds.has(eventId)) {
        stats.duplicateEvents += 1;
        continue;
      }
      seenEventIds.add(eventId);
    } else {
      recordProtocolError({
        reason: 'missing-event-id',
        sequence: event.sequence,
        type: event.type,
      });
    }
    deduped.push({ event, index: i });
  }

  // 3) 按 canonical sequence 升序排序（同 sequence 以输入序为稳定 tiebreak）。
  const sorted = [...deduped].sort(
    (a, b) => a.event.sequence - b.event.sequence || a.index - b.index,
  );

  // 4) 终态单调守卫：确定首个 turn 终态事件（sequence 最小者）。
  let terminalSequence: number | null = null;
  for (const { event } of sorted) {
    if (TURN_TERMINAL_EVENTS.has(event.type)) {
      terminalSequence = event.sequence;
      break;
    }
  }

  // 5) 顺序投影。
  let terminalEmitted = false; // 首个 turn 终态胜出（终态单调）。
  const nodes: ExecutionFlowNode[] = [];
  const toolNodesByCallId = new Map<string, ToolNode>();
  let messageNode: MessageNode | null = null;
  const delegationNodes = new Map<string, DelegationNode>();
  let openReasoning: {
    node: ReasoningNode;
    segments: ReasoningSegmentInput[];
  } | null = null;

  const flushReasoning = () => {
    if (!openReasoning) return;
    const blocks = mergeReasoningBlocks(openReasoning.segments);
    openReasoning.node.blocks = blocks;
    openReasoning.node.text = blocks.map((block) => block.text).join('');
    // 对齐 MessageProcessSummary：无有效推理文本（清洗后为空）时不渲染该行。
    if (blocks.length === 0) {
      const index = nodes.indexOf(openReasoning.node);
      if (index >= 0) nodes.splice(index, 1);
    }
    openReasoning = null;
  };

  const appendReasoning = (event: ReasoningDeltaEvent) => {
    if (openReasoning) {
      openReasoning.segments.push({
        id: deriveKey('reasoning', event),
        text: event.delta ?? '',
      });
      openReasoning.node.sourceEventIds.push(event.eventId);
      if (event.occurredAt) {
        openReasoning.node.lastOccurredAt = event.occurredAt;
      }
      return;
    }
    const node: ReasoningNode = {
      kind: 'reasoning',
      key: deriveKey('reasoning', event),
      firstEventId: event.eventId,
      sequence: event.sequence,
      occurredAt: event.occurredAt,
      sourceEventIds: [event.eventId],
      text: '',
      blocks: [],
    };
    nodes.push(node);
    openReasoning = {
      node,
      segments: [
        { id: deriveKey('reasoning', event), text: event.delta ?? '' },
      ],
    };
  };

  const upsertTool = (
    event: ToolExecutionEventDto,
  ): { node: ToolNode; created: boolean } | null => {
    const toolCallId = event.toolCallId;
    if (typeof toolCallId !== 'string' || !toolCallId.trim()) {
      recordProtocolError({
        reason: 'missing-tool-call-id',
        eventId: event.eventId,
        sequence: event.sequence,
        type: event.type,
      });
      return null;
    }
    const existing = toolNodesByCallId.get(toolCallId);
    if (existing) return { node: existing, created: false };
    const node: ToolNode = {
      kind: 'tool',
      key: `tool:${toolCallId}`,
      firstEventId: event.eventId,
      sequence: event.sequence,
      occurredAt: event.occurredAt,
      sourceEventIds: [],
      toolCallId,
      parentToolCallId: event.parentToolCallId,
      state: 'running',
      placeholder: false,
      children: [],
    };
    toolNodesByCallId.set(toolCallId, node);
    nodes.push(node);
    return { node, created: true };
  };
  for (const { event } of sorted) {
    const type = event.type;

    // 终态单调：turn 终态后的迟到 progress 事件忽略（子代理事件除外）。
    if (
      terminalSequence !== null &&
      event.sequence > terminalSequence &&
      TURN_PROGRESS_EVENTS.has(type)
    ) {
      stats.ignoredAfterTerminal += 1;
      continue;
    }

    stats.projectedEvents += 1;

    switch (type) {
      case 'message.thinking_summary.appended': {
        appendReasoning(event);
        break;
      }
      case 'message.content.appended': {
        flushReasoning();
        if (!messageNode) {
          messageNode = {
            kind: 'message',
            key: 'message',
            firstEventId: event.eventId,
            sequence: event.sequence,
            occurredAt: event.occurredAt,
            sourceEventIds: [event.eventId],
            text: '',
            terminal: 'none',
          };
          nodes.push(messageNode);
        } else {
          messageNode.sourceEventIds.push(event.eventId);
        }
        if (typeof event.delta === 'string') messageNode.text += event.delta;
        break;
      }
      case 'message.completed': {
        flushReasoning();
        if (!messageNode) {
          messageNode = {
            kind: 'message',
            key: 'message',
            firstEventId: event.eventId,
            sequence: event.sequence,
            occurredAt: event.occurredAt,
            sourceEventIds: [event.eventId],
            text: '',
            terminal: 'none',
          };
          nodes.push(messageNode);
        } else {
          messageNode.sourceEventIds.push(event.eventId);
        }
        if (typeof event.reply === 'string' && event.reply.trim()) {
          messageNode.text = event.reply;
        }
        messageNode.terminal = 'completed';
        messageNode.terminalEventId = event.eventId;
        messageNode.terminalSequence = event.sequence;
        break;
      }
      case 'message.failed': {
        flushReasoning();
        if (!messageNode) {
          messageNode = {
            kind: 'message',
            key: 'message',
            firstEventId: event.eventId,
            sequence: event.sequence,
            occurredAt: event.occurredAt,
            sourceEventIds: [event.eventId],
            text: '',
            terminal: 'none',
          };
          nodes.push(messageNode);
        } else {
          messageNode.sourceEventIds.push(event.eventId);
        }
        messageNode.terminal = 'failed';
        messageNode.terminalEventId = event.eventId;
        messageNode.terminalSequence = event.sequence;
        messageNode.errorMessage =
          event.errorMessage ?? event.message ?? undefined;
        break;
      }
      case 'tool.call.requested': {
        flushReasoning();
        const requested = upsertTool(event);
        if (!requested) break;
        const { node } = requested;
        // 终态单调：已终态的节点不被迟到 started 降级；占位调用由 started 补全。
        if (node.placeholder) {
          node.placeholder = false;
          node.name = event.name;
          node.arguments = event.arguments;
          node.parentToolCallId = event.parentToolCallId;
          node.presentation = event.presentation;
          node.sourceEventIds.push(event.eventId);
        } else if (node.state === 'running') {
          node.name = event.name;
          node.arguments = event.arguments;
          node.parentToolCallId = event.parentToolCallId;
          node.presentation = event.presentation;
          node.sourceEventIds.push(event.eventId);
        }
        break;
      }
      case 'tool.call.completed':
      case 'tool.call.failed': {
        flushReasoning();
        const result = upsertTool(event);
        if (!result) break;
        const { node, created } = result;
        const isCompleted = type === 'tool.call.completed';
        const isSuccess = isCompleted && (event.exitCode ?? 0) === 0;
        const nextState: ToolState = isSuccess ? 'completed' : 'failed';
        if (created) {
          // result 先于 started 到达：以 result 事实建占位调用，
          // started 到达后由 requested 分支补全（不降级终态）。
          node.placeholder = true;
          node.name = event.name;
          node.state = nextState;
          node.exitCode = event.exitCode;
          node.output = event.output;
          node.error = event.error;
          node.durationMs = event.durationMs;
          node.sourceEventIds.push(event.eventId);
          break;
        }
        // 占位（result 先于 started）：保留终态；started 补全时不降级。
        if (node.placeholder) {
          node.placeholder = false;
          node.name = event.name;
          node.parentToolCallId = event.parentToolCallId;
          node.presentation = event.presentation;
        }
        // 终态单调：仅 running/占位可进入终态；重复结果只合并缺失字段。
        if (node.state === 'running') {
          node.state = nextState;
          node.exitCode = event.exitCode;
          node.output = event.output;
          node.error = event.error;
          node.durationMs = event.durationMs;
        } else {
          node.output = node.output ?? event.output;
          node.error = node.error ?? event.error;
          node.exitCode = node.exitCode ?? event.exitCode;
          node.durationMs = node.durationMs ?? event.durationMs;
        }
        node.sourceEventIds.push(event.eventId);
        break;
      }
      case 'subagent.spawned': {
        flushReasoning();
        const subAgentId =
          readString(event, ['subAgentId', 'sub_agent_id']) ?? event.eventId;
        const node: DelegationNode = {
          kind: 'delegation',
          key: `delegation:${subAgentId}`,
          firstEventId: event.eventId,
          sequence: event.sequence,
          occurredAt: event.occurredAt,
          sourceEventIds: [event.eventId],
          subAgentId,
          state: 'running',
          template: event.template,
          model: event.model,
          taskSummary: event.task,
        };
        delegationNodes.set(subAgentId, node);
        nodes.push(node);
        break;
      }
      case 'subagent.completed': {
        flushReasoning();
        const subAgentId =
          readString(event, ['subAgentId', 'sub_agent_id']) ?? event.eventId;
        const existing = delegationNodes.get(subAgentId);
        const success = readBoolean(event, ['success']);
        const target: DelegationNode = existing ?? {
          kind: 'delegation',
          key: `delegation:${subAgentId}`,
          firstEventId: event.eventId,
          sequence: event.sequence,
          occurredAt: event.occurredAt,
          sourceEventIds: [],
          subAgentId,
          state: 'running',
        };
        if (!existing) {
          delegationNodes.set(subAgentId, target);
          nodes.push(target);
        }
        // 终态单调：running → completed/failed 一次。
        if (target.state === 'running') {
          target.state = success === false ? 'failed' : 'completed';
        }
        target.success = success;
        target.replySummary =
          readString(event, ['resultSummary', 'result_summary']) ?? event.reply;
        target.error = event.error;
        target.sourceEventIds.push(event.eventId);
        break;
      }
      case 'subconscious_step': {
        flushReasoning();
        const retry = tryParseRetry(event.message);
        if (!retry) break; // 非 retry 形态的 subconscious_step 不投影为节点。
        nodes.push({
          kind: 'retry',
          key: deriveKey('retry', event),
          firstEventId: event.eventId,
          sequence: event.sequence,
          occurredAt: event.occurredAt,
          sourceEventIds: [event.eventId],
          attempt: retry.attempt,
          maxRetries: retry.maxRetries,
          reasonSummary: retry.reasonSummary,
          reasonFull: retry.reasonFull,
        });
        break;
      }
      case 'turn.completed':
      case 'turn.failed':
      case 'turn.cancelled': {
        if (terminalEmitted) {
          stats.ignoredAfterTerminal += 1;
          break;
        }
        terminalEmitted = true;
        flushReasoning();
        const state: TerminalState =
          type === 'turn.completed'
            ? 'completed'
            : type === 'turn.failed'
              ? 'failed'
              : 'cancelled';
        nodes.push({
          kind: 'terminal',
          key: deriveKey('terminal', event),
          firstEventId: event.eventId,
          sequence: event.sequence,
          occurredAt: event.occurredAt,
          sourceEventIds: [event.eventId],
          state,
          reply: type === 'turn.completed' ? event.reply : undefined,
          errorMessage:
            type === 'turn.failed'
              ? event.errorMessage ?? event.message
              : undefined,
          message: type === 'turn.cancelled' ? event.message : undefined,
        });
        break;
      }
      default: {
        // 未知/其他 canonical 事件：不投影为节点（已计入 projectedEvents）。
        flushReasoning();
        break;
      }
    }
  }
  flushReasoning();

  // 6) 工具调用树组装（仅 parentToolCallId 明确存在且父节点存在时成树）。
  const toolNodes = [...toolNodesByCallId.values()];
  if (toolNodes.length > 0) {
    const roots = buildToolTree(toolNodes);
    const rootSet = new Set(roots);
    const kept = nodes.filter(
      (node) => node.kind !== 'tool' || rootSet.has(node),
    );
    nodes.length = 0;
    nodes.push(...kept);
  }

  const terminalNode =
    nodes.find((node): node is TerminalNode => node.kind === 'terminal') ??
    undefined;

  return { nodes, terminal: terminalNode, stats, protocolErrors };
}
