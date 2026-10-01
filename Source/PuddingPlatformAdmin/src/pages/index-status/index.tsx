import React, { useCallback, useEffect, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Checkbox,
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
  UNKNOWN_TEXT,
  formatTriStateBoolean,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateDurationMs,
  formatTriStateText,
  formatTriStateTimestamp,
  formatUtcTime,
  getIndexStatus,
} from './api';
import {
  JOB_FIELDS,
  LEVEL_TONE,
  LEVEL_TEXT,
  RAW_FIELDS_DEFAULT_EXPANDED,
  SCOPE_FIELDS,
  STATUS_TONES,
  SYMBOL_CARD_STATUS,
  deriveConfigCardStatus,
  deriveIndexHealth,
  deriveL0Chips,
  deriveLedgerCardStatus,
  deriveScopeCardStatus,
  describeJobsLedger,
  hasUnknownScopeField,
  healthToneStyle,
  latestIndexWriteUtc,
  pickActiveJob,
  pickLatestTerminalJob,
  summarizeIndexVolume,
  shouldRenderJobsTable,
  totalEntryCount,
} from './health';
import type { CardStatus, FieldKind, StatusTone } from './health';
import type {
  FullTextIndexJobStatus,
  FullTextIndexScopeStatus,
  FullTextIndexStatusDetail,
  FullTextIndexStatusSnapshot,
} from './types';

// ── Slice P1：Admin「索引与检索」页（只读）──────────────────────────────
// 信息架构（规格 §1）：**L0 结论条**（常驻 1 行）→ **L1 四张诊断卡** → **L2 原始字段**（默认折叠）。
//   · 默认视图回答「现在能不能放心让 Agent 去搜」；排障时才下钻到字段 —— **字段一个不丢**，
//     只是不再抢占首屏（原「字段名当列标题 + 8 列宽表」的全部内容都保留在 L2）。
// 数据源：GET /api/admin/index/status（IndexAdminController，Admin JWT）。
//
// 三条纪律：
// 1) 三态贯穿（规格 §4）：`null` = 未知 / `false` = 否 / `0` = 0，三者渲染必须不同；
//    一律经 ./api 与 ./health 的纯函数，**禁止** `?? 0` / `|| '否'` 式折叠。
// 2) 纯只读：本页没有任何写操作能力（不触发供给、不重建、不删除；「重建索引」按钮刻意不做）。
// 3) 轮询照抄 src/pages/storage/index.tsx 的既有用法（window.setInterval + 卸载清理），
//    刻意**不**引入 src/pages/chat/** 的任何模块，以保证该目录零接触。

/** 轮询间隔：与 storage 页同量级（30s），不自创激进间隔。 */
const POLL_INTERVAL_MS = 30_000;

/** 状态族 → 左侧色条（规格 §4 的 5 族配色）。 */
const TONE_BORDER: Record<StatusTone, string> = {
  ok: '#2f7d4f',
  warn: '#b47818',
  error: '#b3261e',
  neutral: '#9aa3af',
  busy: '#1a5fb4',
};

/** 状态角标：图形 + 文字双编码（灰度打印 / 色盲可辨）。 */
function toneTag(tone: StatusTone, text: string, hint?: string): React.ReactNode {
  const style = STATUS_TONES[tone];
  return (
    <Tag color={style.tagColor} title={hint}>
      {style.glyph} {text}
    </Tag>
  );
}

/** 卡片角标（A/C/D 由纯函数推导，B 为固定占位）。 */
function cardTag(status: CardStatus): React.ReactNode {
  return toneTag(status.tone, status.text, status.hint);
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

const IndexStatusPage: React.FC = () => {
  const [snapshot, setSnapshot] = useState<FullTextIndexStatusSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showRawFields, setShowRawFields] = useState<boolean>(RAW_FIELDS_DEFAULT_EXPANDED);

  const load = useCallback(async () => {
    try {
      setSnapshot(await getIndexStatus());
      setError(null);
    } catch (err) {
      // 读不到就如实报读不到，不用上一次的快照冒充当前真值。
      setError(err instanceof Error ? err.message : String(err));
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

  const fullText: FullTextIndexStatusDetail | null = snapshot?.fullText ?? null;
  const verdict = deriveIndexHealth(snapshot);
  const lead = healthToneStyle(verdict.level);
  const chips = deriveL0Chips(fullText);
  const ledger = describeJobsLedger(fullText);
  const volume = fullText === null ? null : summarizeIndexVolume(fullText);
  const scopes = fullText?.scopes ?? [];
  const jobs = fullText?.jobs ?? [];
  const activeJob = pickActiveJob(jobs);
  // 「最近 job」：进行中优先（活的比死的重要），否则取最近终态。
  const latestJob = activeJob ?? pickLatestTerminalJob(jobs);
  const lastWrite = fullText === null ? null : latestIndexWriteUtc(fullText);
  const acceptedCount = fullText === null ? UNKNOWN_TEXT : `${fullText.acceptedScopes.length} 条`;
  const rejectedCount = fullText === null ? UNKNOWN_TEXT : `${fullText.rejectedReasons.length} 条`;
  const largestScopeBytes = volume === null ? null : volume.largestScopeBytes;
  const maxIndexBytes = volume === null ? null : volume.maxIndexBytes;
  const latestElapsedMs = latestJob === null ? null : latestJob.elapsedMs;
  const entriesText =
    fullText === null || hasUnknownScopeField(fullText)
      ? UNKNOWN_TEXT
      : formatTriStateCount(totalEntryCount(fullText));
  const pctText =
    volume === null || volume.ratio === null ? UNKNOWN_TEXT : `${Math.round(volume.ratio * 100)}%`;

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
        {error ? (
          <Alert
            type="error"
            showIcon
            style={{ marginBottom: 12 }}
            message="索引状态读取失败"
            description={`${error}（页面保留上一次成功快照仅作参考，不代表当前真值）`}
          />
        ) : null}

        {snapshot === null ? (
          <Alert
            type="info"
            showIcon
            message="尚无快照"
            description="正在读取 GET /api/admin/index/status …"
          />
        ) : (
          <>
            <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginBottom: 8 }}>
              快照时刻 {formatUtcTime(snapshot.generatedAtUtc)} · 每 30 秒自动刷新 · 本页纯只读
            </Typography.Text>

            {/* ── L0 结论条（常驻 1 行：现在能不能放心让 Agent 去搜） ── */}
            <Card
              size="small"
              style={{ marginBottom: 12, borderLeft: `4px solid ${TONE_BORDER[LEVEL_TONE[verdict.level]]}` }}
              styles={{ body: { padding: '10px 14px' } }}
            >
              <Space size={12} wrap align="center" style={{ width: '100%', justifyContent: 'space-between' }}>
                <Space size={12} wrap align="center">
                  <Tag color={lead.tagColor}>
                    {lead.glyph} {LEVEL_TEXT[verdict.level]}
                  </Tag>
                  <Typography.Text strong style={{ fontSize: 15 }}>
                    {verdict.title}
                  </Typography.Text>
                </Space>
                <Checkbox
                  checked={showRawFields}
                  onChange={(event) => setShowRawFields(event.target.checked)}
                >
                  原始字段
                </Checkbox>
              </Space>
              <Space size={8} wrap style={{ marginTop: 8 }}>
                {chips.map((chip) => (
                  <Tag key={chip.key} title={chip.hint}>
                    {chip.text}
                  </Tag>
                ))}
              </Space>
              <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                {verdict.detail}
              </Typography.Text>
            </Card>

            {/* ── 异常态置顶告警条（仅 error / warn 出现） ── */}
            {verdict.level === 'error' || verdict.level === 'warn' ? (
              <Alert
                type={verdict.level === 'error' ? 'error' : 'warning'}
                showIcon
                style={{ marginBottom: 12 }}
                message={verdict.title}
                description={verdict.detail}
              />
            ) : null}

            {/* ── L1 四张诊断卡（哪儿不对） ── */}
            <Row gutter={[12, 12]}>
              <Col xs={24} xl={12}>
                <Card size="small" title="A · 全文索引（Lucene）" extra={cardTag(deriveScopeCardStatus(fullText))}>
                  <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
                    <Descriptions.Item label="索引根">
                      <Space size={6} wrap>
                        {pathText(fullText?.indexRoot)}
                        <span style={{ fontSize: 12, color: '#6b7280' }}>存在：</span>
                        {triStateTag(fullText?.indexRootExists)}
                      </Space>
                    </Descriptions.Item>
                    <Descriptions.Item label="scope">
                      <Space size={6} wrap>
                        <span>{fullText === null ? UNKNOWN_TEXT : `${fullText.acceptedScopes.length} 条受理`}</span>
                        {fullText?.acceptedScopes.length === 1 ? pathText(fullText.acceptedScopes[0]) : null}
                      </Space>
                    </Descriptions.Item>
                    <Descriptions.Item label="条目 / 体积">
                      {`${entriesText} 项 · ${formatTriStateBytes(largestScopeBytes)}`}
                    </Descriptions.Item>
                    <Descriptions.Item label="上限占用">
                      {`${formatTriStateBytes(maxIndexBytes)} 的 ${pctText}`}
                    </Descriptions.Item>
                    <Descriptions.Item label="最后写入">
                      <Space size={6} wrap>
                        <span>{formatTriStateTimestamp(lastWrite)}</span>
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          {formatUtcTime(lastWrite)}
                        </Typography.Text>
                      </Space>
                    </Descriptions.Item>
                    <Descriptions.Item label="最小重建间隔">
                      {formatTriStateText(fullText?.minRebuildInterval)}
                    </Descriptions.Item>
                  </Descriptions>
                  <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                    本卡只证明「索引就绪」，不等于「搜得到」—— 后者需真跑一次查询（本片不做自动试搜）。
                  </Typography.Text>
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                <Card size="small" title="B · 符号索引（代码）" extra={cardTag(SYMBOL_CARD_STATUS)}>
                  <div
                    style={{
                      border: '1px dashed #9aa3af',
                      borderRadius: 4,
                      padding: 12,
                      background: '#fcfcfd',
                    }}
                  >
                    <Typography.Text>本块尚未接入（待 S-A2：把 codeIndex 并入同一端点）。</Typography.Text>
                    <ul style={{ margin: '8px 0 0', paddingLeft: 18, color: '#4b5563', fontSize: 12 }}>
                      <li>计划显示：项目数 · 各项目状态（Completed / Indexing / Stale）</li>
                      <li>计划显示：最后索引时刻 · 索引体积</li>
                      <li>计划数据源：ICodeIndexMaintenance.GetScopeStatuses()</li>
                    </ul>
                    <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                      纪律：未接入 ≠ 故障 —— 不参与 L0 聚合、不染红、**不留空白**（空白会被读成「坏了」，故必须给出明确占位文案）。
                    </Typography.Text>
                  </div>
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                <Card size="small" title="C · 供给台账（jobs）" extra={cardTag(deriveLedgerCardStatus(fullText))}>
                  <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
                    <Descriptions.Item label="供给组件">
                      {fullText?.compositionCreated === true
                        ? '已启动'
                        : fullText?.compositionCreated === false
                          ? '未启动（无人触发过预建）'
                          : toneTag('neutral', UNKNOWN_TEXT)}
                    </Descriptions.Item>
                    <Descriptions.Item label="最近 job">
                      {latestJob === null ? (
                        toneTag('neutral', UNKNOWN_TEXT)
                      ) : (
                        <Space size={6} wrap>
                          {pathText(latestJob.jobId)}
                          <Tag color="processing">{formatTriStateText(latestJob.state)}</Tag>
                        </Space>
                      )}
                    </Descriptions.Item>
                    <Descriptions.Item label="清点">
                      {latestJob === null
                        ? UNKNOWN_TEXT
                        : `${formatTriStateCount(latestJob.indexedFileCount)} 文件 · ${formatTriStateBytes(latestJob.totalBytes)}`}
                    </Descriptions.Item>
                    <Descriptions.Item label="用时">
                      {formatTriStateDurationMs(latestElapsedMs)}
                    </Descriptions.Item>
                    <Descriptions.Item label="台账条数">
                      {`${jobs.length}（全部 ${JOB_FIELDS.length} 列见 L2）`}
                    </Descriptions.Item>
                  </Descriptions>
                  {ledger.isEmpty ? (
                    <Alert
                      type={fullText?.jobsReason === 'ledger-read-failed' ? 'error' : 'info'}
                      showIcon
                      style={{ marginTop: 8 }}
                      message="台账为空 —— 如实原因如下（不显示空表格）"
                      description={ledger.text}
                    />
                  ) : (
                    <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                      台账非空：逐条状态（状态机状态 / 阶段 / 清点 / 耗时）见 L2 原始字段。
                    </Typography.Text>
                  )}
                </Card>
              </Col>

              <Col xs={24} xl={12}>
                <Card size="small" title="D · 配置与受理" extra={cardTag(deriveConfigCardStatus(fullText))}>
                  <Descriptions column={1} size="small" colon={false} labelStyle={{ width: 110 }}>
                    <Descriptions.Item label="FullTextIndex 节">
                      <Space size={6} wrap>
                        <span>{formatTriStateBoolean(fullText?.configured)}</span>
                        <span style={{ fontSize: 12, color: '#6b7280' }}>enabled =</span>
                        {triStateTag(fullText?.enabled)}
                      </Space>
                    </Descriptions.Item>
                    <Descriptions.Item label="workspaceRoot">
                      {pathText(fullText?.workspaceRoot)}
                    </Descriptions.Item>
                    <Descriptions.Item label="受理 scope">
                      {acceptedCount}
                    </Descriptions.Item>
                    <Descriptions.Item label="拒收原因">
                      {rejectedCount}
                    </Descriptions.Item>
                    <Descriptions.Item label="维护循环">
                      <Space size={6} wrap>
                        <span>
                          {fullText?.maintenance.configured === true ? '已配置' : '未配置（走默认）'}
                        </span>
                        <span style={{ fontSize: 12, color: '#6b7280' }}>是否生效：</span>
                        {triStateTag(fullText?.maintenance.enabled)}
                      </Space>
                    </Descriptions.Item>
                  </Descriptions>
                  <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                    注：维护开关 `null` ⇒ 宿主侧没有该子节的绑定器 ⇒ 生效值**不可知**，不得当作「关」。
                  </Typography.Text>
                </Card>
              </Col>
            </Row>

            {/* ── L2 原始字段（排障；默认折叠，与 L0 的「原始字段」开关联动） ── */}
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
