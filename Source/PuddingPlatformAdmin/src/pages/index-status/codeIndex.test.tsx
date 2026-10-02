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
  SOURCE_COMMIT_OUTCOME_WORD,
  SOURCE_MAINTENANCE_COPY,
  SOURCE_MAINTENANCE_MODE_WORD,
  deriveCalibrationFreshnessRatio,
  deriveCodeIndexBlock,
  deriveCodeIndexProjectMarks,
  deriveSourceMaintenanceMarks,
  describeMaintenanceReason,
  describeMaintenanceStatus,
  describeMaintenanceWord,
  describeSourceCommitOutcome,
  describeSourceMaintenanceMode,
  rawFieldText,
  summarizeCodeIndexProjects,
} from './health';
import { MARK_WORDS, PathBrokenMark, RippleMark, StaleMark, StatusOrb, UnregisteredMark } from './visuals';
import { CodeIndexProjectRow } from './codeIndexRow';
import { classifyIndexStatusFailure } from './api';
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

/**
 * `CodeIndexMaintenanceScopeStatus` 记录**尾部追加**的 9 个 D4「源维护」字段（顺序逐字对齐 C# 记录）。
 * 真源：`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`（record 末尾 9 个默认参数）。
 */
const D4_SOURCE_MAINTENANCE_FIELDS = [
  'sourceMaintenanceMode',
  'sourceMaintenanceRunCount',
  'sourceMaintenanceExtractedFileCount',
  'sourceMaintenanceReusedFileCount',
  'sourceMaintenanceOrphanFileCount',
  'sourceMaintenanceUnresolvedPathCount',
  'sourceMaintenanceDeletedFileCount',
  'lastSourceMaintenanceCommitOutcome',
  'lastSourceMaintenanceSessionKey',
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
    // ── D4「源维护」9 字段：夹具基线 = Coordinator · 已跑过且快照复用 · 无未定论路径 ──
    sourceMaintenanceMode: 1,
    sourceMaintenanceRunCount: 3,
    sourceMaintenanceExtractedFileCount: 20,
    sourceMaintenanceReusedFileCount: 7,
    sourceMaintenanceOrphanFileCount: 0,
    sourceMaintenanceUnresolvedPathCount: 0,
    sourceMaintenanceDeletedFileCount: 2,
    lastSourceMaintenanceCommitOutcome: 0,
    lastSourceMaintenanceSessionKey: 'sess-abc',
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

  it('I1 · 冻结核心：维护态字段表前 23 名（同序前缀）逐字不变（改名/删除/重排 ⇒ 红）', () => {
    // ⛔ 不再写死「恰好 23 项」；改为「slice(0, 23) = 冻结名单」（与后端 A5 修法同构，可增长）。
    expect(
      CODE_INDEX_MAINTENANCE_FIELDS.slice(0, AUTHORITATIVE_MAINTENANCE_FIELDS.length).map(
        (field) => field.key,
      ),
    ).toEqual([...AUTHORITATIVE_MAINTENANCE_FIELDS]);
  });

  it('I2 · 完整性：9 个 D4「源维护」字段全部在表内、追加在末尾、顺序一致、无重复', () => {
    const keys = CODE_INDEX_MAINTENANCE_FIELDS.map((field) => field.key);
    for (const key of D4_SOURCE_MAINTENANCE_FIELDS) {
      expect(keys).toContain(key);
    }
    // 9 个新字段**只能**在末尾（顺序与 §2.1 表逐位一致）
    expect(keys.slice(AUTHORITATIVE_MAINTENANCE_FIELDS.length)).toEqual([
      ...D4_SOURCE_MAINTENANCE_FIELDS,
    ]);
    expect(CODE_INDEX_MAINTENANCE_FIELDS).toHaveLength(
      AUTHORITATIVE_MAINTENANCE_FIELDS.length + D4_SOURCE_MAINTENANCE_FIELDS.length,
    );
    expect(new Set(keys).size).toBe(keys.length);
  });

  it('I2b · 每个字段 key 都能在 types.ts 的接口里找到（源码文本核对）', () => {
    const source = readSource(path.join(__dirname, 'types.ts'));
    for (const key of CODE_INDEX_MAINTENANCE_FIELDS.map((field) => field.key)) {
      expect(new RegExp(`^\\s*${key}\\s*:`, 'm').test(source)).toBe(true);
    }
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

  it('维护态 tooltip：字段行 = 契约常量（冻结 23 前缀 + 9 源维护），末尾追加派生说明', () => {
    const lines = describeMaintenanceStatus(maintenanceFixture()).split('\n');
    const fieldLineCount = CODE_INDEX_MAINTENANCE_FIELDS.length;
    const fieldLines = lines.slice(0, fieldLineCount);
    // 前 23 行仍是那 23 个冻结名（同序前缀）
    expect(
      fieldLines.slice(0, AUTHORITATIVE_MAINTENANCE_FIELDS.length).map((line) => line.split(':')[0]),
    ).toEqual([...AUTHORITATIVE_MAINTENANCE_FIELDS]);
    // 所有字段行逐位 = 契约常量顺序（字段名不得手写）
    expect(fieldLines.map((line) => line.split(':')[0])).toEqual(
      CODE_INDEX_MAINTENANCE_FIELDS.map((field) => field.key),
    );
    // 派生说明行在字段行之后（`▸ ` 前缀），且含「源维护」事实
    const derivedLines = lines.slice(fieldLineCount);
    expect(derivedLines.length).toBeGreaterThan(0);
    for (const line of derivedLines) expect(line.startsWith('▸ ')).toBe(true);
    expect(lines.join('\n')).toContain('sourceMaintenanceMode');
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

// ══ D4「源维护」9 字段 + 派生信号（I3~I8）═══════════════════════════════
// 动机（D10 教训）：这 9 个字段让「索引可能不全」读得到 —— 未定论路径 / 提交被取代 / 世代过期 /
// 退化成逐文件提取。丢了它们，就会把「索引不全」误读成「代码不存在」（D5 两次误判的根因）。
// 三态纪律：`null` = 未知 ≠ `false` ≠ `0`；未登记的枚举取值**原样显示数字**，不得当成 Legacy/Committed。

describe('D4「源维护」派生信号（未定论路径 / 提交结果 / 退化提取）', () => {
  const marks = (overrides: Partial<CodeIndexMaintenanceStatus> = {}) =>
    deriveSourceMaintenanceMarks(maintenanceFixture(overrides));

  it('枚举词表与逐字文案常量在册（未登记取值走「未知取值 <n>」分支）', () => {
    expect(SOURCE_MAINTENANCE_MODE_WORD[0]).toBe('Legacy（逐文件）');
    expect(SOURCE_MAINTENANCE_MODE_WORD[1]).toBe('Coordinator（源维护协调器）');
    expect(SOURCE_COMMIT_OUTCOME_WORD[0]).toBe('Committed（本轮提交已接受）');
    expect(describeSourceMaintenanceMode(3)).toBe('未知取值 3');
    expect(describeSourceMaintenanceMode(null)).toBe('未知');
    expect(describeSourceCommitOutcome(0)).toContain('Committed');
    expect(describeSourceCommitOutcome(7)).toBe(
      `${SOURCE_MAINTENANCE_COPY.outcomeUnknownPrefix}7`,
    );
  });

  it('I3 · sourceMaintenanceMode：1 ⇒ Coordinator（源维护协调器），0 ⇒ Legacy（逐文件）', () => {
    expect(marks({ sourceMaintenanceMode: 1 }).modeWord).toBe('Coordinator（源维护协调器）');
    expect(marks({ sourceMaintenanceMode: 0 }).modeWord).toBe('Legacy（逐文件）');
    // 两者都能读出来 ⇒ 组合 level 是 ok（其余字段取夹具基线：无未定论路径 / Committed）
    expect(marks({ sourceMaintenanceMode: 1 }).level).toBe('ok');
    expect(marks({ sourceMaintenanceMode: 0 }).level).toBe('ok');
  });

  it('I4 · sourceMaintenanceMode = 7（未登记取值）⇒ 含「未知取值 7」且 level 不是 ok', () => {
    const m = marks({ sourceMaintenanceMode: 7 });
    expect(m.modeWord).toBe('未知取值 7');
    expect(m.level).not.toBe('ok');
    expect(m.level).toBe('unknown');
    expect(m.hint).toContain('未知取值 7');
  });

  it('I5 · lastSourceMaintenanceCommitOutcome：0⇒ok / 1⇒warn / 2⇒warn / null⇒未知 / 9⇒未知取值', () => {
    const committed = marks({ lastSourceMaintenanceCommitOutcome: 0 });
    expect(committed.commitOutcomeLevel).toBe('ok');
    expect(committed.level).toBe('ok');

    const superseded = marks({ lastSourceMaintenanceCommitOutcome: 1 });
    expect(superseded.commitOutcomeLevel).toBe('warn');
    expect(superseded.level).toBe('warn');
    expect(superseded.commitOutcomeWord).toBe(SOURCE_MAINTENANCE_COPY.superseded);
    expect(superseded.hint).toContain('需继续补跑');

    const staleEpoch = marks({ lastSourceMaintenanceCommitOutcome: 2 });
    expect(staleEpoch.commitOutcomeLevel).toBe('warn');
    expect(staleEpoch.level).toBe('warn');
    expect(staleEpoch.commitOutcomeWord).toBe(SOURCE_MAINTENANCE_COPY.staleEpoch);
    expect(staleEpoch.hint).toContain('结果未记入');

    // `null` = 从未跑过 ⇒ 未知（**不是** ok，也不得折叠成 Committed）
    const absent = marks({ lastSourceMaintenanceCommitOutcome: null });
    expect(absent.commitOutcomeLevel).toBe('unknown');
    expect(absent.level).toBe('unknown');
    expect(absent.level).not.toBe('ok');
    expect(absent.commitOutcomeWord).toContain('无记录');

    // 未登记的数字化取值 ⇒ 未知 + 原样显示数字（不得当成 Committed / Legacy）
    const unknownValue = marks({ lastSourceMaintenanceCommitOutcome: 9 });
    expect(unknownValue.commitOutcomeLevel).toBe('unknown');
    expect(unknownValue.level).toBe('unknown');
    expect(unknownValue.commitOutcomeWord).toBe(
      `${SOURCE_MAINTENANCE_COPY.outcomeUnknownPrefix}9`,
    );
    expect(unknownValue.hint).toContain('原始取值 9');
  });

  it('I6 · 未定论路径：3 ⇒ warn 且文案含「3」；0 ⇒ 不告警（level 不为 warn）', () => {
    const dirty = marks({ sourceMaintenanceUnresolvedPathCount: 3 });
    expect(dirty.unresolvedPaths).toBe(true);
    expect(dirty.level).toBe('warn');
    expect(dirty.hint).toContain('3');
    expect(dirty.unresolvedText).toContain('3');
    expect(dirty.unresolvedText).toContain('索引可能不全');

    const clean = marks({ sourceMaintenanceUnresolvedPathCount: 0 });
    expect(clean.unresolvedPaths).toBe(false);
    expect(clean.level).not.toBe('warn');
    expect(clean.level).toBe('ok');
    expect(clean.unresolvedText).toBeNull();
  });

  it('I6b · 未定论路径数读不出来（契约漂移）⇒ 未知（不当作 0，也不告警）', () => {
    const m = deriveSourceMaintenanceMarks(
      maintenanceFixture({
        sourceMaintenanceUnresolvedPathCount: undefined as unknown as number,
      }),
    );
    expect(m.unresolvedPaths).toBeNull();
    expect(m.level).toBe('unknown');
    expect(m.level).not.toBe('warn');
    expect(m.hint).toContain('未知');
  });

  it('I7 · runCount>0 且 sessionKey=null ⇒ tooltip 含「逐文件提取」，但 level 不变（不告警）', () => {
    const m = marks({ sourceMaintenanceRunCount: 5, lastSourceMaintenanceSessionKey: null });
    expect(m.degenerateExtraction).toBe(true);
    expect(m.degenerateText).toBe(SOURCE_MAINTENANCE_COPY.degenerate);
    expect(m.hint).toContain('逐文件提取');
    expect(m.level).toBe('ok');
    expect(m.level).not.toBe('warn');

    const reused = marks({ sourceMaintenanceRunCount: 5, lastSourceMaintenanceSessionKey: 'sess-1' });
    expect(reused.degenerateExtraction).toBe(false);
    expect(reused.degenerateText).toBeNull();
    expect(reused.hint).not.toContain('逐文件提取');

    // runCount = 0 ⇒ 还没跑过，不算「退化」
    const neverRan = marks({ sourceMaintenanceRunCount: 0, lastSourceMaintenanceSessionKey: null });
    expect(neverRan.degenerateExtraction).toBe(false);
  });

  it('I7b · 行级 level：有未定论路径 ⇒ 行升格 warn；仅「不可知」不污染行级 level', () => {
    const warnRow = deriveCodeIndexProjectMarks(
      projectFixture({
        maintenance: maintenanceFixture({ sourceMaintenanceUnresolvedPathCount: 2 }),
      }),
    );
    expect(warnRow.level).toBe('warn');

    const unknownRow = deriveCodeIndexProjectMarks(
      projectFixture({
        maintenance: maintenanceFixture({ lastSourceMaintenanceCommitOutcome: null }),
      }),
    );
    // 「不可知」≠「坏」：不把一票普通项目集体染灰（只在 deriveSourceMaintenanceMarks().level 体现）
    expect(unknownRow.level).toBe('ok');
  });

  it('I8 · index.tsx 不再出现字面量 23，且维护态列数由契约常量推导', () => {
    const source = readSource(PAGE_PATH);
    expect(source).not.toMatch(/23\s*字段/);
    expect(source).not.toMatch(/23字段/);
    expect(source).not.toMatch(/，\s*23\s/);
    // 表头列数由常量推导（回退成写死 ⇒ 红）
    expect(source).toContain('${CODE_INDEX_MAINTENANCE_FIELDS.length} 列');
    // 维护态表格的列同样由常量 `.map` 生成 ⇒ 渲染出的列数 === 常量长度（23 冻结 + 9 D4）
    expect(source).toContain('CODE_INDEX_MAINTENANCE_FIELDS.map');
    expect(CODE_INDEX_MAINTENANCE_FIELDS.length).toBeGreaterThan(
      AUTHORITATIVE_MAINTENANCE_FIELDS.length,
    );
  });
});

// ── P5 新增：失败态诚实化（源码文本级断言；本页不作 jsdom 页面级渲染断言）──────
// 命题：页面在「读不到」时不得说「正在读取」；404 这一可行动事实必须与端点路径一起出现；
// 无快照时不得凭空提及「保留上一次成功快照」。M1/M2 变异各自能让 I4/I7 转红。

describe('P5 失败态诚实化：文案与撒谎路径（源码文本断言）', () => {
  const source = readSource(PAGE_PATH);

  it('I4 · 「正在读取」只出现一次，且唯一受 loading 守卫（error 分支不得含该字样）', () => {
    // (a) 唯一性：若复制到 error 分支就会出现第二次 ⇒ 红
    expect(source.split('正在读取').length - 1).toBe(1);
    // (b) 守卫必须是 `loading`：`loading ? ( <Alert ... message="正在读取"`
    expect(source).toMatch(/loading\s*\?\s*\(\s*<Alert[\s\S]{0,300}?message="正在读取"/);
    // (c) 紧邻「正在读取」之前不得出现 error（`loading || error !== null ?` 式改动 ⇒ 红）
    const idx = source.indexOf('正在读取');
    const guardWindow = source.slice(Math.max(0, idx - 120), idx);
    expect(guardWindow).toContain('loading ? (');
    expect(guardWindow).not.toMatch(/error/);
  });

  it('I5 · 404 文案与端点常量齐备，旧的裸路径「正在读取 GET …」已清除', () => {
    expect(source).toContain('后端未接入该端点（HTTP 404）');
    expect(source).toContain('INDEX_STATUS_ENDPOINT');
    // 旧文案（字面量裸路径）必须已被模板常量替换 ⇒ 回退即红
    expect(source).not.toContain('正在读取 GET /api/admin/index/status');
  });

  it('I7 · not-deployed 文案同时含 404 与端点路径（可行动），且分类器确实把 404 判为未部署', () => {
    expect(source).toContain('后端未接入该端点（HTTP 404）');
    expect(source).toContain('GET ${INDEX_STATUS_ENDPOINT} 在运行中的宿主里不存在');
    // M1（把 404 并入 unknown）⇒ 本条红
    expect(classifyIndexStatusFailure({ response: { status: 404 } }).kind).toBe('not-deployed');
  });

  it('§3.3 · 无快照时不得凭空提及「保留上一次成功快照」', () => {
    expect(source).not.toContain('页面保留上一次成功快照仅作参考');
  });

  it('§3.2 · 失败且无快照时给出明确说法（不是空白、也不是「正在读取」）', () => {
    expect(source).toContain('读取失败，尚未取到任何快照（详见上方提示）。');
    expect(source).toContain('端点返回了空快照（既不是错误，也不是零值）。');
  });
});
