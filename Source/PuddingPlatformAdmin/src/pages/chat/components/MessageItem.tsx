// ── MessageItem：轻量 Markdown 文本块（用于 Timeline Answer 和旧版兼容）──
import { CopyOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import Prism from 'prismjs';
import React, { useEffect, useLayoutEffect, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import rehypeRaw from 'rehype-raw';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import { isPerfDiagnosticsEnabled, recordPerfEvent } from '@/utils/debug';
import { useChatStyles } from '../styles';

// ── Markdown 预处理：修复 GFM 表格渲染问题 ─────────────────
// 问题1：LLM 在表格单元格内生成 fenced code block，跨行破坏 GFM 表结构
// 问题2：标题 `##` 后紧接表格行（无空行），ReactMarkdown 把表格当标题内容
// 问题3：标题和表格混在同一行 `## | 测试项 | ...`
const preprocessMarkdown = (md: string): string => {
  const lines = md.split('\n');
  const out: string[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();

    // LLM 偶尔把 fenced code block 写成单独一行的双反引号，规范化后避免吞掉后续 Markdown。
    if (trimmed === '``') {
      out.push('```');
      i++;
      continue;
    }

    // 情况3：heading 行内混杂表格（## | 测试项 | 结果 |）→ 拆分为 heading + 空行 + 表格头
    const headingMatch = /^(#{1,6}\s+)(.*)$/.exec(trimmed);
    if (headingMatch && headingMatch[2].includes('|')) {
      const prefix = headingMatch[1]; // "## "
      const rest = headingMatch[2]; // "| 测试项 | ..." 或 "测试结果 | 测试项 | ..."
      const pipeIdx = rest.indexOf('|');
      const headingText = rest.substring(0, pipeIdx).trim();
      const tablePart = rest.substring(pipeIdx).trim();
      if (headingText) {
        out.push(prefix + headingText);
        out.push(''); // 空行分隔
        out.push(tablePart); // 表格头
        i++;
        continue;
      }
      // heading 文本为空（如 "## | 测试项 |"），保留 ## 作为通用标题
      out.push(prefix + '测试结果');
      out.push('');
      out.push(tablePart);
      i++;
      continue;
    }

    // 表格行（| ... | 或 |---|）→ 合并 code block 续行
    if (/^\|.*\|$/.test(trimmed) || /^\|[-:| ]+\|$/.test(trimmed)) {
      const parts: string[] = [line];
      i++;
      while (i < lines.length) {
        const nl = lines[i].trim();
        if (/^\|/.test(nl)) break; // 新表格行
        if (nl === '') {
          i++;
          break;
        } // 空行 → 表格结束
        parts.push(lines[i]); // code block 续行
        i++;
      }
      const joined = parts.join(' ');
      const fixed = joined
        .replace(/```[^\n`]*\s*/g, '`')
        .replace(/\s*```/g, '`');
      // 如果前一行是 heading（无空行分隔），补空行
      if (out.length > 0 && /^#{1,6}\s/.test(out[out.length - 1].trim())) {
        out.push('');
      }
      out.push(fixed);
    } else {
      out.push(line);
      i++;
    }
  }
  return out.join('\n');
};

// ── 内部 CodeBlock 组件 ──────────────────────────────────────
const CodeBlock: React.FC<{ code: string; className?: string }> = ({
  code,
  className,
}) => {
  const { styles: rawStyles } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const ref = useRef<HTMLElement>(null);
  const lastHighlightRef = useRef(0);
  useEffect(() => {
    if (!ref.current) return;
    // 流式输出时 code 频繁变化，限制 Prism 高亮频率为每 300ms 最多一次
    const now = performance.now();
    if (now - lastHighlightRef.current < 300) return;
    lastHighlightRef.current = now;
    Prism.highlightElement(ref.current);
  }, [code, className]);
  return (
    <div className={styles.codeBlockWrap}>
      <Button
        size="small"
        className={styles.codeCopyButton}
        icon={<CopyOutlined />}
        onClick={() => navigator.clipboard.writeText(code)}
      >
        复制
      </Button>
      <pre>
        <code ref={ref} className={className}>
          {code}
        </code>
      </pre>
    </div>
  );
};

// ── MessageItem：渲染 Markdown 为轻量文本块 ─────────────────
interface MessageItemProps {
  markdownText: string;
  isStreaming?: boolean;
  /** ADR-InkBloom: 流式模式下可安全渲染的稳定 Markdown */
  stableMarkdown?: string;
  /** ADR-InkBloom: 未稳定的尾段完整文本 */
  liveText?: string;
  /** ADR-InkBloom: 已"敲出来"的可见尾段 */
  visibleLiveText?: string;
  /** ADR-InkBloom: visible 在 liveText 中的起始偏移 */
  visibleStartOffset?: number;
}

const MessageItem: React.FC<MessageItemProps> = ({
  markdownText,
  isStreaming,
  stableMarkdown,
  liveText,
  visibleLiveText,
  visibleStartOffset: _visibleStartOffset,
}) => {
  const { styles: rawStyles } = useChatStyles();
  const styles = rawStyles as Record<string, string>;
  const outputRef = useRef<HTMLDivElement | null>(null);
  const renderStart = performance.now();
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
      ((cb: FrameRequestCallback) => window.setTimeout(cb, 0));
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

  // ADR-InkBloom: 流式模式下 stableMarkdown 交给 MarkdownBlock（仅段落边界变化时重解析），
  // visibleLiveText 用纯文本渲染（高频但不触发 ReactMarkdown 重解析）。
  if (isStreaming && stableMarkdown !== undefined) {
    const liveTextToRender = liveText ?? visibleLiveText;
    return (
      <div ref={outputRef} className={styles.markdownBody}>
        {stableMarkdown ? (
          <MarkdownBlock markdownText={stableMarkdown} styles={styles} />
        ) : null}
        {liveTextToRender ? (
          <span className={styles.liveTextSpan}>{liveTextToRender}</span>
        ) : null}
        <span className={styles.inkCursor} />
      </div>
    );
  }

  return (
    <div ref={outputRef} className={styles.markdownBody}>
      <MarkdownBlock
        markdownText={markdownText || (isStreaming ? ' ' : '')}
        styles={styles}
      />
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

const MarkdownBlock = React.memo(
  function MarkdownBlock({
    markdownText,
    styles,
  }: {
    markdownText: string;
    styles: Record<string, string>;
  }) {
    const renderStart = performance.now();
    const preprocessMsRef = React.useRef(0);
    const processedMarkdown = React.useMemo(() => {
      const start = performance.now();
      const processed = preprocessMarkdown(markdownText);
      preprocessMsRef.current = performance.now() - start;
      return processed;
    }, [markdownText]);
    const components = React.useMemo(() => sharedComponents(styles), [styles]);
    React.useEffect(() => {
      recordPerfEvent(
        'chat.markdown.render',
        {
          chars: markdownText.length,
          processedChars: processedMarkdown.length,
          preprocessMs: Math.round(preprocessMsRef.current),
          commitMs: Math.round(performance.now() - renderStart),
        },
        { throttleMs: 500 },
      );
    });
    return (
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeKatex, rehypeRaw]}
        components={components}
      >
        {processedMarkdown}
      </ReactMarkdown>
    );
  },
  (prev, next) => prev.markdownText === next.markdownText,
);

/** 共享的 ReactMarkdown components 配置 */
function sharedComponents(styles: Record<string, string>) {
  return {
    table: ({
      children,
      node: _node,
      ...p
    }: {
      children?: React.ReactNode;
      node?: unknown;
    }) => (
      <div className={styles.markdownTableScroll}>
        <table {...p}>{children}</table>
      </div>
    ),
    a: ({
      children,
      node: _node,
      title: _title,
      ...p
    }: React.AnchorHTMLAttributes<HTMLAnchorElement> & {
      children?: React.ReactNode;
      node?: unknown;
    }) => <a {...p}>{children}</a>,
    code: ({
      inline,
      className,
      children,
      node: _node,
      ...p
    }: {
      inline?: boolean;
      className?: string;
      children?: React.ReactNode;
      node?: unknown;
    }) => {
      const c = String(children ?? '').replace(/\n$/, '');
      const hasLanguageClass = /\blanguage-/.test(className ?? '');
      const isInlineCode =
        inline === true || (!hasLanguageClass && !c.includes('\n'));
      if (isInlineCode)
        return (
          <code className={styles.inlineCode} {...p}>
            {children}
          </code>
        );
      return <CodeBlock code={c} className={className} />;
    },
  };
}

export default MessageItem;
