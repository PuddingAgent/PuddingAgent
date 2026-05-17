// 验证 Markdown 预处理修复

const preprocessMarkdown = (md) => {
  const lines = md.split('\n');
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();
    const headingMatch = /^(#{1,6}\s+)(.*)$/.exec(trimmed);
    if (headingMatch && headingMatch[2].includes('|')) {
      const prefix = headingMatch[1];
      const rest = headingMatch[2];
      const pipeIdx = rest.indexOf('|');
      const headingText = rest.substring(0, pipeIdx).trim();
      const tablePart = rest.substring(pipeIdx).trim();
      if (headingText) {
        out.push(prefix + headingText);
        out.push('');
        out.push(tablePart);
        i++;
        continue;
      }
      out.push('');
      out.push(rest);
      i++;
      continue;
    }
    if (/^\|.*\|$/.test(trimmed) || /^\|[-:| ]+\|$/.test(trimmed)) {
      const parts = [line];
      i++;
      while (i < lines.length) {
        const nl = lines[i].trim();
        if (/^\|/.test(nl)) break;
        if (nl === '') { i++; break; }
        parts.push(lines[i]);
        i++;
      }
      const joined = parts.join(' ');
      const fixed = joined.replace(/```[^\n`]*\s*/g, '`').replace(/\s*```/g, '`');
      if (out.length > 0 && /^#{1,6}\s/.test(out[out.length - 1].trim())) {
        out.push('');
      }
      out.push(fixed);
    } else {
      out.push(line);
      i++;
    }
  }
  return out.join('\n');
};

// 模拟 LLM 实际输出: heading + 表格无空行分隔
const input = `## 测试结果
| 测试项 | 结果 | 说明 |
|--------|------|------|
| 子代理创建 | ✅ 成功 | \`spawn_sub_agent\` 立即返回 |
| 任务执行 | ✅ 成功 | 后台完成全部探索 |`;

const output = preprocessMarkdown(input);
console.log('=== INPUT ===');
console.log(input);
console.log('');
console.log('=== OUTPUT ===');
console.log(output);
console.log('');
console.log('=== LINE-BY-LINE ===');
output.split('\n').forEach((l, j) => console.log(j + ': ' + JSON.stringify(l)));
