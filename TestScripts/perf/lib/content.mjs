// 合成内容生成器（被录制器与诊断脚本共用，无副作用）。
/** 生成一段结构化长 Markdown（段落 + 表格 + 代码块 + 列表），贴近截图场景。 */
export function buildLongMarkdown(targetChars = 50_000) {
  const parts = [];
  parts.push('# 性能诊断长回复样本\n');
  parts.push(
    '本段用于复现截图中的长 Markdown 回复：包含多级标题、长段落、表格、围栏代码与列表，且体量足以触发内容权重虚拟化门槛。\n',
  );
  let section = 0;
  while (parts.join('').length < targetChars) {
    section += 1;
    parts.push(`\n## ${section}. 第 ${section} 节分析\n`);
    parts.push(
      `这是第 ${section} 节的正文段落，用于填充真实的文本流。段落中混合中英文与 inline code \`handleSection${section}()\`，并引用变量 \`renderWeight\`、\`contentWeight\` 与阈值 16000。` +
        '排版上保持长行，以便观察换行、断词与行高测量带来的布局成本。'.repeat(3) +
        '\n',
    );
    parts.push('\n| 指标 | p50 | p95 | 说明 |\n|---|---|---|---|\n');
    for (let r = 1; r <= 6; r += 1) {
      parts.push(
        `| 指标-${section}-${r} | ${(r * 3.7).toFixed(1)}ms | ${(r * 11.3).toFixed(1)}ms | 第 ${section} 组第 ${r} 行样本 |\n`,
      );
    }
    parts.push('\n```ts\n');
    parts.push(`export function handleSection${section}(input: ViewportInput) {\n`);
    parts.push(`  const weight = input.items.reduce((sum, item) => sum + item.block.content.length, 0);\n`);
    parts.push(`  if (weight >= 16_000) return virtualize(input);\n`);
    parts.push(`  return input.items.map((item) => ({ ...item, section: ${section} }));\n`);
    parts.push('}\n```\n');
    parts.push('\n- 要点一：冻结块应保持稳定身份，避免整段重新解析。\n');
    parts.push('- 要点二：尾块随流式增长，只应重扫尾部。\n');
    parts.push('- 要点三：屏外块应减少布局与绘制，但不能免除解析与挂载。\n');
    parts.push('\n> 引用：本样本由性能录制器生成，仅用于渲染成本测量。\n');
  }
  return parts.join('');
}
