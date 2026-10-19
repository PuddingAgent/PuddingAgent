// ── MessageItem：轻量 Markdown 文本块（用于 Timeline Answer 和旧版兼容）──
import React, { Suspense, useLayoutEffect, useRef } from 'react';
import {
  isPerfDiagnosticsEnabled,
  recordPerfEvent,
} from '@/utils/perfEventRuntime';
import { useChatMessageStyles } from '../styles/messageStyleContext';
import IncrementalMarkdown from './IncrementalMarkdown';
import FoldableMessage from './FoldableMessage';
import { useSideArtifact } from '../sideArtifact/SideArtifactContext';
import { extractOutline } from '../markdown/outline';

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
  /**
   * 长消息「在右侧打开全文」回调。
   * 未提供时不渲染该按钮——宁可少一个入口，也不给死按钮。
   */
  onOpenMessageInSide?: (markdown: string) => void;
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
  onOpenMessageInSide,
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

  // Stable Markdown 走增量渲染（对齐 harness IncrementalMarkdownParser）：
  // 冻结块 memo 缓存、提交只重解析尾部块；live 尾段按打字机推进的可见文本
  // 渐进渲染（含块级语法时走 markdown 前缀渲染，纯文字走零解析 span + 墨迹光标）。
  const LIVE_BLOCK_SYNTAX_PATTERN = /[|`#>*_~]|\[[^\]]*\]\(/;
  if (isStreaming && stableMarkdown !== undefined) {
    const liveTextToRender = visibleLiveText ?? liveText;
    const liveHasBlockSyntax =
      liveTextToRender != null && liveTextToRender.length > 0
        ? LIVE_BLOCK_SYNTAX_PATTERN.test(liveTextToRender) ||
          /^\s{0,3}(#{1,6}\s|[-*+]\s|\d+[.)]\s|>\s)/m.test(liveTextToRender)
        : false;
    return (
      <div ref={outputRef} className={styles.markdownBody}>
        {stableMarkdown ? (
          <IncrementalMarkdown
            text={stableMarkdown}
            styles={styles}
            workspaceId={workspaceId}
            isStreaming
          />
        ) : null}
        {liveTextToRender
          ? liveHasBlockSyntax
            ? renderMarkdown(liveTextToRender, true)
            : null
          : null}
        {liveTextToRender && !liveHasBlockSyntax ? (
          <span className={styles.liveTextSpan}>{liveTextToRender}</span>
        ) : null}
        {!liveHasBlockSyntax && <span className={styles.inkCursor} />}
      </div>
    );
  }

  // 落定/非流式路径同样走增量渲染：块 key 与流式期间一致（偏移:长度），
  // settle 时不重挂载已冻结块、不全文重解析，只提交最后的尾块。
  const renderedMarkdown = markdownText || (isStreaming ? ' ' : '');

  // 「在右侧打开」的优先级：显式 prop > 页面级 context > 不渲染按钮。
  // 没有 context 时（单测、独立使用）保持原来的行为，不造死按钮。
  const side = useSideArtifact();
  const openInSide = onOpenMessageInSide
    ? () => onOpenMessageInSide(renderedMarkdown)
    : side
      ? () =>
          side.openDocument({
            // 用第一个标题当文档名；extractOutline 已跳过围栏代码块。
            name: `${extractOutline(renderedMarkdown)[0]?.text ?? '长消息'}.md`,
            markdown: renderedMarkdown,
          })
      : undefined;

  return (
    <div ref={outputRef} className={styles.markdownBody}>
      {renderedMarkdown.trim() ? (
        <FoldableMessage
          markdown={renderedMarkdown}
          disabled={isStreaming}
          onOpenInSide={openInSide}
        >
          <IncrementalMarkdown
            text={renderedMarkdown}
            styles={styles}
            workspaceId={workspaceId}
            isStreaming={isStreaming}
          />
        </FoldableMessage>
      ) : null}
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

export default MessageItem;
