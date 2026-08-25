import { render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import { StorageClassDonut, StorageTrendChart, classColor } from './StorageOverviewCharts';
import { formatBytes, formatCount, formatUtc } from './api';
import type { StorageInventoryClass, StorageInventoryTrendPoint } from './types';

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

describe('StorageClassDonut', () => {
  const classes: StorageInventoryClass[] = [
    {
      targetId: 'diagnostics.telemetry-raw',
      displayName: '原始性能遥测',
      estimatedBytes: 800_000_000,
      estimatedRows: 1_730_155,
      estimateState: 'Updated',
      updatedAtUtc: '2026-08-24T10:30:00Z',
    },
    {
      targetId: 'evidence.conversation-events',
      displayName: '会话事件证据',
      estimatedBytes: 500_000_000,
      estimateState: 'Updated',
      updatedAtUtc: '2026-08-24T10:30:00Z',
    },
  ];

  it('渲染估算合计、图例名称与约数（不只靠颜色表达）', async () => {
    render(<StorageClassDonut classes={classes} />);

    await waitFor(() => {
      expect(screen.getAllByText((_, element) => element?.textContent?.includes('763 MB') === true).length).toBeGreaterThan(0);
    });
    expect(screen.getByText('原始性能遥测')).toBeTruthy();
    expect(screen.getByText('会话事件证据')).toBeTruthy();
    // 占比同时以文字呈现。
    expect(screen.getAllByText((_, element) => element?.textContent?.includes('61.5%') === true).length).toBeGreaterThan(0);
  });

  it('无估算数据时显示估算中提示', () => {
    render(<StorageClassDonut classes={[]} />);
    expect(screen.getByText(/估算中/)).toBeTruthy();
  });
});

describe('StorageTrendChart', () => {
  const points: StorageInventoryTrendPoint[] = [
    {
      capturedAtUtc: '2026-08-20T10:00:00Z',
      classBytes: { 'diagnostics.telemetry-raw': 100 },
      databaseTotalBytes: 100,
    },
    {
      capturedAtUtc: '2026-08-21T10:00:00Z',
      classBytes: { 'diagnostics.telemetry-raw': 200 },
      databaseTotalBytes: 200,
    },
    {
      capturedAtUtc: '2026-08-22T10:00:00Z',
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
});

describe('classColor', () => {
  it('为固定目录类型返回稳定配色，未知类型回退灰色', () => {
    expect(classColor('diagnostics.telemetry-raw')).toBe('#fa8c16');
    expect(classColor('unknown-target')).toBe('#bfbfbf');
  });
});
