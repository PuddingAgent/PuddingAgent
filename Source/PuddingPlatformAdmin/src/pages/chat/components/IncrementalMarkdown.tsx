// ── IncrementalMarkdown：流式 markdown 增量渲染 ───────────────────────────
// 架构对齐 deepseek-harness MarkdownText 的 IncrementalMarkdownParser：
//  - 文本按安全块边界（围栏外空行）切分为块；
//  - 每个块独立 memo 化渲染，key = 源偏移 + 长度（reconciliation 稳定）；
//  - 流式提交时只有尾部块源文本变化 → 只有尾部块重解析；已冻结块不重跑
//    ReactMarkdown，长文流式从 O(n·parse) 全量重渲染降为 O(tail)。
// 不复制 harness 的品牌样式/资源；仅采用其增量渲染结构。
import React, { Suspense, useMemo } from 'react';

const MarkdownBlock =
  process.env.NODE_ENV === 'test'
    ? (require('./MarkdownBlock')
        .default as typeof import('./MarkdownBlock').default)
    : React.lazy(() => import('./MarkdownBlock'));

export interface MarkdownSlice {
  /** 块首字符在全文中的偏移（key 稳定性用）。 */
  offset: number;
  text: string;
}

/** 围栏代码行（``` / ~~~）。 */
const FENCE_LINE = /^\s{0,3}(```|~~~)/;

/**
 * 按围栏外空行切分 markdown 块（连续覆盖全文，跳过纯空白区）。
 * fence 行内空行不切；表格/列表/引用等 GFM 块内部不含空行，天然整块。
 */
export function splitMarkdownBlocks(text: string): MarkdownSlice[] {
  if (!text) return [];
  const lines = text.split('\n');
  // prefix[k] = 第 k 行首字符在全文中的偏移（prefix[lines.length] = text.length+1）
  const prefix = new Array<number>(lines.length + 1);
  prefix[0] = 0;
  for (let k = 0; k < lines.length; k++) {
    prefix[k + 1] = prefix[k] + lines[k].length + 1;
  }

  const slices: MarkdownSlice[] = [];
  let inFence = false;
  let blockStartLine = 0;

  const flush = (endLineExclusive: number) => {
    const blockText = lines.slice(blockStartLine, endLineExclusive).join('\n');
    if (blockText.trim()) {
      slices.push({ offset: prefix[blockStartLine], text: blockText });
    }
  };

  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (FENCE_LINE.test(line)) {
      inFence = !inFence;
    } else if (!inFence && line.trim() === '' && i > blockStartLine) {
      flush(i);
      // 跳过连续空行：块起点推进到下一个非空行
      let j = i;
      while (
        j + 1 < lines.length &&
        lines[j + 1].trim() === '' &&
        !FENCE_LINE.test(lines[j + 1])
      ) {
        j++;
      }
      blockStartLine = j + 1;
      i = j;
    }
    i++;
  }
  flush(lines.length);
  return slices;
}

interface MarkdownSliceViewProps {
  text: string;
  styles: Record<string, string>;
  workspaceId?: string;
  isStreaming?: boolean;
}

/** 单块视图：memo——源文本（及注入 props）不变即不重渲染/重解析。 */
const MarkdownSliceView = React.memo<MarkdownSliceViewProps>(
  function MarkdownSliceView({ text, styles, workspaceId, isStreaming }) {
    return (
      <Suspense
        fallback={
          <span data-testid="incremental-block-fallback" style={{ whiteSpace: 'pre-wrap' }}>
            {text}
          </span>
        }
      >
        <MarkdownBlock
          markdownText={text}
          styles={styles}
          isStreaming={isStreaming}
          workspaceId={workspaceId}
        />
      </Suspense>
    );
  },
);

export interface IncrementalMarkdownProps {
  /** 全量文本（流式期间 = 已冻结的 stableMarkdown）。 */
  text: string;
  styles: Record<string, string>;
  workspaceId?: string;
  isStreaming?: boolean;
}

/** 增量渲染：冻结块缓存 + 尾部块重解析。 */
export const IncrementalMarkdown: React.FC<IncrementalMarkdownProps> = ({
  text,
  styles,
  workspaceId,
  isStreaming,
}) => {
  const slices = useMemo(() => splitMarkdownBlocks(text), [text]);
  return (
    <>
      {slices.map((slice) => (
        <MarkdownSliceView
          key={`${slice.offset}:${slice.text.length}`}
          text={slice.text}
          styles={styles}
          workspaceId={workspaceId}
          isStreaming={isStreaming}
        />
      ))}
    </>
  );
};

export default React.memo(IncrementalMarkdown);
