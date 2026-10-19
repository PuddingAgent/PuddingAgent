// ── 文档大纲 ───────────────────────────────────────────────────
// 从 markdown 提取 H1–H3，供右侧文档视图的侧边目录使用。
//
// 与 messageFold 一样必须**跳过围栏代码块**：脚本/配置里的 `# 注释` 不是标题，
// 否则大纲里会冒出一堆 shell 注释。
//
// 只返回层级与文本，不给标题注入 id——渲染端（MarkdownPreview）是受控组件，
// 改它去注 id 会把"安全渲染"和"目录导航"耦合起来。导航改用
// 「按序取 h1,h2,h3 节点」的方式实现，两边顺序天然一致。
// ─────────────────────────────────────────────────────────────

export interface OutlineItem {
  level: 1 | 2 | 3;
  text: string;
  /** 在源文本中的行号（0-based），排障用。 */
  line: number;
}

const FENCE_PATTERN = /^\s{0,3}(```|~~~)/;
const HEADING_PATTERN = /^(#{1,3})\s+(.+?)\s*#*\s*$/;

export function extractOutline(markdown: string, maxLevel: 1 | 2 | 3 = 3): OutlineItem[] {
  if (!markdown) return [];

  const items: OutlineItem[] = [];
  let inFence = false;

  markdown.split('\n').forEach((line, index) => {
    if (FENCE_PATTERN.test(line)) {
      inFence = !inFence;
      return;
    }
    if (inFence) return;

    const match = HEADING_PATTERN.exec(line);
    if (!match) return;

    const level = match[1].length as 1 | 2 | 3;
    if (level > maxLevel) return;

    const text = match[2].trim();
    if (!text) return;

    items.push({ level, text, line: index });
  });

  return items;
}
