import { useEffect, useRef } from 'react';
import type { ChatTurn } from '../types';

const NOTIFICATION_SOUND = '/assets/sounds/notification.mp3';

/**
 * 监听 ChatTurn 完成事件，在新消息输出完成时播放提示音。
 * 仅在 turn 从流式输出中（isStreaming=true）变为完成状态（success）时触发，
 * 避免历史消息加载和 error/cancelled 状态误触发。
 * @param turns 当前对话轮次列表
 * @param enabled 是否启用提示音
 */
export function useNotificationSound(turns: ChatTurn[], enabled: boolean) {
  const completedTurnIdsRef = useRef<Set<string>>(new Set());
  const streamingTurnIdsRef = useRef<Set<string>>(new Set());

  useEffect(() => {
    if (!enabled) return;

    for (const turn of turns) {
      const turnId = turn.turnId;
      if (completedTurnIdsRef.current.has(turnId)) continue;

      // 追踪正在流式输出的 turn
      if (turn.assistant.isStreaming) {
        streamingTurnIdsRef.current.add(turnId);
        continue;
      }

      // 只对之前处于 streaming 状态、现在变为 success 的 turn 播放提示音
      if (
        turn.assistant.status === 'success' &&
        streamingTurnIdsRef.current.has(turnId) &&
        turn.assistant.answerMarkdown.trim().length > 0
      ) {
        completedTurnIdsRef.current.add(turnId);
        streamingTurnIdsRef.current.delete(turnId);

        const audio = new Audio(NOTIFICATION_SOUND);
        audio.volume = 1;
        audio.play().catch(() => {
          // 自动播放被阻止，静默忽略
        });
      }
    }
  }, [turns, enabled]);
}
