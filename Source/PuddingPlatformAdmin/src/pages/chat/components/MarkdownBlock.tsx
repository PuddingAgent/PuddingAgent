import { CheckOutlined, CopyOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import Prism from 'prismjs';
import React, { useEffect, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import rehypeRaw from 'rehype-raw';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import { recordPerfEvent } from '@/utils/perfEventRuntime';

interface MarkdownBlockProps {
  markdownText: string;
  styles: Record<string, string>;
  isStreaming?: boolean;
  workspaceId?: string;
}

// Keep the parser and its KaTeX/HTML dependencies behind this async boundary.
// MessageItem paints a plain-text fallback immediately while this module loads.
const MarkdownBlock = React.memo(
  function MarkdownBlock({
    markdownText,
    styles,
    isStreaming,
    workspaceId,
  }: MarkdownBlockProps) {
    const renderStart = performance.now();
    const preprocessMsRef = React.useRef(0);
    const processedMarkdown = React.useMemo(() => {
      const start = performance.now();
      const processed = preprocessMarkdown(markdownText);
      preprocessMsRef.current = performance.now() - start;
      return processed;
    }, [markdownText]);
    const components = React.useMemo(
      () => sharedComponents(styles, isStreaming, workspaceId),
      [styles, isStreaming, workspaceId],
    );
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
  (previous, next) =>
    previous.markdownText === next.markdownText &&
    previous.isStreaming === next.isStreaming &&
    previous.workspaceId === next.workspaceId,
);

// 只做逐行安全归一：不合并/吞并任何行。
// 历史「管道行收集 + 标题拆 |」hack 会把分隔行与后续正文 join 成一行、吞掉空行，
// 破坏 GFM「表头必须紧跟分隔行」的表格识别，导致整表降级为 <p> 原文。
//
// emoji 字号收敛（行为链 §UI 微调）：正文中的 emoji run 包一层
// <span data-md-emoji>（配合 markdown.styles 的 0.95em 样式，避免 emoji
// 渲染得比正文大一档）。跳过围栏代码块（fence 状态跟踪）与行内 `code` 段；
// KaTeX 公式内出现 emoji 属异常输入，不做特判。
const EMOJI_RUN_RE = /(?:\p{Extended_Pictographic}|\uFE0F|\u200D)+/gu;

const wrapEmojiRuns = (line: string): string => {
  if (!EMOJI_RUN_RE.test(line)) {
    EMOJI_RUN_RE.lastIndex = 0;
    return line;
  }
  EMOJI_RUN_RE.lastIndex = 0;
  // 行内 code 段（`...`）不包裹：按反引号分段，仅处理偶数索引（code 外）段。
  const segments = line.split('`');
  return segments
    .map((segment, index) =>
      index % 2 === 0
        ? segment.replace(
            EMOJI_RUN_RE,
            (run) => `<span data-md-emoji>${run}</span>`,
          )
        : segment,
    )
    .join('`');
};

const preprocessMarkdown = (markdown: string): string => {
  const lines = markdown.split('\n');
  const output: string[] = [];
  let inFencedCode = false;
  for (const line of lines) {
    if (line.trim() === '``') {
      output.push('```');
      continue;
    }
    // 围栏代码块内的内容原样保留（不包 emoji span，不破坏代码语义/高亮）
    if (/^\s*(```|~~~)/.test(line)) {
      inFencedCode = !inFencedCode;
      output.push(line);
      continue;
    }
    output.push(inFencedCode ? line : wrapEmojiRuns(line));
  }
  return output.join('\n');
};

const extractCodeLanguage = (className?: string): string => {
  const match = /\blanguage-([\w+-]+)/.exec(className ?? '');
  return match ? match[1] : 'code';
};

const CodeBlock: React.FC<{
  code: string;
  styles: Record<string, string>;
  className?: string;
  isStreaming?: boolean;
}> = ({ code, styles, className, isStreaming }) => {
  const ref = useRef<HTMLElement>(null);
  const lastHighlightRef = useRef(0);
  // P0-3: 复制成功 1s 反馈（setTimeout + 卸载保护 ref）
  const [copied, setCopied] = React.useState(false);
  const copyTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mountedRef = useRef(true);
  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    };
  }, []);

  useEffect(() => {
    if (!ref.current || isStreaming) return;
    const now = performance.now();
    if (now - lastHighlightRef.current < 300) return;
    lastHighlightRef.current = now;
    Prism.highlightElement(ref.current);
  }, [code, className, isStreaming]);

  const handleCopy = () => {
    navigator.clipboard.writeText(code).catch(() => {});
    if (copyTimerRef.current) clearTimeout(copyTimerRef.current);
    setCopied(true);
    copyTimerRef.current = setTimeout(() => {
      if (mountedRef.current) setCopied(false);
    }, 1000);
  };

  return (
    <div className={styles.codeBlockWrap}>
      {/* P0-3: sticky banner（语言标签 + 复制按钮行，同深底） */}
      <div className={styles.codeBlockBanner}>
        <span className={styles.codeLanguageLabel}>
          {extractCodeLanguage(className)}
        </span>
        <Button
          size="small"
          className={styles.codeCopyButton}
          icon={copied ? <CheckOutlined /> : <CopyOutlined />}
          data-code-copy
          onClick={handleCopy}
        >
          {copied ? '复制成功' : '复制'}
        </Button>
      </div>
      <pre>
        <code ref={ref} className={className}>
          {code}
        </code>
      </pre>
    </div>
  );
};

function sharedComponents(
  styles: Record<string, string>,
  isStreaming?: boolean,
  workspaceId?: string,
) {
  return {
    table: ({
      children,
      node: _node,
      ...props
    }: {
      children?: React.ReactNode;
      node?: unknown;
    }) => (
      <div className={styles.markdownTableScroll}>
        <table {...props}>{children}</table>
      </div>
    ),
    a: ({
      children,
      node: _node,
      title: _title,
      ...props
    }: React.AnchorHTMLAttributes<HTMLAnchorElement> & {
      children?: React.ReactNode;
      node?: unknown;
    }) => <a {...props}>{children}</a>,
    code: ({
      inline,
      className,
      children,
      node: _node,
      ...props
    }: {
      inline?: boolean;
      className?: string;
      children?: React.ReactNode;
      node?: unknown;
    }) => {
      const code = String(children ?? '').replace(/\n$/, '');
      if (className === 'language-image' && workspaceId) {
        const artifactId = resolveVisionArtifactId(code);
        if (artifactId) {
          const source =
            `/api/workspaces/${encodeURIComponent(workspaceId)}` +
            `/vision-artifacts/${encodeURIComponent(artifactId)}`;
          return (
            <span className={styles.artifactImageWrap}>
              <img
                className={styles.artifactImage}
                src={source}
                alt="Agent 生成的图片"
                loading="lazy"
              />
            </span>
          );
        }
      }
      const hasLanguageClass = /\blanguage-/.test(className ?? '');
      const isInlineCode =
        inline === true || (!hasLanguageClass && !code.includes('\n'));
      if (isInlineCode) {
        return (
          <code className={styles.inlineCode} {...props}>
            {children}
          </code>
        );
      }
      return (
        <CodeBlock
          code={code}
          styles={styles}
          className={className}
          isStreaming={isStreaming}
        />
      );
    },
  };
}

const VISION_ARTIFACT_ID = /^vision-[a-f0-9]{32}$/i;
const VISION_ARTIFACT_FILE =
  /(?:^|[\\/])(vision-[a-f0-9]{32})\.(?:jpe?g|png|webp)$/i;

function resolveVisionArtifactId(reference: string): string | undefined {
  const value = reference.trim();
  if (!value || value.includes('\n') || /^https?:\/\//i.test(value)) {
    return undefined;
  }
  if (VISION_ARTIFACT_ID.test(value)) return value.toLowerCase();
  return VISION_ARTIFACT_FILE.exec(value)?.[1].toLowerCase();
}

export default MarkdownBlock;
