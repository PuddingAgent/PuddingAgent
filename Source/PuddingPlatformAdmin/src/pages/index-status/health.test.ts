import type {
  FullTextIndexJobStatus,
  FullTextIndexMaintenanceStatus,
  FullTextIndexScopeStatus,
  FullTextIndexStatusDetail,
  FullTextIndexStatusSnapshot,
} from './types';
import {
  HEALTH_COPY,
  JOBS_REASON_TEXT,
  JOB_FIELDS,
  LEVEL_TEXT,
  LEVEL_TONE,
  RAW_FIELDS_DEFAULT_EXPANDED,
  SCOPE_FIELDS,
  SIZE_WARN_RATIO,
  STATUS_TONES,
  SYMBOL_CARD_STATUS,
  deriveConfigCardStatus,
  deriveIndexHealth,
  deriveL0Chips,
  deriveLedgerCardStatus,
  deriveScopeCardStatus,
  describeJobsLedger,
  hasUnknownKeyField,
  hasUnknownScopeField,
  healthToneStyle,
  isIndexEmpty,
  jobHasFailureSemantics,
  pickActiveJob,
  pickLatestTerminalJob,
  summarizeIndexVolume,
  shouldRenderJobsTable,
} from './health';

// ── Slice P1 定向测试：L0 聚合健康态（规格 §2 状态矩阵 / §3 文案 / §4 渲染纪律）──
// 断言对象 = `health.ts` 的**纯函数**（不碰 DOM、不读真实时钟：`nowMs` 一律注入）。
//
// 规格真源：`Docs/Features/Index-Status-Panel-UI-Prototype-2026-10-01.md`
//   §2 状态矩阵（首条命中即生效）· §3 文案表（逐字）· §4 三态纪律 / unknown 字段清单
//
// 本文件的用例**全部可失败**：凡把状态矩阵顺序打乱、或把三态折叠成 `?? 0` / `?? false`
// 的改动，都会有具名用例转红（变异取红见切片报告 §M1 / §M2）。

/** 固定「现在」：2026-10-01T05:00:00Z ⇒ 断言不随时间漂移。 */
const NOW = Date.UTC(2026, 9, 1, 5, 0, 0);

function scope(overrides: Partial<FullTextIndexScopeStatus> = {}): FullTextIndexScopeStatus {
  return {
    scopePath: 'D:\\CodeProject\\PuddingAgent\\PuddingAgent',
    scopeExists: true,
    hasIndex: true,
    indexDirectory: 'D:\\Data\\fulltext-index\\scopes\\acme',
    indexDirectoryExists: true,
    indexEntryCount: 12,
    indexBytes: 111_149_056, // 106 MB
    indexDirectoryLastWriteUtc: '2026-10-01T04:48:00Z', // NOW - 12 分钟
    ...overrides,
  };
}

function job(overrides: Partial<FullTextIndexJobStatus> = {}): FullTextIndexJobStatus {
  return {
    jobId: 'supply-ca4328974615',
    state: 'Succeeded',
    phase: 'Completed',
    startedAt: '2026-10-01T04:01:14Z',
    finishedAt: '2026-10-01T04:02:00Z',
    message: '完成：4,560 文件 / 82.8 MB',
    indexedFileCount: 4560,
    totalBytes: 86_822_093, // 82.8 MB
    elapsedMs: 46_000,
    ...overrides,
  };
}

function maintenance(
  overrides: Partial<FullTextIndexMaintenanceStatus> = {},
): FullTextIndexMaintenanceStatus {
  return { configured: false, enabled: false, note: '配置节未提供，走默认。', ...overrides };
}

type SnapshotOverrides = Partial<Omit<FullTextIndexStatusDetail, 'maintenance'>> & {
  maintenance?: Partial<FullTextIndexMaintenanceStatus>;
};

function detail(overrides: SnapshotOverrides = {}): FullTextIndexStatusDetail {
  const { maintenance: maintenanceOverrides, ...rest } = overrides;
  return {
    configured: true,
    enabled: true,
    indexRoot: 'D:\\Data\\fulltext-index',
    indexRootExists: true,
    workspaceRoot: 'D:\\CodeProject\\PuddingAgent',
    maxIndexBytes: 1_073_741_824, // 1.0 GB
    minRebuildInterval: '12:00:00',
    acceptedScopes: ['D:\\CodeProject\\PuddingAgent\\PuddingAgent'],
    rejectedReasons: [],
    compositionCreated: true,
    scopes: [scope()],
    jobs: [job()],
    jobsReason: null,
    ...rest,
    // 放在 spread 之后并一次性合并 ⇒ 避免「同一属性写两遍」（ts2783）。
    maintenance: maintenance(maintenanceOverrides ?? {}),
  };
}

function snapshot(overrides: SnapshotOverrides = {}): FullTextIndexStatusSnapshot {
  return { generatedAtUtc: '2026-10-01T05:00:00Z', fullText: detail(overrides) };
}

const RUNNING_JOB = job({
  jobId: 'supply-running',
  state: 'Running',
  phase: 'Building',
  startedAt: '2026-10-01T04:59:14Z', // NOW - 46 秒
  finishedAt: null,
  message: null,
  elapsedMs: null,
});

// ══ §2 状态矩阵：9 行逐行单测（首条命中即生效）══════════════════════════

describe('L0 状态矩阵 · 规格 §2（首条命中即生效）', () => {
  it('第 1 行 off：enabled=false ⇒ off，**即使同时存在 null 关键字段也不得判成 unknown**（M1 变异点）', () => {
    const verdict = deriveIndexHealth(
      snapshot({ enabled: false, scopes: [scope({ hasIndex: null })] }),
      NOW,
    );
    expect(verdict.level).toBe('off');
    expect(verdict.level).not.toBe('unknown');
    expect(verdict.rule).toBe(1);
    expect(verdict.title).toBe('全文索引已关闭（enabled=false）');
    expect(verdict.title).toBe(HEALTH_COPY.offTitle);
    expect(verdict.detail).toBe('配置未开启，不是故障');
  });

  it('第 2 行 error：rejectedReasons 非空 ⇒ error，标题给条数、副行给逐条原文', () => {
    const verdict = deriveIndexHealth(
      snapshot({ rejectedReasons: ["Scopes['E:\\gone']: 目录不存在"] }),
      NOW,
    );
    expect(verdict.level).toBe('error');
    expect(verdict.rule).toBe(2);
    expect(verdict.title).toBe('配置被拒：1 条 scope 未受理');
    expect(verdict.detail).toContain('E:\\gone');
  });

  it('第 3 行 error：任一 scope scopeExists=false ⇒ error，且标题含该 scope 路径', () => {
    const verdict = deriveIndexHealth(
      snapshot({
        scopes: [scope({ scopeExists: false, scopePath: 'E:\\github\\AgentNetworkPlan\\PuddingAgent' })],
      }),
      NOW,
    );
    expect(verdict.level).toBe('error');
    expect(verdict.rule).toBe(3);
    expect(verdict.title).toBe('scope 路径不存在：E:\\github\\AgentNetworkPlan\\PuddingAgent');
    expect(verdict.detail).toContain('FullTextIndex.Scopes');
  });

  it('第 4 行 unknown：关键字段为 null ⇒ unknown（读不到 ≠ 不存在）', () => {
    const verdict = deriveIndexHealth(snapshot({ scopes: [scope({ indexBytes: null })] }), NOW);
    expect(verdict.level).toBe('unknown');
    expect(verdict.rule).toBe(4);
    expect(verdict.title).toBe('状态未知 · 探测失败');
    expect(verdict.detail).toBe('不代表「未启用」或「不存在」');
  });

  it('第 4 行 unknown：maintenance.enabled=null 同样进 unknown（§4 清单）', () => {
    const verdict = deriveIndexHealth(
      snapshot({ maintenance: { configured: true, enabled: null } }),
      NOW,
    );
    expect(verdict.level).toBe('unknown');
    expect(verdict.rule).toBe(4);
  });

  it('第 5 行 busy：存在非终态 job ⇒ busy，文案只含「已清点 n 文件 · bytes · 已用 s 秒」', () => {
    const verdict = deriveIndexHealth(snapshot({ jobs: [RUNNING_JOB] }), NOW);
    expect(verdict.level).toBe('busy');
    expect(verdict.rule).toBe(5);
    expect(verdict.title).toBe('正在重建 · 已清点 4,560 文件 · 82.8 MB · 已用 46 秒');
  });

  it('第 6 行 warn：全部 scope hasIndex!==true 或条目为 0 ⇒ 索引为空（0 条目）· 尚未建立', () => {
    const verdict = deriveIndexHealth(
      snapshot({ scopes: [scope({ hasIndex: false, indexEntryCount: 0, indexBytes: 0 })] }),
      NOW,
    );
    expect(verdict.level).toBe('warn');
    expect(verdict.rule).toBe(6);
    expect(verdict.title).toBe('索引为空（0 条目）· 尚未建立');
    expect(verdict.detail).toBe('scope 存在、索引目录存在，但无条目');
  });

  it('第 7 行 warn：体积占用 ≥ 80% ⇒ 索引体积接近上限（N%）', () => {
    // 0.9 × 1 GiB ⇒ 90%
    const verdict = deriveIndexHealth(snapshot({ scopes: [scope({ indexBytes: 966_367_642 })] }), NOW);
    expect(verdict.level).toBe('warn');
    expect(verdict.rule).toBe(7);
    expect(verdict.title).toBe('索引体积接近上限（90%）');
    expect(verdict.detail).toBe('922 MB / 1.0 GB'); // 966,367,642 B：既有分档 ≥100 取整 ⇒ 922 MB
  });

  it('第 8 行 warn：最近终态 job 含失败语义 ⇒ 最近一次供给失败：<state>（原文进副行）', () => {
    const verdict = deriveIndexHealth(
      snapshot({ jobs: [job({ state: 'Failed', message: '磁盘写满' })] }),
      NOW,
    );
    expect(verdict.level).toBe('warn');
    expect(verdict.rule).toBe(8);
    expect(verdict.title).toBe('最近一次供给失败：Failed');
    expect(verdict.detail).toBe('磁盘写满');
  });

  it('第 8 行取「最近」终态：按 finishedAt 取最新，历史失败不被新成功掩盖', () => {
    const olderFailed = job({
      jobId: 'old-failed',
      state: 'Failed',
      finishedAt: '2026-10-01T02:00:00Z',
    });
    const newerOk = job({ jobId: 'new-ok', state: 'Succeeded', finishedAt: '2026-10-01T04:30:00Z' });
    expect(pickLatestTerminalJob([olderFailed, newerOk])?.jobId).toBe('new-ok');
    expect(deriveIndexHealth(snapshot({ jobs: [olderFailed, newerOk] }), NOW).level).toBe('ok');

    const newerFailed = job({
      jobId: 'new-failed',
      state: 'Failed',
      finishedAt: '2026-10-01T04:45:00Z',
    });
    // 放在数组靠前：仍须按时间取它 ⇒ 证明取的是 finishedAt 最新者，而非数组末位。
    const verdict = deriveIndexHealth(snapshot({ jobs: [newerFailed, olderFailed] }), NOW);
    expect(verdict.level).toBe('warn');
    expect(verdict.rule).toBe(8);
  });

  it('第 9 行 ok：以上皆否 ⇒ 索引就绪 · <相对时间>更新（绝对时间进副行）', () => {
    const verdict = deriveIndexHealth(snapshot(), NOW);
    expect(verdict.level).toBe('ok');
    expect(verdict.rule).toBe(9);
    expect(verdict.title).toBe('索引就绪 · 12 分钟前更新');
    expect(verdict.detail).toBe('最后写入 2026-10-01 04:48:00 UTC');
  });

  it('第 9 行 ok 的先决条件：这条基线的确落在第 9 行（防基线本身失真）', () => {
    const base = detail();
    expect(base.rejectedReasons).toHaveLength(0);
    expect(hasUnknownKeyField(base)).toBe(false);
    expect(pickActiveJob(base.jobs)).toBeNull();
    expect(isIndexEmpty(base)).toBe(false);
    expect(summarizeIndexVolume(base).ratio).toBeLessThan(SIZE_WARN_RATIO);
    expect(jobHasFailureSemantics(base.jobs[0].state)).toBe(false);
  });
});

// ══ §2「顺序理由」的三条不变量（I1 / I3 / I4）══════════════════════════

describe('状态矩阵顺序不变量（规格 §2「顺序理由」）', () => {
  it('I1：off 优先于「空」判定 —— 关闭是明确状态，不得被判成「索引为空」', () => {
    const verdict = deriveIndexHealth(
      snapshot({ enabled: false, scopes: [scope({ hasIndex: false, indexEntryCount: 0 })] }),
      NOW,
    );
    expect(verdict.level).toBe('off');
    expect(verdict.rule).toBe(1);
  });

  it('I3：error 优先于 busy —— 正在重建不得掩盖「scope 不存在」这类硬错误', () => {
    const verdict = deriveIndexHealth(
      snapshot({ scopes: [scope({ scopeExists: false })], jobs: [RUNNING_JOB] }),
      NOW,
    );
    expect(verdict.level).toBe('error');
    expect(verdict.rule).toBe(3);
  });

  it('I4：unknown 优先于 warn —— 未知不得被渲染成「索引为空」这种确定的坏消息', () => {
    const verdict = deriveIndexHealth(
      snapshot({ scopes: [scope({ hasIndex: null, indexEntryCount: 0 })] }),
      NOW,
    );
    expect(verdict.level).toBe('unknown');
    expect(verdict.rule).toBe(4);
  });

  it('I4：unknown 劣后于 error —— 已知坏 > 不知道', () => {
    const verdict = deriveIndexHealth(
      snapshot({ scopes: [scope({ scopeExists: false, hasIndex: null })] }),
      NOW,
    );
    expect(verdict.level).toBe('error');
    expect(verdict.rule).toBe(3);
  });
});

// ══ I2：关闭态用中性灰，不染红不染黄 ═══════════════════════════════════

describe('I2 关闭态视觉（规格 §2 / §4：off 用中性灰，不染红）', () => {
  it('I2：off 的视觉族是中性灰（tagColor=default），既不是 error 也不是 warning', () => {
    const style = healthToneStyle('off');
    expect(LEVEL_TONE.off).toBe('neutral');
    expect(style.tagColor).toBe('default');
    expect(style.tagColor).not.toBe('error');
    expect(style.tagColor).not.toBe('warning');
    expect(style).toBe(STATUS_TONES.neutral);
  });

  it('I2：off 与「明确故障」（error）的配色不同族', () => {
    expect(healthToneStyle('off').tagColor).not.toBe(healthToneStyle('error').tagColor);
    expect(healthToneStyle('off').tagColor).not.toBe(healthToneStyle('warn').tagColor);
  });
});

// ══ I5：busy 不许造假进度 ═════════════════════════════════════════════

describe('I5 busy 文案（规格 §2 / §3 / §6 缺口 ①：没有 processed/total）', () => {
  it('I5：busy 文案不含百分比、不含进度条/进度字样', () => {
    const title = deriveIndexHealth(snapshot({ jobs: [RUNNING_JOB] }), NOW).title;
    expect(title).not.toContain('%');
    expect(title).not.toContain('％');
    expect(title).not.toContain('进度');
    expect(title).not.toContain('progress');
    expect(title).toMatch(/^正在重建 · 已清点 [\d,]+ 文件 · [\d.]+ [KMG]?B · 已用 \d+ 秒$/);
  });

  it('I5：秒数由 startedAt + 注入的 nowMs 推算（非终态 elapsedMs 恒 null，不拿它冒充）', () => {
    const verdict = deriveIndexHealth(snapshot({ jobs: [RUNNING_JOB] }), NOW);
    expect(verdict.title).toContain('已用 46 秒');
    expect(RUNNING_JOB.elapsedMs).toBeNull();
  });

  it('I5：startedAt 不可解析 ⇒ 秒数渲染「未知」而不是编造 0 秒', () => {
    const verdict = deriveIndexHealth(
      snapshot({ jobs: [job({ finishedAt: null, elapsedMs: null, startedAt: 'not-a-date' })] }),
      NOW,
    );
    expect(verdict.title).toContain('已用 未知 秒');
    expect(verdict.title).not.toContain('已用 0 秒');
  });
});

// ══ I6：三态纪律贯穿到人话层 ══════════════════════════════════════════

describe('I6 三态纪律（null=未知 / false=否 / 0=0，三者渲染互不相同）', () => {
  it('I6：off 与 unknown 同色族但**文案必须不同**（不得同文案）', () => {
    expect(LEVEL_TEXT.off).not.toBe(LEVEL_TEXT.unknown);
    const off = deriveIndexHealth(snapshot({ enabled: false }), NOW);
    const unknown = deriveIndexHealth(snapshot({ scopes: [scope({ hasIndex: null })] }), NOW);
    expect(off.title).not.toBe(unknown.title);
  });

  it('I6：体积汇总里 null ≠ 0（任一 scope 未知 ⇒ 汇总未知，不得折叠成 0）（M2 变异点）', () => {
    const unknownVolume = summarizeIndexVolume(detail({ scopes: [scope({ indexBytes: null })] }));
    const zeroVolume = summarizeIndexVolume(detail({ scopes: [scope({ indexBytes: 0 })] }));
    expect(unknownVolume.bytes).toBeNull();
    expect(zeroVolume.bytes).toBe(0);
    expect(unknownVolume.bytes).not.toBe(zeroVolume.bytes);
    expect(unknownVolume.ratio).toBeNull();
    expect(zeroVolume.ratio).toBe(0);
  });

  it('I6：hasIndex 的 null 与 false 在人话层是两种结论（未知 vs 索引为空）', () => {
    const nullDetail = detail({ scopes: [scope({ hasIndex: null })] });
    const falseDetail = detail({ scopes: [scope({ hasIndex: false, indexEntryCount: 0 })] });
    expect(hasUnknownScopeField(nullDetail)).toBe(true);
    expect(hasUnknownScopeField(falseDetail)).toBe(false);
    expect(deriveIndexHealth({ generatedAtUtc: '', fullText: nullDetail }, NOW).level).toBe('unknown');
    expect(deriveIndexHealth({ generatedAtUtc: '', fullText: falseDetail }, NOW).level).toBe('warn');
  });

  it('I6：indexBytes 的 null 与 0 在 L0 chip 上渲染不同', () => {
    const nullChip = deriveL0Chips(detail({ scopes: [scope({ indexBytes: null })] }), NOW)[2];
    const zeroChip = deriveL0Chips(detail({ scopes: [scope({ indexBytes: 0 })] }), NOW)[2];
    expect(nullChip.text).toContain('未知');
    expect(zeroChip.text).toContain('0 B');
    expect(nullChip.text).not.toBe(zeroChip.text);
  });
});

// ══ I7：L2 原始字段一个不丢 + 默认折叠 ════════════════════════════════

describe('I7 L2 原始字段（字段名与类型逐字对齐 types.ts）', () => {
  it('I7：逐 scope 正好 8 列，字段名与 types.ts 逐字一致（顺序亦一致）', () => {
    expect(SCOPE_FIELDS.map((field) => field.key)).toEqual([
      'scopePath',
      'scopeExists',
      'hasIndex',
      'indexDirectory',
      'indexDirectoryExists',
      'indexEntryCount',
      'indexBytes',
      'indexDirectoryLastWriteUtc',
    ]);
  });

  it('I7：逐 job 正好 9 列，字段名与 types.ts 逐字一致（顺序亦一致）', () => {
    expect(JOB_FIELDS.map((field) => field.key)).toEqual([
      'jobId',
      'state',
      'phase',
      'startedAt',
      'finishedAt',
      'message',
      'indexedFileCount',
      'totalBytes',
      'elapsedMs',
    ]);
  });

  it('I7：每个字段都有非空中文标签（列标题 = 标签（字段名），不许留空列）', () => {
    for (const field of [...SCOPE_FIELDS, ...JOB_FIELDS]) {
      expect(field.label.length).toBeGreaterThan(0);
      expect(field.kind.length).toBeGreaterThan(0);
    }
  });

  it('I7：L2 默认折叠（默认视图是结论，字段不抢首屏）', () => {
    expect(RAW_FIELDS_DEFAULT_EXPANDED).toBe(false);
  });
});

// ══ I8：台账为空必须给中文如实说法（禁止渲染空表格）════════════════════

describe('I8 台账为空的如实说法（规格 §3 jobsReason）', () => {
  it('I8：三个已登记 jobsReason 各给规格 §3 的中文说法，且识别为「空台账」', () => {
    const cases: [string, string][] = [
      ['composition-not-created', '供给组件尚未启动（无人触发过预建）'],
      ['no-jobs-recorded', '供给组件已启动 · 暂无 job'],
      ['ledger-read-failed', '台账读取失败 ⇒ 内容不可知（≠ 没有 job）'],
    ];
    for (const [reason, expected] of cases) {
      const notice = describeJobsLedger(detail({ jobs: [], jobsReason: reason }));
      expect(notice.isEmpty).toBe(true);
      expect(notice.text).toBe(expected);
      expect(notice.text).toBe(JOBS_REASON_TEXT[reason]);
    }
  });

  it('I8：未提供原因码（契约异常）也必须给非空说法，绝不静默成空表格', () => {
    const notice = describeJobsLedger(detail({ jobs: [], jobsReason: null }));
    expect(notice.isEmpty).toBe(true);
    expect(notice.text.length).toBeGreaterThan(0);
    expect(notice.text).toContain('未提供台账原因码');
  });

  it('I8：未登记的原因码原样回显并标注未登记（不吞不猜）', () => {
    const notice = describeJobsLedger(detail({ jobs: [], jobsReason: 'brand-new-reason' }));
    expect(notice.text).toContain('brand-new-reason');
    expect(notice.text).toContain('未登记');
  });

  it('I8：有 job 时不给「空台账说法」', () => {
    expect(describeJobsLedger(detail()).isEmpty).toBe(false);
    expect(describeJobsLedger(null).isEmpty).toBe(true);
  });
});

// ══ L0 结论条的三个 chip ═══════════════════════════════════════════════

describe('L0 结论条 chips（全部由既有 wire 字段推导）', () => {
  it('chip：新鲜度 / scope 受理 / 体积占用三个，口径与规格 §1 一致', () => {
    const chips = deriveL0Chips(detail(), NOW);
    expect(chips.map((chip) => chip.key)).toEqual(['freshness', 'scope', 'volume']);
    expect(chips[0].text).toBe('最后更新 12 分钟前');
    expect(chips[0].hint).toBe('2026-10-01 04:48:00 UTC');
    expect(chips[1].text).toBe('scope 1 受理 · 0 拒绝');
    expect(chips[2].text).toBe('106 MB / 1.0 GB · 10%');
  });

  it('chip：无快照时不给 chip（不伪造空结论）', () => {
    expect(deriveL0Chips(null, NOW)).toEqual([]);
  });
});

// ══ L1 四张卡片的角标 ═════════════════════════════════════════════════

describe('L1 卡片角标（A 全文索引 / B 符号索引 / C 供给台账 / D 配置与受理）', () => {
  it('A 卡：基线就绪；无快照未知；scope 缺失故障（不因 null 而掩盖 error）', () => {
    expect(deriveScopeCardStatus(detail()).tone).toBe('ok');
    expect(deriveScopeCardStatus(null).tone).toBe('neutral');
    expect(deriveScopeCardStatus(detail({ scopes: [scope({ scopeExists: false })] })).tone).toBe('error');
    expect(deriveScopeCardStatus(detail({ scopes: [scope({ hasIndex: null })] })).tone).toBe('neutral');
  });

  it('B 卡：未接入 ≠ 故障（中性灰占位，不留空白、不染红）', () => {
    expect(SYMBOL_CARD_STATUS.tone).toBe('neutral');
    expect(SYMBOL_CARD_STATUS.text).toBe('未接入');
    expect(SYMBOL_CARD_STATUS.hint).toContain('S-A2');
    expect(SYMBOL_CARD_STATUS.tone).not.toBe('error');
    expect(SYMBOL_CARD_STATUS.tone).not.toBe('warn');
  });

  it('C 卡：有台账就绪 / 非终态进行中 / 台账读取失败故障 / 空台账中性', () => {
    expect(deriveLedgerCardStatus(detail()).tone).toBe('ok');
    expect(deriveLedgerCardStatus(detail({ jobs: [RUNNING_JOB] })).tone).toBe('busy');
    expect(deriveLedgerCardStatus(detail({ jobs: [], jobsReason: 'ledger-read-failed' })).tone).toBe('error');
    expect(deriveLedgerCardStatus(detail({ jobs: [], jobsReason: 'no-jobs-recorded' })).tone).toBe('neutral');
  });

  it('D 卡：拒收故障 / enabled=false 中性灰（非故障）/ 维护开关 null 未知', () => {
    expect(deriveConfigCardStatus(detail()).tone).toBe('ok');
    expect(deriveConfigCardStatus(detail({ rejectedReasons: ['x'] })).tone).toBe('error');
    expect(deriveConfigCardStatus(detail({ enabled: false })).tone).toBe('neutral');
    expect(deriveConfigCardStatus(detail({ maintenance: { enabled: null } })).tone).toBe('neutral');
  });
});

describe('I8 渲染决策（台账为空 ⇒ 绝不渲染表格）', () => {
  it('I8：三种 jobsReason 与「未提供原因码」一律判为「不渲染表格」', () => {
    const reasons = ['composition-not-created', 'no-jobs-recorded', 'ledger-read-failed', null];
    for (const reason of reasons) {
      expect(shouldRenderJobsTable(detail({ jobs: [], jobsReason: reason }))).toBe(false);
    }
    expect(shouldRenderJobsTable(null)).toBe(false);
  });

  it('I8：有 job 才渲染表格（空数组不是「有 job」）', () => {
    expect(shouldRenderJobsTable(detail())).toBe(true);
    expect(shouldRenderJobsTable(detail({ jobs: [] }))).toBe(false);
  });
});
