/**
 * YAML front matter 与正文的切分。
 *
 * **为什么必须处理**：仓库里的设计文档与报告都带 front matter，直接丢给 Markdown
 * 渲染器会得到一张 YAML 表格（2026-10-20 在 `/doc-preview` 实测：打开「部署操作手册」
 * 第一屏是 `title: … / author: … / status: draft` 的两列表格，正文标题被压到下面）。
 * 读者要的是文档，不是元数据。
 *
 * 三条被刻意写死的规则：
 * 1. **只认文件开头**。正文中间的分隔线 `---` 是 Markdown 水平线，不是 front matter；
 *    在中间做匹配会把正文切掉一半。
 * 2. **必须有闭合**。只有开头的 `---` 而没有结束行时原样返回 —— 宁可显示一段
 *    异常的文本，也不要吞掉整篇文档。
 * 3. **不做 YAML 解析**。这一层只负责切开，不解释内容；解析失败不该让预览失败。
 */

export interface SplitDocumentResult {
  /** 去掉 front matter 后的正文（保持原始换行，不做 trim 之外的改写）。 */
  body: string;
  /** 原始 front matter 文本（不含两端分隔线）；没有则为 undefined。 */
  frontMatter?: string;
}

/** 一行是否只是分隔线：允许行尾空白与 CR。 */
function isFenceLine(line: string): boolean {
  const trimmed = line.trim();
  return trimmed === '---' || trimmed === '...';
}

/**
 * 把文档切成 { frontMatter, body }。
 *
 * 没有 front matter 时 `body` **逐字等于**输入（包括开头的空行），
 * 这样调用方不需要区分两种路径。
 */
export function splitFrontMatter(markdown: string): SplitDocumentResult {
  // BOM 会让「第一行是 ---」的判断失效，先剥掉（只在开头）。
  const source = markdown.charCodeAt(0) === 0xfeff ? markdown.slice(1) : markdown;
  const lines = source.split(/\r?\n/);

  if (lines.length === 0 || !isFenceLine(lines[0])) {
    return { body: markdown };
  }

  // 从第 2 行起找闭合；找不到就认为这不是 front matter。
  for (let index = 1; index < lines.length; index += 1) {
    if (!isFenceLine(lines[index])) continue;

    const frontMatter = lines.slice(1, index).join('\n');
    // 空块（--- 紧接 ---）不算 front matter：那更可能是被删空的占位。
    if (frontMatter.trim().length === 0) return { body: markdown };

    return {
      frontMatter,
      body: lines.slice(index + 1).join('\n').replace(/^\s*\n/, ''),
    };
  }

  return { body: markdown };
}
