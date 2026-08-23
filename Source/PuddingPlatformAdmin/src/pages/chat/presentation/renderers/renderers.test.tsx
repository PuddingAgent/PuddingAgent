// ── 行为链 P3: 五类 presentation renderer 测试 ────────────────────────────
// 验收（§3.5）：meta（G1/G2 契约字段）优先、payload 回退解析、缺失不伪造、
// 未注册类型回落 Generic（PresentationRegistry 已有覆盖）。
import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { resolveRenderer } from '../PresentationRegistry';
import { TerminalRenderer } from './terminal';
import { DiffRenderer, parseDiffLines } from './diff';
import { ReadRenderer } from './read';
import { SearchRenderer } from './search';
import { WebRenderer } from './web';
import { GenericRenderer } from './generic';

describe('Registry 分派', () => {
  it('五类专用 renderer 已注册；delegation/job 回落 Generic', () => {
    expect(resolveRenderer('terminal')).toBe(TerminalRenderer);
    expect(resolveRenderer('diff')).toBe(DiffRenderer);
    expect(resolveRenderer('read')).toBe(ReadRenderer);
    expect(resolveRenderer('search')).toBe(SearchRenderer);
    expect(resolveRenderer('web')).toBe(WebRenderer);
    expect(resolveRenderer('delegation')).toBe(GenericRenderer);
    expect(resolveRenderer('job')).toBe(GenericRenderer);
  });
});

describe('TerminalRenderer', () => {
  it('meta 命令 + exit code pill + 输出窗口', () => {
    render(
      <TerminalRenderer
        meta={{ command: 'dotnet build', exitCode: 0 }}
        payload={'Build succeeded.'}
      />,
    );
    expect(screen.getByTestId('presentation-terminal')).toBeTruthy();
    expect(screen.getByTestId('presentation-terminal').textContent).toContain(
      'dotnet build',
    );
    expect(screen.getByTestId('presentation-terminal-exit').textContent).toBe(
      'exit 0',
    );
    expect(
      screen.getByTestId('presentation-terminal-output').textContent,
    ).toContain('Build succeeded.');
  });

  it('payload 回退解析命令（纯参数 JSON）；非零 exit 染错误色', () => {
    render(
      <TerminalRenderer
        payload={'{"command":"git push","exitCode":128}'}
      />,
    );
    expect(screen.getByTestId('presentation-terminal').textContent).toContain(
      'git push',
    );
    expect(screen.getByTestId('presentation-terminal-exit').textContent).toBe(
      'exit 128',
    );
    // payload 即参数 JSON 时正文窗口不重复展示参数本身
    expect(screen.queryByTestId('presentation-terminal-output')).toBeNull();
  });

  it('空入参 → null', () => {
    const { container } = render(<TerminalRenderer />);
    expect(container.firstChild).toBeNull();
  });
});

describe('DiffRenderer', () => {
  const diffText = [
    '--- a/src/app.ts',
    '+++ b/src/app.ts',
    '@@ -1,3 +1,4 @@',
    ' const a = 1;',
    '+const b = 2;',
    '-const c = 3;',
    ' export {};',
  ].join('\n');

  it('增删行计数 + 文件路径提取 + 复制', () => {
    render(<DiffRenderer payload={diffText} />);
    const card = screen.getByTestId('presentation-diff');
    expect(card.textContent).toContain('src/app.ts');
    expect(screen.getByTestId('presentation-diff-adds').textContent).toBe('+1');
    expect(screen.getByTestId('presentation-diff-dels').textContent).toBe('−1');
    const writeText = jest.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    fireEvent.click(screen.getByTestId('renderer-copy'));
    expect(writeText).toHaveBeenCalledWith(diffText);
  });

  it('非 diff 文本：原样展示（不解析不伪造）', () => {
    render(<DiffRenderer payload={'plain text result'} />);
    expect(screen.getByTestId('presentation-diff-body').textContent).toContain(
      'plain text result',
    );
  });

  it('parseDiffLines：无 diff 标记返回 null', () => {
    expect(parseDiffLines('just text\nmore text')).toBeNull();
  });
});

describe('ReadRenderer', () => {
  it('meta 路径 + 行范围 + 内容窗口', () => {
    render(
      <ReadRenderer
        meta={{ path: 'src/index.ts', startLine: 10, endLine: 24 }}
        payload={'export const x = 1;'}
      />,
    );
    const card = screen.getByTestId('presentation-read');
    expect(card.textContent).toContain('src/index.ts');
    expect(card.textContent).toContain('行 10–24');
    expect(screen.getByTestId('presentation-read-content').textContent).toContain(
      'export const x = 1;',
    );
  });
});

describe('SearchRenderer', () => {
  it('hits 数组：命中数 pill + 有界列表（>20 条截断）', () => {
    const hits = Array.from({ length: 25 }, (_, i) => `file-${i}.ts:3:match`);
    render(
      <SearchRenderer meta={{ query: 'useState' }} payload={JSON.stringify({ hits })} />,
    );
    expect(screen.getByTestId('presentation-search-count').textContent).toBe(
      '25 命中',
    );
    expect(screen.getByTestId('presentation-search-body').textContent).toContain(
      '共 25 条命中',
    );
  });

  it('纯文本输出：无命中数 pill（不伪造计数），展示查询词', () => {
    render(
      <SearchRenderer meta={{ query: 'formatDuration' }} payload={'3 results in 2 files'} />,
    );
    expect(screen.queryByTestId('presentation-search-count')).toBeNull();
    expect(screen.getByTestId('presentation-search').textContent).toContain(
      'formatDuration',
    );
  });
});

describe('WebRenderer', () => {
  it('动作 + URL + 标题；失败状态染错误色', () => {
    render(
      <WebRenderer
        meta={{ action: 'navigate', url: 'https://example.com/page', title: 'Example' }}
        payload={'loaded'}
      />,
    );
    expect(screen.getByTestId('presentation-web-action').textContent).toBe(
      'navigate',
    );
    expect(screen.getByTestId('presentation-web').textContent).toContain(
      'https://example.com/page',
    );
  });

  it('空入参 → null', () => {
    const { container } = render(<WebRenderer />);
    expect(container.firstChild).toBeNull();
  });
});
