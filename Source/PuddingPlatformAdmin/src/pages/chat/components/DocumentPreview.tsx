import { Button } from 'antd';
import React, { useMemo, useRef } from 'react';
import MarkdownPreview from '../../workspace/[id]/MarkdownPreview';
import { extractOutline } from '../markdown/outline';

/**
 * 文档预览视图（右侧制成品区的文档 Tab）。
 *
 * 解决的具体痛点：长 Markdown 挤在对话里翻找痛苦，读全文还得复制路径去硬盘打开。
 *
 * ## 为什么复用 MarkdownPreview
 *
 * 它明确**不启用 rehype-raw**，链接带 noopener —— 安全约定已经定好。
 * 为文档预览再写一套 Markdown 渲染，等于把这条约定复制一份、日后必然漂移。
 *
 * ## 大纲为什么不用 heading id
 *
 * MarkdownPreview 是受控组件，给它注 id 会把"安全渲染"和"目录导航"绑死。
 * 这里改成**按序取 h1,h2,h3 节点**导航——ReactMarkdown 的渲染顺序与
 * extractOutline 的提取顺序天然一致（两边都跳过围栏代码块）。
 *
 * 组件本身不读文件：文件内容由宿主读好后传入（受工作区边界约束）。
 */
export interface DocumentPreviewProps {
  /** 文件名（展示用）。 */
  name: string;
  markdown: string;
  /** 大小/更新时间等附加信息。 */
  meta?: string;
  /** 存为制成品；不提供则不渲染按钮。 */
  onSaveArtifact?: () => void;
  /** 复制路径；不提供则不渲染按钮。 */
  onCopyPath?: () => void;
}

export const DocumentPreview: React.FC<DocumentPreviewProps> = ({
  name,
  markdown,
  meta,
  onSaveArtifact,
  onCopyPath,
}) => {
  const contentRef = useRef<HTMLDivElement>(null);
  const outline = useMemo(() => extractOutline(markdown), [markdown]);

  const scrollToHeading = (index: number) => {
    const nodes = contentRef.current?.querySelectorAll('h1,h2,h3');
    const node = nodes?.[index];
    node?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  return (
    <div data-testid="document-preview" style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 8,
          padding: '10px 12px',
          borderBottom: '1px solid #e5e7eb',
        }}
      >
        <span style={{ fontSize: 18 }}>📄</span>
        <span style={{ minWidth: 0 }}>
          <span
            data-testid="document-preview-name"
            style={{
              display: 'block',
              fontWeight: 600,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {name}
          </span>
          {meta ? (
            <span style={{ display: 'block', fontSize: 12, opacity: 0.65 }}>{meta}</span>
          ) : null}
        </span>
        <span style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
          {onCopyPath ? (
            <Button size="small" data-testid="document-copy-path" onClick={onCopyPath}>
              复制路径
            </Button>
          ) : null}
          {onSaveArtifact ? (
            <Button
              size="small"
              type="primary"
              data-testid="document-save-artifact"
              onClick={onSaveArtifact}
            >
              存为制成品
            </Button>
          ) : null}
        </span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: '160px 1fr', gap: 16, flex: 1, minHeight: 0 }}>
        <nav
          data-testid="document-outline"
          style={{
            padding: '12px 8px',
            borderRight: '1px solid #e5e7eb',
            overflow: 'auto',
            fontSize: 12,
          }}
        >
          {outline.length === 0 ? (
            <span style={{ opacity: 0.5 }}>无标题</span>
          ) : (
            outline.map((item, index) => (
              <button
                key={`${item.line}:${item.text}`}
                type="button"
                data-testid={`outline-item-${index}`}
                onClick={() => scrollToHeading(index)}
                style={{
                  display: 'block',
                  width: '100%',
                  textAlign: 'left',
                  border: 0,
                  background: 'none',
                  padding: `3px 0 3px ${(item.level - 1) * 12}px`,
                  cursor: 'pointer',
                  color: '#5b6169',
                  font: 'inherit',
                }}
              >
                {item.text}
              </button>
            ))
          )}
        </nav>

        <div ref={contentRef} style={{ overflow: 'auto', padding: '12px 12px 24px' }}>
          <MarkdownPreview markdown={markdown} emptyText="（空文档）" />
        </div>
      </div>
    </div>
  );
};

export default DocumentPreview;
