import React, { useCallback, useEffect, useState } from 'react';
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
import { DatabaseOutlined, ReloadOutlined } from '@ant-design/icons';
import { PageContainer } from '@ant-design/pro-components';
import {
  formatJobsReason,
  formatTriStateBoolean,
  formatTriStateBytes,
  formatTriStateCount,
  formatTriStateDurationMs,
  formatTriStateText,
  formatTriStateTimestamp,
  getIndexStatus,
} from './api';
import type {
  FullTextIndexJobStatus,
  FullTextIndexScopeStatus,
  FullTextIndexStatusSnapshot,
} from './types';

// ── Slice B：全文索引（Lucene 全文检索）状态**只读**面板 ─────────────
// 数据源：GET /api/admin/index/status（IndexAdminController，Admin JWT）。
//
// 两条纪律：
// 1) R4/D3 三态：后端用 `null` 表达「未知」，与 `false` / `0` 是**不同事实**。
//    本页面所有值一律经 ./api 的三态纯函数渲染，禁止 `?? 0` / `|| '否'` 式折叠。
// 2) 纯只读：本页面没有任何写操作能力（不触发供给、不重建、不删除）。
//
// 轮询照抄 src/pages/storage/index.tsx 的既有用法（window.setInterval + 卸载清理），
// 刻意**不**引入 src/pages/chat/** 的任何模块，以保证该目录零接触。

/** 轮询间隔：与 storage 页同量级（30s），不自创激进间隔。 */
const POLL_INTERVAL_MS = 30_000;

/** 三态标签：未知(null) 用 warning 色以示「不可知」，与 false 的 default 区分。 */
function triStateTag(value: boolean | null | undefined): React.ReactNode {
  const text = formatTriStateBoolean(value);
  const color = value === null || value === undefined ? 'warning' : value ? 'success' : 'default';
  return <Tag color={color}>{text}</Tag>;
}

/** 已受理 scope 的展示（数组原值，逐条列出）。 */
function renderStringList(values: string[]): React.ReactNode {
  if (values.length === 0) {
    return <Typography.Text type="secondary">（空数组：无此项）</Typography.Text>;
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

const IndexStatusPage: React.FC = () => {
  const [snapshot, setSnapshot] = useState<FullTextIndexStatusSnapshot | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

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

  const fullText = snapshot?.fullText ?? null;
  const scopes = fullText?.scopes ?? [];
  const jobs = fullText?.jobs ?? [];

  const scopeColumns: ColumnsType<FullTextIndexScopeStatus> = [
    {
      title: 'scope 路径（scopePath）',
      dataIndex: 'scopePath',
      key: 'scopePath',
      width: 260,
      render: (value: string) => (
        <Typography.Text code style={{ fontSize: 12, wordBreak: 'break-all' }}>
          {value}
        </Typography.Text>
      ),
    },
    {
      title: '目录存在（scopeExists）',
      dataIndex: 'scopeExists',
      key: 'scopeExists',
      width: 120,
      render: (value: boolean) => <Tag color={value ? 'success' : 'default'}>{formatTriStateBoolean(value)}</Tag>,
    },
    {
      title: '已有索引（hasIndex）',
      dataIndex: 'hasIndex',
      key: 'hasIndex',
      width: 110,
      render: (value: boolean | null) => triStateTag(value),
    },
    {
      title: '索引目录（indexDirectory）',
      dataIndex: 'indexDirectory',
      key: 'indexDirectory',
      width: 240,
      render: (value: string | null) =>
        value === null ? (
          <Tag color="warning">{formatTriStateText(value)}</Tag>
        ) : (
          <Typography.Text code style={{ fontSize: 12, wordBreak: 'break-all' }}>
            {value}
          </Typography.Text>
        ),
    },
    {
      title: '索引目录存在（indexDirectoryExists）',
      dataIndex: 'indexDirectoryExists',
      key: 'indexDirectoryExists',
      width: 130,
      render: (value: boolean | null) => triStateTag(value),
    },
    {
      title: '条目数（indexEntryCount）',
      dataIndex: 'indexEntryCount',
      key: 'indexEntryCount',
      width: 130,
      align: 'right',
      render: (value: number | null) => formatTriStateCount(value),
    },
    {
      title: '字节（indexBytes）',
      dataIndex: 'indexBytes',
      key: 'indexBytes',
      width: 130,
      align: 'right',
      render: (value: number | null) => formatTriStateBytes(value),
    },
    {
      title: '最后写入（indexDirectoryLastWriteUtc）',
      dataIndex: 'indexDirectoryLastWriteUtc',
      key: 'indexDirectoryLastWriteUtc',
      width: 180,
      render: (value: string | null) => formatTriStateTimestamp(value),
    },
  ];

  const jobColumns: ColumnsType<FullTextIndexJobStatus> = [
    {
      title: 'jobId',
      dataIndex: 'jobId',
      key: 'jobId',
      width: 200,
      render: (value: string) => (
        <Typography.Text code style={{ fontSize: 12, wordBreak: 'break-all' }}>
          {value}
        </Typography.Text>
      ),
    },
    {
      title: '状态（state）',
      dataIndex: 'state',
      key: 'state',
      width: 120,
      render: (value: string) => <Tag color="processing">{value}</Tag>,
    },
    {
      title: '阶段（phase）',
      dataIndex: 'phase',
      key: 'phase',
      width: 120,
      render: (value: string) => <Tag>{value}</Tag>,
    },
    {
      title: '开始（startedAt）',
      dataIndex: 'startedAt',
      key: 'startedAt',
      width: 170,
      render: (value: string) => formatTriStateTimestamp(value),
    },
    {
      title: '结束（finishedAt）',
      dataIndex: 'finishedAt',
      key: 'finishedAt',
      width: 170,
      render: (value: string | null) => formatTriStateTimestamp(value),
    },
    {
      title: '耗时（elapsedMs）',
      dataIndex: 'elapsedMs',
      key: 'elapsedMs',
      width: 120,
      align: 'right',
      render: (value: number | null) => formatTriStateDurationMs(value),
    },
    {
      title: '可索引文件数（indexedFileCount）',
      dataIndex: 'indexedFileCount',
      key: 'indexedFileCount',
      width: 150,
      align: 'right',
      render: (value: number) => formatTriStateCount(value),
    },
    {
      title: '语料字节（totalBytes）',
      dataIndex: 'totalBytes',
      key: 'totalBytes',
      width: 130,
      align: 'right',
      render: (value: number) => formatTriStateBytes(value),
    },
    {
      title: '说明（message）',
      dataIndex: 'message',
      key: 'message',
      width: 260,
      render: (value: string | null) => formatTriStateText(value),
    },
  ];

  return (
    <PageContainer
      header={{
        title: '全文索引状态',
        subTitle: '全文检索（Lucene）服务只读观测 · 数据源 GET /api/admin/index/status',
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
            message="全文索引状态读取失败"
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
            <Alert
              type="info"
              showIcon
              style={{ marginBottom: 12 }}
              message={`快照生成时刻（generatedAtUtc）：${formatTriStateTimestamp(snapshot.generatedAtUtc)}`}
            />

            <Row gutter={[12, 12]}>
              <Col span={24}>
                <Card size="small" title="① 配置真值（FullTextIndex 配置节）">
                  <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} bordered>
                    <Descriptions.Item label="配置节是否存在（configured）">
                      {formatTriStateBoolean(fullText?.configured)}
                    </Descriptions.Item>
                    <Descriptions.Item label="总开关（enabled）">
                      {formatTriStateBoolean(fullText?.enabled)}
                    </Descriptions.Item>
                    <Descriptions.Item label="解析基准（workspaceRoot）">
                      {formatTriStateText(fullText?.workspaceRoot)}
                    </Descriptions.Item>
                    <Descriptions.Item label="索引根目录（indexRoot）">
                      <Typography.Text code style={{ fontSize: 12, wordBreak: 'break-all' }}>
                        {formatTriStateText(fullText?.indexRoot)}
                      </Typography.Text>
                    </Descriptions.Item>
                    <Descriptions.Item label="根目录当前存在（indexRootExists）">
                      {formatTriStateBoolean(fullText?.indexRootExists)}
                    </Descriptions.Item>
                    <Descriptions.Item label="单库体积上限（maxIndexBytes）">
                      {formatTriStateBytes(fullText?.maxIndexBytes)}
                      {fullText ? (
                        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                          {' '}
                          （{fullText.maxIndexBytes} B）
                        </Typography.Text>
                      ) : null}
                    </Descriptions.Item>
                    <Descriptions.Item label="最小重建间隔（minRebuildInterval）">
                      <Typography.Text code>{formatTriStateText(fullText?.minRebuildInterval)}</Typography.Text>
                    </Descriptions.Item>
                    <Descriptions.Item label={`已受理 scope（acceptedScopes，共 ${fullText?.acceptedScopes.length ?? 0} 项）`} span={3}>
                      {renderStringList(fullText?.acceptedScopes ?? [])}
                    </Descriptions.Item>
                  </Descriptions>

                  {fullText && fullText.rejectedReasons.length > 0 ? (
                    <Alert
                      type="warning"
                      showIcon
                      style={{ marginTop: 12 }}
                      message={`存在被拒绝的 scope（rejectedReasons，共 ${fullText.rejectedReasons.length} 条）`}
                      description={
                        <ul style={{ margin: 0, paddingLeft: 18 }}>
                          {fullText.rejectedReasons.map((reason) => (
                            <li key={reason}>{reason}</li>
                          ))}
                        </ul>
                      }
                    />
                  ) : (
                    <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                      rejectedReasons：空数组 ⇒ 无被拒绝的 scope。
                    </Typography.Text>
                  )}
                </Card>
              </Col>

              <Col span={24}>
                <Card
                  size="small"
                  title={
                    <Space>
                      <DatabaseOutlined /> ② 供给组合与 job 台账
                    </Space>
                  }
                  extra={
                    <Space size={8}>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        compositionCreated
                      </Typography.Text>
                      <Tag color={fullText?.compositionCreated ? 'success' : 'default'}>
                        {formatTriStateBoolean(fullText?.compositionCreated)}
                      </Tag>
                    </Space>
                  }
                >
                  {jobs.length === 0 ? (
                    <Alert
                      type="warning"
                      showIcon
                      message={`job 台账为空（jobs.length = ${jobs.length}）`}
                      description={`jobsReason：${formatJobsReason(fullText?.jobsReason)}`}
                    />
                  ) : (
                    <>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        jobsReason：{formatTriStateText(fullText?.jobsReason)}（有 job 时按契约应为 null）
                      </Typography.Text>
                      <Table<FullTextIndexJobStatus>
                        size="small"
                        rowKey="jobId"
                        style={{ marginTop: 8 }}
                        scroll={{ x: 1400 }}
                        pagination={{ pageSize: 10, hideOnSinglePage: true }}
                        dataSource={jobs}
                        columns={jobColumns}
                      />
                    </>
                  )}
                </Card>
              </Col>

              <Col span={24}>
                <Card size="small" title={`③ 逐 scope 观测（scopes，共 ${scopes.length} 项）`}>
                  <Table<FullTextIndexScopeStatus>
                    size="small"
                    rowKey="scopePath"
                    scroll={{ x: 1300 }}
                    pagination={false}
                    dataSource={scopes}
                    columns={scopeColumns}
                  />
                </Card>
              </Col>

              <Col span={24}>
                <Card size="small" title="④ 维护循环开关（FullTextIndex:Maintenance）">
                  <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} bordered>
                    <Descriptions.Item label="维护子节是否存在（configured）">
                      {formatTriStateBoolean(fullText?.maintenance.configured)}
                    </Descriptions.Item>
                    <Descriptions.Item label="生效开关（enabled，null ⇒ 不可知）">
                      <Space size={6}>
                        {triStateTag(fullText?.maintenance.enabled)}
                        {fullText?.maintenance.enabled === null ? (
                          <Typography.Text type="warning" style={{ fontSize: 12 }}>
                            不可知（不是「关」）
                          </Typography.Text>
                        ) : null}
                      </Space>
                    </Descriptions.Item>
                    <Descriptions.Item label="说明（note）">
                      {formatTriStateText(fullText?.maintenance.note)}
                    </Descriptions.Item>
                  </Descriptions>
                  <Typography.Text type="secondary" style={{ fontSize: 12, display: 'block', marginTop: 8 }}>
                    注：enabled = null 表示宿主侧没有该子节的绑定器 ⇒ 生效值不可知，不得当作「关」。
                  </Typography.Text>
                </Card>
              </Col>
            </Row>
          </>
        )}
      </Spin>
    </PageContainer>
  );
};

export default IndexStatusPage;
