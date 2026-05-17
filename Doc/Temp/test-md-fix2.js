// 测试 heading 与表格头混在同一行的场景
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

// 场景1：heading 与表格头粘连 (## | 测试项 | 结果 | 说明 |)
const input1 = `## | 测试项 | 结果 | 说明 |
|--------|------|------|
| 子代理 | ✅ | \`spawn\` 完成 |`;
console.log('=== SCENARIO 1: heading+table on same line ===');
console.log('INPUT: ' + JSON.stringify(input1));
console.log('OUTPUT:');
preprocessMarkdown(input1).split('\n').forEach((l,j) => console.log('  ' + j + ': ' + JSON.stringify(l)));
console.log('PASS: ' + (preprocessMarkdown(input1).includes('\n## 测试项\n\n|') ? 'YES' : 'NO'));

// 场景2：heading 后直接接表格（无空行，但不同行）
const input2 = `## 测试结果
| 测试项 | 结果 | 说明 |
|--------|------|------|
| 子代理 | ✅ | 完成 |`;
console.log('\n=== SCENARIO 2: no blank line between heading and table ===');
console.log('INPUT: ' + JSON.stringify(input2));
console.log('OUTPUT:');
preprocessMarkdown(input2).split('\n').forEach((l,j) => console.log('  ' + j + ': ' + JSON.stringify(l)));
console.log('PASS: ' + (preprocessMarkdown(input2).includes('\n## 测试结果\n\n|') ? 'YES' : 'NO'));
