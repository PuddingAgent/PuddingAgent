import { render, screen } from '@testing-library/react';
import * as React from 'react';
import { ChartBlock } from './ChartBlock';
import { RICH_BLOCK_LANGUAGES, isRichBlockLanguage } from './RichBlockRegistry';

/**
 * ```chart 围栏：内容必须是 JSON spec。
 * 重点是三类失败都要**显性说明**，不能画空图（空图会被误读为"数据全为 0"）。
 */
const inlineSpec = JSON.stringify({
  type: 'line',
  title: '温度',
  source: { kind: 'inline', rows: [{ t: '0', k: 24 }, { t: '1', k: 25 }] },
  x: 't',
  y: ['k'],
});

describe('ChartBlock', () => {
  it('inline spec 直接渲染图表', () => {
    render(<ChartBlock code={inlineSpec} />);
    expect(screen.getByTestId('mini-chart-svg')).toBeTruthy();
  });

  it('JSON 语法错误时给出可读错误', () => {
    render(<ChartBlock code="{ not json" />);
    const error = screen.getByTestId('chart-block-error');
    expect(error.textContent).toContain('图表 spec 无效');
    expect(error.textContent).toContain('JSON 解析失败');
  });

  it('缺少 y 时指出字段问题', () => {
    render(
      <ChartBlock
        code={JSON.stringify({ type: 'line', source: { kind: 'inline', rows: [] }, x: 't' })}
      />,
    );
    expect(screen.getByTestId('chart-block-error').textContent).toContain('y 必须是非空数组');
  });

  it('不支持的图表类型会列出可用类型', () => {
    render(
      <ChartBlock
        code={JSON.stringify({
          type: 'radar',
          source: { kind: 'inline', rows: [] },
          x: 't',
          y: ['k'],
        })}
      />,
    );
    const text = screen.getByTestId('chart-block-error').textContent ?? '';
    expect(text).toContain('radar');
    expect(text).toContain('line');
  });

  it('artifact 来源但没有宿主解析器时显式提示（不画空图）', () => {
    render(
      <ChartBlock
        code={JSON.stringify({
          type: 'line',
          source: { kind: 'artifact', artifactId: 'vision-abc' },
          x: 't',
          y: ['k'],
        })}
      />,
    );
    const notice = screen.getByTestId('chart-block-needs-data');
    expect(notice.textContent).toContain('vision-abc');
    expect(screen.queryByTestId('mini-chart-svg')).toBeNull();
  });

  it('注入解析器后 artifact 来源能正常渲染', () => {
    render(
      <ChartBlock
        code={JSON.stringify({
          type: 'bar',
          source: { kind: 'artifact', artifactId: 'vision-abc' },
          x: 't',
          y: ['k'],
        })}
        resolveArtifactRows={() => [{ t: '0', k: 1 }, { t: '1', k: 2 }]}
      />,
    );
    expect(screen.getByTestId('mini-chart-svg')).toBeTruthy();
  });

  it('解析器返回 null 时回到"没有数据行"错误', () => {
    render(
      <ChartBlock
        code={JSON.stringify({
          type: 'line',
          source: { kind: 'artifact', artifactId: 'x' },
          x: 't',
          y: ['k'],
        })}
        resolveArtifactRows={() => null}
      />,
    );
    expect(screen.getByTestId('mini-chart-error').textContent).toContain('没有数据行');
  });
});

describe('RichBlockRegistry', () => {
  it('chart 已注册', () => {
    expect(isRichBlockLanguage('chart')).toBe(true);
    expect(RICH_BLOCK_LANGUAGES).toContain('chart');
  });

  it('大小写不敏感', () => {
    expect(isRichBlockLanguage('CHART')).toBe(true);
  });

  it('未注册语言返回 undefined（调用方回退为代码块）', () => {
    expect(isRichBlockLanguage('python')).toBe(false);
    expect(isRichBlockLanguage(null)).toBe(false);
  });
});
