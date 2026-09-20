// ── S6b-2 分类器健康明显提示横幅测试 ──────────────────────────────
// 注意：遵循本仓测试约定（见 GoalBanner.test.tsx 头注），本文件不使用
// 「jest.mock 工厂 + import type」组合；fetchHealth 经 props 注入，不触网。
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import {
  CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS,
  FETCH_ERROR_DECISION,
  HEALTH_LABELS,
  HEALTH_TAG_COLOR,
  resolveBannerDecision,
} from './ClassifierHealthBanner';
import ClassifierHealthBanner from './ClassifierHealthBanner';

// 本地结构类型（与组件 props 结构兼容），避免 import type 组合坑。
type HealthState = 'unknown' | 'healthy' | 'degraded' | 'unavailable';

interface HealthItem {
  classifierId: string;
  health: HealthState;
  detail?: string;
  consecutiveFailures: number;
  lastCheckedAtUtc?: string;
  lastLatencyMs?: number;
}

interface HealthSnapshot {
  configured: boolean;
  classifiers: HealthItem[];
}

const makeItem = (overrides: Partial<HealthItem> = {}): HealthItem => ({
  classifierId: 'jev-main',
  health: 'healthy',
  detail: undefined,
  consecutiveFailures: 0,
  lastCheckedAtUtc: '2026-09-21T08:00:00Z',
  lastLatencyMs: 1100,
  ...overrides,
});

const makeSnapshot = (
  overrides: Partial<HealthSnapshot> = {},
  items: HealthItem[] = [makeItem()],
): HealthSnapshot => ({
  configured: true,
  classifiers: items,
  ...overrides,
});

const renderBanner = async (snapshot: HealthSnapshot, props: Record<string, unknown> = {}) => {
  const fetchHealth = jest.fn().mockResolvedValue(snapshot);
  const view = render(
    <ClassifierHealthBanner
      fetchHealth={fetchHealth as unknown as () => Promise<HealthSnapshot>}
      {...props}
    />,
  );
  await screen.findByTestId('classifier-health-banner');
  return view;
};

describe('ClassifierHealthBanner', () => {
  it('shows an error banner (never silent pass-through) when any classifier is unavailable', async () => {
    await renderBanner(
      makeSnapshot({}, [
        makeItem({ health: 'healthy' }),
        makeItem({ classifierId: 'jev-backup', health: 'unavailable', consecutiveFailures: 3 }),
      ]),
    );

    const banner = screen.getByTestId('classifier-health-banner');
    expect(banner.getAttribute('data-banner-kind')).toBe('error');
    expect(banner.textContent).toContain('不可用');
    expect(banner.textContent).toContain('延迟或挂起');
    expect(banner.textContent).toContain('不会静默放行');
    // 明细折叠面板：展开后健康档位以中文+颜色 Tag 呈现，不暴露原始英文枚举。
    fireEvent.click(screen.getByText('分类器健康明细'));
    expect(await screen.findByText('不可用').then((el) => el.tagName)).toBe('SPAN');
  });

  it('shows a warning banner when a classifier is degraded but none unavailable', async () => {
    await renderBanner(
      makeSnapshot({}, [makeItem({ health: 'degraded', detail: '超时率上升' })]),
    );

    const banner = screen.getByTestId('classifier-health-banner');
    expect(banner.getAttribute('data-banner-kind')).toBe('warning');
    expect(banner.textContent).toContain('降级');
    // 展开明细后：降级档位 Tag 与后端 detail 人类可读说明可见。
    fireEvent.click(screen.getByText('分类器健康明细'));
    expect(await screen.findByText('超时率上升')).toBeTruthy();
    expect(screen.getByText('降级')).toBeTruthy();
  });

  it('treats configured=false as explicit unknown and never as healthy', async () => {
    await renderBanner(makeSnapshot({ configured: false }, []));

    const banner = screen.getByTestId('classifier-health-banner');
    expect(banner.getAttribute('data-banner-kind')).toBe('warning');
    expect(banner.textContent).toContain('未接线');
    expect(banner.textContent).toContain('状态未知');
    // 服务端权威：未知态绝不渲染出「健康」档位标签。
    expect(screen.queryByText('健康')).toBeNull();
  });

  it('shows fetch failure as explicit unknown instead of falling back to healthy', async () => {
    const fetchHealth = jest.fn().mockRejectedValue(new Error('boom'));
    render(
      <ClassifierHealthBanner
        fetchHealth={fetchHealth as unknown as () => Promise<HealthSnapshot>}
      />,
    );

    const banner = await screen.findByTestId('classifier-health-banner');
    expect(banner.getAttribute('data-banner-kind')).toBe(
      FETCH_ERROR_DECISION.kind,
    );
    expect(banner.textContent).toContain('获取失败');
    expect(banner.textContent).toContain('未知');
    expect(screen.queryByText('健康')).toBeNull();
  });

  it('renders no error/warning banner when every classifier is healthy', async () => {
    const fetchHealth = jest.fn().mockResolvedValue(makeSnapshot());
    const { container } = render(
      <ClassifierHealthBanner
        fetchHealth={fetchHealth as unknown as () => Promise<HealthSnapshot>}
      />,
    );

    // 等一拍让初始拉取完成。
    await waitFor(() => expect(fetchHealth).toHaveBeenCalledTimes(1));
    expect(container.querySelector('[role="alert"]')).toBeNull();
    expect(container.querySelector('[data-testid="classifier-health-banner"]')).toBeNull();
  });

  it('maps every health union member in the single-source label/color tables', () => {
    const allStates: HealthState[] = ['unknown', 'healthy', 'degraded', 'unavailable'];
    for (const state of allStates) {
      expect(Object.keys(HEALTH_LABELS)).toContain(state);
      expect(Object.keys(HEALTH_TAG_COLOR)).toContain(state);
      expect(HEALTH_LABELS[state].length).toBeGreaterThan(0);
      expect(HEALTH_TAG_COLOR[state].length).toBeGreaterThan(0);
    }
    // 编译器已强制 Record 完备；这里再防运行期手写遗漏/多余键（键序为 sort 后字典序）。
    expect(Object.keys(HEALTH_LABELS).sort()).toEqual(
      ['degraded', 'healthy', 'unavailable', 'unknown'],
    );
  });

  it('resolves hidden for all-healthy and warning for never-probed unknown classifiers', () => {
    expect(resolveBannerDecision(makeSnapshot()).kind).toBe('hidden');
    expect(
      resolveBannerDecision(
        makeSnapshot({}, [makeItem({ health: 'unknown', lastCheckedAtUtc: undefined })]),
      ).kind,
    ).toBe('warning');
    expect(
      resolveBannerDecision(
        makeSnapshot({}, [
          makeItem({ classifierId: 'a', health: 'unknown', lastCheckedAtUtc: undefined }),
          makeItem({ classifierId: 'b', health: 'healthy' }),
        ]),
      ).headline,
    ).toContain('未知');
  });

  it('polls on the explicit interval and stops after unmount', async () => {
    jest.useFakeTimers();
    try {
      const fetchHealth = jest.fn().mockResolvedValue(makeSnapshot({}, []));
      const view = render(
        <ClassifierHealthBanner
          fetchHealth={fetchHealth as unknown as () => Promise<HealthSnapshot>}
          refreshIntervalMs={CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS}
        />,
      );
      await act(async () => {});
      expect(fetchHealth).toHaveBeenCalledTimes(1);

      await act(async () => {
        jest.advanceTimersByTime(CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS);
      });
      expect(fetchHealth).toHaveBeenCalledTimes(2);

      view.unmount();
      await act(async () => {
        jest.advanceTimersByTime(CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS * 5);
      });
      expect(fetchHealth).toHaveBeenCalledTimes(2);
    } finally {
      jest.useRealTimers();
    }
  });
});
