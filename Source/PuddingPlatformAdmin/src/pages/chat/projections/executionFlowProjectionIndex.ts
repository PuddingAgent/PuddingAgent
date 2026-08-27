import type {
  ExecutionFlowEvent,
  ExecutionFlowProjection,
} from './executionFlowProjector';
import { projectExecutionFlow } from './executionFlowProjector';

interface MutableTurnProjectionState {
  eventIds: Set<string>;
  events: ExecutionFlowEvent[];
  projection: ExecutionFlowProjection;
  terminal: boolean;
}

export interface ExecutionFlowProjectionIndexFlushResult {
  changed: boolean;
  changedTurnIds: readonly string[];
}

/**
 * 当前会话的增量执行流索引。
 *
 * 事件先进入按 Turn 分桶的 pending 区，再由调用方在一个渲染帧内统一 flush：
 * - 一次只重投影发生变化的 Turn，不再扫描会话内全部事件和全部 Turn；
 * - 快照 Map 与未变化 Turn 的 Projection 均保持引用稳定；
 * - Turn 进入终态后释放原始事件与 eventId，保留最终 Projection，并拒绝迟到进度；
 * - reset 是会话切换的硬边界，禁止事件和 Projection 跨会话滞留。
 */
export class ExecutionFlowProjectionIndex {
  private readonly states = new Map<string, MutableTurnProjectionState>();
  private readonly pendingByTurn = new Map<string, ExecutionFlowEvent[]>();
  private readonly pendingEventIdsByTurn = new Map<string, Set<string>>();
  private snapshot: ReadonlyMap<string, ExecutionFlowProjection> = new Map();
  private revision = 0;

  enqueue(events: readonly ExecutionFlowEvent[]): number {
    let accepted = 0;
    for (const event of events) {
      const turnId = event.turnId?.trim();
      if (!turnId) continue;

      const state = this.states.get(turnId);
      if (state?.terminal) continue;

      const eventId = event.eventId?.trim();
      if (eventId && state?.eventIds.has(eventId)) continue;

      let pendingIds = this.pendingEventIdsByTurn.get(turnId);
      if (!pendingIds) {
        pendingIds = new Set<string>();
        this.pendingEventIdsByTurn.set(turnId, pendingIds);
      }
      if (eventId && pendingIds.has(eventId)) continue;
      if (eventId) pendingIds.add(eventId);

      const pending = this.pendingByTurn.get(turnId);
      if (pending) pending.push(event);
      else this.pendingByTurn.set(turnId, [event]);
      accepted += 1;
    }
    return accepted;
  }

  flush(): ExecutionFlowProjectionIndexFlushResult {
    if (this.pendingByTurn.size === 0) {
      return { changed: false, changedTurnIds: [] };
    }

    const changedTurnIds: string[] = [];
    const nextSnapshot = new Map(this.snapshot);
    for (const [turnId, incoming] of this.pendingByTurn) {
      const existing = this.states.get(turnId);
      if (existing?.terminal || incoming.length === 0) continue;

      const state: MutableTurnProjectionState = existing ?? {
        eventIds: new Set<string>(),
        events: [],
        projection: projectExecutionFlow([]),
        terminal: false,
      };
      const pendingIds = this.pendingEventIdsByTurn.get(turnId);
      pendingIds?.forEach((eventId) => {
        state.eventIds.add(eventId);
      });
      state.events.push(...incoming);
      state.projection = projectExecutionFlow(state.events);
      state.terminal = Boolean(state.projection.terminal);

      if (state.terminal) {
        // 最终 Projection 已完整持有可见事实；释放重复的原始事件字符串。
        state.events = [];
        state.eventIds.clear();
      }

      this.states.set(turnId, state);
      nextSnapshot.set(turnId, state.projection);
      changedTurnIds.push(turnId);
    }

    this.pendingByTurn.clear();
    this.pendingEventIdsByTurn.clear();
    if (changedTurnIds.length === 0) {
      return { changed: false, changedTurnIds };
    }

    this.snapshot = nextSnapshot;
    this.revision += 1;
    return { changed: true, changedTurnIds };
  }

  getProjection(turnId: string): ExecutionFlowProjection | undefined {
    return this.snapshot.get(turnId);
  }

  getSnapshot(): ReadonlyMap<string, ExecutionFlowProjection> {
    return this.snapshot;
  }

  getRevision(): number {
    return this.revision;
  }

  getStats(): {
    turns: number;
    activeEvents: number;
    pendingEvents: number;
    terminalTurns: number;
  } {
    let activeEvents = 0;
    let terminalTurns = 0;
    for (const state of this.states.values()) {
      activeEvents += state.events.length;
      if (state.terminal) terminalTurns += 1;
    }
    let pendingEvents = 0;
    for (const events of this.pendingByTurn.values())
      pendingEvents += events.length;
    return {
      turns: this.states.size,
      activeEvents,
      pendingEvents,
      terminalTurns,
    };
  }

  reset(): boolean {
    const changed =
      this.states.size > 0 ||
      this.pendingByTurn.size > 0 ||
      this.snapshot.size > 0;
    this.states.clear();
    this.pendingByTurn.clear();
    this.pendingEventIdsByTurn.clear();
    this.snapshot = new Map();
    this.revision += 1;
    return changed;
  }
}
