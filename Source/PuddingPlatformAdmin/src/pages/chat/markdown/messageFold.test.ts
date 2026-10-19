import { analyzeLongMessage, excerptLines, FOLD_THRESHOLDS } from './messageFold';

/**
 * 折叠判据：重点是"结构型长"也要折叠，以及围栏代码块不能污染统计。
 */
const longProse = '这是一段文字。'.repeat(500); // 3500 字

describe('analyzeLongMessage', () => {
  it('短消息不折叠', () => {
    const decision = analyzeLongMessage('客厅 26.1°C，其余平稳。');
    expect(decision.fold).toBe(false);
    expect(decision.reasons).toHaveLength(0);
  });

  it('超长纯文本因字数折叠', () => {
    const decision = analyzeLongMessage(longProse);
    expect(decision.fold).toBe(true);
    expect(decision.reasons).toContain('length');
    expect(decision.chars).toBe(longProse.length);
  });

  it('字数不多但标题多也要折叠', () => {
    const doc = ['# 一', '内容', '## 二', '内容', '### 三', '内容'].join('\n');
    const decision = analyzeLongMessage(doc);
    expect(decision.fold).toBe(true);
    expect(decision.reasons).toContain('headings');
    expect(decision.headings).toBe(3);
  });

  it('代码块多也要折叠', () => {
    const doc = ['```ts', 'a', '```', '说明', '```ts', 'b', '```', '说明', '```ts', 'c', '```'].join('\n');
    const decision = analyzeLongMessage(doc);
    expect(decision.fold).toBe(true);
    expect(decision.reasons).toContain('codeBlocks');
    expect(decision.codeBlocks).toBe(3);
  });

  it('代码块内的 # 注释不算标题', () => {
    const doc = ['```bash', '# 注释一', '# 注释二', '# 注释三', '```'].join('\n');
    const decision = analyzeLongMessage(doc);
    expect(decision.headings).toBe(0);
    expect(decision.reasons).not.toContain('headings');
  });

  it('未闭合的围栏也按代码块计入，不会把后续行当标题', () => {
    const doc = ['```ts', '# 不是标题', 'const a = 1;'].join('\n');
    const decision = analyzeLongMessage(doc);
    expect(decision.codeBlocks).toBe(1);
    expect(decision.headings).toBe(0);
  });

  it('阈值可配置（调用方可以按场景放宽）', () => {
    const decision = analyzeLongMessage('# 一\n## 二', {
      ...FOLD_THRESHOLDS,
      headings: 2,
    });
    expect(decision.reasons).toContain('headings');
  });
});

describe('excerptLines', () => {
  it('按行截断，不会切半行', () => {
    const text = Array.from({ length: 30 }, (_, i) => `第 ${i} 行`).join('\n');
    const excerpt = excerptLines(text, 15);
    expect(excerpt.split('\n')).toHaveLength(15);
    expect(excerpt.endsWith('第 14 行')).toBe(true);
  });

  it('不足行数时原样返回', () => {
    expect(excerptLines('a\nb', 15)).toBe('a\nb');
  });
});
