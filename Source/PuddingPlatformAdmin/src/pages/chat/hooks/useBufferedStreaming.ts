// ── useBufferedStreaming：流式输出块级凝聚 Hook ──
// 前端即使收到 SSE delta，也不逐字打印，而是按边界聚合
import { useCallback, useEffect, useRef, useState } from 'react';

interface UseBufferedStreamingOptions {
  /** 完整累积文本（来自 SSE 实时拼合） */
  text: string;
  /** 是否仍在流式传输中 */
  isStreaming: boolean;
  /** 缓冲间隔 ms，默认 180ms */
  bufferMs?: number;
}

interface UseBufferedStreamingResult {
  /** 当前应展示的文本（滞后于 text） */
  displayText: string;
  /** 是否处于凝聚状态（仍有 delta 未刷新到显示） */
  isCondensing: boolean;
  /** 刚完成 settle 的标志（用于触发最终动画） */
  justSettled: boolean;
}

/**
 * 缓冲策略：
 * 1. 每 bufferMs 聚合一次 delta
 * 2. 遇到自然边界（\n\n 段落、``` 代码块、\n- 列表项）立即刷新
 * 3. 超过 200 字符强制刷新避免可见延迟
 * 4. 流式结束后立即全量刷新并触发 settle
 */
export function useBufferedStreaming({
  text,
  isStreaming,
  bufferMs = 180,
}: UseBufferedStreamingOptions): UseBufferedStreamingResult {
  const [displayText, setDisplayText] = useState('');
  const [justSettled, setJustSettled] = useState(false);
  const lastFlushedRef = useRef(0);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const settleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clearTimers = useCallback(() => {
    if (timerRef.current) { clearTimeout(timerRef.current); timerRef.current = null; }
    if (settleTimerRef.current) { clearTimeout(settleTimerRef.current); settleTimerRef.current = null; }
  }, []);

  // 流式结束 → 立即全量刷新并标记 settle
  useEffect(() => {
    if (!isStreaming && text) {
      clearTimers();
      setDisplayText(text);
      lastFlushedRef.current = text.length;
      setJustSettled(true);
      settleTimerRef.current = setTimeout(() => setJustSettled(false), 600);
    }
  }, [isStreaming, text, clearTimers]);

  // 流式进行中 → 缓冲刷新
  useEffect(() => {
    if (!isStreaming) return;

    const flushTo = (pos: number) => {
      if (pos <= lastFlushedRef.current) return;
      setDisplayText(text.slice(0, pos));
      lastFlushedRef.current = pos;
      timerRef.current = null;
    };

    const scheduleFlush = (pos: number, delay: number) => {
      if (timerRef.current) clearTimeout(timerRef.current);
      timerRef.current = setTimeout(() => flushTo(pos), delay);
    };

    const remaining = text.slice(lastFlushedRef.current);

    // 无新内容
    if (remaining.length === 0) return;

    // 超过 200 字符强制刷新
    if (remaining.length >= 200) {
      flushTo(text.length);
      return;
    }

    // 查找自然边界
    const boundaryIdx = findNaturalBoundary(remaining);
    if (boundaryIdx > 0) {
      flushTo(lastFlushedRef.current + boundaryIdx);
      return;
    }

    // 首次 delta 到达时立即显示以消除空白延迟
    if (lastFlushedRef.current === 0) {
      flushTo(text.length);
      return;
    }

    // 无自然边界 → 延迟 bufferMs 刷新
    scheduleFlush(text.length, bufferMs);

    return () => {
      if (timerRef.current) { clearTimeout(timerRef.current); timerRef.current = null; }
    };
  }, [text, isStreaming, bufferMs]);

  // 重置（新对话开始）
  useEffect(() => {
    if (!isStreaming && !text) {
      setDisplayText('');
      lastFlushedRef.current = 0;
    }
  }, [isStreaming, text]);

  // 卸载清理
  useEffect(() => () => clearTimers(), [clearTimers]);

  const isCondensing = isStreaming && displayText.length < text.length;

  return { displayText, isCondensing, justSettled };
}

/** 查找最近的自然文本边界索引 */
function findNaturalBoundary(chunk: string): number {
  // 段落边界：双换行
  const paraIdx = chunk.indexOf('\n\n');
  if (paraIdx >= 0) return paraIdx + 2;

  // 代码块边界：```
  const codeIdx = chunk.indexOf('```');
  if (codeIdx >= 0) return codeIdx + 3;

  // 列表项边界：\n- 或 \n* 或 \n1.
  const listMatch = chunk.match(/\n[-*\d+]\.?\s/);
  if (listMatch && listMatch.index !== undefined) return listMatch.index + 1;

  // 标题边界：\n# 或 \n##
  const headingIdx = chunk.search(/\n#{1,6}\s/);
  if (headingIdx >= 0) return headingIdx + 1;

  // 句号 + 空格（中英文）
  const sentIdx = chunk.search(/[。！？.!?]\s/);
  if (sentIdx >= 0) return sentIdx + 2;

  return -1;
}
