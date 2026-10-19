import { render } from '@testing-library/react';
import * as React from 'react';
import { ArtifactDataProvider, useArtifactData } from './ArtifactDataContext';
import type { ArtifactDataResolver } from './ArtifactDataContext';

/**
 * 数据解析器注入：核心约束是 **context value 引用恒定**。
 *
 * 如果它每次渲染都变，MarkdownBlock 缓存的 react-markdown components 会重建，
 * 每条消息的 markdown 都要重解析——长对话里代价很大，而且不报错。
 */
const capture = (): {
  Probe: React.FC<{ onValue: (value: ArtifactDataResolver | null) => void }>;
} => ({
  Probe: ({ onValue }) => {
    onValue(useArtifactData());
    return null;
  },
});

describe('ArtifactDataContext', () => {
  it('无 Provider 时返回 null（图表据此显式提示，不画空图）', () => {
    const { Probe } = capture();
    let seen: ArtifactDataResolver | null | undefined;
    render(<Probe onValue={(value) => (seen = value)} />);
    expect(seen).toBeNull();
  });

  it('外部每次传新 resolver 对象，context value 引用仍不变', () => {
    const { Probe } = capture();
    let first: ArtifactDataResolver | null | undefined;
    let second: ArtifactDataResolver | null | undefined;

    const { rerender } = render(
      <ArtifactDataProvider resolver={{ resolveArtifactRows: () => [] }}>
        <Probe onValue={(value) => (first = value)} />
      </ArtifactDataProvider>,
    );

    // 故意传一个全新对象（模拟调用方没做 useCallback）
    rerender(
      <ArtifactDataProvider resolver={{ resolveArtifactRows: () => [] }}>
        <Probe onValue={(value) => (second = value)} />
      </ArtifactDataProvider>,
    );

    expect(first).not.toBeNull();
    expect(second).toBe(first);
  });

  it('引用虽稳定，但内部调用的是最新 resolver', () => {
    const { Probe } = capture();
    let stable: ArtifactDataResolver | null | undefined;

    const { rerender } = render(
      <ArtifactDataProvider resolver={{ resolveArtifactRows: () => [{ v: 'old' }] }}>
        <Probe onValue={(value) => (stable = value)} />
      </ArtifactDataProvider>,
    );

    expect(stable?.resolveArtifactRows('x')).toEqual([{ v: 'old' }]);

    rerender(
      <ArtifactDataProvider resolver={{ resolveArtifactRows: () => [{ v: 'new' }] }}>
        <Probe onValue={(value) => (stable = value)} />
      </ArtifactDataProvider>,
    );

    expect(stable?.resolveArtifactRows('x')).toEqual([{ v: 'new' }]);
  });
});
