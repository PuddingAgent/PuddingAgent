import { CopyOutlined } from '@ant-design/icons';
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

const preprocessMarkdown = (markdown: string): string => {
  const lines = markdown.split('\n');
  const output: string[] = [];
  let index = 0;
  while (index < lines.length) {
    const line = lines[index];
    const trimmed = line.trim();

    if (trimmed === '``') {
      output.push('```');
      index++;
      continue;
    }

    const headingMatch = /^(#{1,6}\s+)(.*)$/.exec(trimmed);
    if (headingMatch?.[2].includes('|')) {
      const prefix = headingMatch[1];
      const rest = headingMatch[2];
      const pipeIndex = rest.indexOf('|');
      const headingText = rest.substring(0, pipeIndex).trim();
      const tablePart = rest.substring(pipeIndex).trim();
      output.push(prefix + (headingText || '测试结果'));
      output.push('');
      output.push(tablePart);
      index++;
      continue;
    }

    if (/^\|.*\|$/.test(trimmed) || /^\|[-:| ]+\|$/.test(trimmed)) {
      const parts: string[] = [line];
      index++;
      while (index < lines.length) {
        const nextLine = lines[index].trim();
        if (/^\|/.test(nextLine)) break;
        if (nextLine === '') {
          index++;
          break;
        }
        parts.push(lines[index]);
        index++;
      }
      const fixed = parts
        .join(' ')
        .replace(/```[^\n`]*\s*/g, '`')
        .replace(/\s*```/g, '`');
      if (
        output.length > 0 &&
        /^#{1,6}\s/.test(output[output.length - 1].trim())
      ) {
        output.push('');
      }
      output.push(fixed);
      continue;
    }

    output.push(line);
    index++;
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
  useEffect(() => {
    if (!ref.current || isStreaming) return;
    const now = performance.now();
    if (now - lastHighlightRef.current < 300) return;
    lastHighlightRef.current = now;
    Prism.highlightElement(ref.current);
  }, [code, className, isStreaming]);
  return (
    <div className={styles.codeBlockWrap}>
      {/* P0-3: 左上角语言标签（从 language-* 提取，无语言时显示「code」） */}
      <span className={styles.codeLanguageLabel}>
        {extractCodeLanguage(className)}
      </span>
      <Button
        size="small"
        className={styles.codeCopyButton}
        icon={<CopyOutlined />}
        data-code-copy
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
