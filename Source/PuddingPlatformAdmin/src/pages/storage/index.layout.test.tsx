// ── 存储页 S3a 布局测试：首屏三行结构 + 其余内容进 Tabs ──────────────
//
// 为什么不测 scrollHeight / clientHeight：jsdom 没有布局引擎，两者恒为 0，
// 「首屏不滚动」在那里写出来必然假绿（恒真断言比没有断言更糟）。本文件只断言
// **结构位置**：谁必须在首屏容器里、谁只允许出现在 Tab 里、口径说明是否只挂在
// title 上。真实视口的一屏验证留给 S3b 的真实浏览器。
//
// 这些断言能区分「放错位置」：把 treemap 挪出首屏容器会让第 1 个用例红；
// 把口径说明放回正文会让第 2 个用例红；把 Tab 内容平铺回首页会让第 3 个用例红。

import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as React from 'react';
import StoragePage from './index';

// ⚠️ 这里刻意用 `type X = import('./types').Y` 而不是 `import type { Y }`：
// 文件里出现 `jest.mock(` 时，@umijs/test 的 esbuild 转换器会先跑一遍 babel
// （只做 ESM→CJS，不剥类型），随后 esbuild 会因为「导入绑定被用在类型标注里」直接
// 抛 Cannot transform the imported binding。内联 import 类型不引入绑定，能通过。
type Snapshot = import('./types').StorageInventorySnapshot;
type DataClass = import('./types').StorageDataClass;
type RetentionPolicy = import('./types').StorageRetentionPolicy;
type DatabaseSpace = import('./types').StorageDatabaseSpace;
type CleanupJob = import('./types').StorageCleanupJob;

// rc-tabs 的指示条用 ResizeObserver 观测容器宽度；jsdom 没有这个 API。
if (!(globalThis as { ResizeObserver?: unknown }).ResizeObserver) {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

jest.mock('./api', () => {
  const actual = jest.requireActual('./api');
  return {
    ...actual,
    cancelCleanupJob: jest.fn(),
    confirmCleanupJob: jest.fn(),
    createCleanupPreview: jest.fn(),
    getCleanupJobEvents: jest.fn(),
    getDatabaseSpace: jest.fn(),
    getInventoryTrend: jest.fn(),
    getProtectedObjects: jest.fn(),
    getRetentionPolicy: jest.fn(),
    getStorageDataClasses: jest.fn(),
    getStorageOverview: jest.fn(),
    listCleanupJobs: jest.fn(),
    requestInventoryRefresh: jest.fn(),
  };
});

const api = jest.requireMock('./api') as {
  getDatabaseSpace: jest.Mock;
  getInventoryTrend: jest.Mock;
  getProtectedObjects: jest.Mock;
  getRetentionPolicy: jest.Mock;
  getStorageDataClasses: jest.Mock;
  getStorageOverview: jest.Mock;
  listCleanupJobs: jest.Mock;
};

// 真值口径：平台库按页计量（pageSizeBytes > 0），全文检索索引目录不是 SQLite 库
// （pageSizeBytes = 0），因此它只计入「非数据库目录」，不计入「数据库合计」。
const snapshot: Snapshot = {
  snapshotId: 's-1',
  revision: 7,
  schemaVersion: 1,
  capturedAtUtc: '2026-11-13T08:00:00Z',
  updatedAtUtc: '2026-11-13T08:00:00Z',
  databases: [
    {
      databaseId: 'platform',
      displayName: '平台库',
      relativePath: 'platform.db',
      mainBytes: 3_000_000_000,
      walBytes: 0,
      sharedMemoryBytes: 0,
      totalBytes: 3_000_000_000,
      pageSizeBytes: 4096,
      pageCount: 732_422,
      freePageCount: 50_000,
      reusableFreeBytes: 204_800_000,
    },
    {
      databaseId: 'fulltext-index',
      displayName: '全文检索索引',
      relativePath: 'index',
      mainBytes: 400_000_000,
      walBytes: 0,
      sharedMemoryBytes: 0,
      totalBytes: 400_000_000,
      pageSizeBytes: 0,
      pageCount: 0,
      freePageCount: 0,
      reusableFreeBytes: 0,
    },
  ],
  classes: [
    {
      targetId: 'diagnostics.telemetry-raw',
      displayName: '遥测原始数据',
      estimatedBytes: 800_000_000,
      estimatedRows: 1_730_155,
      estimateState: 'Updated',
      updatedAtUtc: '2026-11-13T08:00:00Z',
    },
    {
      targetId: 'evidence.conversation-events',
      displayName: '会话事件证据',
      estimatedBytes: 1_500_000_000,
      estimatedRows: 4_000_480,
      estimateState: 'Updated',
      updatedAtUtc: '2026-11-13T08:00:00Z',
    },
    {
      targetId: 'storage.redundant-indexes',
      displayName: '冗余索引',
      estimatedBytes: 200_000_000,
      estimatedRows: 0,
      estimateState: 'Updated',
      updatedAtUtc: '2026-11-13T08:00:00Z',
    },
  ],
  isRefreshing: false,
  warnings: [],
};

const dataClasses: DataClass[] = [
  {
    targetId: 'diagnostics.telemetry-raw',
    displayName: '遥测原始数据',
    description: '性能遥测原始事件',
    safetyLevel: 'Disposable',
    safetyLevelName: '可丢弃',
    catalogVersion: 3,
    manualCleanupAllowed: true,
    automaticCleanupAllowed: true,
    protected: false,
    requiresRollupBeforeAutomatic: false,
  },
  {
    targetId: 'evidence.conversation-events',
    displayName: '会话事件证据',
    description: '会话事件日志',
    safetyLevel: 'Evidence',
    safetyLevelName: '证据',
    catalogVersion: 3,
    manualCleanupAllowed: false,
    automaticCleanupAllowed: true,
    protected: true,
    requiresRollupBeforeAutomatic: false,
  },
  {
    targetId: 'storage.redundant-indexes',
    displayName: '冗余索引',
    description: '重复与孤儿索引',
    safetyLevel: 'Derived',
    safetyLevelName: '可重建',
    catalogVersion: 3,
    manualCleanupAllowed: true,
    automaticCleanupAllowed: false,
    protected: false,
    requiresRollupBeforeAutomatic: false,
  },
];

const policy: RetentionPolicy = {
  policyRevision: 2,
  automaticCleanupEnabled: true,
  runIntervalHours: 24,
  startupDelaySeconds: 60,
  targets: [],
  warnings: [],
};

const databaseSpaces: DatabaseSpace[] = [
  {
    key: 'platform',
    displayName: '平台库',
    databaseFile: 'platform.db',
    exists: true,
    fileBytes: 3_000_000_000,
    pageSize: 4096,
    pageCount: 732_422,
    perTableAvailable: true,
    spaceSource: 'dbstat',
    dataClasses: [
      {
        targetId: 'diagnostics.telemetry-raw',
        displayName: '遥测原始数据',
        safetyLevel: 'Disposable',
        manualCleanupAllowed: true,
        bytes: 800_000_000,
        pages: 195_313,
        tables: ['telemetry_metric_events'],
      },
      {
        targetId: 'evidence.conversation-events',
        displayName: '会话事件证据',
        safetyLevel: 'Evidence',
        manualCleanupAllowed: false,
        bytes: 1_500_000_000,
        pages: 366_211,
        tables: ['conversation_events'],
      },
    ],
  },
];

const jobs: CleanupJob[] = [];

// 只声明用得到的那一个方法：这里不能写 ReturnType<typeof render>——
// 同一个文件里有 jest.mock(，类型标注引用导入绑定会被 esbuild 直接拒掉（见文件头注释）。
let rendered: { unmount: () => void } | null = null;

// 显式卸载：页面装载时会起一个 30 s 的概览轮询定时器，靠自动 cleanup 也能清，
// 但显式 unmount 让「本用例到底留下了什么」不依赖 RTL 的隐式行为。
afterEach(() => {
  rendered?.unmount();
  rendered = null;
});

/** 把首屏容器与「首屏之外」分开取：结构断言的判据只能是「在哪棵子树里」。 */
async function renderPage() {
  rendered = render(<StoragePage />);
  const firstScreen = await screen.findByTestId('storage-first-screen');
  await waitFor(() => {
    expect(within(firstScreen).getAllByTestId('treemap-cell').length).toBeGreaterThan(0);
    expect(within(firstScreen).getAllByTestId('stacked-row').length).toBeGreaterThan(0);
  });
  return firstScreen;
}

describe('存储页首屏结构（S3a）', () => {
  beforeEach(() => {
    jest.clearAllMocks();
    api.getStorageOverview.mockResolvedValue(snapshot);
    api.getStorageDataClasses.mockResolvedValue(dataClasses);
    api.getProtectedObjects.mockResolvedValue(['会话事件证据：受 ADR-076 保护']);
    api.getRetentionPolicy.mockResolvedValue(policy);
    api.getInventoryTrend.mockResolvedValue([]);
    api.listCleanupJobs.mockResolvedValue(jobs);
    api.getDatabaseSpace.mockResolvedValue(databaseSpaces);
  });

  it('首屏三行只含指标条 / treemap + 堆叠条 / 可回收三段条，趋势与报表不在其中', async () => {
    const firstScreen = await renderPage();

    // 行1：四个指标格都在首屏容器内。
    for (const testId of ['metric-total', 'metric-databases', 'metric-non-database', 'metric-reusable']) {
      expect(within(firstScreen).getByTestId(testId)).toBeTruthy();
    }

    // 行2：treemap 与每库堆叠条都在首屏容器内（renderPage 已等到它们出现）。
    expect(within(firstScreen).queryAllByTestId('treemap-cell').length).toBeGreaterThan(1);
    expect(within(firstScreen).queryAllByTestId('stacked-row').length).toBeGreaterThan(0);

    // 行3：可回收三段条正好三段，且顺序与语义固定。
    expect(
      within(firstScreen)
        .getAllByTestId('reclaim-item')
        .map((element) => element.getAttribute('data-segment-key')),
    ).toEqual(['vacuum', 'redundant-indexes', 'retention']);

    // 趋势图在文档里（默认页签就是它），但**不能**在首屏容器里 —— 这一条能抓「又挪回首屏」。
    expect(within(firstScreen).queryByTestId('storage-tab-trend')).toBeNull();
    expect(screen.getByTestId('storage-tab-trend')).toBeTruthy();

    // 分类报表默认不渲染（非当前页签 + destroyOnHidden）。
    expect(screen.queryByTestId('storage-tab-report')).toBeNull();
    expect(within(firstScreen).queryByTestId('storage-tab-report')).toBeNull();

    // 首屏里不应出现报表表格的表头列名，否则说明报表被平铺回了首屏。
    expect(within(firstScreen).queryByText('占类合计')).toBeNull();
    expect(within(firstScreen).queryByText('分类统计报表')).toBeNull();
  });

  it('四项口径说明不在首屏可见文本里，但出现在 title 属性中', async () => {
    const firstScreen = await renderPage();

    const visibleText = firstScreen.textContent ?? '';
    const titles = Array.from(document.querySelectorAll('[title]')).map(
      (element) => element.getAttribute('title') ?? '',
    );

    const cases: Array<[string, string]> = [
      ['全部数据库的主文件 + WAL + 共享内存，加上非数据库目录', '总占用口径'],
      ['单库的 GB = 该库主文件 + WAL + 共享内存', '单库大小口径'],
      ['已删除数据留下的空闲页', '可复用页口径'],
      ['按当前保留策略还能再删的数据量', '可清理估算口径'],
    ];

    for (const [needle, label] of cases) {
      expect({ label, visible: visibleText.includes(needle) }).toEqual({ label, visible: false });
      expect({ label, inTitle: titles.some((title) => title.includes(needle)) }).toEqual({
        label,
        inTitle: true,
      });
    }
  });

  it('五个页签的内容默认不在首屏，切到对应页签后才出现（可清理的勾选入口全程可达）', async () => {
    const firstScreen = await renderPage();

    // 默认页签是「趋势」：可清理的勾选框还不存在。
    expect(screen.queryByRole('checkbox')).toBeNull();
    for (const testId of [
      'storage-tab-trend',
      'storage-tab-report',
      'storage-tab-database',
      'storage-tab-cleanup',
      'storage-tab-protected',
    ]) {
      expect(within(firstScreen).queryByTestId(testId)).toBeNull();
    }

    // 切到「可清理」：勾选框、预览按钮（禁用态）都从 Tab 里出现。
    fireEvent.click(screen.getByRole('tab', { name: '可清理' }));
    const cleanupTab = await screen.findByTestId('storage-tab-cleanup');
    const checkboxes = within(cleanupTab).getAllByRole('checkbox');
    expect(checkboxes.length).toBeGreaterThan(0);
    const previewButton = screen.getByRole('button', { name: /预览所选/ }) as HTMLButtonElement;
    expect(previewButton.disabled).toBe(true);

    // 勾选一个类型 → 预览按钮变为可用（清理入口在 Tab 里仍然可达）。
    fireEvent.click(checkboxes[0]);
    await waitFor(() => {
      expect((screen.getByRole('button', { name: /预览所选/ }) as HTMLButtonElement).disabled).toBe(
        false,
      );
    });

    // 切回「趋势」后，可清理页签被销毁（destroyOnHidden），不会在后台撑高页面。
    fireEvent.click(screen.getByRole('tab', { name: '趋势' }));
    await waitFor(() => {
      expect(screen.queryByTestId('storage-tab-cleanup')).toBeNull();
    });
  });

  it('分类报表与数据库占用（含 DatabaseSpacePanel）分别在各自页签里，页头刷新/策略入口保留', async () => {
    await renderPage();

    // 页头两个入口（刷新估算 / 策略设置）仍然存在。
    expect(screen.getByRole('button', { name: /刷新估算/ })).toBeTruthy();
    expect(screen.getByRole('button', { name: /策略设置/ })).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: '分类报表' }));
    const reportTab = await screen.findByTestId('storage-tab-report');
    expect(within(reportTab).getByText('分类统计报表')).toBeTruthy();
    expect(within(reportTab).getByText('占类合计')).toBeTruthy();

    fireEvent.click(screen.getByRole('tab', { name: '数据库占用' }));
    const databaseTab = await screen.findByTestId('storage-tab-database');
    expect(within(databaseTab).getByText('数据库文件大小（含 WAL）')).toBeTruthy();
    // DatabaseSpacePanel 自带「点按钮才测量」的入口，说明它被整体搬进 Tab 而没有被改写。
    expect(within(databaseTab).getByRole('button', { name: /查看数据库占用/ })).toBeTruthy();
  });
});
