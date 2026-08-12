// ── Checkpoint Timeline hook (P2#7) ────────────────────────────
// 状态层：管理当前会话的快照列表、还原（Restore）与分支（Fork）。
// 快照在每次 turn 前由 useMessageSend 触发 captureBeforeTurn 保存。
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type Dispatch,
  type MutableRefObject,
  type SetStateAction,
} from 'react';
import type { ChatTurn } from '../types';
import {
  captureCheckpoint,
  checkpointSnapshotToTurns,
  clearCheckpoints,
  loadCheckpointStore,
  loadSessionCheckpoints,
  persistCheckpointStore,
  removeCheckpoint,
  type ChatCheckpoint,
  type CheckpointStoreMap,
} from '../client/checkpointStore';

export interface UseCheckpointTimelineOptions {
  /** 当前选中的会话（快照列表按会话隔离）。 */
  sessionId: string | null;
  workspaceId?: string;
  agentId?: string;
  turnsRef: MutableRefObject<ChatTurn[]>;
  setTurns: Dispatch<SetStateAction<ChatTurn[]>>;
  /** Fork 回调：由 useChatState 注入，负责创建新会话并切换过去。 */
  onForkCheckpoint: (
    checkpoint: ChatCheckpoint,
  ) => Promise<string | undefined>;
}

export interface UseCheckpointTimelineReturn {
  /** 当前会话的快照列表（按时间倒序，最新在前）。 */
  checkpoints: ChatCheckpoint[];
  /** 时间线面板开关。 */
  timelineOpen: boolean;
  setTimelineOpen: (open: boolean) => void;
  /** 当前处于「已还原到快照」状态时的快照 id（用于顶部提示条）。 */
  restoredCheckpointId: string | null;
  clearRestoredMarker: () => void;
  /** 每次 turn 前调用：为指定会话保存一份当前 turns 的快照。 */
  captureBeforeTurn: (targetSessionId: string, label: string) => void;
  /** Restore：把视图 turns 还原为快照内容（前端视图还原）。 */
  restoreCheckpoint: (checkpointId: string) => void;
  /** Fork：从快照分支一个新会话（委托给 onForkCheckpoint）。 */
  forkCheckpoint: (checkpointId: string) => Promise<string | undefined>;
  /** 删除单个快照。 */
  deleteCheckpoint: (checkpointId: string) => void;
  /** 清空当前会话全部快照。 */
  clearAllCheckpoints: () => void;
}

export function useCheckpointTimeline({
  sessionId,
  workspaceId,
  agentId,
  turnsRef,
  setTurns,
  onForkCheckpoint,
}: UseCheckpointTimelineOptions): UseCheckpointTimelineReturn {
  const [checkpoints, setCheckpoints] = useState<ChatCheckpoint[]>(() =>
    loadSessionCheckpoints(sessionId ?? ''),
  );
  const [timelineOpen, setTimelineOpen] = useState(false);
  const [restoredCheckpointId, setRestoredCheckpointId] = useState<
    string | null
  >(null);
  const storeMapRef = useRef<CheckpointStoreMap | null>(null);
  if (storeMapRef.current === null) {
    storeMapRef.current = loadCheckpointStore();
  }
  const storeMap = storeMapRef.current as CheckpointStoreMap;

  // 会话切换时重新加载该会话的快照，并清除还原标记
  useEffect(() => {
    setCheckpoints(loadSessionCheckpoints(sessionId ?? ''));
    setRestoredCheckpointId(null);
  }, [sessionId]);

  // 快照列表变化时持久化整表（只写一次 localStorage，防抖由 React 批处理承担）
  useEffect(() => {
    if (!sessionId) return;
    storeMap[sessionId] = checkpoints;
    persistCheckpointStore(storeMap);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [checkpoints, sessionId]);

  const captureBeforeTurn = useCallback(
    (targetSessionId: string, label: string) => {
      if (!targetSessionId) return;
      const existing = storeMap[targetSessionId] ?? [];
      const next = captureCheckpoint(
        {
          sessionId: targetSessionId,
          workspaceId,
          agentId,
          createdAt: Date.now(),
          label,
          turns: turnsRef.current,
        },
        existing,
      );
      if (next === existing) return; // 重复快照（幂等重试）跳过
      storeMap[targetSessionId] = next;
      storeMapRef.current = { ...storeMap };
      persistCheckpointStore(storeMapRef.current);
      if (targetSessionId === sessionId) {
        setCheckpoints(next);
      }
    },
    [agentId, sessionId, turnsRef, workspaceId],
  );

  const restoreCheckpoint = useCallback(
    (checkpointId: string) => {
      const checkpoint = checkpoints.find(
        (item) => item.checkpointId === checkpointId,
      );
      if (!checkpoint) return;
      setTurns(checkpointSnapshotToTurns(checkpoint.turns));
      setRestoredCheckpointId(checkpointId);
    },
    [checkpoints, setTurns],
  );

  const forkCheckpoint = useCallback(
    async (checkpointId: string) => {
      const checkpoint = checkpoints.find(
        (item) => item.checkpointId === checkpointId,
      );
      if (!checkpoint) return undefined;
      return onForkCheckpoint(checkpoint);
    },
    [checkpoints, onForkCheckpoint],
  );

  const deleteCheckpoint = useCallback(
    (checkpointId: string) => {
      if (!sessionId) return;
      const existing = storeMap[sessionId] ?? [];
      const next = removeCheckpoint(checkpointId, existing);
      storeMap[sessionId] = next;
      storeMapRef.current = { ...storeMap };
      setCheckpoints(next);
      if (restoredCheckpointId === checkpointId) {
        setRestoredCheckpointId(null);
      }
    },
    [restoredCheckpointId, sessionId],
  );

  const clearAllCheckpoints = useCallback(() => {
    if (!sessionId) return;
    storeMap[sessionId] = clearCheckpoints(storeMap[sessionId]);
    storeMapRef.current = { ...storeMap };
    setCheckpoints([]);
    setRestoredCheckpointId(null);
  }, [sessionId]);

  const clearRestoredMarker = useCallback(() => {
    setRestoredCheckpointId(null);
  }, []);

  return {
    checkpoints,
    timelineOpen,
    setTimelineOpen,
    restoredCheckpointId,
    clearRestoredMarker,
    captureBeforeTurn,
    restoreCheckpoint,
    forkCheckpoint,
    deleteCheckpoint,
    clearAllCheckpoints,
  };
}
