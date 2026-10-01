import { render, screen } from '@testing-library/react';
import * as React from 'react';
import FrontendVersionBadge from './index';

/**
 * 徽标必须能从“构建期注入的信息”渲染出版本号/哈希/时间；
 * 注入缺失时退化为 v0.0.0 · unknown（见 utils/frontendBuild.ts）。
 */
const setInjected = (value: unknown) => {
  (globalThis as unknown as { __PUDDING_FRONTEND__?: unknown }).__PUDDING_FRONTEND__ =
    value;
};

describe('FrontendVersionBadge', () => {
  afterEach(() => setInjected(undefined));

  it('渲染构建期注入的版本号、短哈希与构建时间', () => {
    setInjected({
      version: '9.9.9',
      commit: 'deadbeefdeadbeef',
      commitShort: 'deadbee',
      commitTime: '2026-10-01T12:00:00.000Z',
      dirty: false,
      builtAt: '2026-10-01T14:57:00.000Z',
    });

    render(<FrontendVersionBadge />);

    const badge = screen.getByTestId('frontend-version-badge');
    expect(badge.textContent).toMatch(
      /^v9\.9\.9 · deadbee · \d{4}-\d{2}-\d{2} \d{2}:\d{2}$/,
    );
    // 可见文本本身就是可访问名称（不依赖 tooltip 或 aria-label）
    expect(badge.tagName).toBe('SPAN');
    expect((badge.getAttribute('class') ?? '').length).toBeGreaterThan(0);
  });

  it('脏工作树在徽标上标出 +dirty', () => {
    setInjected({
      version: '9.9.9',
      commit: 'deadbeef',
      commitShort: 'deadbee',
      commitTime: '',
      dirty: true,
      builtAt: '2026-10-01T14:57:00.000Z',
    });

    render(<FrontendVersionBadge />);

    expect(
      screen.getByTestId('frontend-version-badge').textContent,
    ).toContain('v9.9.9+dirty');
  });

  it('未注入时退化为 unknown 占位而非编造版本', () => {
    setInjected(undefined);

    render(<FrontendVersionBadge />);

    expect(screen.getByTestId('frontend-version-badge').textContent).toBe(
      'v0.0.0 · unknown',
    );
  });
});
