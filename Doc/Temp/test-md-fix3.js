// Final verification
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
      } else {
        out.push(prefix + '测试结果');
        out.push('');
        out.push(tablePart);
      }
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

// Test S1
const r1 = preprocessMarkdown("## | 测试项 | 结果 | 说明 |\n|--------|------|------|\n| A | B | C |");
console.log('S1:', r1.includes('\n## 测试结果\n\n|') ? 'PASS' : 'FAIL');
console.log(r1);

console.log('---');

// Test S2
const r2 = preprocessMarkdown("## 测试结果\n| 测试项 | 结果 |\n|--------|------|\n| A | B |");
console.log('S2:', r2.includes('\n## 测试结果\n\n|') ? 'PASS' : 'FAIL');
console.log(r2);
