import type { StorageDatabaseSpace, StorageInventoryDatabase } from './types';
import {
  FREE_PAGES_TARGET_ID,
  OTHER_USAGE_TARGET_ID,
  computeUsageBreakdown,
  percentOf,
  resolveFreeBytes,
} from './usageBreakdown';

// ── 度量口径的定向测试 ────────────────────────────────────────────
// 这里钉的是**契约**而不是外观：
//   ① 合计必须闭合到库文件大小（差额为正/为负两种边界都要有确定行为）；
//   ② 百分比分母恒为库文件大小（不是「已归类之和」）；
//   ③ 估算值必须被标出来（estimated / inferred），精确值不许被标成估算。

const GB = 1024 ** 3;

function space(overrides: Partial<StorageDatabaseSpace> = {}): StorageDatabaseSpace {
  return {
    key: 'platform',
    displayName: 'Pudding 平台数据库',
    databaseFile: 'D:/data/databases/pudding_platform.db',
    exists: true,
    fileBytes: 12 * GB,
    pageSize: 4096,
    pageCount: 3_145_728,
    perTableAvailable: false,
    spaceSource: 'rowcount-sample',
    dataClasses: [
      {
        targetId: 'platform.session-evidence',
        displayName: '会话事件证据',
        safetyLevel: 'Evidence',
        manualCleanupAllowed: true,
        bytes: 2 * GB,
        pages: 0,
        tables: ['conversation_events'],
      },
      {
        targetId: 'unclassified',
        displayName: '未归类',
        safetyLevel: '',
        manualCleanupAllowed: false,
        bytes: 1 * GB,
        pages: 0,
        tables: ['mystery_table'],
      },
    ],
    ...overrides,
  };
}

function inventoryDatabase(overrides: Partial<StorageInventoryDatabase> = {}): StorageInventoryDatabase {
  return {
    databaseId: 'platform',
    displayName: 'Pudding 平台数据库',
    relativePath: 'databases/pudding_platform.db',
    mainBytes: 6 * GB,
    walBytes: 0,
    sharedMemoryBytes: 0,
    totalBytes: 6 * GB,
    pageSizeBytes: 4096,
    pageCount: 1_572_864,
    freePageCount: 1_310_720,
    reusableFreeBytes: 4096 * 1_310_720,
    ...overrides,
  };
}

describe('percentOf（百分比一律以库文件大小为分母）', () => {
  it('按分母归一化并保留 1 位小数', () => {
    expect(percentOf(2 * GB, 12 * GB)).toBe(16.7);
    expect(percentOf(1 * GB, 3 * GB)).toBe(33.3);
    expect(percentOf(0, 12 * GB)).toBe(0);
  });

  it('分母为 0/负数/非数时返回 0，而不是 Infinity 或 NaN', () => {
    expect(percentOf(1 * GB, 0)).toBe(0);
    expect(percentOf(1 * GB, -5)).toBe(0);
    expect(percentOf(1 * GB, Number.NaN)).toBe(0);
    expect(percentOf(Number.NaN, 12 * GB)).toBe(0);
    expect(percentOf(null, 12 * GB)).toBe(0);
    expect(percentOf(1 * GB, undefined)).toBe(0);
  });

  it('分子大于分母时封顶 100（不画出超出轨道的长度）', () => {
    expect(percentOf(20 * GB, 12 * GB)).toBe(100);
  });
});

describe('computeUsageBreakdown（合计闭合）', () => {
  it('差额为正：补一行兜底行后合计精确等于库文件大小', () => {
    const breakdown = computeUsageBreakdown(space(), { freeBytes: 5 * GB });

    // 12 = 2 + 1 + 其它 4 + 空闲页 5
    expect(breakdown.rows.map((row) => row.targetId)).toEqual([
      'platform.session-evidence',
      'unclassified',
      OTHER_USAGE_TARGET_ID,
      FREE_PAGES_TARGET_ID,
    ]);
    expect(breakdown.otherBytes).toBe(4 * GB);
    expect(breakdown.sumBytes).toBe(12 * GB);
    expect(breakdown.sumBytes).toBe(breakdown.fileBytes);
    expect(breakdown.closed).toBe(true);
    expect(breakdown.overflowBytes).toBe(0);

    // 分母恒为该库文件大小：2GB/12GB = 16.7%，5GB/12GB = 41.7%。
    expect(breakdown.rows[0].percent).toBe(16.7);
    expect(breakdown.rows[1].percent).toBe(8.3);
    expect(breakdown.rows[2].percent).toBe(33.3);
    expect(breakdown.rows[3].percent).toBe(41.7);
  });

  it('差额恰为 0：不产生兜底行，合计仍然闭合', () => {
    const breakdown = computeUsageBreakdown(space({ fileBytes: 8 * GB }), { freeBytes: 5 * GB });

    expect(breakdown.rows.map((row) => row.targetId)).toEqual([
      'platform.session-evidence',
      'unclassified',
      FREE_PAGES_TARGET_ID,
    ]);
    expect(breakdown.otherBytes).toBe(0);
    expect(breakdown.sumBytes).toBe(8 * GB);
    expect(breakdown.closed).toBe(true);
  });

  it('差额为负（归类 + 空闲页超过库大小）：不出负字节行，如实报超出量且不声称闭合', () => {
    const breakdown = computeUsageBreakdown(space({ fileBytes: 2 * GB }), { freeBytes: 5 * GB });

    // 3GB 归类 + 5GB 空闲页 > 2GB 文件
    expect(breakdown.otherBytes).toBe(-6 * GB);
    expect(breakdown.rows.some((row) => row.targetId === OTHER_USAGE_TARGET_ID)).toBe(false);
    expect(breakdown.overflowBytes).toBe(6 * GB);
    expect(breakdown.sumBytes).toBe(8 * GB);
    expect(breakdown.closed).toBe(false);
    // 单行仍受钳制，进度条不会反向。
    expect(breakdown.rows.every((row) => row.percent >= 0 && row.percent <= 100)).toBe(true);
  });

  it('空闲页未知时不谎报 0：不产生空闲页行，兜底行名字里说明含空闲页', () => {
    const breakdown = computeUsageBreakdown(space());

    expect(breakdown.freeBytesKnown).toBe(false);
    expect(breakdown.rows.some((row) => row.targetId === FREE_PAGES_TARGET_ID)).toBe(false);
    expect(breakdown.otherBytes).toBe(9 * GB);
    expect(breakdown.closed).toBe(true);

    const other = breakdown.rows.find((row) => row.targetId === OTHER_USAGE_TARGET_ID)!;
    expect(other.displayName).toContain('空闲页');
    expect(other.inferred).toBe(true);
    expect(other.hint).toContain('按差值推算');
  });

  it('文件大小缺失时回落到 pageSize × pageCount 作分母，并标记为回落', () => {
    const breakdown = computeUsageBreakdown(space({ fileBytes: 0, pageSize: 4096, pageCount: 1024 }), {
      freeBytes: 0,
    });

    expect(breakdown.denominatorBytes).toBe(4096 * 1024);
    expect(breakdown.denominatorIsFallback).toBe(true);
    // 数据类 3GB 远大于回落分母 4MB ⇒ 差额为负，不声称闭合。
    expect(breakdown.closed).toBe(false);
    expect(breakdown.overflowBytes).toBe(3 * GB - 4096 * 1024);
  });

  it('分母为 0 时不崩：全部 0%、不声称闭合', () => {
    const breakdown = computeUsageBreakdown(space({ fileBytes: 0, pageSize: 0, pageCount: 0 }), {
      freeBytes: 0,
    });

    expect(breakdown.denominatorBytes).toBe(0);
    expect(breakdown.rows.every((row) => row.percent === 0)).toBe(true);
    expect(breakdown.closed).toBe(false);
  });

  it('负数/NaN 字节数被收敛成 0，不会把合计算歪', () => {
    const breakdown = computeUsageBreakdown(
      space({
        fileBytes: 4 * GB,
        dataClasses: [
          {
            targetId: 'weird',
            displayName: '异常行',
            safetyLevel: '',
            manualCleanupAllowed: false,
            bytes: Number.NaN,
            pages: 0,
            tables: [],
          },
        ],
      }),
      { freeBytes: -1 },
    );

    expect(breakdown.classBytes).toBe(0);
    expect(breakdown.freeBytes).toBe(0);
    expect(breakdown.otherBytes).toBe(4 * GB);
    expect(breakdown.closed).toBe(true);
  });
});

describe('computeUsageBreakdown（估算标识）', () => {
  it('采样来源：数据类行标估算，兜底行标推算，精确的文件大小与空闲页不标', () => {
    const breakdown = computeUsageBreakdown(space(), { freeBytes: 5 * GB });

    const sampled = breakdown.rows.filter((row) => row.kind === 'class');
    expect(sampled.every((row) => row.estimated)).toBe(true);
    expect(sampled.every((row) => row.hint && row.hint.includes('估算'))).toBe(true);

    const other = breakdown.rows.find((row) => row.kind === 'other')!;
    expect(other.estimated).toBe(false);
    expect(other.inferred).toBe(true);

    const free = breakdown.rows.find((row) => row.kind === 'free')!;
    expect(free.estimated).toBe(false);
    expect(free.inferred).toBe(false);
  });

  it('dbstat 来源：数据类行是精确测量，不标估算', () => {
    const breakdown = computeUsageBreakdown(space({ spaceSource: 'dbstat', perTableAvailable: true }), {
      freeBytes: 5 * GB,
    });

    expect(breakdown.estimated).toBe(false);
    expect(breakdown.rows.filter((row) => row.kind === 'class').every((row) => !row.estimated)).toBe(true);
    expect(breakdown.rows.filter((row) => row.kind === 'class').every((row) => row.hint === null)).toBe(true);
  });
});

describe('resolveFreeBytes（从快照元数据取空闲页）', () => {
  it('快照里有 PRAGMA 结果时按 pageSize × freePageCount 取出', () => {
    expect(resolveFreeBytes(space(), [inventoryDatabase()])).toBe(4096 * 1_310_720);
  });

  it('快照当时没读到 PRAGMA（pageCount = 0）时返回 null 而不是 0', () => {
    expect(resolveFreeBytes(space(), [inventoryDatabase({ pageCount: 0, freePageCount: 0, reusableFreeBytes: 0 })])).toBeNull();
    expect(resolveFreeBytes(space(), [inventoryDatabase({ pageSizeBytes: 0, reusableFreeBytes: 0 })])).toBeNull();
  });

  it('库不存在或快照里没有该库时返回 null', () => {
    expect(resolveFreeBytes(space({ exists: false }), [inventoryDatabase()])).toBeNull();
    expect(resolveFreeBytes(space(), [])).toBeNull();
    expect(resolveFreeBytes(space(), null)).toBeNull();
    expect(resolveFreeBytes(space(), [inventoryDatabase({ databaseId: 'memory' })])).toBeNull();
  });

  it('确实没有空闲页时返回 0（已知的 0，不是未知）', () => {
    expect(resolveFreeBytes(space(), [inventoryDatabase({ freePageCount: 0, reusableFreeBytes: 0 })])).toBe(0);
  });
});
