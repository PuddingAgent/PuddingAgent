import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import ComposerContextBar from './ComposerContextBar';

jest.mock('../styles', () => {
  const styles = new Proxy(
    {},
    {
      get: (_target, prop) => String(prop),
    },
  );
  return {
    useChatStyles: () => ({
      styles,
    }),
  };
});

describe('ComposerContextBar', () => {
  it('renders the percentage text and compaction status', () => {
    render(
      <ComposerContextBar
        tLimit={128000}
        tUsed={100000}
        tPct={78}
        compactionStatus="上次压缩：2 分钟前"
      />,
    );
    expect(screen.getByText('上下文窗口 78% 已用')).toBeTruthy();
    expect(screen.getByText('上次压缩：2 分钟前')).toBeTruthy();
  });

  it('shows unconfigured copy when the window limit is unknown', () => {
    render(<ComposerContextBar tLimit={0} tUsed={0} tPct={0} />);
    expect(screen.getByText('上下文窗口 未配置')).toBeTruthy();
  });

  it('opens a popup with window details on click', () => {
    render(
      <ComposerContextBar
        tLimit={128000}
        tUsed={100000}
        tPct={78}
        cacheHitTokens={60000}
        cacheMissTokens={40000}
      />,
    );
    fireEvent.click(screen.getByTestId('composer-context-bar'));
    expect(screen.getByTestId('composer-context-bar-popup')).toBeTruthy();
    expect(screen.getByText('总占用')).toBeTruthy();
    expect(screen.getByText('缓存命中率')).toBeTruthy();
    expect(screen.getByText('60%')).toBeTruthy();
  });
});
