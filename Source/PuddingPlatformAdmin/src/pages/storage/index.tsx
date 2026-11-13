import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Checkbox,
  Col,
  Modal,
  Popconfirm,
  Progress,
  Row,
  Segmented,
  Select,
  Space,
  Spin,
  Table,
  Tabs,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import {
  ClearOutlined,
  DatabaseOutlined,
  EyeOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import {
  cancelCleanupJob,
  confirmCleanupJob,
  createCleanupPreview,
  formatBytes,
  formatCount,
  formatUtc,
  getCleanupJobEvents,
  getDatabaseSpace,
  getInventoryTrend,
  getProtectedObjects,
  getRetentionPolicy,
  getStorageDataClasses,
  getStorageOverview,
  listCleanupJobs,
  requestInventoryRefresh,
} from './api';
import type {
  StorageCleanupJob,
  StorageCleanupJobStatus,
  StorageCleanupPreview,
  StorageDataClass,
  StorageDatabaseSpace,
  StorageInventoryClass,
  StorageInventorySnapshot,
  StorageInventoryTrendPoint,
  StorageRetentionPolicy,
} from './types';
import { StorageClassDonut, StorageTrendChart, classColor } from './StorageOverviewCharts';
import { StoragePolicyDrawer } from './StoragePolicyDrawer';
import { CleanupPreviewModal } from './CleanupPreviewModal';
import { DatabaseSpacePanel } from './DatabaseSpacePanel';
import { StorageStackedBars, type StackedBarRow } from './StorageStackedBars';
import { StorageTreemap, type StorageTreemapDatum } from './StorageTreemap';
import {
  AUTO_CLEANUP_HINT,
  AUTO_CLEANUP_SCHEDULING_NOTE,
  AUTO_CLEANUP_TAG_COLOR,
  resolveAutoCleanupState,
  resolveEffectiveRetention,
} from './autoCleanupState';
import type { AutoCleanupState, EffectiveRetention } from './autoCleanupState';

// ── ADR-076 Web 存储管理页 ─────────────────────────────────────
// 首屏只读缓存快照；刷新是异步 202 请求（页面保持可交互）；
// 全部数量/字节均为约数（≈），最终清理以作业结果为准。

const JOB_STATUS_TAG: Record<StorageCleanupJobStatus, { color: string; text: string }> = {
  Queued: { color: 'default', text: '排队中' },
  Running: { color: 'processing', text: '执行中' },
  PausedBusy: { color: 'warning', text: '忙让步' },
  NeedsConfirmation: { color: 'warning', text: '待确认' },
  Cancelling: { color: 'warning', text: '取消中' },
  Completed: { color: 'success', text: '已完成' },
  Partial: { color: 'gold', text: '部分完成' },
  Failed: { color: 'error', text: '失败' },
  Cancelled: { color: 'default', text: '已取消' },
};

const OLDER_THAN_OPTIONS = [
  { value: 7, label: '早于 7 天' },
  { value: 14, label: '早于 14 天' },
  { value: 30, label: '早于 30 天' },
  { value: 90, label: '早于 90 天' },
];

// ── 首屏口径说明（只以 title 悬浮存在，不进正文）────────────────────
// 这些解释原本是首屏正文里的四行 secondary 文本，视觉权重高于数字本身（改版问题 P4）。
// 现在数字旁只留一行极短标签，完整口径挂到 title：鼠标悬停可读，一屏高度可预测。
const HEADLINE_TOTAL_NOTE =
  '口径：该数字 = 全部数据库的主文件 + WAL + 共享内存，加上非数据库目录（如全文检索索引）的合计。' +
  '「数据库占用（按数据类）」里每个库的大小只是该库自身的主文件 + WAL + 共享内存，所以那几个库相加会小于这里。';
const DATABASE_ONLY_NOTE =
  '数据库主文件 + WAL + 共享内存。单库的 GB = 该库主文件 + WAL + 共享内存（与该库「大小」列同一口径）；' +
  '首屏「总占用」还含非数据库目录，所以各库相加会小于总占用。';
const REUSABLE_PAGE_NOTE = '可复用页：已删除数据留下的空闲页，会被后续写入复用，不是可再清理的额度。';
const CLEANABLE_NOTE = '可清理估算：按当前保留策略还能再删的数据量，不含上述空闲页。';
const TREEMAP_NOTE =
  '面积＝字节。分类按 targetId 取色，块内文字只在放得下时出现，精确字节与占比由悬浮提示给出。';
const STACKED_NOTE =
  '长度＝该段占「该库已分类段之和」的比例；右侧数字是该库文件大小（含 WAL），所以条上可能留出未分类的空白。';
const RECLAIM_NOTE =
  '三项性质不同：空闲页要 VACUUM 才归还文件系统；冗余索引删掉索引即回收；保留策略按时间裁剪。' +
  '三者不能相加当作「可清理额度」。';

/** 首屏指标格：一个数字 + 一行短标签；完整口径只放在 title 里。 */
const MetricCell: React.FC<{
  testId: string;
  label: string;
  value: string;
  note: string;
  hint: string;
}> = ({ testId, label, value, note, hint }) => (
  <Card size="small" styles={{ body: { padding: 12 } }}>
    <div data-testid={testId} style={{ display: 'grid', gap: 2 }}>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {label}
      </Typography.Text>
      <span title={hint} style={{ fontSize: 22, fontWeight: 600, lineHeight: 1.2, cursor: 'help' }}>
        {value}
      </span>
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        {note}
      </Typography.Text>
    </div>
  </Card>
);

/** 首屏第 3 行「可回收三段条」的一段：字节 + 一句性质说明（不参与任何合计口径）。 */
interface ReclaimSegment {
  key: string;
  label: string;
  bytes: number;
  color: string;
  note: string;
  /**
   * 本段可预览清理的 targetId（空数组 = 本段不在清理范围）。
   *
   * <p>为什么把它做成数据而不是渲染处 if-else：VACUUM 段是「库内空闲页」——
   * 它没有 targetId，也不属于任何可删数据类，必须靠 VACUUM 才归还文件系统。
   * 把「能不能清理」写在段上，渲染层就不用知道三种可回收的性质差异。
   * </p>
   */
  previewTargets: string[];
}

interface ClassRow extends StorageInventoryClass {
  policyEnabled?: boolean;
  policyRetentionDays?: number | null;
  manualAllowed: boolean;
  showInSelector: boolean;
  description: string;
  requiresRollup: boolean;
  /** 自动清理状态（纯前端推导，见 autoCleanupState.ts）。 */
  autoCleanup: AutoCleanupState;
  /** 保留策略生效天数与可接受范围（策略值优先，回落目录默认值）。 */
  retention: EffectiveRetention;
}

const StoragePage: React.FC = () => {
  const [snapshot, setSnapshot] = useState<StorageInventorySnapshot | null>(null);
  const [dataClasses, setDataClasses] = useState<StorageDataClass[]>([]);
  const [policy, setPolicy] = useState<StorageRetentionPolicy | null>(null);
  const [protectedObjects, setProtectedObjects] = useState<string[]>([]);
  const [trend, setTrend] = useState<StorageInventoryTrendPoint[]>([]);
  // 首屏「各库构成」需要「库 → 数据类」的拆分，只有 database-space 端点提供。
  const [databaseSpaces, setDatabaseSpaces] = useState<StorageDatabaseSpace[]>([]);
  const [trendDays, setTrendDays] = useState<number>(30);
  const [jobs, setJobs] = useState<StorageCleanupJob[]>([]);
  const [selectedTargets, setSelectedTargets] = useState<string[]>([]);
  const [olderThanDays, setOlderThanDays] = useState<number>(14);
  const [previewOpen, setPreviewOpen] = useState(false);
  const [preview, setPreview] = useState<StorageCleanupPreview | null>(null);
  const [previewSubmitting, setPreviewSubmitting] = useState(false);
  const [previewJobId, setPreviewJobId] = useState<string | null>(null);
  const [policyOpen, setPolicyOpen] = useState(false);
  const [loading, setLoading] = useState(true);
  const refreshTimer = useRef<number | null>(null);
  // database-space 只测量一次的守卫：装载、重渲染、StrictMode 都不重复付费。
  const spacesRequested = useRef(false);

  const loadOverview = useCallback(async () => {
    try {
      setSnapshot(await getStorageOverview());
    } catch {
      // Core 不可达时保留旧快照；页面不挂起。
    }
  }, []);

  const loadJobs = useCallback(async () => {
    try {
      setJobs(await listCleanupJobs(20));
    } catch {
      // 忽略作业列表瞬时失败。
    }
  }, []);

  const loadPolicy = useCallback(async () => {
    try {
      setPolicy(await getRetentionPolicy());
    } catch {
      // 忽略策略瞬时失败。
    }
  }, []);

  const loadTrend = useCallback(async (days: number) => {
    try {
      setTrend(await getInventoryTrend(days));
    } catch {
      setTrend([]);
    }
  }, []);

  useEffect(() => {
    (async () => {
      setLoading(true);
      try {
        const [classesSnapshot, objects] = await Promise.all([
          getStorageDataClasses(),
          getProtectedObjects(),
        ]);
        setDataClasses(classesSnapshot);
        setProtectedObjects(objects);
        await Promise.all([loadOverview(), loadJobs(), loadPolicy(), loadTrend(trendDays)]);
      } finally {
        setLoading(false);
      }
    })();
  }, [loadOverview, loadJobs, loadPolicy, loadTrend, trendDays]);

  // 首屏渲染后不再阻塞：后台 30s 轮询快照/作业（SSE 增量通道后续接入）。
  useEffect(() => {
    const timer = window.setInterval(() => {
      void loadOverview();
      void loadJobs();
    }, 30_000);
    return () => window.clearInterval(timer);
  }, [loadOverview, loadJobs]);

  useEffect(() => {
    void loadTrend(trendDays);
  }, [trendDays, loadTrend]);

  // 首屏「各库构成」的数据：database-space 是一次显式只读测量（逐表取行数，GB 级库需要几秒），
  // 所以只在装载时触发一次（ref 去重）。Tab 里的 DatabaseSpacePanel 仍是「点按钮才测量」，
  // 装载时不会叠第二次请求；两处消费同一端点，但同一时刻只有一处真的在测量。
  useEffect(() => {
    if (spacesRequested.current) return;
    spacesRequested.current = true;
    let alive = true;
    (async () => {
      try {
        const spaces = await getDatabaseSpace();
        if (alive) setDatabaseSpaces(spaces);
      } catch {
        // 测量失败只让堆叠条回落空态，不影响首屏其余数字。
      }
    })();
    return () => {
      alive = false;
    };
  }, []);

  // 组件卸载清理快速轮询定时器（不取消 Core 侧共享估算任务）。
  useEffect(
    () => () => {
      if (refreshTimer.current != null) window.clearInterval(refreshTimer.current);
    },
    [],
  );

  const classRows = useMemo<ClassRow[]>(() => {
    const classes = snapshot?.classes ?? [];
    return classes.map((entry) => {
      const definition = dataClasses.find((item) => item.targetId === entry.targetId);
      const target = policy?.targets.find((item) => item.targetId === entry.targetId);
      return {
        ...entry,
        manualAllowed: definition?.manualCleanupAllowed ?? false,
        showInSelector: definition?.manualCleanupAllowed ?? false,
        description: definition?.description ?? '',
        requiresRollup: definition?.requiresRollupBeforeAutomatic ?? false,
        policyEnabled: target?.enabled,
        policyRetentionDays: target?.retentionDays,
        // 目录缺项时两个推导都失败关闭：不自动清理、保留天数未设置。
        autoCleanup: resolveAutoCleanupState(definition ?? {}),
        retention: resolveEffectiveRetention({
          policyRetentionDays: target?.retentionDays,
          defaultRetentionDays: definition?.defaultRetentionDays ?? target?.defaultRetentionDays,
          minRetentionDays: definition?.minRetentionDays ?? target?.minRetentionDays,
          maxRetentionDays: definition?.maxRetentionDays ?? target?.maxRetentionDays,
        }),
      };
    });
  }, [snapshot, dataClasses, policy]);

  const selectableRows = useMemo(
    () => classRows.filter((row) => row.manualAllowed),
    [classRows],
  );

  const databaseTotal = useMemo(
    () => (snapshot?.databases ?? []).reduce((sum, db) => sum + db.totalBytes, 0),
    [snapshot],
  );
  const reusableTotal = useMemo(
    () => (snapshot?.databases ?? []).reduce((sum, db) => sum + db.reusableFreeBytes, 0),
    [snapshot],
  );
  // 顶部合计里除了 SQLite 库，还含非数据库目录（如全文检索索引）。后端只在 SQLite 打开
  // 成功时才填 pageSizeBytes（见 StorageInventorySampler.ReadDatabaseSnapshot 的 catch 分支），
  // 因此 pageSizeBytes <= 0 的条目就是「不是按页计量的目录」。
  const nonDatabaseTotal = useMemo(
    () =>
      (snapshot?.databases ?? [])
        .filter((db) => db.pageSizeBytes <= 0)
        .reduce((sum, db) => sum + db.totalBytes, 0),
    [snapshot],
  );
  const classBytesTotal = useMemo(
    () => classRows.reduce((sum, row) => sum + (row.estimatedBytes ?? 0), 0),
    [classRows],
  );
  const classNames = useMemo(
    () => Object.fromEntries(classRows.map((row) => [row.targetId, row.displayName])),
    [classRows],
  );

  // ── 首屏派生数据：只把已有事实映射成图形入参，不引入新口径 ──────────
  const treemapData = useMemo<StorageTreemapDatum[]>(
    () =>
      (snapshot?.classes ?? []).map((row) => ({
        targetId: row.targetId,
        label: row.displayName,
        bytes: row.estimatedBytes ?? 0,
      })),
    [snapshot],
  );

  const stackedRows = useMemo<StackedBarRow[]>(
    () =>
      databaseSpaces
        .filter((space) => space.exists)
        .map((space) => ({
          key: space.key,
          label: space.displayName,
          // 文件大小只用于右侧绝对值；占比分母取各段之和（见 StorageStackedBars 注释）。
          totalBytes: space.fileBytes,
          segments: space.dataClasses.map((dataClass) => ({
            targetId: dataClass.targetId,
            label: dataClass.displayName,
            bytes: dataClass.bytes,
          })),
        })),
    [databaseSpaces],
  );

  // 冗余索引是目录里唯一有 targetId 的「可删对象」；缺项时按「暂无」表达，不伪造成 0。
  const redundantIndexBytes = useMemo(() => {
    const entry = classRows.find((row) => row.targetId === 'storage.redundant-indexes');
    return entry?.estimatedBytes ?? null;
  }, [classRows]);

  // 第 3 行只表达「三种性质不同的可回收」，各自带自己的口径，不做四则运算合并。
  const reclaimSegments = useMemo<ReclaimSegment[]>(
    () => [
      {
        key: 'vacuum',
        label: 'VACUUM 可回收',
        bytes: reusableTotal,
        color: '#fa8c16',
        note: '库内可复用页；VACUUM 后才归还文件系统（不可再清理）',
        // 空闲页不是可删数据：没有任何清理 targetId 能回收它，只能靠 VACUUM。
        previewTargets: [],
      },
      {
        key: 'redundant-indexes',
        label: '冗余索引',
        bytes: redundantIndexBytes ?? 0,
        color: '#ffc53d',
        note:
          redundantIndexBytes == null
            ? '暂无：当前快照没有该分类的估算值'
            : '重复/孤儿索引，删除索引即可回收',
        // 冗余索引是目录里唯一有独立 targetId 的可删对象。
        previewTargets: redundantIndexBytes == null ? [] : ['storage.redundant-indexes'],
      },
      {
        key: 'retention',
        label: '保留策略可清理',
        bytes: classBytesTotal,
        color: '#13c2c2',
        note: '分类估算合计，含仅受策略治理的证据类',
        // 本段字节是**全部**分类合计，但可手动预览的只有目录允许手删的那些；
        // 两者口径不同是如实的（证据类只能受策略治理），不在这里合并或伪装。
        previewTargets: selectableRows.map((row) => row.targetId),
      },
    ],
    [reusableTotal, redundantIndexBytes, classBytesTotal, selectableRows],
  );

  const reclaimTotal = useMemo(
    () => reclaimSegments.reduce((sum, segment) => sum + segment.bytes, 0),
    [reclaimSegments],
  );

  const refreshEstimates = async () => {
    try {
      await requestInventoryRefresh();
      message.success('已提交后台刷新估算请求（页面继续使用当前快照）');
      if (refreshTimer.current != null) window.clearInterval(refreshTimer.current);
      let attempts = 0;
      refreshTimer.current = window.setInterval(async () => {
        attempts++;
        await loadOverview();
        if (attempts > 150 || snapshot?.isRefreshing === false) {
          if (refreshTimer.current != null) window.clearInterval(refreshTimer.current);
          refreshTimer.current = null;
        }
      }, 2_000);
    } catch {
      message.error('提交刷新请求失败');
    }
  };

  const startPreview = async (explicitTargets?: string[]) => {
    // 首屏三段条的按钮带自己的 targetId；可清理页签内的按钮仍用勾选状态。
    const targetIds = explicitTargets ?? selectedTargets;
    if (targetIds.length === 0) {
      message.warning('请先选择要清理的数据类型');
      return;
    }

    setPreviewSubmitting(true);
    setPreviewJobId(null);
    try {
      const created = await createCleanupPreview({ targetIds, olderThanDays });
      if (!created.hasCandidates) {
        message.info('所选类型在该截止时间之前没有可清理数据');
      }
      setPreviewOpen(true);
      setPreview(created);
    } catch (error) {
      const detail = (error as { data?: { detail?: string }; message?: string }) ?? {};
      message.error(detail.data?.detail ?? detail.message ?? '生成预览失败');
    } finally {
      setPreviewSubmitting(false);
    }
  };

  const activeJobs = jobs.filter((job) =>
    ['Queued', 'Running', 'PausedBusy', 'NeedsConfirmation', 'Cancelling'].includes(job.status),
  );

  const reportColumns: ColumnsType<ClassRow> = [
    {
      title: '类型',
      dataIndex: 'displayName',
      width: 150,
      render: (name: string, row) => (
        <Space size={8}>
          <span aria-hidden style={{ width: 10, height: 10, borderRadius: 2, background: classColor(row.targetId), display: 'inline-block' }} />
          <span>{name}</span>
        </Space>
      ),
    },
    {
      title: '约占空间',
      dataIndex: 'estimatedBytes',
      width: 110,
      align: 'right',
      render: (bytes: number | null, row) =>
        row.estimateState === 'Updated' && bytes != null ? `≈${formatBytes(bytes)}` : '估算中',
    },
    {
      title: '占类合计',
      dataIndex: 'share',
      width: 90,
      align: 'right',
      render: (_: unknown, row) =>
        classBytesTotal > 0 && row.estimatedBytes
          ? `${((row.estimatedBytes / classBytesTotal) * 100).toFixed(1)}%`
          : '—',
    },
    {
      title: '约记录数',
      dataIndex: 'estimatedRows',
      width: 110,
      align: 'right',
      render: (rows: number | null, entry) =>
        entry.targetId.endsWith('.verbose') || entry.targetId.endsWith('.error')
          ? rows == null
            ? '—'
            : `≈${formatCount(rows)} 文件`
          : rows == null
            ? '—'
            : `≈${formatCount(rows)}`,
    },
    {
      title: '最早数据',
      dataIndex: 'oldestUtc',
      width: 150,
      render: (value: string | null) => formatUtc(value),
    },
    {
      title: '保留策略',
      dataIndex: 'policy',
      width: 130,
      render: (_: unknown, row) => {
        const policyLabel =
          row.policyEnabled == null ? '策略未配置' : row.policyEnabled ? '策略启用' : '策略关闭';
        const sourceLabel =
          row.retention.source === 'policy'
            ? '生效值取自策略'
            : row.retention.source === 'default'
              ? '策略未设值，回落目录默认值'
              : '策略与默认值均未设置';
        const hint = [policyLabel, sourceLabel, row.retention.rangeLabel]
          .filter(Boolean)
          .join('｜');
        return (
          <Tooltip title={hint}>
            <span>{row.retention.label}</span>
          </Tooltip>
        );
      },
    },
    {
      title: '自动清理状态',
      dataIndex: 'autoCleanup',
      width: 200,
      render: (_: unknown, row) => (
        <Tooltip title={AUTO_CLEANUP_HINT[row.autoCleanup.kind]}>
          <Tag color={AUTO_CLEANUP_TAG_COLOR[row.autoCleanup.tone]}>{row.autoCleanup.label}</Tag>
        </Tooltip>
      ),
    },
    {
      title: '更新于',
      dataIndex: 'updatedAtUtc',
      width: 150,
      render: (value: string | null) => formatUtc(value),
    },
    {
      title: '状态',
      dataIndex: 'estimateState',
      width: 90,
      render: (state: string) =>
        state === 'Updated' ? (
          <Tag color="success">已更新</Tag>
        ) : state === 'Estimating' ? (
          <Tag color="processing">估算中</Tag>
        ) : (
          <Tag>暂不可用</Tag>
        ),
    },
  ];

  const jobColumns: ColumnsType<StorageCleanupJob> = [
    {
      title: '时间',
      dataIndex: 'createdAtUtc',
      width: 150,
      render: (value: string) => formatUtc(value),
    },
    {
      title: '触发',
      dataIndex: 'trigger',
      width: 80,
      render: (trigger: string) =>
        trigger === 'automatic' ? '自动' : trigger === 'manual' ? '手动' : '旧端点',
    },
    {
      title: '数据类型',
      dataIndex: 'targetIds',
      render: (ids: string[]) =>
        ids.map((id) => classNames[id] ?? id).join('、') || '—',
    },
    {
      title: '状态',
      dataIndex: 'status',
      width: 100,
      render: (status: StorageCleanupJobStatus) => {
        const tag = JOB_STATUS_TAG[status];
        return <Tag color={tag.color}>{tag.text}</Tag>;
      },
    },
    {
      title: '进度（已处理/发现）',
      dataIndex: 'progress',
      width: 220,
      render: (_: unknown, job) => {
        const total = Math.max(job.progress.discoveredRows, job.progress.processedRows, 1);
        return (
          <Space direction="vertical" size={2} style={{ width: '100%' }}>
            <Progress
              size="small"
              percent={Math.round((job.progress.processedRows / total) * 100)}
              status={job.status === 'Failed' ? 'exception' : undefined}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {formatCount(job.progress.processedRows)} / {formatCount(job.progress.discoveredRows)}｜删{' '}
              {formatCount(job.progress.deletedRows)}｜清字段 {formatCount(job.progress.clearedRows)}
              {job.progress.deletedFiles ? `｜文件 ${formatCount(job.progress.deletedFiles)}` : ''}
            </Typography.Text>
          </Space>
        );
      },
    },
    {
      title: '库内可复用',
      dataIndex: 'reusable',
      width: 110,
      align: 'right',
      render: (_: unknown, job) => `≈${formatBytes(job.progress.reusableBytesEstimate)}`,
    },
    {
      title: '操作',
      dataIndex: 'actions',
      width: 150,
      render: (_: unknown, job) => (
        <Space size={4}>
          {['Queued', 'Running', 'PausedBusy'].includes(job.status) ? (
            <Popconfirm
              title="确认取消？"
              description="取消在当前小批事务完成后生效。"
              onConfirm={async () => {
                try {
                  await cancelCleanupJob(job.jobId);
                  message.success('已请求取消（批次边界生效）');
                  void loadJobs();
                } catch {
                  message.error('取消请求失败');
                }
              }}
            >
              <Button size="small" danger>
                取消
              </Button>
            </Popconfirm>
          ) : null}
          {job.status === 'NeedsConfirmation' ? (
            <Popconfirm
              title="确认继续处理超出预算的数据？"
              onConfirm={async () => {
                try {
                  await confirmCleanupJob(job.jobId);
                  message.success('已确认继续');
                  void loadJobs();
                } catch {
                  message.error('确认失败');
                }
              }}
            >
              <Button size="small" type="primary">
                确认继续
              </Button>
            </Popconfirm>
          ) : null}
          <Button
            size="small"
            type="link"
            onClick={async () => {
              const events = await getCleanupJobEvents(job.jobId);
              Modal.info({
                title: '作业事件',
                width: 640,
                content: (
                  <ul style={{ maxHeight: 360, overflow: 'auto', paddingLeft: 18, fontSize: 12 }}>
                    {events.length === 0 ? <li>暂无事件</li> : null}
                    {events.map((event, index) => (
                      <li key={`${event.timestampUtc}-${index}`}>
                        {formatUtc(event.timestampUtc)}｜{event.kind}
                        {event.counters
                          ? `｜${Object.entries(event.counters)
                              .map(([key, value]) => `${key}=${value}`)
                              .join(' ')}`
                          : ''}
                      </li>
                    ))}
                  </ul>
                ),
              });
            }}
          >
            详情
          </Button>
        </Space>
      ),
    },
  ];

  return (
    <PageContainer
      header={{
        title: '存储管理',
        subTitle: '遥测与调试数据治理（ADR-076）',
        extra: [
          <Button
            key="refresh"
            icon={<ReloadOutlined />}
            onClick={refreshEstimates}
            loading={snapshot?.isRefreshing ?? false}
          >
            刷新估算
          </Button>,
          <Button key="policy" icon={<SettingOutlined />} onClick={() => setPolicyOpen(true)}>
            策略设置
          </Button>,
        ],
      }}
    >
      <Spin spinning={loading}>
        {snapshot?.warnings?.length ? (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 12 }}
            message={`快照更新于 ${formatUtc(snapshot.updatedAtUtc)}（revision ${snapshot.revision}）`}
            description={
              <ul style={{ margin: 0, paddingLeft: 18 }}>
                {snapshot.warnings.slice(0, 5).map((warning) => (
                  <li key={warning}>{warning}</li>
                ))}
              </ul>
            }
          />
        ) : null}

        <div data-testid="storage-first-screen">
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            快照更新于 {formatUtc(snapshot?.updatedAtUtc)}｜状态：
            {snapshot?.isRefreshing ? '后台估算中' : '空闲'}
            {snapshot && snapshot.revision > 0 ? `｜revision ${snapshot.revision}` : ''}
          </Typography.Text>

          {/* 行1：指标条 —— 数字留在正文，口径说明只挂在 title 上 */}
          <Row gutter={[12, 12]} style={{ marginTop: 8 }}>
            <Col xs={12} md={6}>
              <MetricCell
                testId="metric-total"
                label="总占用"
                value={formatBytes(databaseTotal)}
                note="含非数据库目录"
                hint={HEADLINE_TOTAL_NOTE}
              />
            </Col>
            <Col xs={12} md={6}>
              <MetricCell
                testId="metric-databases"
                label="数据库合计"
                value={formatBytes(databaseTotal - nonDatabaseTotal)}
                note="主文件 + WAL + 共享内存"
                hint={DATABASE_ONLY_NOTE}
              />
            </Col>
            <Col xs={12} md={6}>
              <MetricCell
                testId="metric-non-database"
                label="非数据库目录"
                value={formatBytes(nonDatabaseTotal)}
                note="全文检索索引等"
                hint={HEADLINE_TOTAL_NOTE}
              />
            </Col>
            <Col xs={12} md={6}>
              <MetricCell
                testId="metric-reusable"
                label="可回收"
                value={formatBytes(reusableTotal)}
                note="库内可复用页"
                hint={REUSABLE_PAGE_NOTE}
              />
            </Col>
          </Row>

          {/* 行2：左矩形树图（分类面积）+ 右每库堆叠条（库内构成） */}
          <Row gutter={[12, 12]} style={{ marginTop: 12 }}>
            <Col xs={24} lg={12}>
              <Card
                size="small"
                title="分类占用（矩形树图）"
                extra={<span title={TREEMAP_NOTE}>面积＝字节</span>}
              >
                <div style={{ width: '100%', overflowX: 'auto' }}>
                  {/* 高度固定：首屏总高度要可预测，图形不随数据条数变化。 */}
                  <StorageTreemap data={treemapData} width={560} height={240} />
                </div>
              </Card>
            </Col>
            <Col xs={24} lg={12}>
              <Card
                size="small"
                title="各库构成（按数据类）"
                extra={<span title={STACKED_NOTE}>长度＝该库占比</span>}
              >
                <StorageStackedBars rows={stackedRows} />
              </Card>
            </Col>
          </Row>

          {/* 行3：可回收三段条 —— 三项性质不同，各自带口径，不合并成一个「可清理额度」 */}
          <Card
            size="small"
            style={{ marginTop: 12 }}
            title="可回收空间（三项性质不同）"
            extra={<span title={CLEANABLE_NOTE + RECLAIM_NOTE}>口径见悬浮</span>}
          >
            <div data-testid="reclaim-strip" style={{ display: 'grid', gap: 8 }}>
              <div
                aria-hidden
                style={{
                  display: 'flex',
                  height: 10,
                  borderRadius: 2,
                  overflow: 'hidden',
                  background: '#f0f0f0',
                }}
              >
                {reclaimTotal <= 0
                  ? null
                  : reclaimSegments.map((segment) => (
                      <div
                        key={segment.key}
                        data-testid="reclaim-segment"
                        data-segment-key={segment.key}
                        title={`${segment.label} · ≈${formatBytes(segment.bytes)}`}
                        style={{
                          width: `${(segment.bytes / reclaimTotal) * 100}%`,
                          background: segment.color,
                        }}
                      />
                    ))}
              </div>
              <Row gutter={[12, 8]}>
                {reclaimSegments.map((segment) => (
                  <Col xs={24} md={8} key={segment.key}>
                    <div data-testid="reclaim-item" data-segment-key={segment.key}>
                      <Space size={6} align="baseline">
                        <span
                          aria-hidden
                          style={{
                            width: 8,
                            height: 8,
                            borderRadius: 2,
                            background: segment.color,
                            display: 'inline-block',
                          }}
                        />
                        <Typography.Text strong>{segment.label}</Typography.Text>
                        <Typography.Text type="secondary">≈{formatBytes(segment.bytes)}</Typography.Text>
                      </Space>
                      <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block' }}>
                        {segment.note}
                      </Typography.Text>
                      {segment.previewTargets.length > 0 ? (
                        <Button
                          size="small"
                          type="link"
                          style={{ padding: 0, height: 'auto' }}
                          data-testid="reclaim-preview"
                          data-segment-key={segment.key}
                          loading={previewSubmitting}
                          onClick={() => {
                            // 只创建**预览**（绝不执行）：把该段的 targetId 送进既有 PreviewCleanup 流程，
                            // 同时同步勾选状态，避开「预览了但可清理页签里没勾」的错觉。
                            setSelectedTargets(segment.previewTargets);
                            void startPreview(segment.previewTargets);
                          }}
                        >
                          预览清理
                        </Button>
                      ) : (
                        <Typography.Text
                          type="secondary"
                          style={{ fontSize: 12 }}
                          data-testid="reclaim-not-cleanable"
                          data-segment-key={segment.key}
                        >
                          不在清理范围
                        </Typography.Text>
                      )}
                    </div>
                  </Col>
                ))}
              </Row>
            </div>
          </Card>
        </div>

        {/* 其余内容收进 Tabs：默认只渲染当前页签，首屏高度不再被它们撑开 */}
        <Tabs
          style={{ marginTop: 12 }}
          destroyOnHidden
          items={[
            {
              key: 'trend',
              label: '趋势',
              children: (
                <div data-testid="storage-tab-trend">
                  <Row gutter={[12, 12]}>
                    <Col xs={24} lg={10}>
                      <Card size="small" title="分类占比（估算）">
                        <StorageClassDonut classes={snapshot?.classes ?? []} />
                      </Card>
                    </Col>
                    <Col xs={24} lg={14}>
                      <Card
                        size="small"
                        title={`近 ${trendDays} 天存储趋势（堆叠面积，按历史快照聚合）`}
                        extra={
                          <Segmented
                            size="small"
                            value={trendDays}
                            options={[
                              { label: '7 天', value: 7 },
                              { label: '30 天', value: 30 },
                              { label: '90 天', value: 90 },
                            ]}
                            onChange={(value) => setTrendDays(value as number)}
                          />
                        }
                      >
                        <StorageTrendChart points={trend} days={trendDays} classNames={classNames} />
                      </Card>
                    </Col>
                  </Row>
                </div>
              ),
            },
            {
              key: 'report',
              label: '分类报表',
              children: (
                <div data-testid="storage-tab-report">
                  <Card size="small" title="分类统计报表">
                    <Typography.Text
                      type="secondary"
                      style={{ fontSize: 12, display: 'block', marginBottom: 8 }}
                    >
                      {AUTO_CLEANUP_SCHEDULING_NOTE}
                    </Typography.Text>
                    <Table<ClassRow>
                      size="small"
                      rowKey="targetId"
                      pagination={false}
                      dataSource={classRows}
                      columns={reportColumns}
                    />
                  </Card>
                </div>
              ),
            },
            {
              key: 'database',
              label: '数据库占用',
              children: (
                <div data-testid="storage-tab-database">
                  <Space direction="vertical" size={12} style={{ width: '100%' }}>
                    <Card size="small" title="数据库文件大小（含 WAL）">
                      <Table
                        size="small"
                        pagination={false}
                        rowKey="databaseId"
                        dataSource={snapshot?.databases ?? []}
                        columns={[
                          { title: '数据库', dataIndex: 'displayName' },
                          {
                            title: (
                              <Tooltip title="该库主文件 + WAL + 共享内存；首屏「总占用」是这些库与非数据库目录的合计">
                                <span>大小（含 WAL）</span>
                              </Tooltip>
                            ),
                            dataIndex: 'totalBytes',
                            align: 'right' as const,
                            render: (value: number) => formatBytes(value),
                          },
                          {
                            title: '可复用页',
                            dataIndex: 'reusableFreeBytes',
                            align: 'right' as const,
                            render: (value: number) => formatBytes(value),
                          },
                        ]}
                      />
                    </Card>
                    <DatabaseSpacePanel
                      databases={snapshot?.databases ?? []}
                      headlineTotalBytes={databaseTotal}
                    />
                  </Space>
                </div>
              ),
            },
            {
              key: 'cleanup',
              label: '可清理',
              children: (
                <div data-testid="storage-tab-cleanup">
                  <Space direction="vertical" size={12} style={{ width: '100%' }}>
                    <Card
                      size="small"
                      title="可清理数据"
                      extra={
                        <Space>
                          <Select
                            size="small"
                            value={olderThanDays}
                            options={OLDER_THAN_OPTIONS}
                            onChange={setOlderThanDays}
                            style={{ width: 120 }}
                          />
                          <Button
                            size="small"
                            type="primary"
                            danger
                            icon={<ClearOutlined />}
                            loading={previewSubmitting}
                            disabled={selectedTargets.length === 0}
                            // 不能直接传 startPreview：它现在多了一个可选 targetId 参数，
                            // 而 onClick 会把 MouseEvent 当成第一个实参传进来。
                            onClick={() => void startPreview()}
                          >
                            预览所选（{selectedTargets.length}）
                          </Button>
                        </Space>
                      }
                    >
                      <Space direction="vertical" size={6} style={{ width: '100%' }}>
                        {selectableRows.length === 0 ? (
                          <Typography.Text type="secondary">等待目录与快照加载…</Typography.Text>
                        ) : null}
                        {selectableRows.map((row) => (
                          <div
                            key={row.targetId}
                            style={{ display: 'flex', alignItems: 'center', gap: 8 }}
                          >
                            <Checkbox
                              checked={selectedTargets.includes(row.targetId)}
                              onChange={(event) => {
                                setSelectedTargets((previous) =>
                                  event.target.checked
                                    ? [...previous, row.targetId]
                                    : previous.filter((id) => id !== row.targetId),
                                );
                              }}
                            >
                              <Space size={8}>
                                <span
                                  aria-hidden
                                  style={{
                                    width: 10,
                                    height: 10,
                                    borderRadius: 2,
                                    background: classColor(row.targetId),
                                    display: 'inline-block',
                                  }}
                                />
                                {row.displayName}
                              </Space>
                            </Checkbox>
                            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                              ≈{formatBytes(row.estimatedBytes)}｜≈{formatCount(row.estimatedRows)}{' '}
                              {row.targetId.includes('logs') ? '文件' : '行'}｜最早{' '}
                              {formatUtc(row.oldestUtc)}
                            </Typography.Text>
                            {row.requiresRollup ? (
                              <Tag color="orange">自动清理默认关闭（聚合未实现）</Tag>
                            ) : null}
                          </div>
                        ))}
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          清理条件统一为 eventTime &lt; 截止时间（等于截止时间的数据保留）。
                        </Typography.Text>
                      </Space>
                    </Card>
                    <Card
                      size="small"
                      title={
                        <Space>
                          <DatabaseOutlined /> 清理作业
                          {activeJobs.length ? (
                            <Tag color="processing">{activeJobs.length} 个进行中</Tag>
                          ) : null}
                        </Space>
                      }
                      extra={<EyeOutlined />}
                    >
                      <Table<StorageCleanupJob>
                        size="small"
                        rowKey="jobId"
                        pagination={{ pageSize: 10, hideOnSinglePage: true }}
                        dataSource={jobs}
                        columns={jobColumns}
                      />
                      {previewJobId ? (
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          最新作业：{previewJobId}
                        </Typography.Text>
                      ) : null}
                    </Card>
                  </Space>
                </div>
              ),
            },
            {
              key: 'protected',
              label: '受保护',
              children: (
                <div data-testid="storage-tab-protected">
                  <Card
                    size="small"
                    title={
                      <Space>
                        <SafetyCertificateOutlined /> 受保护数据（不可选择）
                      </Space>
                    }
                  >
                    <Space direction="vertical" size={2}>
                      {protectedObjects.map((entry) => (
                        <Typography.Text key={entry} type="secondary" style={{ fontSize: 12 }}>
                          ● {entry}
                        </Typography.Text>
                      ))}
                      {classRows
                        .filter((row) => !row.manualAllowed)
                        .map((row) => (
                          <Typography.Text key={row.targetId} style={{ fontSize: 12 }}>
                            ● {row.displayName}：仅按独立证据保留策略治理（当前{' '}
                            {row.policyEnabled ? `${row.policyRetentionDays} 天自动归档裁剪` : '关闭'}）
                          </Typography.Text>
                        ))}
                    </Space>
                  </Card>
                </div>
              ),
            },
          ]}
        />
      </Spin>

      <CleanupPreviewModal
        open={previewOpen}
        preview={preview}
        submitting={previewSubmitting}
        onClose={() => setPreviewOpen(false)}
        onConfirmed={(jobId) => {
          setPreviewJobId(jobId);
          void loadJobs();
          void loadOverview();
        }}
      />

      <StoragePolicyDrawer
        open={policyOpen}
        policy={policy}
        onClose={() => setPolicyOpen(false)}
        onSaved={() => {
          void loadPolicy();
        }}
      />
    </PageContainer>
  );
};

export default StoragePage;
