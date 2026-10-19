// ── 长消息判定 ─────────────────────────────────────────────────
// 用来决定一条助手消息是否折叠成"摘要卡 + 在右侧打开全文"。
//
// 为什么不能只看字数：
// 设计方案类内容的特征是**多标题、多表格、多代码块**。一份 1800 字但有 6 个
// 小节 + 4 段代码的文档，在对话里滚起来比 4000 字的一段散文更痛苦。
//
// 因此判据是三条任一满足：
//   字数 > 3000  或  标题数 ≥ 3  或  围栏代码块 ≥ 3
//
// 扫描必须跳过围栏代码块：代码里的 `# 注释` 不是标题，``` 也不是新代码块。
// ─────────────────────────────────────────────────────────────

export interface FoldThresholds {
  chars: number;
  headings: number;
  codeBlocks: number;
}

export const FOLD_THRESHOLDS: FoldThresholds = {
  chars: 3000,
  headings: 3,
  codeBlocks: 3,
};

export type FoldReason = 'length' | 'headings' | 'codeBlocks';

export interface FoldDecision {
  fold: boolean;
  reasons: FoldReason[];
  chars: number;
  headings: number;
  codeBlocks: number;
}

const FENCE_PATTERN = /^\s*(```|~~~)/;
const HEADING_PATTERN = /^#{1,6}\s+\S/;

/** 分析 markdown 结构，给出是否折叠及原因。纯函数，便于单测。 */
export function analyzeLongMessage(
  markdown: string,
  thresholds: FoldThresholds = FOLD_THRESHOLDS,
): FoldDecision {
  const chars = markdown.length;
  let headings = 0;
  let codeBlocks = 0;
  let inFence = false;

  for (const line of markdown.split('\n')) {
    if (FENCE_PATTERN.test(line)) {
      if (!inFence) codeBlocks += 1;
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    if (HEADING_PATTERN.test(line)) headings += 1;
  }

  const reasons: FoldReason[] = [];
  if (chars > thresholds.chars) reasons.push('length');
  if (headings >= thresholds.headings) reasons.push('headings');
  if (codeBlocks >= thresholds.codeBlocks) reasons.push('codeBlocks');

  return { fold: reasons.length > 0, reasons, chars, headings, codeBlocks };
}

/**
 * 折叠卡上显示的前若干行。
 * 刻意按**行**截断而不是按字数：按字数会把一行代码或一个表格行切一半，
 * 展开后的缩略看起来像乱码。
 */
export function excerptLines(markdown: string, maxLines = 15): string {
  const lines = markdown.split('\n');
  if (lines.length <= maxLines) return markdown;
  return lines.slice(0, maxLines).join('\n');
}
