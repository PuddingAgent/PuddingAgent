import { render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { STORAGE_CLASS_COLORS, StorageTrendChart, classColor } from './StorageOverviewCharts';
import { formatBytes, formatCount, formatUtc } from './api';
import type { StorageInventoryTrendPoint } from './types';

// ── ADR-076 存储页定向测试：字节/计数口径、估算约数标识、
// 分类占比图与趋势图渲染。整页集成由浏览器 smoke 覆盖（jsdom 下
// antd Table 的测量循环不稳定，不适合页面级断言）。──

describe('formatBytes / formatCount / formatUtc', () => {
  it('formatBytes 按 1024 进制输出人类可读约数', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(800_000_000)).toBe('763 MB');
    expect(formatBytes(2_100_000_000)).toBe('2.0 GB');
    expect(formatBytes(null)).toBe('—');
    expect(formatBytes(undefined)).toBe('—');
  });

  it('formatCount 输出万/亿计数', () => {
    expect(formatCount(1_730_155)).toBe('173.0 万');
    expect(formatCount(999)).toBe('999');
    expect(formatCount(null)).toBe('—');
  });

  it('formatUtc 解析 UTC ISO 字符串', () => {
    expect(formatUtc(null)).toBe('—');
    expect(formatUtc('2026-08-24T10:30:00Z')).toContain('2026');
  });
});

describe('StorageTrendChart', () => {
  // ⚠️ 组件会按 `Date.now() - days * 24h` 过滤「最近 days 天」，且不足 2 天即渲染空态
  //   （文案「历史快照不足…」）。因此 fixture 日期**必须相对当前时间生成**：
  //   原先写死 2026-08-20/21/22，到 2026-09-21 已全部滑出 days=30 的窗口 ⇒ 用例无端变红。
  //   生产逻辑是对的（窗口过滤 + 空态提示），是测试自己会随时间腐化 ⇒ 改为相对日期。
  const daysAgoIso = (daysAgo: number) =>
    new Date(Date.now() - daysAgo * 24 * 3600 * 1000).toISOString();

  const points: StorageInventoryTrendPoint[] = [
    {
      capturedAtUtc: daysAgoIso(2),
      classBytes: { 'diagnostics.telemetry-raw': 100 },
      databaseTotalBytes: 100,
    },
    {
      capturedAtUtc: daysAgoIso(1),
      classBytes: { 'diagnostics.telemetry-raw': 200 },
      databaseTotalBytes: 200,
    },
    {
      capturedAtUtc: daysAgoIso(0),
      classBytes: { 'diagnostics.telemetry-raw': 300 },
      databaseTotalBytes: 300,
    },
  ];

  it('渲染堆叠面积路径与图例标签', () => {
    const { container } = render(
      <StorageTrendChart points={points} days={30} classNames={{ 'diagnostics.telemetry-raw': '原始性能遥测' }} />,
    );
    const paths = container.querySelectorAll('path');
    expect(paths.length).toBeGreaterThan(0);
    expect(screen.getAllByText('原始性能遥测').length).toBeGreaterThan(0);
  });

  it('历史点不足时显示提示而不崩溃', () => {
    render(
      <StorageTrendChart points={points.slice(0, 1)} days={30} classNames={{}} />,
    );
    expect(screen.getByText(/历史快照不足/)).toBeTruthy();
  });

  it('数据点少于两个时不渲染折线/面积，但保留坐标轴框架', () => {
    // 单个/两个点连成的斜线会被读成「暴涨」，因此空态只留坐标轴与文案。
    const { container } = render(
      <StorageTrendChart points={[points[2]]} days={30} classNames={{}} />,
    );

    expect(container.querySelectorAll('path').length).toBe(0);
    expect(screen.getByText(/历史快照不足/)).toBeTruthy();
    expect(container.querySelectorAll('line').length).toBeGreaterThanOrEqual(2);
  });

  it('尚未采样的零值点被丢弃，不参与趋势绘制', () => {
    const emptySample: StorageInventoryTrendPoint = {
      capturedAtUtc: daysAgoIso(3),
      classBytes: {},
      databaseTotalBytes: 0,
    };

    const { container } = render(
      <StorageTrendChart points={[emptySample, points[2]]} days={30} classNames={{}} />,
    );

    // 零值日不是「用量为 0」而是「还没采样」，剩一个有效点仍走空态。
    expect(container.querySelectorAll('path').length).toBe(0);
  });
});

describe('classColor', () => {
  it('为固定目录类型返回稳定配色，未知类型回退灰色', () => {
    expect(classColor('diagnostics.telemetry-raw')).toBe('#fa8c16');
    expect(classColor('unknown-target')).toBe('#bfbfbf');
  });
});

// 事故回溯：evidence.conversation-events 的配色曾是 #8c8c8c（深灰），而「未归类」的回退色是
// #bfbfbf（浅灰）。两类并排都是中性灰，而前者恰好是占比最大的一块（实测 65.46%），
// 于是一整条横条看起来像「一大片没归类」—— 我自己就照着这个误解排查了半小时。
// 下面这条断言把「灰只留给未归类」钉死：任何已登记的数据类都不许用中性灰。
describe('配色：灰色只留给「未归类」', () => {
  const NEUTRAL_GRAYS = ['#bfbfbf', '#8c8c8c'];

  it('每个已登记的数据类都必须是彩色，不得与未归类共用中性灰', () => {
    const entries = Object.entries(STORAGE_CLASS_COLORS);
    expect(entries.length).toBeGreaterThan(5);
    const offenders = entries
      .filter(([, color]) => NEUTRAL_GRAYS.includes(String(color).toLowerCase()))
      .map(([targetId]) => targetId);
    expect(offenders).toEqual([]);
  });

  it('未登记的 targetId 才回退到未归类灰', () => {
    expect(classColor('__not_registered__')).toBe('#bfbfbf');
  });
});
