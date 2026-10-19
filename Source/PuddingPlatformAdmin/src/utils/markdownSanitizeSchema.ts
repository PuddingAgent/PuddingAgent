import { defaultSchema } from 'rehype-sanitize';

/**
 * chat 正文的 HTML 白名单。
 *
 * ## 为什么必须有它
 *
 * `MarkdownBlock` 用 `rehypeRaw` 解析模型输出里的原始 HTML——起因只是要把
 * `preprocessMarkdown` 注入的 `<span data-md-emoji>` 渲染出来。但开启 raw HTML
 * 而**不做消毒**，等于把模型输出（以及它可能读到的外部内容）当成可执行 HTML。
 * 这与 LightRAG 被披露的存储型 XSS（GHSA-xpjq-3w4w-w5wr）是完全同构的形态：
 * 一个 `<iframe srcdoc="<script>…">` 或 `<svg><script>` 就能在查看者浏览器里执行。
 *
 * ## 为什么消毒放在 KaTeX 之前
 *
 * 管线顺序是 `rehypeRaw → rehypeSanitize → rehypeKatex`。
 * KaTeX 的输出由库自己生成（不是用户 HTML），但它会写内联样式与大量 MathML
 * 属性；若排在消毒之后，这些会被白名单剥掉，公式直接错版。
 * KaTeX 侧的危险面（`\href{javascript:}`、`\includegraphics` 远程加载）
 * 由渲染时的 `trust: false` 关闭，不依赖本 schema。
 *
 * ## 白名单只放行「功能真的需要」的东西
 *
 * - `code[class^=language-]`：代码块语言分派的基础（现状用它区分 image，
 *   后续 Mermaid / ECharts / 表格等富媒体块也走同一个分派）。
 * - `span[data-md-emoji]`：emoji 字号收敛标记。
 * - `th/td[align]`：GFM 表格对齐。
 * - `mark` / `u`：模型常用但默认 schema 不含。
 *
 * 危险项一律保留默认阻断：`<script>` `<iframe>` `<object>` `<embed>` `<style>`、
 * 全部 `on*` 事件、`style` 属性、`javascript:` / `data:` 协议。
 */
export const markdownSanitizeSchema = {
  ...defaultSchema,
  tagNames: [...(defaultSchema.tagNames ?? []), 'mark', 'u'],
  attributes: {
    ...defaultSchema.attributes,
    code: [
      ...(defaultSchema.attributes?.code ?? []),
      ['className', /^language-[\w+-]+$/],
    ],
    span: [...(defaultSchema.attributes?.span ?? []), 'dataMdEmoji'],
    th: [...(defaultSchema.attributes?.th ?? []), 'align'],
    td: [...(defaultSchema.attributes?.td ?? []), 'align'],
  },
  protocols: {
    ...defaultSchema.protocols,
    href: ['http', 'https', 'mailto'],
    src: ['http', 'https'],
  },
};

export default markdownSanitizeSchema;
