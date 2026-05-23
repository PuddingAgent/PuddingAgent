// ── MessageItem：轻量 Markdown 文本块（用于 Timeline Answer 和旧版兼容）──
import { CopyOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import Prism from 'prismjs';
import React, { useEffect, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import { useChatStyles } from '../styles';
import { chunkVisibleText } from '../hooks/useTypewriterStreaming';

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

    // 情况3：heading 行内混杂表格（## | 测试项 | 结果 |）→ 拆分为 heading + 空行 + 表格头
    const headingMatch = /^(#{1,6}\s+)(.*)$/.exec(trimmed);
    if (headingMatch && headingMatch[2].includes('|')) {
      const prefix = headingMatch[1];  // "## "
      const rest = headingMatch[2];    // "| 测试项 | ..." 或 "测试结果 | 测试项 | ..."
      const pipeIdx = rest.indexOf('|');
      const headingText = rest.substring(0, pipeIdx).trim();
      const tablePart = rest.substring(pipeIdx).trim();
      if (headingText) {
        out.push(prefix + headingText);
        out.push('');  // 空行分隔
        out.push(tablePart);  // 表格头
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
        if (/^\|/.test(nl)) break;       // 新表格行
        if (nl === '') { i++; break; }   // 空行 → 表格结束
        parts.push(lines[i]);            // code block 续行
        i++;
      }
      const joined = parts.join(' ');
      const fixed = joined.replace(/```[^\n`]*\s*/g, '`').replace(/\s*```/g, '`');
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
const CodeBlock: React.FC<{ code: string; className?: string }> = ({ code, className }) => {
  const { styles } = useChatStyles();
  const ref = useRef<HTMLElement>(null);
  useEffect(() => { if (ref.current) Prism.highlightElement(ref.current); }, [code, className]);
  return (
    <div className={styles.codeBlockWrap}>
      <Button size="small" className={styles.codeCopyButton} icon={<CopyOutlined />} onClick={() => navigator.clipboard.writeText(code)}>复制</Button>
      <pre><code ref={ref} className={className}>{code}</code></pre>
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
  const { styles } = useChatStyles();

  // ADR-InkBloom: 流式渲染模式 — 稳定 Markdown + 轻量 liveText
  if (isStreaming && stableMarkdown !== undefined) {
    return (
      <div className={styles.markdownBody}>
        {stableMarkdown && (
          <ReactMarkdown
            remarkPlugins={[remarkGfm, remarkMath]}
            rehypePlugins={[rehypeKatex]}
            components={{ ...sharedComponents(styles) }}
          >
            {preprocessMarkdown(stableMarkdown)}
          </ReactMarkdown>
        )}
        <span className={styles.typewriterLiveText}>
          {visibleLiveText ? chunkVisibleText(visibleLiveText).map(chunk => (
            <span key={chunk.key} className={styles.inkChunk}>{chunk.text}</span>
          )) : (liveText ? (
            <span className={styles.inkChunk}>{liveText}</span>
          ) : null)}
          <span className={styles.inkCursor} />
        </span>
      </div>
    );
  }

  return (
    <div className={styles.markdownBody}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeKatex]}
        components={{ ...sharedComponents(styles) }}
      >
        {markdownText ? preprocessMarkdown(markdownText) : (isStreaming ? ' ' : '')}
      </ReactMarkdown>
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

/** 共享的 ReactMarkdown components 配置 */
function sharedComponents(styles: ReturnType<typeof useChatStyles>['styles']) {
  return {
    table: ({ children, node: _node, ...p }: { children?: React.ReactNode; node?: unknown }) => (
      <div className={styles.markdownTableScroll}><table {...p}>{children}</table></div>
    ),
    code: ({ inline, className, children, node: _node, ...p }: { inline?: boolean; className?: string; children?: React.ReactNode; node?: unknown }) => {
      const c = String(children ?? '').replace(/\n$/, '');
      const hasLanguageClass = /\blanguage-/.test(className ?? '');
      const isInlineCode = inline === true || (!hasLanguageClass && !c.includes('\n'));
      if (isInlineCode) return <code className={styles.inlineCode} {...p}>{children}</code>;
      return <CodeBlock code={c} className={className} />;
    },
  };
}

export default MessageItem;
