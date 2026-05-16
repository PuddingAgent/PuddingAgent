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

// ── Markdown 预处理：确保 GFM 表格可正确解析 ───────────────
// 问题：LLM 可能在表格单元格内使用 fenced code block，GFM 表格不支持跨行单元格
// 策略：将所有 fenced code block 转为 inline code（单行）或 <br/> 连接的 inline code（多行）
// 注意：如需保留独立代码块高亮，LLM 应使用缩进代码块（4空格）代替 fenced 语法
const preprocessMarkdown = (md: string): string => {
  return md.replace(
    /```[^\n]*\n([\s\S]*?)\n```/g,
    (_full: string, content: string) => {
      const lines = content.trim().split('\n').map((l: string) => l.trim()).filter(Boolean);
      if (lines.length === 1) return '`' + lines[0] + '`';
      // 多行 → 用 <br/> 连接各行为 inline code
      return lines.map((l: string) => '`' + l + '`').join('<br/>');
    },
  );
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
}

const MessageItem: React.FC<MessageItemProps> = ({ markdownText, isStreaming }) => {
  const { styles } = useChatStyles();

  return (
    <div className={styles.markdownBody}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeKatex]}
        components={{
          table: ({ children, node: _node, ...p }: { children?: React.ReactNode; node?: unknown }) => (
            <div className={styles.markdownTableScroll}><table {...p}>{children}</table></div>
          ),
          code: ({ inline, className, children, node: _node, ...p }: { inline?: boolean; className?: string; children?: React.ReactNode; node?: unknown }) => {
            const c = String(children ?? '').replace(/\n$/, '');
            if (inline) return <code className={styles.inlineCode} {...p}>{children}</code>;
            return <CodeBlock code={c} className={className} />;
          },
        }}
      >
        {markdownText ? preprocessMarkdown(markdownText) : (isStreaming ? ' ' : '')}
      </ReactMarkdown>
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

export default MessageItem;
