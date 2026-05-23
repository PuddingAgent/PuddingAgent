// ── useTypewriterStreaming: Ink Bloom Typewriter 渲染调度 ──
// 职责：将完整累积文本分为「稳定 Markdown 块」和「正在键入的尾段」，
//       并按视觉节奏逐 chunk 显示 liveText。
import { useCallback, useEffect, useRef, useState } from 'react';

export interface TypewriterStreamingState {
  /** 可安全交给 ReactMarkdown 的稳定部分 */
  stableMarkdown: string;
  /** 当前未稳定的尾段（完整） */
  liveText: string;
  /** 已经"敲出来"的 visible 部分 */
  visibleLiveText: string;
  /** visible 在 liveText 中的起始偏移量 */
  visibleStartOffset: number;
  /** 是否正在键入 */
  isTyping: boolean;
  /** 是否已 stopping 但尚有字符未显示 */
  isSettling: boolean;
}

export interface TypewriterStreamingOptions {
  /** 完整累积文本（来自 answerMarkdown） */
  text: string;
  /** 是否仍在流式传输中 */
  isStreaming: boolean;
  /** 每 tick 间隔 ms，默认 28 */
  tickMs?: number;
  /** 最大滞后字符数，默认 240 */
  maxLagChars?: number;
}

/**
 * 找到稳定 Markdown 提交边界。
 * 只提交已闭合段落/代码块/表格，不提交半截语法。
 */
function findStableMarkdownBoundary(text: string): number {
  if (!text) return 0;

  // 从后向前扫描安全边界
  const lines = text.split('\n');
  let lastSafeEnd = 0;
  let accumulatedLen = 0;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const nextLine = i + 1 < lines.length ? lines[i + 1] : '';

    // 双换行段落结束
    if (line === '' && i > 0 && lines[i - 1] !== '') {
      lastSafeEnd = accumulatedLen;
    }

    // 标题行
    if (/^#{1,6}\s/.test(line) && nextLine !== '') {
      // 标题本身是完整一行，但下一行不是空行时可能是不安全
      // 允许在 heading 行尾提交
    }

    // 表格行：连续表格行结束后提交
    if (/^\|.*\|$/.test(line) && !nextLine.startsWith('|') && !nextLine.startsWith('|---')) {
      lastSafeEnd = accumulatedLen + line.length;
    }

    // 列表项：`\n- ` 或 `\n* ` 或 `\n1. ` 独立行后可提交
    if (/^(\s*[-*\d+.]\s)/.test(line) && line.trim() !== '' && nextLine && !/^(\s*[-*\d+.]\s)/.test(nextLine)) {
      lastSafeEnd = accumulatedLen + line.length;
    }

    accumulatedLen += line.length + 1; // +1 for \n
  }

  // 检查未闭合的 fenced code block: 如果 ``` 数量为奇数，退回上一个安全点
  const fenceMatches = text.match(/```/g);
  if (fenceMatches && fenceMatches.length % 2 !== 0) {
    // 有未闭合代码块，只能提交到最近的```之前的安全边界
    const lastFence = text.lastIndexOf('```');
    // 找到上一个安全边界
    let safeEnd = lastSafeEnd;
    while (safeEnd > 0 && safeEnd > lastFence) {
      // 逐步后退
      safeEnd = text.lastIndexOf('\n', safeEnd - 2);
      if (safeEnd < lastFence) break;
    }
    return Math.max(0, safeEnd);
  }

  // 避免空行过多
  return Math.max(0, lastSafeEnd);
}

/**
 * 将文本按 visual chunk 分组（避免每个字符一个 DOM 节点）。
 * 中文固定 2 字一组，英文按 word/chunk 分组。
 * 分组必须确定性，否则同一段 liveText 重渲染时会反复重建 DOM 节点。
 */
export function chunkVisibleText(text: string): { key: number; text: string }[] {
  if (!text) return [];
  const chunks: { key: number; text: string }[] = [];
  const charArray = [...text]; // 支持 Unicode
  let i = 0;
  let key = 0;

  while (i < charArray.length) {
    const c = charArray[i];
    // CJK / 标点
    if (/[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]/.test(c)) {
      // 中文固定 2 字一组，保持 chunk 边界稳定
      const end = Math.min(i + 2, charArray.length);
      const grp = charArray.slice(i, end).join('');
      chunks.push({ key: key++, text: grp });
      i = end;
    } else if (/[\s]/.test(c)) {
      // 空格/换行单独一组
      chunks.push({ key: key++, text: c });
      i++;
    } else {
      // 英文/数字：按 word chunk
      let end = i + 1;
      while (end < charArray.length && !/[\s\u4e00-\u9fff]/.test(charArray[end])) {
        end++;
      }
      const grp = charArray.slice(i, end).join('');
      chunks.push({ key: key++, text: grp });
      i = end;
    }
  }
  return chunks;
}

export function useTypewriterStreaming({
  text,
  isStreaming,
  tickMs = 28,
  maxLagChars = 240,
}: TypewriterStreamingOptions): TypewriterStreamingState {
  const [stableMarkdown, setStableMarkdown] = useState('');
  const [liveText, setLiveText] = useState('');
  const [visibleLiveText, setVisibleLiveText] = useState('');
  const [visibleStartOffset, setVisibleStartOffset] = useState(0);
  const [isTyping, setIsTyping] = useState(false);
  const [isSettling, setIsSettling] = useState(false);

  const prevTextRef = useRef('');
  const stableLenRef = useRef(0);
  const visiblePosRef = useRef(0);
  const liveTextRef = useRef('');
  const tickTimerRef = useRef<number | null>(null);
  const tickActiveRef = useRef(false);

  // 清除 tick 定时器
  const clearTick = useCallback(() => {
    if (tickTimerRef.current != null) {
      clearTimeout(tickTimerRef.current);
      tickTimerRef.current = null;
    }
    tickActiveRef.current = false;
  }, []);

  // 单次 tick：推进 visiblePos
  const tick = useCallback(() => {
    tickActiveRef.current = false;

    const currentLiveText = liveTextRef.current;
    const liveLen = currentLiveText.length;
    if (visiblePosRef.current >= liveLen) {
      clearTick();
      setIsTyping(false);
      return;
    }

    // dynamic charsPerTick: 根据剩余长度调整速度
    const remaining = liveLen - visiblePosRef.current;
    let charsPerTick = 1;
    if (remaining > 100) charsPerTick = 3;
    else if (remaining > 50) charsPerTick = 2;

    const nextPos = Math.min(visiblePosRef.current + charsPerTick, liveLen);
    visiblePosRef.current = nextPos;
    setVisibleLiveText(currentLiveText.slice(0, nextPos));
    setVisibleStartOffset(0);
    setIsTyping(true);

    // 调度下一个 tick
    tickTimerRef.current = window.setTimeout(tick, tickMs);
    tickActiveRef.current = true;
  }, [tickMs, clearTick]);

  // 当 text 变化时
  useEffect(() => {
    if (text === prevTextRef.current && isStreaming === false && visiblePosRef.current >= text.length) {
      return;
    }
    prevTextRef.current = text;

    if (isStreaming) {
      // 流式中：计算 stableMarkdown 边界
      const boundary = findStableMarkdownBoundary(text);

      // 如果 stable 边界推进了，更新
      if (boundary > stableLenRef.current) {
        const previousStableLen = stableLenRef.current;
        const visibleAbsolutePos = previousStableLen + visiblePosRef.current;
        stableLenRef.current = boundary;
        setStableMarkdown(text.slice(0, boundary));
        visiblePosRef.current = Math.max(0, Math.min(visibleAbsolutePos - boundary, text.length - boundary));
      }

      // liveText = stable 之后的部分
      const newLive = text.slice(stableLenRef.current);
      liveTextRef.current = newLive;
      setLiveText(newLive);
      setVisibleLiveText(newLive.slice(0, Math.min(visiblePosRef.current, newLive.length)));

      // 如果 live 长度增长超过 maxLagChars，强制推进 visiblePos
      if (newLive.length - visiblePosRef.current > maxLagChars) {
        const forceAdvance = newLive.length - maxLagChars;
        if (forceAdvance > visiblePosRef.current) {
          visiblePosRef.current = forceAdvance;
          setVisibleLiveText(newLive.slice(0, forceAdvance));
        }
      }

      // 启动 tick 进程
      if (!tickActiveRef.current) {
        tick();
      }
    } else {
      // 流式结束：先把 stable 后的尾段打完，再一次性提交为 stable Markdown。
      const finalLive = text.slice(stableLenRef.current);
      liveTextRef.current = finalLive;
      setLiveText(finalLive);
      visiblePosRef.current = Math.min(visiblePosRef.current, finalLive.length);
      setVisibleLiveText(finalLive.slice(0, visiblePosRef.current));

      if (visiblePosRef.current < finalLive.length) {
        setIsSettling(true);
        if (!tickActiveRef.current) {
          tick();
        }
        // 剩余字符显示完毕后 → 全部转为 stable
        const settleCheck = () => {
          if (visiblePosRef.current >= liveTextRef.current.length) {
            setStableMarkdown(text);
            stableLenRef.current = text.length;
            liveTextRef.current = '';
            setLiveText('');
            setVisibleLiveText('');
            setVisibleStartOffset(0);
            visiblePosRef.current = 0;
            setIsTyping(false);
            setIsSettling(false);
            clearTick();
          } else {
            tickTimerRef.current = window.setTimeout(settleCheck, tickMs);
          }
        };
        tickTimerRef.current = window.setTimeout(settleCheck, tickMs);
      } else {
        // 已经全部显示
        setStableMarkdown(text);
        stableLenRef.current = text.length;
        liveTextRef.current = '';
        setLiveText('');
        setVisibleLiveText('');
        setVisibleStartOffset(0);
        visiblePosRef.current = 0;
        setIsTyping(false);
        setIsSettling(false);
        clearTick();
      }
    }

    return () => {
      // 不在此处 cleanup timer，由后续流式状态管理
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [text, isStreaming, maxLagChars, tickMs]);

  // 重置（新对话开始）
  useEffect(() => {
    if (!isStreaming && !text) {
      setStableMarkdown('');
      setLiveText('');
      setVisibleLiveText('');
      setVisibleStartOffset(0);
      setIsTyping(false);
      setIsSettling(false);
      stableLenRef.current = 0;
      visiblePosRef.current = 0;
      liveTextRef.current = '';
      prevTextRef.current = '';
      clearTick();
    }
  }, [isStreaming, text, clearTick]);

  // 卸载清理
  useEffect(() => () => clearTick(), [clearTick]);

  return {
    stableMarkdown,
    liveText,
    visibleLiveText,
    visibleStartOffset,
    isTyping: isTyping || (isStreaming && visiblePosRef.current < liveTextRef.current.length),
    isSettling: isSettling || (!isStreaming && visiblePosRef.current < liveTextRef.current.length),
  };
}
