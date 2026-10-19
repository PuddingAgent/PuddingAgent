import { Button } from 'antd';
import React, { useMemo, useState } from 'react';
import { analyzeLongMessage, excerptLines } from '../markdown/messageFold';

/**
 * 长消息折叠外壳。
 *
 * ## 为什么是"外壳"而不是"渲染器"
 *
 * 折叠逻辑与 Markdown 渲染无关：短消息直接把 children 原样渲染，长消息才
 * 换成摘要 + 操作按钮。所以这里不 import MarkdownBlock，避免把两件事绑死。
 *
 * ## 为什么必须可展开
 *
 * 折叠是默认，不是强制。有人就喜欢在对话里滚——所以永远保留「展开」，
 * 且展开后可以「收起」回到摘要。
 *
 * ## 摘要按行截断
 *
 * 用 excerptLines 而不是按字数切：按字数会把一行代码或一个表格行切一半，
 * 展开后的缩略看起来像乱码。
 */
export interface FoldableMessageProps {
  markdown: string;
  /** 完整渲染内容（折叠时不会挂载）。 */
  children: React.ReactNode;
  /** 在右侧文档视图打开全文。 */
  onOpenInSide?: () => void;
  /** 默认是否展开（默认 false）。 */
  defaultExpanded?: boolean;
  /**
   * 禁用折叠（流式期间必须为 true）。
   * 流式时内容还在增长，此时折叠会导致正文突然缩水、跳动。
   */
  disabled?: boolean;
  /** 摘要行数上限。 */
  excerptLineLimit?: number;
}

export const FoldableMessage: React.FC<FoldableMessageProps> = ({
  markdown,
  children,
  onOpenInSide,
  defaultExpanded = false,
  disabled = false,
  excerptLineLimit = 15,
}) => {
  const decision = useMemo(() => analyzeLongMessage(markdown), [markdown]);
  const [expanded, setExpanded] = useState(defaultExpanded);

  // 流式期间禁用折叠；短消息：零额外 DOM，保持原来的阅读体验。
  if (disabled || !decision.fold) return <>{children}</>;

  if (expanded) {
    return (
      <div data-testid="foldable-message-expanded">
        {children}
        <div style={{ marginTop: 8 }}>
          <Button size="small" data-testid="fold-collapse" onClick={() => setExpanded(false)}>
            收起
          </Button>
        </div>
      </div>
    );
  }

  const excerpt = excerptLines(markdown, excerptLineLimit);

  return (
    <div data-testid="foldable-message-collapsed">
      <div
        style={{
          border: '1px solid #e5e7eb',
          borderRadius: 10,
          overflow: 'hidden',
          background: '#fafbfc',
        }}
      >
        <div
          style={{
            padding: '10px 12px',
            fontSize: 12,
            color: '#5b6169',
            borderBottom: '1px solid #e5e7eb',
            display: 'flex',
            gap: 8,
            alignItems: 'center',
          }}
        >
          <span>📄 长文档</span>
          <span data-testid="fold-stats" style={{ marginLeft: 'auto', opacity: 0.7 }}>
            约 {decision.chars} 字
            {decision.headings ? ` · ${decision.headings} 节` : ''}
            {decision.codeBlocks ? ` · ${decision.codeBlocks} 段代码` : ''}
          </span>
        </div>

        <pre
          data-testid="fold-excerpt"
          style={{
            margin: 0,
            padding: '10px 12px',
            fontSize: 13,
            lineHeight: 1.6,
            whiteSpace: 'pre-wrap',
            maxHeight: 260,
            overflow: 'hidden',
            color: '#3d434c',
          }}
        >
          {excerpt}
        </pre>

        <div
          style={{
            display: 'flex',
            gap: 12,
            alignItems: 'center',
            padding: '10px 12px',
            borderTop: '1px solid #e5e7eb',
            background: '#fff',
          }}
        >
          {/*
            只给一个「主按钮」。
            之前是两个同权重按钮（在右侧打开 / 在对话中展开）—— 用户实测反馈
            "多了一个按钮"：两者都是"展开"，视觉上重复，得先读才能分辨差别。
            现在：主路径是右侧阅读（长文档在对话里滚很痛苦），
            留在对话里降为文字链接 —— 需要时仍在，但不抢注意力。
          */}
          {onOpenInSide ? (
            <>
              <Button
                type="primary"
                size="small"
                data-testid="fold-open-side"
                onClick={onOpenInSide}
              >
                展开阅读
              </Button>
              <Button type="link" size="small" data-testid="fold-expand" onClick={() => setExpanded(true)}>
                在对话中展开
              </Button>
            </>
          ) : (
            <Button size="small" data-testid="fold-expand" onClick={() => setExpanded(true)}>
              在对话中展开
            </Button>
          )}
        </div>
      </div>
    </div>
  );
};

export default FoldableMessage;
