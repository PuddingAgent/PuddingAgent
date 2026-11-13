import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { formatBytes } from './api';
import { classColor } from './StorageOverviewCharts';
import { StorageTreemap } from './StorageTreemap';
import { layoutTreemap } from './treemapLayout';

// 这里不 import 类型再写进注解：Babel 对「导入的 type + 注解」组合有转换限制
// （见 DatabaseSpacePanel.test.tsx 同类注释）。fixture 由 prop 位置做结构化校验。

const WIDTH = 640;
const HEIGHT = 320;

/** 三个已知配色分类：4:2:1，比例好算，且都能命中 STORAGE_CLASS_COLORS。 */
const THREE_CLASSES = [
  { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 4_000_000_000 },
  { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 2_000_000_000 },
  { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 1_000_000_000 },
];

const cells = () => screen.getAllByTestId('treemap-cell');

/** 4 个等权条目：等分布局下几何可精确预测，用于钉住标签阈值的边界。 */
const EQUAL_FOUR = [
  { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 100 },
  { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 100 },
  { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 100 },
  { targetId: 'diagnostics.logs.verbose', label: '详细日志', bytes: 100 },
];

const byTarget = () =>
  new Map(cells().map((el) => [el.getAttribute('data-target-id') ?? '', el]));

const areaOf = (el: Element) =>
  Number(el.getAttribute('width')) * Number(el.getAttribute('height'));

describe('StorageTreemap · 规则1 面积正比（几何直接用 layoutTreemap 的结果）', () => {
  it('每个矩形的 x/y/width/height 与 layoutTreemap 输出逐值一致，面积比等于字节比', () => {
    render(<StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} />);

    const rects = byTarget();
    const expected = layoutTreemap(
      THREE_CLASSES.map((datum) => ({ key: datum.targetId, weight: datum.bytes })),
      { x: 0, y: 0, width: WIDTH, height: HEIGHT },
    );

    const baseBytes = THREE_CLASSES[0].bytes;
    const baseArea = areaOf(rects.get(THREE_CLASSES[0].targetId) as Element);

    for (const item of expected) {
      const rect = rects.get(item.key);
      expect(rect).toBeTruthy();
      if (!rect) continue;
      // 逐值相等 ⇒ 组件没有自己再算一遍面积，只是把布局结果画出来。
      expect(Number(rect.getAttribute('x'))).toBe(item.x);
      expect(Number(rect.getAttribute('y'))).toBe(item.y);
      expect(Number(rect.getAttribute('width'))).toBe(item.width);
      expect(Number(rect.getAttribute('height'))).toBe(item.height);

      const datum = THREE_CLASSES.find((entry) => entry.targetId === item.key);
      expect(datum).toBeTruthy();
      if (!datum) continue;
      expect(areaOf(rect) / baseArea).toBeCloseTo(datum.bytes / baseBytes, 3);
    }

    // 全部矩形一起铺满 viewBox，不留下未解释的空白。
    const totalArea = expected.reduce((sum, item) => sum + item.width * item.height, 0);
    expect(totalArea).toBeCloseTo(WIDTH * HEIGHT, 0);
  });
});

describe('StorageTreemap · 规则2 只剩一个分类时降级为文字', () => {
  it('单个正权重条目不画单色块，改渲染一行文字', () => {
    const { container } = render(
      <StorageTreemap
        data={[
          { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 1_400_000_000 },
          // 0 字节条目不算「一个分类」，不阻止降级。
          { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 0 },
        ]}
      />,
    );

    const node = screen.getByTestId('treemap-single');
    expect(node.textContent).toContain('仅一个分类');
    expect(node.textContent).toContain('遥测原始数据');
    expect(node.textContent).toContain(formatBytes(1_400_000_000));
    // 100% 单色块必须消失：既不画矩形，也不渲染 svg。
    expect(screen.queryAllByTestId('treemap-cell').length).toBe(0);
    expect(container.querySelector('svg')).toBeNull();
  });
});

describe('StorageTreemap · 规则3 空态不渲染空 svg', () => {
  it('全部 bytes 为 0 时给出明确文案且没有 svg', () => {
    const { container } = render(
      <StorageTreemap
        data={[
          { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 0 },
          { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 0 },
        ]}
      />,
    );

    expect(screen.getByTestId('treemap-empty').textContent).toContain('暂无分类占用数据');
    expect(container.querySelector('svg')).toBeNull();
    expect(screen.queryAllByTestId('treemap-cell').length).toBe(0);
  });

  it('空数组同样走空态', () => {
    const { container } = render(<StorageTreemap data={[]} />);

    expect(screen.getByTestId('treemap-empty')).toBeTruthy();
    expect(container.querySelector('svg')).toBeNull();
  });
});

describe('StorageTreemap · 规则4 每个色块可被程序识别', () => {
  it('矩形带 data-testid=treemap-cell 与 data-target-id，且覆盖全部正权重组目', () => {
    render(<StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} />);

    const ids = cells().map((el) => el.getAttribute('data-target-id'));
    expect(ids.slice().sort()).toEqual(
      THREE_CLASSES.map((datum) => datum.targetId).sort(),
    );
    for (const el of cells()) {
      expect(el.tagName.toLowerCase()).toBe('rect');
      expect(el.getAttribute('data-target-id')).toBeTruthy();
    }
  });

  it('小块（宽 < 56 或高 < 24）不显示文字标签，大块显示', () => {
    const { container } = render(
      <StorageTreemap
        data={[
          { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 200_000 },
          { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 100 },
          { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 100 },
          { targetId: 'diagnostics.logs.verbose', label: '详细日志', bytes: 100 },
        ]}
        width={480}
        height={240}
      />,
    );

    const labelled = new Set(
      Array.from(container.querySelectorAll('[data-testid="treemap-label"]')).map((el) =>
        el.getAttribute('data-target-id'),
      ),
    );

    for (const cell of cells()) {
      const targetId = cell.getAttribute('data-target-id');
      const wide = Number(cell.getAttribute('width'));
      const tall = Number(cell.getAttribute('height'));
      expect(labelled.has(targetId)).toBe(wide >= 56 && tall >= 24);
    }

    // 两个方向都要有样本，否则这条断言等于没测：至少一块显示文字、至少一块省略。
    expect(labelled.size).toBeGreaterThan(0);
    expect(labelled.size).toBeLessThan(cells().length);
    // 大块里出现的必须是它自己的分类名（不是相邻块的）。
    expect(container.textContent).toContain('遥测原始数据');
  });

  // 两个阈值的**边界样本**（2026-11-13 补）：上面那条用例遍历真实几何，断言右侧写的是
  // 测试自己硬编码的 56/24；当数据里的小块在两个方向都不足时，单独把任一阈值改成 0
  // 测试仍然全绿（实测：MIN_LABEL_WIDTH=0 → 11/11 绿；MIN_LABEL_HEIGHT=0 → 11/11 绿），
  // 即两个阈值实际都没有被样本区分开。下面用等分布局造出「只管住一个方向」的几何。
  it('宽够高不够：每块 150×20 时省略文字（钉住高度阈值 24）', () => {
    // 4 个等权块在 600×20 里各成一条 150 宽 20 高的竖带：宽 ≥ 56 成立、高 ≥ 24 不成立。
    const { container } = render(
      <StorageTreemap
        data={EQUAL_FOUR}
        width={600}
        height={20}
      />,
    );

    const rects = Array.from(container.querySelectorAll('[data-testid="treemap-cell"]'));
    expect(rects).toHaveLength(4);
    // 先把几何钉住：若布局改变导致不再是「宽够高不够」，本条就失去意义，必须显式报错。
    for (const rect of rects) {
      expect(Number(rect.getAttribute('width'))).toBeGreaterThanOrEqual(56);
      expect(Number(rect.getAttribute('height'))).toBeLessThan(24);
    }

    expect(container.querySelectorAll('[data-testid="treemap-label"]')).toHaveLength(0);
  });

  it('高够宽不够：每块 50×100 时省略文字（钉住宽度阈值 56）', () => {
    // 4 个等权块在 50×400 里各成一条 50 宽 100 高的竖带（实测几何，见下断言）：
    // 高 ≥ 24 成立、宽 ≥ 56 不成立。
    const { container } = render(
      <StorageTreemap
        data={EQUAL_FOUR}
        width={50}
        height={400}
      />,
    );

    const rects = Array.from(container.querySelectorAll('[data-testid="treemap-cell"]'));
    expect(rects).toHaveLength(4);
    for (const rect of rects) {
      expect(Number(rect.getAttribute('width'))).toBeLessThan(56);
      expect(Number(rect.getAttribute('height'))).toBeGreaterThanOrEqual(24);
    }

    expect(container.querySelectorAll('[data-testid="treemap-label"]')).toHaveLength(0);
  });
});

describe('StorageTreemap · 规则5 配色复用 classColor', () => {
  it('每块 fill 等于 classColor(targetId)，且不同分类不会被涂成同一个颜色', () => {
    render(<StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} />);

    const fills: string[] = [];
    for (const cell of cells()) {
      const targetId = cell.getAttribute('data-target-id') ?? '';
      const fill = cell.getAttribute('fill');
      expect(fill).toBe(classColor(targetId));
      fills.push(fill ?? '');
    }
    // 写死成单一常量的实现会在这里取红。
    expect(new Set(fills).size).toBe(THREE_CLASSES.length);
  });
});

describe('StorageTreemap · 规则6 可访问性', () => {
  it('svg 带 role=img 与 aria-label，每块 <title> 给出「名称 · 字节 · 百分比」', () => {
    render(<StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} />);

    const svg = document.querySelector('svg');
    expect(svg).toBeTruthy();
    expect(svg?.getAttribute('role')).toBe('img');
    expect(svg?.getAttribute('aria-label')).toContain('3 个分类');

    const expected = layoutTreemap(
      THREE_CLASSES.map((datum) => ({ key: datum.targetId, weight: datum.bytes })),
      { x: 0, y: 0, width: WIDTH, height: HEIGHT },
    );

    for (const item of expected) {
      const cell = byTarget().get(item.key);
      const title = cell?.querySelector('title')?.textContent ?? '';
      const datum = THREE_CLASSES.find((entry) => entry.targetId === item.key);
      if (!datum) throw new Error(`fixture 缺 ${item.key}`);
      expect(title).toContain(datum.label);
      expect(title).toContain(formatBytes(datum.bytes));
      expect(title).toContain(`${(item.fraction * 100).toFixed(1)}%`);
    }
  });
});

describe('StorageTreemap · 规则8 百分比取布局给的 fraction，不自行换算', () => {
  it('title 里的百分比与 layoutTreemap 返回的 fraction 完全一致', () => {
    // 3:1 的比例刻意不整除 100：自算除法写成 `/total*100` 与 fraction*100 会差在小数位上。
    const data = [
      { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 3_000_000_000 },
      { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 1_000_000_000 },
    ];
    render(<StorageTreemap data={data} width={WIDTH} height={HEIGHT} />);

    const items = layoutTreemap(
      data.map((datum) => ({ key: datum.targetId, weight: datum.bytes })),
      { x: 0, y: 0, width: WIDTH, height: HEIGHT },
    );
    const rects = byTarget();
    for (const item of items) {
      const title = rects.get(item.key)?.querySelector('title')?.textContent ?? '';
      expect(title).toContain(`${(item.fraction * 100).toFixed(1)}%`);
    }
    expect(rects.get('diagnostics.telemetry-raw')?.querySelector('title')?.textContent).toContain(
      '75.0%',
    );
  });
});

describe('StorageTreemap · 规则7/点击 onSelect 仅在传入时透传', () => {
  it('传入 onSelect 时点击某块以它的 targetId 回调一次', () => {
    const onSelect = jest.fn();
    render(
      <StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} onSelect={onSelect} />,
    );

    const target = byTarget().get('diagnostics.rollups') as Element;
    expect(target.getAttribute('role')).toBe('button');
    fireEvent.click(target);

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith('diagnostics.rollups');
  });

  it('不传 onSelect 时点击不抛错，也不把色块伪装成按钮', () => {
    render(<StorageTreemap data={THREE_CLASSES} width={WIDTH} height={HEIGHT} />);

    for (const cell of cells()) {
      expect(cell.getAttribute('role')).toBeNull();
      expect(cell.getAttribute('tabindex')).toBeNull();
    }
    expect(() => fireEvent.click(cells()[0])).not.toThrow();
  });
});
