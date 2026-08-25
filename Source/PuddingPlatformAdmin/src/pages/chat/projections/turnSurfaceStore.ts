// ── TurnSurfaceStore：跨源 Turn 表面（chat UI 行为链重构 2026-08-24）─────────
//
// 设计目标（对齐重构方案 §前端数据设计）：
//  1. 卡片 identity 永远是 canonical turnId；别名表把 messageId/runId/
//     commandClientId 归并到同一张卡片（本地发送、activeRun、服务端投影
//     三源同卡）。
//  2. bootstrap（历史明细接口）、activeRun 快照、live SSE 使用同一个
//     增量事件流：全部归一为 ExecutionFlowEvent 后按 eventId 幂等合流，
//     投影复用 executionFlowProjector（sequence 交错 / toolCallId 配对 /
//     终态单调）。
//  3. turn.completed/failed/cancelled 只更新 TurnCard 状态，绝不清空
//     节点——终态轨迹永久保留，刷新后由历史明细水合恢复。
//
// 纯度约束：无 DOM / 时间源 / 网络副作用。API 请求由 hooks 层发起，
// 本模块只负责状态归并与派生（projectExecutionFlow 输出）。
import {
  projectExecutionFlow,
  type ExecutionFlowEvent,
  type ExecutionFlowProjection,
} from './executionFlowProjector';
import type { ProcessSummaryItem } from '../client/types';

export type TurnSurfaceStatus = 'running' | 'completed' | 'failed' | 'cancelled';

export interface TurnSurface {
  /** canonical turn identity（会话投影 message.turnId / runId / messageId）。 */
  turnId: string;
  /** 别名（messageId/runId/commandClientId…）→ 归并到本 turn。 */
  aliases: Set<string>;
  status: TurnSurfaceStatus;
  /** canonical 事件日志（按 sequence 升序；eventId 幂等去重后追加）。 */
  events: ExecutionFlowEvent[];
  /** events 变化时重算；交错/配对/终态单调全部由 projector 保证。 */
  projection: ExecutionFlowProjection;
  revision: number;
}

export interface ApplyEventsResult {
  /** 实际新增（未因 eventId 重复被忽略）的事件数。 */
  applied: number;
  /** 事件归属的 turnId（按 turnId 过滤或 hint 解析）。 */
  turnId?: string;
}

/**
 * ProcessSummaryItem（服务端过程明细 / activeRun 快照项）→ ExecutionFlowEvent。
 *
 * 顺序事实：服务端 2026-08-25 硬切为 canonical sequence 必填，直接透传真实
 * sequence（跨源合流的重放等价前提）；不再以 baseSequence + index 合成顺序。
 * occurredAt 透传服务端 timestamp。
 */
export function processItemsToFlowEvents(
  items: readonly ProcessSummaryItem[],
  options: { turnId: string },
): ExecutionFlowEvent[] {
  const events: ExecutionFlowEvent[] = [];
  items.forEach((item) => {
    // 运行时也 fail closed：即使缓存/旧 Core 绕过了 TypeScript 契约，也不以
    // 数组位置伪造 canonical 顺序。升级后缺 sequence 的项目必须重新水合。
    if (!Number.isSafeInteger(item.sequence) || item.sequence < 0) return;
    const common = {
      eventId: item.id,
      sequence: item.sequence,
      runId: item.runId ?? options.turnId,
      turnId: item.turnId ?? options.turnId,
      occurredAt: item.timestamp,
    } as const;
    const toolCallId = item.toolCallId ?? item.id;
    switch (item.kind) {
      case 'text':
        events.push({ ...common, type: 'message.content.appended', delta: item.text });
        break;
      case 'thinking':
        events.push({
          ...common,
          type: 'message.thinking_summary.appended',
          delta: item.text,
        });
        break;
      case 'tool_call':
        events.push({
          ...common,
          type: 'tool.call.requested',
          toolCallId,
          name: item.name ?? item.text.slice(0, 80),
          arguments: item.arguments ?? undefined,
        });
        break;
      case 'tool_result': {
        const failed =
          item.status === 'error' ||
          item.status === 'failed' ||
          (typeof item.exitCode === 'number' && item.exitCode !== 0);
        events.push({
          ...common,
          type: failed ? 'tool.call.failed' : 'tool.call.completed',
          toolCallId,
          name: item.name ?? item.text.slice(0, 80),
          output: item.output ?? undefined,
          // 客户端 DTO 无独立 error 字段：失败摘要回退 message → output。
          error: failed
            ? (item.message ?? item.output ?? undefined)
            : undefined,
          exitCode: item.exitCode ?? undefined,
        });
        break;
      }
      case 'delegation': {
        const subAgentId = item.delegationRunId ?? item.name ?? item.id;
        if (item.status === 'running') {
          events.push({
            ...common,
            type: 'subagent.spawned',
            sub_agent_id: subAgentId,
            template: item.name ?? undefined,
            task: item.text,
          });
        } else {
          events.push({
            ...common,
            type: 'subagent.completed',
            sub_agent_id: subAgentId,
            success: item.status === 'success',
            reply: item.status === 'success' ? item.text : undefined,
            error: item.status === 'error' ? item.text : undefined,
          });
        }
        break;
      }
      default:
        // 未知 kind（服务端新增）：不投影，避免破坏节点流语义。
        break;
    }
  });
  return events;
}

/**
 * 按 canonical turnId 组织的跨源 Turn 表面存储。
 * 单实例服务于一个会话视图；session 切换由持有方重建。
 */
export class TurnSurfaceStore {
  private readonly byTurn = new Map<string, TurnSurface>();
  private readonly turnIdByAlias = new Map<string, string>();
  private readonly turnIdByEventId = new Map<string, string>();
  private globalRevision = 0;

  /** 建立 turn ↔ 别名（messageId/runId/commandClientId）双向映射。 */
  linkAlias(turnId: string, alias?: string | null): TurnSurface {
    const surface = this.ensureSurface(turnId);
    if (alias && alias.trim() && alias !== turnId) {
      surface.aliases.add(alias);
      this.turnIdByAlias.set(alias, turnId);
    }
    return surface;
  }

  resolveTurnId(alias?: string | null): string | undefined {
    if (!alias) return undefined;
    if (this.byTurn.has(alias)) return alias;
    return this.turnIdByAlias.get(alias);
  }

  get(turnIdOrAlias?: string | null): TurnSurface | undefined {
    const turnId = this.resolveTurnId(turnIdOrAlias);
    return turnId ? this.byTurn.get(turnId) : undefined;
  }

  getProjection(turnIdOrAlias?: string | null): ExecutionFlowProjection | undefined {
    return this.get(turnIdOrAlias)?.projection;
  }

  /** 更新 turn 状态（终态单调：running→终态；终态间不回退）。 */
  setStatus(turnIdOrAlias: string | null | undefined, status: TurnSurfaceStatus): void {
    const surface = this.get(turnIdOrAlias);
    if (!surface) return;
    if (surface.status === status) return;
    if (surface.status !== 'running' && status === 'running') return;
    surface.status = status;
    surface.revision += 1;
    this.globalRevision += 1;
  }

  /**
   * 幂等合流事件：按 event.turnId（缺失时用 turnIdHint 解析出的 canonical
   * turnId）分组去重后追加并重投影。同一 eventId 只归属一个 turn（首个到达
   * 者胜出，与 projector 语义一致）。
   */
  applyEvents(
    events: readonly ExecutionFlowEvent[],
    options?: { turnIdHint?: string | null },
  ): ApplyEventsResult {
    let applied = 0;
    let lastTurnId: string | undefined;
    const byTurn = new Map<string, ExecutionFlowEvent[]>();
    for (const event of events) {
      const turnId = this.resolveEventTurnId(event, options?.turnIdHint);
      if (!turnId) continue;
      if (event.eventId) {
        if (this.turnIdByEventId.has(event.eventId)) continue;
        this.turnIdByEventId.set(event.eventId, turnId);
      }
      const bucket = byTurn.get(turnId);
      if (bucket) bucket.push(event);
      else byTurn.set(turnId, [event]);
    }
    for (const [turnId, incoming] of byTurn) {
      const surface = this.ensureSurface(turnId);
      if (incoming.length === 0) continue;
      surface.events = mergeSortedBySequence(surface.events, incoming);
      // 事件已按 turn 分组，不再传 turnId 过滤（无标签事件会被滤掉）。
      surface.projection = projectExecutionFlow(surface.events);
      surface.revision += 1;
      surface.status = deriveStatusFromProjection(surface, surface.projection);
      this.globalRevision += 1;
      applied += incoming.length;
      lastTurnId = turnId;
    }
    return { applied, turnId: lastTurnId };
  }

  getRevision(): number {
    return this.globalRevision;
  }

  /**
   * 就地清空（会话切换）：保持实例身份稳定，避免持有方闭包捕获到被替换的
   * 旧实例后把事件写进孤儿 store。
   */
  reset(): void {
    this.byTurn.clear();
    this.turnIdByAlias.clear();
    this.turnIdByEventId.clear();
    this.globalRevision = 0;
  }

  getStats(): { turns: number; events: number } {
    let events = 0;
    for (const surface of this.byTurn.values()) events += surface.events.length;
    return { turns: this.byTurn.size, events };
  }

  private ensureSurface(turnId: string): TurnSurface {
    const existing = this.byTurn.get(turnId);
    if (existing) return existing;
    const surface: TurnSurface = {
      turnId,
      aliases: new Set<string>(),
      status: 'running',
      events: [],
      projection: projectExecutionFlow([]),
      revision: 0,
    };
    this.byTurn.set(turnId, surface);
    return surface;
  }

  /**
   * 事件 → canonical turnId：优先事件自带 turnId 的别名/现面解析，其次
   * turnIdHint（activeRun 快照等无标签事件），最后以事件自身 turnId 落新面。
   */
  private resolveEventTurnId(
    event: ExecutionFlowEvent,
    hint?: string | null,
  ): string | undefined {
    if (event.turnId) {
      if (this.byTurn.has(event.turnId)) return event.turnId;
      const viaAlias = this.turnIdByAlias.get(event.turnId);
      if (viaAlias) return viaAlias;
    }
    if (hint) {
      const viaHint = this.resolveTurnId(hint);
      if (viaHint) return viaHint;
    }
    return event.turnId ?? undefined;
  }
}

function mergeSortedBySequence(
  existing: ExecutionFlowEvent[],
  incoming: readonly ExecutionFlowEvent[],
): ExecutionFlowEvent[] {
  const seen = new Set(existing.map((e) => e.eventId).filter(Boolean));
  const merged = existing.slice();
  for (const event of incoming) {
    if (event.eventId && seen.has(event.eventId)) continue;
    if (event.eventId) seen.add(event.eventId);
    merged.push(event);
  }
  merged.sort((a, b) => a.sequence - b.sequence);
  return merged;
}

/**
 * 从投影终态派生 turn 状态（仅推进；无终态节点时保持现状——运行中状态由
 * 持有方按 activeRun/消息 status 显式维护）。
 */
function deriveStatusFromProjection(
  surface: TurnSurface,
  projection: ExecutionFlowProjection,
): TurnSurfaceStatus {
  const terminal = projection.terminal;
  if (!terminal) return surface.status;
  if (terminal.state === 'completed') return 'completed';
  if (terminal.state === 'failed') return 'failed';
  return 'cancelled';
}
