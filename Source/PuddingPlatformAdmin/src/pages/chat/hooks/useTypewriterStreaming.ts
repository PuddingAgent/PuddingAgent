// ── useTypewriterStreaming: Ink Bloom Typewriter 渲染调度 ──
// 职责：将完整累积文本分为「稳定 Markdown 块」和「正在键入的尾段」，
//       并按视觉节奏逐 chunk 显示 liveText。
import { useCallback, useEffect, useRef, useState } from 'react';
import { recordPerfEvent } from '@/utils/debug';

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
 * @param fromOffset 增量扫描起始偏移（默认 0 = 全量扫描）。
 *   传入 stableLenRef.current 可跳过已提交文本，将 O(n) 降为 O(delta)。
 *   内部会回退到最近的段落边界（\n\n）以确保语法完整性。
 */
function findStableMarkdownBoundary(text: string, fromOffset: number = 0): number {
  if (!text) return 0;

  // 增量优化：从 fromOffset 回退到最近的段落边界开始扫描
  let scanStart = 0;
  if (fromOffset > 0 && fromOffset < text.length) {
    // 找到 fromOffset 之前最近的 \n\n（段落边界）
    const paraBreak = text.lastIndexOf('\n\n', fromOffset);
    if (paraBreak > 0) {
      scanStart = paraBreak + 1; // 从第二个 \n 之后开始（即新段落起始）
    }
    // 如果没找到段落边界，保持从 0 扫描（安全回退）
  }

  // 从 scanStart 向后扫描安全边界
  const slice = text.slice(scanStart);
  const lines = slice.split('\n');
  let lastSafeEnd = scanStart;
  let accumulatedLen = scanStart;

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
    if (
      /^\|.*\|$/.test(line) &&
      !nextLine.startsWith('|') &&
      !nextLine.startsWith('|---')
    ) {
      lastSafeEnd = accumulatedLen + line.length;
    }

    // 列表项：`\n- ` 或 `\n* ` 或 `\n1. ` 独立行后可提交
    if (
      /^(\s*[-*\d+.]\s)/.test(line) &&
      line.trim() !== '' &&
      nextLine &&
      !/^(\s*[-*\d+.]\s)/.test(nextLine)
    ) {
      lastSafeEnd = accumulatedLen + line.length;
    }

    accumulatedLen += line.length + 1; // +1 for \n
  }

    // 检查未闭合的 fenced code block: 如果 ``` 数量为奇数，退回上一个安全点
  // 注意：fence 检查必须对全文进行（奇偶性依赖完整文本）
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
export function chunkVisibleText(
  text: string,
): { key: number; text: string }[] {
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
      while (
        end < charArray.length &&
        !/[\s\u4e00-\u9fff]/.test(charArray[end])
      ) {
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
  tickMs = 24,
  maxLagChars = 240,
}: TypewriterStreamingOptions): TypewriterStreamingState {
  const [stableMarkdown, setStableMarkdown] = useState(() =>
    isStreaming ? '' : text,
  );
  const [twState, setTwState] = useState({
    liveText: '',
    visibleLiveText: '',
    visibleStartOffset: 0,
    isTyping: false,
  });
  const [isSettling, setIsSettling] = useState(false);

    const prevTextRef = useRef(isStreaming ? '' : text);
  const prevIncomingLengthRef = useRef(isStreaming ? text.length : 0);
  const stableLenRef = useRef(isStreaming ? 0 : text.length);
  const visiblePosRef = useRef(0);
  const liveTextRef = useRef('');
  const latestTextRef = useRef(isStreaming ? text : '');
  const tickTimerRef = useRef<number | null>(null);
  const tickActiveRef = useRef(false);
  const lastTickScheduledAtRef = useRef<number>(0);
  const wasStreamingRef = useRef(isStreaming);

  // B2: Adaptive typewriter speed — track incoming stream rate (chars/sec)
  const streamRateRef = useRef(0);
  const lastDeltaTimestampRef = useRef(0);
  const rateWindowRef = useRef<{ chars: number; elapsed: number }[]>([]);

  // 清除 tick 定时器
  const clearTick = useCallback(() => {
    if (tickTimerRef.current != null) {
      clearTimeout(tickTimerRef.current);
      tickTimerRef.current = null;
    }
    tickActiveRef.current = false;
  }, []);

    const commitStableUpToVisible = useCallback(
    (fullText: string, visibleAbsolutePos: number) => {
      // A1 增量优化：从 stableLenRef.current 开始扫描，跳过已提交文本
      const safeBoundary = findStableMarkdownBoundary(fullText, stableLenRef.current);
      if (safeBoundary > visibleAbsolutePos) return;
      const commitBoundary = Math.min(safeBoundary, fullText.length);
      if (commitBoundary <= stableLenRef.current) return;

      stableLenRef.current = commitBoundary;
      setStableMarkdown(fullText.slice(0, commitBoundary));
    },
    [],
  );

  // 单次 tick：推进 visiblePos，主线程繁忙时自动降速
  const tick = useCallback(() => {
    tickActiveRef.current = false;

    const currentLiveText = liveTextRef.current;
    const liveLen = currentLiveText.length;
    if (visiblePosRef.current >= liveLen) {
      clearTick();
      setTwState((s) => ({ ...s, isTyping: false }));
      return;
    }

    // P1-perf: 检测主线程拥堵 — 如果实际触发延迟 >> 预期，减少本 tick 工作
    const now = performance.now();
    const scheduledGap =
      lastTickScheduledAtRef.current > 0
        ? now - lastTickScheduledAtRef.current
        : tickMs;
    const congestionFactor = Math.max(1, scheduledGap / (tickMs * 2));

    // dynamic charsPerTick: 根据剩余长度调整速度，并发拥堵时降速
    const remaining = liveLen - visiblePosRef.current;
    let charsPerTick = 2;
    if (remaining > 180) charsPerTick = 10;
    else if (remaining > 96) charsPerTick = 6;
    else if (remaining > 48) charsPerTick = 3;

    // 拥堵降速：因子 > 2 时跳过本 tick 让主线程喘息
    if (congestionFactor > 2.5) {
      tickTimerRef.current = window.setTimeout(tick, tickMs * 2);
      lastTickScheduledAtRef.current = performance.now();
      tickActiveRef.current = true;
      return;
    }
    if (congestionFactor > 1.5) {
      charsPerTick = Math.max(1, Math.floor(charsPerTick / 2));
    }

    const nextPos = Math.min(visiblePosRef.current + charsPerTick, liveLen);
    const visibleAbsolutePos = stableLenRef.current + nextPos;
    commitStableUpToVisible(latestTextRef.current, visibleAbsolutePos);

    const nextLiveText = latestTextRef.current.slice(stableLenRef.current);
    liveTextRef.current = nextLiveText;
    visiblePosRef.current = Math.max(
      0,
      Math.min(visibleAbsolutePos - stableLenRef.current, nextLiveText.length),
    );
    setTwState((s) => ({
      ...s,
      liveText: nextLiveText,
      visibleLiveText: nextLiveText.slice(0, visiblePosRef.current),
      visibleStartOffset: 0,
      isTyping: true,
    }));
    recordPerfEvent(
      'chat.typewriter.tick',
      {
        liveLen,
        remaining,
        charsPerTick,
        visiblePos: visiblePosRef.current,
        stableLen: stableLenRef.current,
      },
      { throttleMs: 500 },
    );

    // 调度下一个 tick，记录调度时间用于拥堵检测
    tickTimerRef.current = window.setTimeout(tick, tickMs);
    lastTickScheduledAtRef.current = performance.now();
    tickActiveRef.current = true;
  }, [tickMs, clearTick, commitStableUpToVisible]);

  // 当 text 变化时
  useEffect(() => {
    if (
      text === prevTextRef.current &&
      isStreaming === false &&
      visiblePosRef.current >= text.length
    ) {
      return;
    }
    prevTextRef.current = text;

    if (isStreaming) {
      wasStreamingRef.current = true;
      latestTextRef.current = text;
      const incomingDelta = Math.max(
        0,
        text.length - prevIncomingLengthRef.current,
      );
      prevIncomingLengthRef.current = text.length;
      recordPerfEvent(
        'chat.typewriter.input',
        {
          totalChars: text.length,
          incomingDelta,
          stableLen: stableLenRef.current,
          visiblePos: visiblePosRef.current,
        },
        { throttleMs: 500 },
      );
      const visibleAbsolutePos = Math.min(
        stableLenRef.current + visiblePosRef.current,
        text.length,
      );
      commitStableUpToVisible(text, visibleAbsolutePos);
      visiblePosRef.current = Math.max(
        0,
        Math.min(
          visibleAbsolutePos - stableLenRef.current,
          text.length - stableLenRef.current,
        ),
      );

      // liveText = stable 之后的部分
      const newLive = text.slice(stableLenRef.current);
      liveTextRef.current = newLive;
      setTwState((s) => ({
        ...s,
        liveText: newLive,
        visibleLiveText: newLive.slice(
          0,
          Math.min(visiblePosRef.current, newLive.length),
        ),
      }));

            // B2: Compute adaptive maxLagChars from recent stream speed
      const now = performance.now();
      if (incomingDelta > 0 && lastDeltaTimestampRef.current > 0) {
        const dt = now - lastDeltaTimestampRef.current;
        if (dt > 0 && dt < 5000) {
          rateWindowRef.current.push({ chars: incomingDelta, elapsed: dt });
          // Keep only last 2 seconds of samples
          let totalElapsed = 0;
          const window = rateWindowRef.current;
          while (window.length > 1 && totalElapsed < 2000) {
            totalElapsed += window[window.length - 1].elapsed;
            if (totalElapsed >= 2000) break;
            window.shift();
          }
          // Compute chars/sec over the window
          const totalChars = window.reduce((s, w) => s + w.chars, 0);
          const totalTime = window.reduce((s, w) => s + w.elapsed, 0);
          streamRateRef.current = totalTime > 0 ? (totalChars / totalTime) * 1000 : 0;
        }
      }
      lastDeltaTimestampRef.current = now;

      // B2: adaptiveMaxLag — ~1.5s buffer of incoming text, clamped [48, 200]
      const adaptiveMaxLag = Math.max(
        48,
        Math.min(200, Math.round(streamRateRef.current * 1.5)),
      );

      // 如果 live 长度增长超过 adaptiveMaxLag，强制推进 visiblePos
      if (newLive.length - visiblePosRef.current > adaptiveMaxLag) {
        const forceAdvance = newLive.length - adaptiveMaxLag;
        if (forceAdvance > visiblePosRef.current) {
          visiblePosRef.current = forceAdvance;
          setTwState((s) => ({
            ...s,
            visibleLiveText: newLive.slice(0, forceAdvance),
          }));
        }
      }

      // 启动 tick 进程
      if (!tickActiveRef.current) {
        tick();
      }
    } else {
      if (!wasStreamingRef.current) {
        setStableMarkdown(text);
        stableLenRef.current = text.length;
        liveTextRef.current = '';
        setTwState((s) => ({
          ...s,
          liveText: '',
          visibleLiveText: '',
          visibleStartOffset: 0,
          isTyping: false,
        }));
        visiblePosRef.current = 0;
        setIsSettling(false);
        clearTick();
        return;
      }
      wasStreamingRef.current = false;
      // 流式结束：先把 stable 后的尾段打完，再一次性提交为 stable Markdown。
      const finalLive = text.slice(stableLenRef.current);
      liveTextRef.current = finalLive;
      visiblePosRef.current = Math.min(visiblePosRef.current, finalLive.length);
      setTwState((s) => ({
        ...s,
        liveText: finalLive,
        visibleLiveText: finalLive.slice(0, visiblePosRef.current),
      }));

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
            setTwState((s) => ({
              ...s,
              liveText: '',
              visibleLiveText: '',
              visibleStartOffset: 0,
              isTyping: false,
            }));
            visiblePosRef.current = 0;
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
        setTwState((s) => ({
          ...s,
          liveText: '',
          visibleLiveText: '',
          visibleStartOffset: 0,
          isTyping: false,
        }));
        visiblePosRef.current = 0;
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
      setTwState((s) => ({
        ...s,
        liveText: '',
        visibleLiveText: '',
        visibleStartOffset: 0,
        isTyping: false,
      }));
      setIsSettling(false);
      stableLenRef.current = 0;
      visiblePosRef.current = 0;
      liveTextRef.current = '';
      prevTextRef.current = '';
      prevIncomingLengthRef.current = 0;
      latestTextRef.current = '';
      clearTick();
    }
  }, [isStreaming, text, clearTick]);

  // 卸载清理
  useEffect(() => () => clearTick(), [clearTick]);

  return {
    stableMarkdown,
    liveText: twState.liveText,
    visibleLiveText: twState.visibleLiveText,
    visibleStartOffset: twState.visibleStartOffset,
    isTyping:
      twState.isTyping ||
      (isStreaming && visiblePosRef.current < liveTextRef.current.length),
    isSettling:
      isSettling ||
      (!isStreaming && visiblePosRef.current < liveTextRef.current.length),
  };
}
