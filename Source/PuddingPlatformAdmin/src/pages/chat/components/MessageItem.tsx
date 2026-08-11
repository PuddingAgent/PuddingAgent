// ── MessageItem：轻量 Markdown 文本块（用于 Timeline Answer 和旧版兼容）──
import React, { Suspense, useLayoutEffect, useRef } from 'react';
import {
  isPerfDiagnosticsEnabled,
  recordPerfEvent,
} from '@/utils/perfEventRuntime';
import { useChatMessageStyles } from '../styles/messageStyleContext';

const MarkdownBlock =
  process.env.NODE_ENV === 'test'
    ? (require('./MarkdownBlock')
        .default as typeof import('./MarkdownBlock').default)
    : React.lazy(() => import('./MarkdownBlock'));

interface MessageItemProps {
  markdownText: string;
  isStreaming?: boolean;
  /** 当前工作区，用于把受控 vision-* image 代码块解析为 Artifact URL */
  workspaceId?: string;
  /** ADR-InkBloom: 流式模式下可安全渲染的稳定 Markdown */
  stableMarkdown?: string;
  /** ADR-InkBloom: 未稳定的尾段完整文本 */
  liveText?: string;
  /** ADR-InkBloom: 已"敲出来"的可见尾段 */
  visibleLiveText?: string;
  /** ADR-InkBloom: visible 在 liveText 中的起始偏移 */
  visibleStartOffset?: number;
}

const DeferredMarkdownFallback: React.FC<{
  markdownText: string;
}> = ({ markdownText }) => (
  <span
    data-testid="deferred-markdown-fallback"
    style={{ whiteSpace: 'pre-wrap' }}
  >
    {markdownText}
  </span>
);

const MessageItem: React.FC<MessageItemProps> = ({
  markdownText,
  isStreaming,
  workspaceId,
  stableMarkdown,
  liveText,
  visibleLiveText,
  visibleStartOffset: _visibleStartOffset,
}) => {
  const { styles: rawStyles } = useChatMessageStyles();
  const styles = rawStyles as Record<string, string>;
  const outputRef = useRef<HTMLDivElement | null>(null);
  const renderStart = performance.now();

  // B3: Settle FLIP transition — smooth the DOM jump when streaming ends.
  const wasStreamingRef = useRef(false);
  const preSettleHeightRef = useRef<number | null>(null);

  useLayoutEffect(() => {
    if (isStreaming && outputRef.current) {
      preSettleHeightRef.current =
        outputRef.current.getBoundingClientRect().height;
    }
    if (
      wasStreamingRef.current &&
      !isStreaming &&
      outputRef.current &&
      preSettleHeightRef.current !== null
    ) {
      const element = outputRef.current;
      const firstHeight = preSettleHeightRef.current;
      const lastHeight = element.getBoundingClientRect().height;
      const delta = firstHeight - lastHeight;
      if (Math.abs(delta) > 2) {
        element.style.transform = `translateY(${delta}px)`;
        element.style.transition = 'none';
        requestAnimationFrame(() => {
          element.style.transition = 'transform 200ms ease-out';
          element.style.transform = '';
          const onEnd = () => {
            element.style.transition = '';
            element.removeEventListener('transitionend', onEnd);
          };
          element.addEventListener('transitionend', onEnd);
        });
      }
      preSettleHeightRef.current = null;
    }
    wasStreamingRef.current = Boolean(isStreaming);
  }, [isStreaming]);

  const totalTextChars = markdownText.length;
  const stableChars =
    stableMarkdown?.length ?? (isStreaming ? 0 : markdownText.length);
  const visibleLiveChars = visibleLiveText?.length ?? 0;
  const liveChars = liveText?.length ?? 0;
  useLayoutEffect(() => {
    if (!isPerfDiagnosticsEnabled()) return;
    const node = outputRef.current;
    if (!node) return;
    const commitAt = performance.now();
    const domTextChars = node.textContent?.length ?? 0;
    const commonPayload = {
      isStreaming: Boolean(isStreaming),
      totalTextChars,
      stableChars,
      liveChars,
      visibleLiveChars,
      domTextChars,
      scrollHeight: node.scrollHeight,
      clientHeight: node.clientHeight,
      renderToCommitMs: Math.round(commitAt - renderStart),
    };
    recordPerfEvent('chat.output.commit', commonPayload, { throttleMs: 250 });

    const requestFrame =
      window.requestAnimationFrame ??
      ((callback: FrameRequestCallback) => window.setTimeout(callback, 0));
    const frameId = requestFrame(() => {
      const paintAt = performance.now();
      recordPerfEvent(
        'chat.output.paint',
        {
          ...commonPayload,
          domTextChars: node.textContent?.length ?? domTextChars,
          scrollHeight: node.scrollHeight,
          clientHeight: node.clientHeight,
          commitToPaintMs: Math.round(paintAt - commitAt),
          renderToPaintMs: Math.round(paintAt - renderStart),
        },
        { throttleMs: 250 },
      );
    });
    return () => {
      if (
        typeof window.cancelAnimationFrame === 'function' &&
        typeof frameId === 'number'
      ) {
        window.cancelAnimationFrame(frameId);
      }
    };
  }, [
    isStreaming,
    totalTextChars,
    stableChars,
    liveChars,
    visibleLiveChars,
    renderStart,
  ]);

  const renderMarkdown = (value: string, streaming?: boolean) => (
    <Suspense fallback={<DeferredMarkdownFallback markdownText={value} />}>
      <MarkdownBlock
        markdownText={value}
        styles={styles}
        isStreaming={streaming}
        workspaceId={workspaceId}
      />
    </Suspense>
  );

  // Stable Markdown is parsed only at paragraph boundaries. The live tail is
  // painted as text, so token updates never re-run the Markdown parser.
  if (isStreaming && stableMarkdown !== undefined) {
    const liveTextToRender = liveText ?? visibleLiveText;
    return (
      <div ref={outputRef} className={styles.markdownBody}>
        {stableMarkdown ? renderMarkdown(stableMarkdown, true) : null}
        {liveTextToRender ? (
          <span className={styles.liveTextSpan}>{liveTextToRender}</span>
        ) : null}
        <span className={styles.inkCursor} />
      </div>
    );
  }

  const renderedMarkdown = markdownText || (isStreaming ? ' ' : '');
  return (
    <div ref={outputRef} className={styles.markdownBody}>
      {renderMarkdown(renderedMarkdown, isStreaming)}
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

export default MessageItem;
