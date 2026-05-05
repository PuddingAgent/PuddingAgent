// ── MessageItem：Markdown 渲染 + 代码块 ─────────────────────
import { CopyOutlined } from '@ant-design/icons';
import { Button } from 'antd';
import Prism from 'prismjs';
import React, { useEffect, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import { useChatStyles } from '../styles';

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

// ── MessageItem：渲染 Markdown 内容 ─────────────────────────
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
        {markdownText || (isStreaming ? ' ' : '')}
      </ReactMarkdown>
      {isStreaming && <span className={styles.streamingCursor}>▌</span>}
    </div>
  );
};

export default MessageItem;
