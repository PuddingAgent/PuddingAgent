import fs from 'node:fs';
import path from 'node:path';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import {
  CALIBRATION_FRESH_WINDOW_MS,
  CODE_INDEX_BLOCK_FIELDS,
  CODE_INDEX_BLOCK_TEXT,
  CODE_INDEX_MAINTENANCE_FIELDS,
  CODE_INDEX_PROJECT_FIELDS,
  CODE_INDEX_VISIBLE_PROJECT_LIMIT,
  MAINTENANCE_REASON_TEXT,
  deriveCalibrationFreshnessRatio,
  deriveCodeIndexBlock,
  deriveCodeIndexProjectMarks,
  describeMaintenanceReason,
  describeMaintenanceStatus,
  describeMaintenanceWord,
  rawFieldText,
  summarizeCodeIndexProjects,
} from './health';
import { MARK_WORDS, PathBrokenMark, RippleMark, StaleMark, StatusOrb, UnregisteredMark } from './visuals';
import { CodeIndexProjectRow } from './codeIndexRow';
import type {
  CodeIndexMaintenanceStatus,
  CodeIndexProjectStatus,
  CodeIndexStatusDetail,
  FullTextIndexStatusDetail,
  FullTextIndexStatusSnapshot,
} from './types';

// ── Slice P3 定向测试：B 卡（符号索引 / codeIndex）真实数据 + 四态降级 ──────
// 断言对象 = `health.ts` 的**纯函数** + `visuals.tsx` 的标记组件（静态渲染）+ **源文件静态扫描**。
//
// 契约真源（**字段名从后端反读，不是猜的**）：
//   · `Source/PuddingHost/Services/CodeIndexStatusProbe.cs`（两个 record 的成员）
//   · `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexStatusTests.cs` A5（**恰好 23 个 camelCase** 的逐个点名断言）
//   · `Tests/PuddingHost.Tests/Hosting/SA2CodeIndexTestDoubles.cs`（`Sa2Samples.MaintenanceStatus` 逐字段赋值）
//
// 本文件的用例**全部可失败**：把「块缺失」折叠成空对象（变异 MUTA-P3-1）、把未知画成正常、
// 把注册态与维护态合成一个「状态」，都会有具名用例转红。

/** 固定「现在」：2026-10-01T05:00:00Z ⇒ 断言不随时间漂移。 */
const NOW = Date.UTC(2026, 9, 1, 5, 0, 0);

const ROOT = 'D:\\CodeProject\\PuddingAgent\\PuddingAgent';
const PROJECT_ID = '8a48458b30150fdbed4baaced35d24cf';

const CSS_PATH = path.join(__dirname, 'index.css');
const PAGE_PATH = path.join(__dirname, 'index.tsx');

// ══ 权威字段名清单（**逐字抄自后端**，抄错即测试红）══════════════════════

/** `CodeIndexStatusDetailSnapshot` 的 8 个字段（`CodeIndexStatusProbe.cs` 第 50~60 行）。 */
const AUTHORITATIVE_BLOCK_FIELDS = [
  'workspaceIds',
  'maintenanceRunning',
  'batchesProcessed',
  'reconcileRequests',
  'removalObservations',
  'pendingReconcileScopeCount',
  'projects',
  'note',
] as const;

/** `CodeIndexProjectStatusSnapshot` 的 12 个字段（同文件第 100~115 行）。 */
const AUTHORITATIVE_PROJECT_FIELDS = [
  'workspaceId',
  'projectId',
  'displayName',
  'rootPath',
  'registered',
  'registrationState',
  'registrationStatus',
  'registrationSource',
  'maintenance',
  'maintenanceReason',
  'rootPathExists',
  'stale',
] as const;

/** `CodeIndexMaintenanceScopeStatus` 的 **23** 个字段 —— 顺序逐字对齐 SA2 A5 的断言数组。 */
const AUTHORITATIVE_MAINTENANCE_FIELDS = [
  'workspaceId',
  'scopeId',
  'rootPath',
  'observedVersion',
  'desiredVersion',
  'committedVersion',
  'markedWhileInFlightCount',
  'indexPending',
  'indexInFlight',
  'needsReconcile',
  'reconcileReason',
  'reconcileRequestCount',
  'removalObservationCount',
  'lastRemovalPaths',
  'removedFileCount',
  'incrementallyIndexedFileCount',
  'scopeEscalationCount',
  'sweptFileCount',
  'calibrationRunCount',
  'rejectedCalibrationRunCount',
  'lastCalibrationAtUtc',
  'recentObservationCount',
  'watcherAttached',
] as const;

// ══ 夹具 ═══════════════════════════════════════════════════════════════

function maintenanceFixture(
  overrides: Partial<CodeIndexMaintenanceStatus> = {},
): CodeIndexMaintenanceStatus {
  return {
    workspaceId: 'default',
    scopeId: PROJECT_ID,
    rootPath: ROOT,
    observedVersion: 11,
    desiredVersion: 12,
    committedVersion: 10,
    markedWhileInFlightCount: 1,
    indexPending: false,
    indexInFlight: false,
    needsReconcile: false,
    reconcileReason: null,
    reconcileRequestCount: 2,
    removalObservationCount: 3,
    lastRemovalPaths: ['D:\\code\\a.cs'],
    removedFileCount: 4,
    incrementallyIndexedFileCount: 5,
    scopeEscalationCount: 6,
    sweptFileCount: 7,
    calibrationRunCount: 8,
    rejectedCalibrationRunCount: 0,
    lastCalibrationAtUtc: new Date(NOW).toISOString(),
    recentObservationCount: 13,
    watcherAttached: true,
    ...overrides,
  };
}

function projectFixture(overrides: Partial<CodeIndexProjectStatus> = {}): CodeIndexProjectStatus {
  return {
    workspaceId: 'default',
    projectId: PROJECT_ID,
    displayName: 'PuddingAgent',
    rootPath: ROOT,
    registered: true,
    registrationState: 'Active',
    registrationStatus: 'Active',
    registrationSource: 'Manual',
    maintenance: maintenanceFixture(),
    maintenanceReason: null,
    rootPathExists: true,
    stale: false,
    ...overrides,
  };
}

function blockFixture(overrides: Partial<CodeIndexStatusDetail> = {}): CodeIndexStatusDetail {
  return {
    workspaceIds: ['default'],
    maintenanceRunning: true,
    batchesProcessed: 42,
    reconcileRequests: 3,
    removalObservations: 5,
    pendingReconcileScopeCount: 2,
    projects: [projectFixture()],
    note: null,
    ...overrides,
  };
}

const FULL_TEXT: FullTextIndexStatusDetail = {
  configured: true,
  enabled: true,
  indexRoot: 'D:\\Data\\fulltext-index',
  indexRootExists: true,
  workspaceRoot: 'D:\\CodeProject\\PuddingAgent',
  maxIndexBytes: 1_073_741_824,
  minRebuildInterval: '12:00:00',
  acceptedScopes: [ROOT],
  rejectedReasons: [],
  compositionCreated: true,
  maintenance: { configured: false, enabled: false, note: 'n/a' },
  scopes: [
    {
      scopePath: ROOT,
      scopeExists: true,
      hasIndex: true,
      indexDirectory: 'D:\\Data\\fulltext-index\\scopes\\default',
      indexDirectoryExists: true,
      indexEntryCount: 12,
      indexBytes: 1_048_576,
      indexDirectoryLastWriteUtc: '2026-10-01T04:48:00Z',
    },
  ],
  jobs: [],
  jobsReason: 'no-jobs-recorded',
};

/** 根响应：**不带**第 3 个参数时 `codeIndex` 键**不存在**（= 未重启的线上 Core）。 */
function rootFixture(codeIndex?: CodeIndexStatusDetail | null): FullTextIndexStatusSnapshot {
  const base: FullTextIndexStatusSnapshot = {
    generatedAtUtc: '2026-10-01T05:00:00Z',
    fullText: FULL_TEXT,
  };
  return codeIndex === undefined ? base : { ...base, codeIndex };
}

function readSource(file: string): string {
  // 仪器纪律：本断言管的是**代码**，不是注释里的举例 ⇒ 先把块注释与**行注释**一并剥掉
  //（否则“文档里提到的字段名”会假红）。行注释只剥“行首或空白后”的 // ⇒ 不会误删字符串里的 `http://`。
  return fs
    .readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[ \t])\/\/[^\n]*/gm, '$1');
}

// ══ 契约常量：字段名与顺序 ══════════════════════════════════════════════

describe('契约常量：字段名与顺序从后端反读（页面不得手写散落）', () => {
  it('codeIndex 块：8 个字段名与顺序逐字等于 CodeIndexStatusDetailSnapshot', () => {
    expect(CODE_INDEX_BLOCK_FIELDS.map((field) => field.key)).toEqual([
      ...AUTHORITATIVE_BLOCK_FIELDS,
    ]);
  });

  it('逐项目：12 个字段名与顺序逐字等于 CodeIndexProjectStatusSnapshot', () => {
    expect(CODE_INDEX_PROJECT_FIELDS.map((field) => field.key)).toEqual([
      ...AUTHORITATIVE_PROJECT_FIELDS,
    ]);
  });

  it('维护态：**恰好 23** 个 camelCase 字段，顺序逐字等于 SA2 A5 的断言数组', () => {
    expect(CODE_INDEX_MAINTENANCE_FIELDS).toHaveLength(23);
    expect(CODE_INDEX_MAINTENANCE_FIELDS.map((field) => field.key)).toEqual([
      ...AUTHORITATIVE_MAINTENANCE_FIELDS,
    ]);
  });

  it('字段清单里每个字段都有中文标签（L2 列标题可读，不是裸 key）', () => {
    for (const field of [
      ...CODE_INDEX_BLOCK_FIELDS,
      ...CODE_INDEX_PROJECT_FIELDS,
      ...CODE_INDEX_MAINTENANCE_FIELDS,
    ]) {
      expect(field.label.length).toBeGreaterThan(0);
      expect(field.label).not.toBe(field.key);
    }
  });

  it('页面（index.tsx）不手写字段名：字段名不得以字符串字面量出现，且确实引用了常量', () => {
    const source = readSource(PAGE_PATH);
    for (const key of [
      ...AUTHORITATIVE_BLOCK_FIELDS,
      ...AUTHORITATIVE_PROJECT_FIELDS,
      ...AUTHORITATIVE_MAINTENANCE_FIELDS,
    ]) {
      expect(source).not.toContain(`'${key}'`);
      expect(source).not.toContain(`"${key}"`);
      expect(source).not.toContain(`\`${key}\``);
    }
    expect(source).toContain('CODE_INDEX_BLOCK_FIELDS');
    expect(source).toContain('CODE_INDEX_PROJECT_FIELDS');
    expect(source).toContain('CODE_INDEX_MAINTENANCE_FIELDS');
  });
});

// ══ 四态降级 ═══════════════════════════════════════════════════════════

describe('四态降级：块缺失 / 观测不可用 / 无项目 / 已观测（四态互不相同）', () => {
  const absent = () => deriveCodeIndexBlock(rootFixture());
  const emptyBlock = () => deriveCodeIndexBlock(rootFixture(blockFixture({ projects: [] })));
  const unavailableBlock = () =>
    deriveCodeIndexBlock(
      rootFixture(
        blockFixture({
          projects: [],
          note: 'code-index-status-unavailable: InvalidOperationException: boom',
          maintenanceRunning: null,
          batchesProcessed: null,
          reconcileRequests: null,
          removalObservations: null,
          pendingReconcileScopeCount: null,
        }),
      ),
    );
  const observed = () => deriveCodeIndexBlock(rootFixture(blockFixture()));

  it('态一 · 块缺失（键不存在）⇒ absent ·「未接入」· 中性灰 · 不当作空项目', () => {
    const verdict = absent();
    expect(verdict.state).toBe('absent');
    expect(verdict.rule).toBe(1);
    expect(verdict.status.text).toBe(CODE_INDEX_BLOCK_TEXT.absent);
    expect(verdict.status.tone).toBe('neutral');
    // 键缺失 ≠「没有项目」：不得把未知折叠成空对象。
    expect(verdict.block).toBeNull();
    expect(verdict.state).not.toBe('empty');
  });

  it('态二 · 块存在但 projects 为空 ⇒ empty ·「无项目」· 与块缺失是**不同**状态', () => {
    const verdict = emptyBlock();
    expect(verdict.state).toBe('empty');
    expect(verdict.rule).toBe(3);
    expect(verdict.status.text).toBe(CODE_INDEX_BLOCK_TEXT.empty);
    expect(verdict.state).not.toBe(absent().state);
    expect(verdict.status.text).not.toBe(absent().status.text);
  });

  it('态三 · note 不可用 ⇒ unavailable · warn · 升格为长句（未知 ≠ 健康）', () => {
    const verdict = unavailableBlock();
    expect(verdict.state).toBe('unavailable');
    expect(verdict.rule).toBe(2);
    expect(verdict.status.tone).toBe('warn');
    expect(verdict.headline).toContain('code-index-status-unavailable');
    expect(verdict.guidance).toContain('不可知');
    expect(verdict.state).not.toBe(absent().state);
    expect(verdict.state).not.toBe(emptyBlock().state);
  });

  it('态三 b · maintenanceRunning 为 null（无 note）⇒ 同样归 unavailable，绝不折叠成 false', () => {
    const verdict = deriveCodeIndexBlock(rootFixture(blockFixture({ maintenanceRunning: null })));
    expect(verdict.state).toBe('unavailable');
    expect(verdict.status.hint).toContain('maintenanceRunning');
    expect(verdict.status.text).not.toBe(CODE_INDEX_BLOCK_TEXT.empty);
  });

  it('态四 · 已观测：健康 ⇒ ok；有陈旧 ⇒ warn；有进行中 ⇒ busy', () => {
    expect(observed().state).toBe('observed');
    expect(observed().level).toBe('ok');
    expect(
      deriveCodeIndexBlock(rootFixture(blockFixture({ projects: [projectFixture({ stale: true })] })))
        .level,
    ).toBe('warn');
    expect(
      deriveCodeIndexBlock(
        rootFixture(
          blockFixture({
            projects: [projectFixture({ maintenance: maintenanceFixture({ indexInFlight: true }) })],
          }),
        ),
      ).level,
    ).toBe('busy');
  });

  it('四态短词两两不同 + 主视觉 level 两两不同（不靠颜色区分状态）', () => {
    const words = Object.values(CODE_INDEX_BLOCK_TEXT);
    expect(new Set(words).size).toBe(4);
    const levels = [absent().level, unavailableBlock().level, emptyBlock().level, observed().level];
    expect(new Set(levels).size).toBe(4);
  });

  it('快照本身缺失（连 fullText 都没有）⇒ 也是 absent，不抛错', () => {
    expect(deriveCodeIndexBlock(null).state).toBe('absent');
    expect(deriveCodeIndexBlock(undefined).state).toBe('absent');
  });
});

// ══ D3：注册态与维护态不合并 ═══════════════════════════════════════════

describe('D3：注册态（索引注册表）与维护态（维护驱动）分开呈现，绝不合并', () => {
  it('registrationStatus=Registering 与 maintenance.indexPending=true 同时可见且互不覆盖', () => {
    const entry = projectFixture({
      registrationState: 'Active',
      registrationStatus: 'Registering',
      maintenance: maintenanceFixture({ indexPending: true }),
    });
    const marks = deriveCodeIndexProjectMarks(entry);
    expect(marks.registrationHint).toContain('Registering');
    expect(marks.registrationHint).toContain('Active');
    expect(marks.maintenanceHint).toContain('indexPending: 是');
    expect(marks.registrationHint).not.toBe(marks.maintenanceHint);
  });

  it('维护态 tooltip 是 23 行，行序 = 契约常量顺序（字段名不得手写）', () => {
    const lines = describeMaintenanceStatus(maintenanceFixture()).split('\n');
    expect(lines).toHaveLength(23);
    expect(lines.map((line) => line.split(':')[0])).toEqual([...AUTHORITATIVE_MAINTENANCE_FIELDS]);
  });

  it('maintenance 为 null ⇒「维护态缺席」（有如实原因），与「已挂接但空闲」是不同呈现', () => {
    const missing = deriveCodeIndexProjectMarks(
      projectFixture({ maintenance: null, maintenanceReason: 'scope-not-attached' }),
    );
    const idle = deriveCodeIndexProjectMarks(projectFixture());
    expect(missing.maintenanceMissing).toBe(true);
    expect(missing.watcherAttached).toBeNull();
    expect(missing.maintenanceHint).toContain('缺席');
    expect(idle.maintenanceMissing).toBe(false);
    expect(idle.watcherAttached).toBe(true);
    expect(missing.maintenanceHint).not.toBe(idle.maintenanceHint);
    expect(describeMaintenanceWord(projectFixture({ maintenance: null }))).toBeNull();
    expect(describeMaintenanceWord(projectFixture())).toBe('空闲');
    expect(describeMaintenanceReason('scope-not-attached')).toBe(
      MAINTENANCE_REASON_TEXT['scope-not-attached'],
    );
  });

  it('陈旧只由「未登记 / 根路径不存在」驱动：维护态缺席**不**置位陈旧', () => {
    const missingMaintenance = deriveCodeIndexProjectMarks(
      projectFixture({ maintenance: null, maintenanceReason: 'maintenance-driver-not-running' }),
    );
    expect(missingMaintenance.stale).toBe(false);
    expect(missingMaintenance.level).toBe('ok');

    const unregistered = deriveCodeIndexProjectMarks(
      projectFixture({
        registered: false,
        registrationState: null,
        registrationStatus: null,
        registrationSource: null,
        stale: true,
      }),
    );
    expect(unregistered.unregistered).toBe(true);
    expect(unregistered.level).toBe('warn');

    const broken = deriveCodeIndexProjectMarks(
      projectFixture({ rootPathExists: false, stale: true }),
    );
    expect(broken.pathBroken).toBe(true);
    expect(broken.level).toBe('error');
  });

  it('被拒校准 > 0 ⇒ warn（不是「正常」），watcherAttached=false ⇒ 开关为「关」而非未知', () => {
    const rejected = deriveCodeIndexProjectMarks(
      projectFixture({ maintenance: maintenanceFixture({ rejectedCalibrationRunCount: 3 }) }),
    );
    expect(rejected.calibrationRejected).toBe(true);
    expect(rejected.level).toBe('warn');
    const detached = deriveCodeIndexProjectMarks(
      projectFixture({ maintenance: maintenanceFixture({ watcherAttached: false }) }),
    );
    expect(detached.watcherAttached).toBe(false);
  });
});

// ══ 计数与首屏上限 ═════════════════════════════════════════════════════

describe('逐项目计数（各自独立，不折叠）', () => {
  it('陈旧 / 未登记 / 路径失效 / 进行中 / 维护态缺席 / 被拒校准分别计数', () => {
    const block = blockFixture({
      projects: [
        projectFixture(),
        projectFixture({
          projectId: 'p-stale',
          registered: false,
          registrationState: null,
          registrationStatus: null,
          registrationSource: null,
          stale: true,
        }),
        projectFixture({ projectId: 'p-broken', rootPathExists: false, stale: true }),
        projectFixture({
          projectId: 'p-inflight',
          maintenance: maintenanceFixture({ indexInFlight: true, rejectedCalibrationRunCount: 2 }),
        }),
        projectFixture({
          projectId: 'p-nomaint',
          maintenance: null,
          maintenanceReason: 'scope-not-attached',
        }),
      ],
    });
    expect(summarizeCodeIndexProjects(block)).toEqual({
      total: 5,
      stale: 2,
      unregistered: 1,
      pathBroken: 1,
      inFlight: 1,
      maintenanceMissing: 1,
      rejectedCalibration: 1,
      watcherDetached: 0,
    });
  });

  it('首屏逐项目上限是显式常量（其余走 L2，不静默丢弃）', () => {
    expect(CODE_INDEX_VISIBLE_PROJECT_LIMIT).toBe(6);
  });
});

// ══ 表达层：标记三重编码 + 校准环三态 ══════════════════════════════════

describe('B 卡逐项目行：注册态与维护态**确实分别渲染**（D3 的渲染级落点）', () => {
  it('一行里同时给出两个独立分区（registry / driver），标记与开关按事实出现', () => {
    const html = renderToStaticMarkup(
      <CodeIndexProjectRow
        entry={projectFixture({
          registered: false,
          registrationState: null,
          registrationStatus: null,
          registrationSource: null,
          rootPathExists: false,
          stale: true,
          maintenance: maintenanceFixture({ rejectedCalibrationRunCount: 2, watcherAttached: false }),
        })}
        nowMs={NOW}
      />,
    );
    // 两个分区各自存在（不是合成的一个「状态」）
    expect(html).toContain('data-zone="registry"');
    expect(html).toContain('data-zone="driver"');
    // 未登记 / 陈旧 / 路径失效三个标记同时出现（且都带短词）
    expect(html).toContain('vs-mark--unregistered');
    expect(html).toContain('vs-mark--stale');
    expect(html).toContain('vs-mark--pathbroken');
    expect(html).toContain(MARK_WORDS.unregistered);
    expect(html).toContain(MARK_WORDS.stale);
    expect(html).toContain(MARK_WORDS.pathBroken);
    // 被拒校准 ⇒ warn 短词；watcher 关 ⇒ 开关落在 off 位（不是未知）
    expect(html).toContain('被拒校准 2');
    expect(html).toContain('vs-toggle--off');
    // 陈旧标记自带急闪（动效），但**行内球体不呼吸**（§9.7 ⑥：呼吸名额只给 L0）
    expect(html).toContain('vs-anim-alert');
    expect(html).not.toContain('vs-anim-ambient');
  });

  it('维护态缺席：渲染「缺席 + 原因」的虚线空心圆，而不是一个假的开关/环', () => {
    const html = renderToStaticMarkup(
      <CodeIndexProjectRow
        entry={projectFixture({ maintenance: null, maintenanceReason: 'scope-not-attached' })}
        nowMs={NOW}
      />,
    );
    expect(html).toContain('维护态缺席');
    expect(html).toContain('vs-unknown__hole');
    expect(html).not.toContain('vs-toggle');
    expect(html).not.toContain('vs-mark--inflight');
  });

  it('健康项目：涟漪不出现，watcher 开关与校准环出现（就绪时不吵）', () => {
    const html = renderToStaticMarkup(<CodeIndexProjectRow entry={projectFixture()} nowMs={NOW} />);
    expect(html).toContain('vs-toggle--on');
    expect(html).toContain('vs-ring');
    expect(html).not.toContain('vs-mark--stale');
    expect(html).not.toContain('vs-mark--inflight');
    // §9.7 ⑥：行内不得出现任何动效（唯一的呼吸名额留给 L0）
    expect(html).not.toContain('vs-anim-');
  });

  it('行内球体的 animate=false 不改既有默认：StatusOrb 默认仍呼吸（ok），传 false 则静止', () => {
    expect(renderToStaticMarkup(<StatusOrb level="ok" />)).toContain('vs-anim-ambient');
    expect(renderToStaticMarkup(<StatusOrb level="ok" animate={false} />)).not.toContain('vs-anim-');
    // 未知/关闭态本来就不呼吸（I10），传 true 也不给动效
    expect(renderToStaticMarkup(<StatusOrb level="unknown" animate />)).not.toContain('vs-anim-');
  });

  it('索引进行中：出现涟漪（不定长，不给百分比）', () => {
    const html = renderToStaticMarkup(
      <CodeIndexProjectRow
        entry={projectFixture({ maintenance: maintenanceFixture({ indexInFlight: true }) })}
        nowMs={NOW}
      />,
    );
    expect(html).toContain('vs-mark--inflight');
    expect(html).toContain('vs-anim-ripple');
    expect(html).not.toMatch(/%/);
  });
});

describe('B 卡标记：形状 + 动效 + 短词三重编码（颜色永不单独承载语义）', () => {
  it('陈旧标记：形状类 + 急闪 + 短词「陈旧」三者同屏', () => {
    const html = renderToStaticMarkup(<StaleMark title="t" />);
    expect(html).toContain('vs-mark--stale');
    expect(html).toContain('vs-anim-alert');
    expect(html).toContain(MARK_WORDS.stale);
  });

  it('未登记 / 路径失效标记：各有独立形状类 + 短词，且为静态（无动效 = 不确定/事实已定，不制造噪声）', () => {
    const unregistered = renderToStaticMarkup(<UnregisteredMark />);
    const broken = renderToStaticMarkup(<PathBrokenMark />);
    expect(unregistered).toContain('vs-mark--unregistered');
    expect(broken).toContain('vs-mark--pathbroken');
    expect(unregistered).toContain(MARK_WORDS.unregistered);
    expect(broken).toContain(MARK_WORDS.pathBroken);
    expect(unregistered).not.toContain('vs-anim-');
    expect(broken).not.toContain('vs-anim-');
  });

  it('进行中涟漪标记：涟漪 + 短词「索引中」', () => {
    const html = renderToStaticMarkup(<RippleMark />);
    expect(html).toContain('vs-mark--inflight');
    expect(html).toContain('vs-anim-ripple');
    expect(html).toContain(MARK_WORDS.indexInFlight);
  });

  it('四个标记的形状类两两不同（灰度打印 / 色盲下仍可辨）', () => {
    const shapes = [
      renderToStaticMarkup(<StaleMark />),
      renderToStaticMarkup(<UnregisteredMark />),
      renderToStaticMarkup(<PathBrokenMark />),
      renderToStaticMarkup(<RippleMark />),
    ].map((html) => /vs-mark--[a-z]+/.exec(html)?.[0] ?? '');
    expect(shapes.every((shape) => shape.length > 0)).toBe(true);
    expect(new Set(shapes).size).toBe(4);
  });

  it('不自创第四档动效：index.css 的 keyframes 仍只有 §9.4 的 4 个', () => {
    const css = readSource(CSS_PATH);
    const names = [...css.matchAll(/@keyframes\s+([A-Za-z0-9_-]+)/g)].map((match) => match[1]).sort();
    expect(names).toEqual(['vs-breathe', 'vs-flash', 'vs-flow', 'vs-ripple']);
  });

  it('新增的标记类不自行声明 animation/transition（动效走既有 .vs-anim-*，reduced-motion 降级自动覆盖）', () => {
    const css = readSource(CSS_PATH);
    const reduceAt = css.search(/@media\s*\(prefers-reduced-motion:\s*reduce\)\s*\{/);
    expect(reduceAt).toBeGreaterThan(0);
    const head = css.slice(0, reduceAt);
    const blockRe = /([^{}]+)\{([^{}]*)\}/g;
    let hit: RegExpExecArray | null = blockRe.exec(head);
    let inspected = 0;
    while (hit !== null) {
      const selector = hit[1];
      const body = hit[2];
      if (selector.includes('.vs-mark')) {
        inspected += 1;
        expect(body).not.toMatch(/animation\s*:/);
        expect(body).not.toMatch(/transition\s*:/);
      }
      hit = blockRe.exec(head);
    }
    // 非空性检查：确实扫到了标记规则块（否则本断言退化成恒真）。
    expect(inspected).toBeGreaterThanOrEqual(5);
  });
});

describe('校准新鲜度环（表达层窗口）三态', () => {
  it('时间戳不可得 ⇒ ratio = null（未知环，不是 0 填充）', () => {
    expect(deriveCalibrationFreshnessRatio(null, NOW)).toBeNull();
    expect(deriveCalibrationFreshnessRatio(undefined, NOW)).toBeNull();
    expect(deriveCalibrationFreshnessRatio('not-a-date', NOW)).toBeNull();
  });

  it('刚校准 ⇒ 满环；超过一个窗口 ⇒ 空环（窗口是显式常量）', () => {
    expect(CALIBRATION_FRESH_WINDOW_MS).toBe(24 * 60 * 60 * 1000);
    expect(deriveCalibrationFreshnessRatio(new Date(NOW).toISOString(), NOW)).toBe(1);
    expect(
      deriveCalibrationFreshnessRatio(
        new Date(NOW - CALIBRATION_FRESH_WINDOW_MS * 2).toISOString(),
        NOW,
      ),
    ).toBe(0);
  });
});

// ══ 纯文本三态（tooltip 用）════════════════════════════════════════════

describe('rawFieldText：未知 ≠ 否 ≠ 0，空数组 ≠ 未知', () => {
  it('null 一律「未知」；0 原样「0」；两者不同', () => {
    expect(rawFieldText('count', null)).toBe('未知');
    expect(rawFieldText('count', undefined)).toBe('未知');
    expect(rawFieldText('count', 0)).toBe('0');
    expect(rawFieldText('bool', false)).toBe('否');
    expect(rawFieldText('bool', null)).toBe('未知');
  });

  it('数组：空 ⇒「（空）」；非空 ⇒「；」拼接；复合结构 ⇒ 指向下方表', () => {
    expect(rawFieldText('list', [])).toBe('（空）');
    expect(rawFieldText('list', ['a', 'b'])).toBe('a；b');
    expect(rawFieldText('nested', { a: 1 })).toContain('见下方表');
    expect(rawFieldText('nested', null)).toBe('未知');
  });
});
