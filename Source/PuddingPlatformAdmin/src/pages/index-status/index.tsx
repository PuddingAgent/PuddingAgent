import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Row,
  Space,
  Spin,
  Table,
  Tag,
  Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import {
  EMPTY_TEXT,
  INDEX_STATUS_ENDPOINT,
  UNKNOWN_TEXT,
  classifyIndexStatusFailure,
  formatRelativeTime,
  formatTriStateBoolean,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateDurationMs,
  formatTriStateText,
  formatTriStateTimestamp,
  formatUtcTime,
  getIndexStatus,
} from './api';
import type { IndexStatusFailure } from './api';
import {
  CODE_INDEX_BLOCK_FIELDS,
  CODE_INDEX_MAINTENANCE_FIELDS,
  CODE_INDEX_PROJECT_FIELDS,
  CODE_INDEX_VISIBLE_PROJECT_LIMIT,
  JOB_FIELDS,
  LEVEL_TONE,
  RAW_FIELDS_DEFAULT_EXPANDED,
  SCOPE_FIELDS,
  STATUS_TONES,
  buildBusyTitle,
  deriveCalibrationFreshnessRatio,
  deriveCodeIndexBlock,
  deriveConfigCardStatus,
  deriveIndexHealth,
  deriveL0Chips,
  deriveLedgerCardStatus,
  deriveScopeCardStatus,
  describeJobsLedger,
  describeMaintenanceReason,
  hasUnknownScopeField,
  jobHasFailureSemantics,
  latestIndexWriteUtc,
  pickActiveJob,
  pickLatestTerminalJob,
  shouldRenderJobsTable,
  summarizeCodeIndexProjects,
  summarizeIndexVolume,
  totalEntryCount,
} from './health';
import type { CardStatus, FieldKind, StatusTone } from './health';
import { CodeIndexProjectRow } from './codeIndexRow';
import {
  L0Strip,
  LEVEL_VISUALS,
  RingGauge,
  ScopeDots,
  SparkBars,
  ToggleGlyph,
  UnknownGlyph,
  cx,
  frameClass,
} from './visuals';
import type { SparkBar } from './visuals';
import type {
  CodeIndexMaintenanceStatus,
  CodeIndexProjectStatus,
  FullTextIndexJobStatus,
  FullTextIndexScopeStatus,
  FullTextIndexStatusDetail,
  FullTextIndexStatusSnapshot,
} from './types';
import './index.css';

// ── Slice P2：Admin「索引与检索」页（只读 · **视觉优先** v2）──────────────
// 信息架构（规格 §1，v2 不变）：**L0 结论条**（常驻 1 行）→ **L1 四张诊断卡** → **L2 原始字段**（默认折叠）。
// v2 改的是**表达层**（规格 §9）：状态由「图形 + 动效 + 颜色」三重编码承载，文字退为短词与
// tooltip；异常时才允许升格为句子（§9.6）。事实层（health.ts 的 9 行状态矩阵）**一字未改**。
// 数据源：GET /api/admin/index/status（IndexAdminController，Admin JWT）。
//
// 四条纪律：
// 1) 三态贯穿（规格 §4）：`null` = 未知 / `false` = 否 / `0` = 0，三者渲染必须不同；
//    一律经 ./api 与 ./health 的纯函数，**禁止** `?? 0` / `|| '否'` 式折叠。
// 2) 纯只读：本页没有任何写操作能力（不触发供给、不重建、不删除；「重建索引」按钮刻意不做）。
// 3) 视觉层只吃 props（./visuals 不推导业务真值）：`level` / 值 / 标签都在此文件由 health.ts 的
//    纯函数结果喂给组件 —— 「事实」与「视觉」互不污染。
// 4) 轮询照抄 src/pages/storage/index.tsx 的既有用法（window.setInterval + 卸载清理），
//    刻意**不**引入 src/pages/chat/** 的任何模块，以保证该目录零接触。

/** 轮询间隔：与 storage 页同量级（30s），不自创激进间隔。 */
const POLL_INTERVAL_MS = 30_000;

/** 状态角标：图形 + 文字双编码（灰度打印 / 色盲可辨）—— **仅 L2 证据层使用**。 */
function toneTag(tone: StatusTone, text: string, hint?: string): React.ReactNode {
  const style = STATUS_TONES[tone];
  return (
    <Tag color={style.tagColor} title={hint}>
      {style.glyph} {text}
    </Tag>
  );
}

/** L1 卡片的角标（短词 + tooltip；§9.6 正常态只给短词，异常态才升格）。 */
function cardWord(status: CardStatus): React.ReactNode {
  return (
    <span className={cx('vs-shortword', `vs-tone-${status.tone}`)} title={status.hint}>
      {status.text}
    </span>
  );
}

/** 布尔三态标签：`null` ⇒ 灰「○ 未知」· `false` ⇒ 「否」· `true` ⇒ 绿「是」。 */
function triStateTag(value: boolean | null | undefined): React.ReactNode {
  const text = formatTriStateBoolean(value);
  if (value === null || value === undefined) return toneTag('neutral', text, '探测失败，不代表不存在');
  return <Tag color={value ? 'success' : 'default'}>{text}</Tag>;
}

/** 长路径（`path` 类字段）：有值给等宽文本，未知给中性灰标签（不留空白）。 */
function pathText(value: string | null | undefined): React.ReactNode {
  const text = formatTriStateText(value);
  if (value === null || value === undefined) return toneTag('neutral', text);
  return (
    <Typography.Text code style={{ fontSize: 12, wordBreak: 'break-all' }}>
      {text}
    </Typography.Text>
  );
}

/** 末行说明（数组中逐条列出；空数组给明确说法，不是空白）。 */
function stringList(values: readonly string[], emptyText: string): React.ReactNode {
  if (values.length === 0) {
    return <Typography.Text type="secondary">{emptyText}</Typography.Text>;
  }
  return (
    <Space direction="vertical" size={2} style={{ width: '100%' }}>
      {values.map((value) => (
        <Typography.Text key={value} code style={{ fontSize: 12, wordBreak: 'break-all' }}>
          {value}
        </Typography.Text>
      ))}
    </Space>
  );
}

/** L2 字段渲染口径：按 `FieldKind` 选三态纯函数（未知一律「未知」，绝不折叠成否/零）。 */
function renderFieldValue(kind: FieldKind, value: unknown): React.ReactNode {
  switch (kind) {
    case 'path':
      return pathText(typeof value === 'string' ? value : null);
    case 'bool':
      return triStateTag(typeof value === 'boolean' || value === null ? value : null);
    case 'count':
      return formatTriStateCount(typeof value === 'number' ? value : null);
    case 'bytes':
      return formatTriStateBytes(typeof value === 'number' ? value : null);
    case 'timestamp':
      return formatTriStateTimestamp(typeof value === 'string' ? value : null);
    case 'durationMs':
      return formatTriStateDurationMs(typeof value === 'number' ? value : null);
    case 'text':
      return formatTriStateText(typeof value === 'string' ? value : null);
    // P3 新增：字符串数组（`lastRemovalPaths`）—— 空数组是「（空）」（有值但为空），不是未知。
    case 'list': {
      if (!Array.isArray(value)) return toneTag('neutral', UNKNOWN_TEXT);
      const items = value.map((item) => String(item));
      if (items.length === 0) return <Typography.Text type="secondary">{EMPTY_TEXT}</Typography.Text>;
      return stringList(items, EMPTY_TEXT);
    }
    // P3 新增：复合结构（`projects` / `maintenance`）—— 逐字段在下方专用表里，不在此堆叠。
    case 'nested': {
      if (value === null || value === undefined) {
        return toneTag('neutral', '缺席（原因为 null/缺失，见下方缺席清单）');
      }
      if (Array.isArray(value)) return <Tag>{`共 ${value.length} 项（见下方表）`}</Tag>;
      return <Tag>有（见下方表）</Tag>;
    }
    default:
      return toneTag('neutral', UNKNOWN_TEXT);
  }
}

// ── L2 表格列：**由 health.ts 的字段清单生成** ⇒ 字段名/顺序与 types.ts 一致，一个不丢 ──
const scopeColumns: ColumnsType<FullTextIndexScopeStatus> = SCOPE_FIELDS.map((field) => ({
  title: `${field.label}（${field.key}）`,
  dataIndex: field.key,
  key: field.key,
  render: (value: unknown) => renderFieldValue(field.kind, value),
}));

const jobColumns: ColumnsType<FullTextIndexJobStatus> = JOB_FIELDS.map((field) => ({
  title: `${field.label}（${field.key}）`,
  dataIndex: field.key,
  key: field.key,
  render: (value: unknown) => renderFieldValue(field.kind, value),
}));

// ── L2 · codeIndex 两张表（**列由契约常量生成**，页面不手写任何字段名）──────
// 字段名与顺序全部来自 health.ts 的 `CODE_INDEX_*_FIELDS`（其声明序 = wire 序，
// 且受 `Record<keyof X, …>` 编译期穷尽性约束）—— 页面这里只负责“把常量变成列”。
const codeIndexProjectColumns: ColumnsType<CodeIndexProjectStatus> = CODE_INDEX_PROJECT_FIELDS.map(
  (field) => ({
    title: `${field.label}（${field.key}）`,
    dataIndex: field.key,
    key: field.key,
    render: (value: unknown) => renderFieldValue(field.kind, value),
  }),
);

const codeIndexMaintenanceColumns: ColumnsType<CodeIndexMaintenanceStatus> =
  CODE_INDEX_MAINTENANCE_FIELDS.map((field) => ({
    title: `${field.label}（${field.key}）`,
    dataIndex: field.key,
    key: field.key,
    render: (value: unknown) => renderFieldValue(field.kind, value),
  }));

// ── 表达层映射（**不是业务真值**：真值仍在 health.ts / wire 契约）──────────

/** `"12:00:00"`（TimeSpan 字符串）⇒ 毫秒；不可解析 ⇒ `null`（不假装成 0）。 */
function parseTimeSpanMs(value: string | null | undefined): number | null {
  if (typeof value !== 'string') return null;
  const match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})/.exec(value.trim());
  if (match === null) return null;
  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  const total = ((days * 24 + hours) * 60 + minutes) * 60 + seconds;
  return Number.isFinite(total) ? total * 1000 : null;
}

/**
 * 新鲜度环的填充率（§9.2：满 = 新鲜，随时间变空变灰）。
 * 映射到 `minRebuildInterval`：刚写完 = 满环，超过一个最小重建间隔 = 空环。
 * 任一输入不可得 ⇒ `null`（环画虚线，不假装成 0）。
 */
function deriveFreshnessRatio(
  lastWriteUtc: string | null,
  minRebuildInterval: string,
  nowMs: number,
): number | null {
  const spanMs = parseTimeSpanMs(minRebuildInterval);
  if (lastWriteUtc === null || spanMs === null || spanMs <= 0) return null;
  const wroteAt = new Date(lastWriteUtc).getTime();
  if (Number.isNaN(wroteAt)) return null;
  const ageMs = nowMs - wroteAt;
  if (!Number.isFinite(ageMs)) return null;
  return Math.max(0, Math.min(1, 1 - ageMs / spanMs));
}

/** 单条 job ⇒ 火花线条（长度 = 耗时占比 · 颜色 = 结果 · 非终态 = 不定长流光）。 */
function toSparkBar(job: FullTextIndexJobStatus, maxElapsedMs: number): SparkBar {
  const active = job.finishedAt === null || job.finishedAt === undefined;
  const tone: StatusTone = active
    ? 'busy'
    : jobHasFailureSemantics(job.state)
      ? 'warn'
      : 'ok';
  return {
    key: job.jobId,
    ratio: job.elapsedMs === null ? null : maxElapsedMs > 0 ? job.elapsedMs / maxElapsedMs : 1,
    tone,
    active,
    title: [
      job.jobId,
      `状态 ${formatTriStateText(job.state)} · 阶段 ${formatTriStateText(job.phase)}`,
      `清点 ${formatTriStateCount(job.indexedFileCount)} 文件 · ${formatTriStateBytes(job.totalBytes)}`,
      `耗时 ${formatTriStateDurationMs(job.elapsedMs)}（${formatUtcTime(job.startedAt)} 起）`,
    ].join('\n'),
  };
}


/**
 * P5：失败态诚实化 —— 把分类结果映射成失败 Alert 的三元组（文案逐字见任务书 §3.1）。
 * `hasSnapshot` 为真（确实存在上一次快照）时才补「快照仅作参考」的说明；
 * 无快照时不得凭空提及快照（§3.3：那句话只在 snapshot !== null 时出现）。
 */
function failureAlert(
  failure: IndexStatusFailure,
  hasSnapshot: boolean,
): { type: 'error' | 'warning'; message: string; description: string } {
  const snapshotNote = hasSnapshot ? '（页面不使用上一次快照冒充当前真值）' : '';
  switch (failure.kind) {
    case 'not-deployed':
      return {
        type: 'warning',
        message: '后端未接入该端点（HTTP 404）',
        description: `GET ${INDEX_STATUS_ENDPOINT} 在运行中的宿主里不存在 ⇒ 面板没有数据可显示。这通常意味着宿主尚未部署包含该端点的构建；部署 Core 后本页自动就绪。`,
      };
    case 'unauthorized':
      return {
        type: 'error',
        message: '未授权（HTTP 401/403）',
        description: '需要有效的 Admin 登录态：请重新登录后点「刷新」。',
      };
    case 'network':
      return {
        type: 'error',
        message: '无法连接后端',
        description: `${failure.rawMessage}${snapshotNote}`,
      };
    default:
      return {
        type: 'error',
        message: '索引状态读取失败',
        description: `${failure.rawMessage}${snapshotNote}`,
      };
  }
}

const IndexStatusPage: React.FC = () => {
  const [snapshot, setSnapshot] = useState<FullTextIndexStatusSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<IndexStatusFailure | null>(null);
  const [showRawFields, setShowRawFields] = useState<boolean>(RAW_FIELDS_DEFAULT_EXPANDED);

  const load = useCallback(async () => {
    try {
      setSnapshot(await getIndexStatus());
      setError(null);
    } catch (err) {
      // P5：读不到就**如实分类**报读不到（404 / 未授权 / 网络层 / 其它），
      // 取不到状态码时绝不猜成 404，也不用上一次的快照冒充当前真值。
      setError(classifyIndexStatusFailure(err));
    }
  }, []);

  useEffect(() => {
    (async () => {
      setLoading(true);
      try {
        await load();
      } finally {
        setLoading(false);
      }
    })();
  }, [load]);

  // 首屏渲染后 30s 轮询；组件卸载时清理定时器（照抄 storage 页清理方式）。
  useEffect(() => {
    const timer = window.setInterval(() => {
      void load();
    }, POLL_INTERVAL_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  const nowMs = Date.now();
  const fullText: FullTextIndexStatusDetail | null = snapshot?.fullText ?? null;
  const verdict = deriveIndexHealth(snapshot, nowMs);
  const visual = LEVEL_VISUALS[verdict.level];
  const levelTone = LEVEL_TONE[verdict.level];
  const chips = deriveL0Chips(fullText, nowMs);
  const chipHint = (key: 'freshness' | 'scope' | 'volume'): string | undefined =>
    chips.find((chip) => chip.key === key)?.hint;

  const volume = fullText === null ? null : summarizeIndexVolume(fullText);
  const volumeRatio = volume === null ? null : volume.ratio;
  const volumeLabel =
    volume !== null && volume.ratio !== null ? `${Math.round(volume.ratio * 100)}%` : UNKNOWN_TEXT;

  const scopes = fullText?.scopes ?? [];
  const jobs = fullText?.jobs ?? [];
  const activeJob = pickActiveJob(jobs);
  // 「最近 job」：进行中优先（活的比死的重要），否则取最近终态。
  const latestJob = activeJob ?? pickLatestTerminalJob(jobs);
  const lastWrite = fullText === null ? null : latestIndexWriteUtc(fullText);
  const freshnessRatio =
    fullText === null ? null : deriveFreshnessRatio(lastWrite, fullText.minRebuildInterval, nowMs);
  const freshnessLabel = formatRelativeTime(lastWrite, nowMs);
  const acceptedCount = fullText === null ? null : fullText.acceptedScopes.length;
  const rejectedCount = fullText === null ? null : fullText.rejectedReasons.length;
  const ledger = describeJobsLedger(fullText);
  const scopeStatus = deriveScopeCardStatus(fullText);
  const ledgerStatus = deriveLedgerCardStatus(fullText);
  const configStatus = deriveConfigCardStatus(fullText);
  const latestElapsedMs = latestJob === null ? null : latestJob.elapsedMs;
  const entriesText =
    fullText === null || hasUnknownScopeField(fullText)
      ? UNKNOWN_TEXT
      : formatTriStateCount(totalEntryCount(fullText));

  // ── B 卡（符号索引 / codeIndex）：四态 + 逐项目标记（全部来自 health.ts 纯函数）──
  const codeIndex = deriveCodeIndexBlock(snapshot);
  const codeIndexRaw = codeIndex.block;
  const codeIndexCounts = codeIndexRaw === null ? null : summarizeCodeIndexProjects(codeIndexRaw);
  const codeIndexProjects = codeIndexRaw?.projects ?? [];
  // 首屏只列前 N 项（不截断事实，只截断首屏噪声）；其余走 L2 证据层。
  const visibleProjects = codeIndexProjects.slice(0, CODE_INDEX_VISIBLE_PROJECT_LIMIT);
  const hiddenProjectCount = codeIndexProjects.length - visibleProjects.length;
  // 维护态「有」（可画表）与「缺席」（只能如实列原因）分开 —— 不得把缺席画成一张全空的表。
  const maintenanceRows = codeIndexProjects
    .map((entry) => entry.maintenance)
    .filter((value): value is CodeIndexMaintenanceStatus => value !== null);
  const maintenanceMissing = codeIndexProjects.filter((entry) => entry.maintenance === null);

  // 火花线只画最近 5 条（长尾走 L2 台账），长度按**最能表达相对量级**的口径归一。
  const jobBars = useMemo<SparkBar[]>(() => {
    const recent = jobs.slice(-5);
    const maxElapsedMs = recent.reduce<number>(
      (max, job) => (job.elapsedMs !== null && job.elapsedMs > max ? job.elapsedMs : max),
      0,
    );
    return recent.map((job) => toSparkBar(job, maxElapsedMs));
  }, [jobs]);

  return (
    <PageContainer
      header={{
        title: '索引与检索',
        subTitle: '只读观测 · 数据源 GET /api/admin/index/status（零后端改动）',
        extra: [
          <Button key="refresh" icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
            刷新
          </Button>,
        ],
      }}
    >
      <Spin spinning={loading}>
        {error !== null ? (
          <Alert {...failureAlert(error, snapshot !== null)} showIcon style={{ marginBottom: 12 }} />
        ) : null}

        {snapshot === null ? (
          loading ? (
            <Alert
              type="info"
              showIcon
              message="正在读取"
              description={`GET ${INDEX_STATUS_ENDPOINT} …`}
            />
          ) : error !== null ? (
            <Alert
              type="warning"
              showIcon
              message="尚无快照"
              description="读取失败，尚未取到任何快照（详见上方提示）。"
            />
          ) : (
            <Alert
              type="info"
              showIcon
              message="尚无快照"
              description="端点返回了空快照（既不是错误，也不是零值）。"
            />
          )
        ) : (
          <>
            <Typography.Text
              type="secondary"
              style={{ fontSize: 12, display: 'block', marginBottom: 8 }}
            >
              快照时刻 {formatUtcTime(snapshot.generatedAtUtc)} · 每 30 秒自动刷新 · 本页纯只读
            </Typography.Text>

            {/* ── L0 状态条（图 3 · A）：四个视觉单元 + 一个开关 —— 图形/动效/颜色承载状态，
                   文字只在 tooltip 与**异常时**出现（§9.6）。I13：L0 内最多 1 个呼吸元素。 ── */}
            <L0Strip
              level={verdict.level}
              shortWord={visual.word}
              headline={verdict.level === 'warn' || verdict.level === 'error' ? verdict.title : undefined}
              guidance={
                verdict.level === 'warn' || verdict.level === 'error'
                  ? `${verdict.detail} · 命中矩阵第 ${verdict.rule} 行`
                  : undefined
              }
              title={verdict.level === 'warn' || verdict.level === 'error' ? verdict.detail : verdict.title}
              freshness={{ tone: levelTone, ratio: freshnessRatio, label: freshnessLabel, title: chipHint('freshness') }}
              volume={{ tone: levelTone, ratio: volumeRatio, label: volumeLabel, title: chipHint('volume') }}
              scopes={{ accepted: acceptedCount, rejected: rejectedCount, title: chipHint('scope') }}
              busyLabel={activeJob === null ? undefined : buildBusyTitle(activeJob, nowMs)}
              showRawFields={showRawFields}
              onToggleRawFields={setShowRawFields}
            />

            {/* ── L1 四张诊断卡（哪儿不对）：每卡一种**主视觉**，文字只剩短词 + tooltip ── */}
            <Row gutter={[12, 12]}>
              <Col xs={24} xl={12}>
                <Card
                  size="small"
                  className={cx('vs-card', 'vs-t-fast', frameClass(scopeStatus.tone))}
                  title="A · 全文索引（Lucene）"
                  extra={cardWord(scopeStatus)}
                >
                  <div className="vs-metrics">
                    <RingGauge
                      tone={scopeStatus.tone}
                      ratio={volumeRatio}
                      label={volumeLabel}
                      caption="体积占用"
                      title={chipHint('volume')}
                    />
                    <RingGauge
                      tone={scopeStatus.tone}
                      ratio={freshnessRatio}
                      label={freshnessLabel}
                      caption="新鲜度"
                      title={chipHint('freshness')}
                    />
                    <span className="vs-metric">
                      <span className="vs-bignum">{entriesText}</span>
                      <span className="vs-caption">条目数</span>
                    </span>
                    <ScopeDots
                      accepted={acceptedCount}
                      rejected={rejectedCount}
                      caption="scope 受理"
                      title={chipHint('scope')}
                    />
                  </div>
                  <span className="vs-note" title="本卡只证明「索引就绪」，不等于「搜得到」">
                    索引就绪 ≠ 搜得到
                  </span>
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                {/* ── B 卡 · 符号索引（代码）：S-A2/P3 起消费真实的 `codeIndex` 块。
                       四态互不相同（块缺失 / 观测不可用 / 无项目 / 已观测）；
                       `maintenance` 为 null 的项目在行内是**独立的缺席态**（虚线空心圆 + 原因）。 ── */}
                <Card
                  size="small"
                  className={cx('vs-card', 'vs-t-fast', frameClass(codeIndex.status.tone))}
                  title="B · 符号索引（代码）"
                  extra={cardWord(codeIndex.status)}
                >
                  {/* 态一：块**键缺失** —— 端点未重启（预期状态），未接入 ≠ 故障，也不等于「没有项目」 */}
                  {codeIndex.state === 'absent' ? (
                    <div className="vs-metrics">
                      <UnknownGlyph
                        label={codeIndex.status.text}
                        title={`${codeIndex.status.hint}\n本卡不参与 L0 聚合（L0 仍只聚合全文索引）`}
                      />
                    </div>
                  ) : null}

                  {/* 态二：块在，但**一块项目也没有**（已知的空 ≠ 不知道） */}
                  {codeIndex.state === 'empty' ? (
                    <div className="vs-metrics">
                      <span className="vs-metric">
                        <span className="vs-bignum">{codeIndexProjects.length}</span>
                        <span className="vs-caption">项目</span>
                      </span>
                      <UnknownGlyph label={codeIndex.status.text} title={codeIndex.status.hint} />
                    </div>
                  ) : null}

                  {/* 态三：整块降级 / 运行态不可知 —— **未知 ≠ 健康**，升格为异常态长句（§9.6） */}
                  {codeIndex.state === 'unavailable' ? (
                    <>
                      <div className="vs-metrics">
                        <span className={cx('vs-shortword', 'vs-tone-warn')}>
                          {codeIndex.status.text}
                        </span>
                        <span className="vs-caption" title={codeIndex.status.hint}>
                          {`命中规则 #${codeIndex.rule}`}
                        </span>
                      </div>
                      <span className="vs-note">{codeIndex.headline}</span>
                      {codeIndex.guidance !== null ? (
                        <span className="vs-note">{codeIndex.guidance}</span>
                      ) : null}
                    </>
                  ) : null}

                  {/* 态四：已观测到项目 —— 计数 + 逐项目行（原始字段只在 tooltip 与 L2） */}
                  {codeIndex.state === 'observed' && codeIndexCounts !== null ? (
                    <>
                      <div className="vs-metrics">
                        <span className="vs-metric" title={codeIndex.status.hint}>
                          <span className="vs-bignum">{codeIndexCounts.total}</span>
                          <span className="vs-caption">项目</span>
                        </span>
                        <span className="vs-metric">
                          <span className="vs-bignum">{codeIndexCounts.stale}</span>
                          <span className="vs-caption">陈旧</span>
                        </span>
                        <span className="vs-metric">
                          <span className="vs-bignum">{codeIndexCounts.unregistered}</span>
                          <span className="vs-caption">未登记</span>
                        </span>
                        <span className="vs-metric">
                          <span className="vs-bignum">{codeIndexCounts.inFlight}</span>
                          <span className="vs-caption">索引中</span>
                        </span>
                      </div>
                      <div className="vs-ci-rows">
                        {visibleProjects.map((entry) => (
                          <CodeIndexProjectRow
                            key={`${entry.workspaceId}/${entry.projectId}`}
                            entry={entry}
                            nowMs={nowMs}
                          />
                        ))}
                      </div>
                      {hiddenProjectCount > 0 ? (
                        <span className="vs-ci-more">
                          {`还有 ${hiddenProjectCount} 项未列（首屏上限 ${CODE_INDEX_VISIBLE_PROJECT_LIMIT}）⇒ 见下方 L2 证据层`}
                        </span>
                      ) : null}
                    </>
                  ) : null}

                  {codeIndex.state === 'observed' && codeIndexCounts !== null ? (
                    <span
                      className="vs-note"
                      title="本卡只报「索引在不在 / 陈旧不陈旧」；「搜不搜得到」不在本卡结论里"
                    >
                      索引存在 ≠ 搜得到 · 陈旧与路径失效由后端 fail-closed 标记（D1/D2）
                    </span>
                  ) : null}
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                <Card
                  size="small"
                  className={cx('vs-card', 'vs-t-fast', frameClass(ledgerStatus.tone))}
                  title="C · 供给台账（jobs）"
                  extra={cardWord(ledgerStatus)}
                >
                  <SparkBars
                    bars={jobBars}
                    caption={
                      jobBars.length === 0
                        ? undefined
                        : `最近 ${jobBars.length} 次供给 · 长度=耗时 · 颜色=结果`
                    }
                    emptyText={ledger.isEmpty ? ledger.text : undefined}
                  />
                  {activeJob !== null ? (
                    <span className="vs-note" title="不定长流光：只表达「还在走」，不给百分比（后端无 processed/total）">
                      {buildBusyTitle(activeJob, nowMs)}
                    </span>
                  ) : null}
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                <Card
                  size="small"
                  className={cx('vs-card', 'vs-t-fast', frameClass(configStatus.tone))}
                  title="D · 配置与受理"
                  extra={cardWord(configStatus)}
                >
                  <div className="vs-metrics">
                    <ToggleGlyph
                      value={fullText?.enabled ?? null}
                      label="全文索引"
                      title={`configured = ${formatTriStateBoolean(fullText?.configured)} · enabled = ${formatTriStateBoolean(fullText?.enabled)}`}
                    />
                    <ToggleGlyph
                      value={fullText?.maintenance.enabled ?? null}
                      label="维护循环"
                      title={`configured = ${formatTriStateBoolean(fullText?.maintenance.configured)} · 生效值 null ⇒ 不可知（≠「关」）`}
                    />
                    <ScopeDots
                      accepted={acceptedCount}
                      rejected={rejectedCount}
                      caption="scope 受理"
                      title={chipHint('scope')}
                    />
                  </div>
                  <span
                    className="vs-note"
                    title="维护开关 null ⇒ 宿主侧没有该子节的绑定器 ⇒ 生效值不可知，不得当作「关」"
                  >
                    未配置 ≠ 已关闭
                  </span>
                </Card>
              </Col>
            </Row>

            {/* ── L2 原始字段（排障；默认折叠，与 L0 的「原始字段」开关联动）
                    ⚠️ 证据层**原样保留**（规格 §9.6）：本层本来就该密，字段一个不丢。 ── */}
            <Card
              size="small"
              style={{ marginTop: 12 }}
              title="L2 · 原始字段（排障）· 默认折叠"
              extra={
                <Button size="small" onClick={() => setShowRawFields((value) => !value)}>
                  {showRawFields ? '收起 ▴' : '展开 ▾'}
                </Button>
              }
            >
              {showRawFields && fullText !== null ? (
                <>
                  <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} bordered>
                    <Descriptions.Item label="generatedAtUtc">
                      {formatUtcTime(snapshot.generatedAtUtc)}
                    </Descriptions.Item>
                    <Descriptions.Item label="configured">
                      {formatTriStateBoolean(fullText.configured)}
                    </Descriptions.Item>
                    <Descriptions.Item label="enabled">{formatTriStateBoolean(fullText.enabled)}</Descriptions.Item>
                    <Descriptions.Item label="indexRoot">{pathText(fullText.indexRoot)}</Descriptions.Item>
                    <Descriptions.Item label="indexRootExists">
                      {formatTriStateBoolean(fullText.indexRootExists)}
                    </Descriptions.Item>
                    <Descriptions.Item label="workspaceRoot">{pathText(fullText.workspaceRoot)}</Descriptions.Item>
                    <Descriptions.Item label="maxIndexBytes">
                      {`${formatTriStateBytes(fullText.maxIndexBytes)}（${fullText.maxIndexBytes} B）`}
                    </Descriptions.Item>
                    <Descriptions.Item label="minRebuildInterval">{fullText.minRebuildInterval}</Descriptions.Item>
                    <Descriptions.Item label="compositionCreated">
                      {formatTriStateBoolean(fullText.compositionCreated)}
                    </Descriptions.Item>
                    <Descriptions.Item label="maintenance.configured">
                      {formatTriStateBoolean(fullText.maintenance.configured)}
                    </Descriptions.Item>
                    <Descriptions.Item label="maintenance.enabled">
                      {triStateTag(fullText.maintenance.enabled)}
                    </Descriptions.Item>
                    <Descriptions.Item label="jobsReason">
                      {formatTriStateText(fullText.jobsReason)}
                    </Descriptions.Item>
                    <Descriptions.Item label={`acceptedScopes（${fullText.acceptedScopes.length}）`} span={3}>
                      {stringList(fullText.acceptedScopes, '（空数组：无受理 scope）')}
                    </Descriptions.Item>
                    <Descriptions.Item label={`rejectedReasons（${fullText.rejectedReasons.length}）`} span={3}>
                      {stringList(fullText.rejectedReasons, '（空数组：无被拒 scope）')}
                    </Descriptions.Item>
                    <Descriptions.Item label="maintenance.note" span={3}>
                      {formatTriStateText(fullText.maintenance.note)}
                    </Descriptions.Item>
                  </Descriptions>

                  <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                    {`逐 scope 观测（scopes，共 ${scopes.length} 项）· ${SCOPE_FIELDS.length} 列`}
                  </Typography.Text>
                  <Table<FullTextIndexScopeStatus>
                    size="small"
                    rowKey="scopePath"
                    scroll={{ x: 1300 }}
                    pagination={false}
                    dataSource={scopes}
                    columns={scopeColumns}
                    locale={{ emptyText: '（无受理 scope：acceptedScopes 为空）' }}
                  />

                  <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                    {`供给 job 台账（jobs，共 ${jobs.length} 条）· ${JOB_FIELDS.length} 列`}
                  </Typography.Text>
                  {!shouldRenderJobsTable(fullText) ? (
                    <Alert
                      type={fullText.jobsReason === 'ledger-read-failed' ? 'error' : 'info'}
                      showIcon
                      message="台账为空（不渲染空表格）"
                      description={`jobsReason：${formatTriStateText(fullText.jobsReason)} ⇒ ${ledger.text}`}
                    />
                  ) : (
                    <Table<FullTextIndexJobStatus>
                      size="small"
                      rowKey="jobId"
                      scroll={{ x: 1400 }}
                      pagination={{ pageSize: 10, hideOnSinglePage: true }}
                      dataSource={jobs}
                      columns={jobColumns}
                    />
                  )}

                  {/* ── codeIndex（符号索引）原始字段：字段名/顺序**由契约常量生成** ─────
                       折叠机制与上文完全一致（L0 的「原始字段」开关）。
                       ⚠️ 块缺失在这里也必须**显式**说明（不留空白 —— 空白会被读成「坏了」）。 ── */}
                  <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                    {`符号索引块（codeIndex）· ${CODE_INDEX_BLOCK_FIELDS.length} 字段`}
                  </Typography.Text>
                  {codeIndexRaw === null ? (
                    <Alert
                      type="info"
                      showIcon
                      message="响应里没有 codeIndex 键"
                      description="该块由 Core 上的 CodeIndexStatusProbe 产出，改动要重启 Core 才生效 —— 未接入 ≠ 故障。这与「块在、但 projects 为空」是两个不同事实。"
                    />
                  ) : (
                    <>
                      <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} bordered>
                        {CODE_INDEX_BLOCK_FIELDS.map((field) => (
                          <Descriptions.Item key={field.key} label={`${field.label}（${field.key}）`}>
                            {renderFieldValue(
                              field.kind,
                              (codeIndexRaw as unknown as Record<string, unknown>)[field.key],
                            )}
                          </Descriptions.Item>
                        ))}
                      </Descriptions>

                      <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                        {`符号索引逐项目（projects，共 ${codeIndexProjects.length} 项）· ${CODE_INDEX_PROJECT_FIELDS.length} 列`}
                      </Typography.Text>
                      <Table<CodeIndexProjectStatus>
                        size="small"
                        rowKey={(row) => `${row.workspaceId}/${row.projectId}`}
                        scroll={{ x: 1600 }}
                        pagination={false}
                        dataSource={codeIndexProjects}
                        columns={codeIndexProjectColumns}
                        locale={{
                          emptyText:
                            '（projects 为空：注册表与维护驱动都没有条目 —— 这是「已知的空」，不是「读不到」）',
                        }}
                      />

                      <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                        {`维护态（maintenance 原样透传；可画表 ${maintenanceRows.length} 项）· ${CODE_INDEX_MAINTENANCE_FIELDS.length} 列`}
                      </Typography.Text>
                      {maintenanceRows.length === 0 ? (
                        <Alert
                          type="info"
                          showIcon
                          message="没有任何项目的维护态可见"
                          description="maintenance 全为 null ⇒ 逐项缺席原因见下（这里**不渲染一张全空的表** —— 那会把「未知」画成「正常」）。"
                        />
                      ) : (
                        <Table<CodeIndexMaintenanceStatus>
                          size="small"
                          rowKey={(row) => `${row.workspaceId}/${row.scopeId}`}
                          scroll={{ x: 2200 }}
                          pagination={false}
                          dataSource={maintenanceRows}
                          columns={codeIndexMaintenanceColumns}
                        />
                      )}

                      {maintenanceMissing.length > 0 ? (
                        <>
                          <Typography.Text strong style={{ display: 'block', margin: '12px 0 4px' }}>
                            {`维护态缺席清单（maintenance === null，共 ${maintenanceMissing.length} 项）`}
                          </Typography.Text>
                          <Space direction="vertical" size={2} style={{ width: '100%' }}>
                            {maintenanceMissing.map((entry) => (
                              <Typography.Text
                                key={`${entry.workspaceId}/${entry.projectId}`}
                                style={{ fontSize: 12, wordBreak: 'break-all' }}
                              >{`${entry.displayName ?? entry.projectId}（${entry.projectId}）：maintenanceReason = ${formatTriStateText(
                                entry.maintenanceReason,
                              )} ⇒ ${describeMaintenanceReason(entry.maintenanceReason)}`}</Typography.Text>
                            ))}
                          </Space>
                        </>
                      ) : null}
                    </>
                  )}
                </>
              ) : (
                <Typography.Text type="secondary">
                  默认折叠：需要排障时展开 —— 展开后的字段与「字段名当列标题」的旧视图一一对应，**一个字段都不丢**。
                </Typography.Text>
              )}
            </Card>
          </>
        )}
      </Spin>
    </PageContainer>
  );
};

export default IndexStatusPage;
