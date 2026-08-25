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
  Tag,
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
  StorageInventoryClass,
  StorageInventorySnapshot,
  StorageInventoryTrendPoint,
  StorageRetentionPolicy,
} from './types';
import { StorageClassDonut, StorageTrendChart, classColor } from './StorageOverviewCharts';
import { StoragePolicyDrawer } from './StoragePolicyDrawer';
import { CleanupPreviewModal } from './CleanupPreviewModal';

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

interface ClassRow extends StorageInventoryClass {
  policyEnabled?: boolean;
  policyRetentionDays?: number | null;
  manualAllowed: boolean;
  showInSelector: boolean;
  description: string;
  requiresRollup: boolean;
}

const StoragePage: React.FC = () => {
  const [snapshot, setSnapshot] = useState<StorageInventorySnapshot | null>(null);
  const [dataClasses, setDataClasses] = useState<StorageDataClass[]>([]);
  const [policy, setPolicy] = useState<StorageRetentionPolicy | null>(null);
  const [protectedObjects, setProtectedObjects] = useState<string[]>([]);
  const [trend, setTrend] = useState<StorageInventoryTrendPoint[]>([]);
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
  const classBytesTotal = useMemo(
    () => classRows.reduce((sum, row) => sum + (row.estimatedBytes ?? 0), 0),
    [classRows],
  );
  const classNames = useMemo(
    () => Object.fromEntries(classRows.map((row) => [row.targetId, row.displayName])),
    [classRows],
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

  const startPreview = async () => {
    if (selectedTargets.length === 0) {
      message.warning('请先选择要清理的数据类型');
      return;
    }

    setPreviewSubmitting(true);
    setPreviewJobId(null);
    try {
      const created = await createCleanupPreview({ targetIds: selectedTargets, olderThanDays });
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
      render: (_: unknown, row) =>
        row.policyEnabled == null
          ? '—'
          : row.policyEnabled
            ? `${row.policyRetentionDays ?? '—'} 天`
            : '自动关闭',
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

        <Row gutter={[12, 12]}>
          <Col xs={24} lg={12}>
            <Card size="small" title="存储空间总览">
              <Space direction="vertical" size={4} style={{ width: '100%' }}>
                <Typography.Text strong style={{ fontSize: 20 }}>
                  Pudding 数据约 {formatBytes(databaseTotal)}
                </Typography.Text>
                <Typography.Text type="secondary">
                  更新于 {formatUtc(snapshot?.updatedAtUtc)}｜状态：
                  {snapshot?.isRefreshing ? '后台估算中' : '空闲'}
                  {snapshot && snapshot.revision > 0 ? `｜revision ${snapshot.revision}` : ''}
                </Typography.Text>
                <Typography.Text type="secondary">
                  数据库主文件 + WAL 合计 {formatBytes(databaseTotal)}｜库内可复用页{' '}
                  {formatBytes(reusableTotal)}｜可清理分类估算合计 ≈{formatBytes(classBytesTotal)}
                </Typography.Text>
                <Table
                  size="small"
                  pagination={false}
                  rowKey="databaseId"
                  dataSource={snapshot?.databases ?? []}
                  columns={[
                    { title: '数据库', dataIndex: 'displayName' },
                    {
                      title: '大小',
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
              </Space>
            </Card>
          </Col>
          <Col xs={24} lg={12}>
            <Card
              size="small"
              title="分类占比（估算）"
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
              <StorageClassDonut classes={snapshot?.classes ?? []} />
            </Card>
          </Col>
          <Col span={24}>
            <Card size="small" title={`近 ${trendDays} 天存储趋势（堆叠面积，按历史快照聚合）`}>
              <StorageTrendChart points={trend} days={trendDays} classNames={classNames} />
            </Card>
          </Col>
          <Col span={24}>
            <Card size="small" title="分类统计报表" extra={<Typography.Text type="secondary">全部为估算约数（≈）</Typography.Text>}>
              <Table<ClassRow>
                size="small"
                rowKey="targetId"
                pagination={false}
                dataSource={classRows}
                columns={reportColumns}
              />
            </Card>
          </Col>
          <Col xs={24} lg={14}>
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
                    onClick={startPreview}
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
                  <div key={row.targetId} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
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
                        <span aria-hidden style={{ width: 10, height: 10, borderRadius: 2, background: classColor(row.targetId), display: 'inline-block' }} />
                        {row.displayName}
                      </Space>
                    </Checkbox>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      ≈{formatBytes(row.estimatedBytes)}｜≈{formatCount(row.estimatedRows)}{' '}
                      {row.targetId.includes('logs') ? '文件' : '行'}｜最早 {formatUtc(row.oldestUtc)}
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
          </Col>
          <Col xs={24} lg={10}>
            <Card size="small" title={<Space><SafetyCertificateOutlined /> 受保护数据（不可选择）</Space>}>
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
          </Col>
          <Col span={24}>
            <Card
              size="small"
              title={
                <Space>
                  <DatabaseOutlined /> 清理作业
                  {activeJobs.length ? <Tag color="processing">{activeJobs.length} 个进行中</Tag> : null}
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
          </Col>
        </Row>
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
