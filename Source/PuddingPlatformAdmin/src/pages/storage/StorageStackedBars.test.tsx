import { fireEvent, render, screen } from '@testing-library/react';
import * as React from 'react';
import { formatBytes } from './api';
import { classColor } from './StorageOverviewCharts';
import { StorageStackedBars } from './StorageStackedBars';

// 这里不 import 类型再写进注解：Babel 对「导入的 type + 注解」组合有转换限制
// （见 DatabaseSpacePanel.test.tsx 同类注释）。fixture 由 prop 位置做结构化校验。

const segments = () => screen.getAllByTestId('stacked-segment');

/** targetId → 段节点，用来断言「某一段」而不是「第几段」。 */
const byTarget = () =>
  new Map(segments().map((el) => [el.getAttribute('data-target-id') ?? '', el]));

/** 段宽（几何由属性承载：width="43.5%"）。 */
const widthOf = (el: Element) => el.getAttribute('width');

/** 每段宽度按渲染顺序取出来，用于逐值比对。 */
const widths = () => segments().map((el) => widthOf(el));

/** 只有一行的 fixture：返回该行内的段，避免多行相互干扰。 */
const singleRow = (row: Parameters<typeof StorageStackedBars>[0]['rows'][number]) =>
  render(<StorageStackedBars rows={[row]} />);

describe('StorageStackedBars · 规则1 分母是段字节之和，不是 totalBytes', () => {
  it('totalBytes=1000 而两段各 250 字节时，两段各占 50%（不是 25%）', () => {
    // 关键构造：totalBytes 远大于段之和，模拟「库文件里含有未分类页（空闲页）」。
    // 若实现误用 totalBytes 当分母，两段会变成 25.0%，本条立刻红。
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 250 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 250 },
      ],
    });

    expect(widths()).toEqual(['50.0%', '50.0%']);
    // 显式钉住「拿 totalBytes 当分母」会长成的两个错误形状：各 25%，或各 100%。
    expect(widths()).not.toEqual(['25.0%', '25.0%']);
    expect(widths()).not.toEqual(['100%', '100%']);
    // 右侧总量仍按 totalBytes 展示，说明分母换了对象、总量没有跟着变。
    expect(screen.getByTestId('stacked-row-total').textContent).toBe(formatBytes(1000));
  });

  it('多段归一：300/200/500 在 totalBytes=1000 时逐值与字节比一致', () => {
    // 这条的 totalBytes 恰好等于段之和，因此它只钉「多段归一」本身，
    // 不承担「分母该取谁」的判据（那条判据在上一个用例里）。
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 300 },
        { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 200 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 500 },
      ],
    });

    expect(widths()).toEqual(['30.0%', '20.0%', '50.0%']);
  });
});

describe('StorageStackedBars · 规则2 段宽是可读百分比字符串', () => {
  it('width 属性为保留一位小数的百分比，可直接读数值断言', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 435 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 565 },
      ],
    });

    expect(widths()).toEqual(['43.5%', '56.5%']);
    // 逐段确认是「百分比文本」而不是隐形比例：每段都有可解析的数字与 % 号。
    for (const el of segments()) {
      const raw = widthOf(el) ?? '';
      expect(raw).toMatch(/^\d+(\.\d)?%$/);
      expect(Number.parseFloat(raw)).toBeGreaterThan(0);
    }
    expect(Number.parseFloat(widthOf(segments()[0]) ?? '')).toBeCloseTo(43.5, 5);
  });

  it('偏移量累加：第二段从第一段宽度处开始，末段右边界落在 100%', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 435 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 565 },
      ],
    });

    const [first, second] = segments();
    expect(first.getAttribute('x')).toBe('0.000%');
    expect(second.getAttribute('x')).toBe('43.500%');
    const rightEdge =
      Number.parseFloat(second.getAttribute('x') ?? '0') +
      Number.parseFloat(widthOf(second) ?? '0');
    expect(rightEdge).toBeCloseTo(100, 5);
  });
});

describe('StorageStackedBars · 规则3 0 字节段不渲染', () => {
  it('0 字节数据类既不占段，也不参与段数统计', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 600 },
        { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 0 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 400 },
      ],
    });

    // 段数按正字节计：3 个数据类里只有 2 个有占用。
    expect(segments().length).toBe(2);
    expect(byTarget().has('diagnostics.rollups')).toBe(false);
    expect(segments().map((el) => el.getAttribute('data-target-id'))).toEqual([
      'diagnostics.telemetry-raw',
      'evidence.conversation-events',
    ]);
    // 0 字节段不参与分母，也不吃掉宽度。
    expect(widths()).toEqual(['60.0%', '40.0%']);
  });
});

describe('StorageStackedBars · 规则4 单段占满 100%', () => {
  it('只有一个正字节数据类时该段宽度为 100%（不降级成文字）', () => {
    singleRow({
      key: 'code-index',
      label: '代码索引库',
      totalBytes: 500,
      segments: [{ targetId: 'code-index.obsolete-scopes', label: '失效范围', bytes: 500 }],
    });

    expect(widths()).toEqual(['100%']);
    expect(segments().length).toBe(1);
    // 单段仍然是一段真实横条，而不是 treemap 那种「仅一个分类」的文字降级。
    expect(screen.getByTestId('stacked-bar')).toBeTruthy();
    expect(screen.queryByTestId('stacked-row-empty')).toBeNull();
  });
});

describe('StorageStackedBars · 规则5 无明细行', () => {
  it('segments 为空时保留库名与总量，横条位置换成文案', () => {
    singleRow({ key: 'code-index', label: '代码索引库', totalBytes: 1_234_567_890, segments: [] });

    const row = screen.getByTestId('stacked-row');
    expect(row.textContent).toContain('代码索引库');
    expect(row.textContent).toContain(formatBytes(1_234_567_890));
    expect(screen.getByTestId('stacked-row-empty').textContent).toContain('无占用明细');
    // 不渲染空的段，也不渲染空横条。
    expect(screen.queryAllByTestId('stacked-segment').length).toBe(0);
    expect(screen.queryAllByTestId('stacked-bar').length).toBe(0);
  });

  it('segments 全部为 0 字节时同样走空明细', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 2_000_000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 0 },
        { targetId: 'diagnostics.rollups', label: '汇总数据', bytes: 0 },
      ],
    });

    expect(screen.getByTestId('stacked-row').textContent).toContain('无占用明细');
    expect(screen.queryAllByTestId('stacked-segment').length).toBe(0);
    expect(screen.queryAllByTestId('stacked-bar').length).toBe(0);
  });
});

describe('StorageStackedBars · 规则6 空 rows', () => {
  it('rows 为空数组时给出空态文案，不渲染任何行', () => {
    const { container } = render(<StorageStackedBars rows={[]} />);

    expect(screen.getByTestId('stacked-bars-empty').textContent).toContain('暂无库占用明细');
    expect(screen.queryAllByTestId('stacked-row').length).toBe(0);
    expect(container.querySelector('svg')).toBeNull();
  });
});

describe('StorageStackedBars · 规则7 段与行可被程序识别', () => {
  it('每段带 data-testid/data-target-id 与 <title>「label · 字节 · 百分比」', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 750 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 250 },
      ],
    });

    const targetIds = segments().map((el) => el.getAttribute('data-target-id'));
    expect(targetIds).toEqual(['diagnostics.telemetry-raw', 'evidence.conversation-events']);

    const telemetry = byTarget().get('diagnostics.telemetry-raw') as Element;
    expect(telemetry.getAttribute('data-testid')).toBe('stacked-segment');
    const title = telemetry.querySelector('title')?.textContent ?? '';
    expect(title).toContain('遥测原始数据');
    expect(title).toContain(formatBytes(750));
    expect(title).toContain('75.0%');

    const row = screen.getByTestId('stacked-row');
    expect(row.getAttribute('data-database-key')).toBe('platform');
  });

  it('多行各自带自己的 data-database-key，段不串行', () => {
    render(
      <StorageStackedBars
        rows={[
          {
            key: 'platform',
            label: '平台库',
            totalBytes: 100,
            segments: [{ targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 100 }],
          },
          {
            key: 'code-index',
            label: '代码索引库',
            totalBytes: 100,
            segments: [{ targetId: 'code-index.obsolete-scopes', label: '失效范围', bytes: 100 }],
          },
        ]}
      />,
    );

    expect(
      screen.getAllByTestId('stacked-row').map((el) => el.getAttribute('data-database-key')),
    ).toEqual(['platform', 'code-index']);
    expect(segments().map((el) => el.getAttribute('data-target-id'))).toEqual([
      'diagnostics.telemetry-raw',
      'code-index.obsolete-scopes',
    ]);
  });
});

describe('StorageStackedBars · 规则8 配色复用 classColor', () => {
  it('每段填充色等于 classColor(targetId)，不同分类不同色', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 700 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 300 },
      ],
    });

    const map = byTarget();
    const telemetry = map.get('diagnostics.telemetry-raw') as Element;
    const evidence = map.get('evidence.conversation-events') as Element;
    expect(telemetry.getAttribute('fill')).toBe(classColor('diagnostics.telemetry-raw'));
    expect(evidence.getAttribute('fill')).toBe(classColor('evidence.conversation-events'));
    expect(telemetry.getAttribute('fill')).not.toBe(evidence.getAttribute('fill'));
  });
});

describe('StorageStackedBars · 规则9 右侧总量文字', () => {
  it('总量文字来自 formatBytes(row.totalBytes)，与横条分段无关', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 1_500_000_000,
      segments: [
        { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 1_000_000_000 },
        { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 500_000_000 },
      ],
    });

    expect(screen.getByTestId('stacked-row-total').textContent).toBe(formatBytes(1_500_000_000));
  });
});

describe('StorageStackedBars · 交互与尺寸', () => {
  it('传入 onSelect 时点击某段以它的 targetId 回调一次', () => {
    const onSelect = jest.fn();
    render(
      <StorageStackedBars
        rows={[
          {
            key: 'platform',
            label: '平台库',
            totalBytes: 1000,
            segments: [
              { targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 600 },
              { targetId: 'evidence.conversation-events', label: '会话事件证据', bytes: 400 },
            ],
          },
        ]}
        onSelect={onSelect}
      />,
    );

    const target = byTarget().get('evidence.conversation-events') as Element;
    expect(target.getAttribute('role')).toBe('button');
    fireEvent.click(target);

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith('evidence.conversation-events');
  });

  it('不传 onSelect 时点击不抛错，也不把段伪装成按钮', () => {
    singleRow({
      key: 'platform',
      label: '平台库',
      totalBytes: 100,
      segments: [{ targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 100 }],
    });

    for (const el of segments()) {
      expect(el.getAttribute('role')).toBeNull();
      expect(el.getAttribute('tabindex')).toBeNull();
    }
    expect(() => fireEvent.click(segments()[0])).not.toThrow();
  });

  it('barHeight 默认 10px，可按 prop 覆盖', () => {
    const row = {
      key: 'platform',
      label: '平台库',
      totalBytes: 100,
      segments: [{ targetId: 'diagnostics.telemetry-raw', label: '遥测原始数据', bytes: 100 }],
    };
    const { unmount } = render(<StorageStackedBars rows={[row]} />);
    expect(screen.getByTestId('stacked-bar').getAttribute('height')).toBe('10');
    unmount();

    render(<StorageStackedBars rows={[row]} barHeight={18} />);
    expect(screen.getByTestId('stacked-bar').getAttribute('height')).toBe('18');
  });
});
