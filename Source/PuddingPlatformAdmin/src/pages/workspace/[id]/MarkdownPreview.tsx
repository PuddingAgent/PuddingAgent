import React from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

export interface MarkdownPreviewProps {
  markdown: string;
  /** 内容为空时的占位文案。 */
  emptyText?: string;
  className?: string;
}

/**
 * Agent 配置文档（SOUL / AGENTS / TOOLS / MEMORY / BOOTSTRAP / heartbeat）的只读预览。
 *
 * 安全约定（设计文档第 10 节）：不启用 rehype-raw，文档中的 HTML 按普通文本处理，
 * 不会执行脚本；链接由 react-markdown 默认的 URL 转换过滤（javascript: 等协议被清空），
 * 并以新窗口 + noopener 打开。
 */
const MarkdownPreview: React.FC<MarkdownPreviewProps> = ({
  markdown,
  emptyText = '（未填写）',
  className,
}) => {
  if (!markdown.trim()) {
    return <div className={className}>{emptyText}</div>;
  }

  return (
    <div className={className} data-testid="prompt-document-preview">
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ children, node: _node, ...props }) => (
            <a {...props} target="_blank" rel="noreferrer noopener">
              {children}
            </a>
          ),
        }}
      >
        {markdown}
      </ReactMarkdown>
    </div>
  );
};

export default MarkdownPreview;
