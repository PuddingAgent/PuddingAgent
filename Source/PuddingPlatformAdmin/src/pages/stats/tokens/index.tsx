// ── TokenStatsPage：Token 消耗统计页面（ADR-018/ADR-043）────
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import {
  Button,
  Card,
  Col,
  DatePicker,
  Row,
  Select,
  Space,
  Statistic,
  Typography,
  message,
} from 'antd';
import {
  BarChartOutlined,
  DollarOutlined,
  ReloadOutlined,
  ThunderboltOutlined,
  ApiOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import {
  getMonthlyTokenStats,
  rebuildTokenEvents,
  type MonthlyTokenStatsResponse,
} from '@/services/platform/api';
import dayjs from 'dayjs';

interface TokenStatRow {
  providerId: string;
  modelId: string;
  promptTokens: number;
  completionTokens: number;
  cacheHitTokens: number;
  cacheMissTokens: number;
  cacheHitRate: number;
  totalCost: number;
  requestCount: number;
}

const fmtTokens = (n: number): string => {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
};

const fmtCost = (usd: number): string => `$${usd.toFixed(4)}`;

const fmtRate = (rate: number): string => `${(rate * 100).toFixed(1)}%`;

const TokenStatsPage: React.FC = () => {
  const [yearMonth, setYearMonth] = useState<string>(dayjs().format('YYYY-MM'));
  const [providerFilter, setProviderFilter] = useState<string | undefined>();
  const [rebuilding, setRebuilding] = useState(false);
  const [loading, setLoading] = useState(false);
  const [data, setData] = useState<MonthlyTokenStatsResponse | null>(null);
  const [flatRows, setFlatRows] = useState<TokenStatRow[]>([]);

  // 提取所有可用的 Provider 列表
  const providerOptions = React.useMemo(() => {
    if (!data?.byProvider) return [];
    return data.byProvider.map(p => ({
      label: p.providerId,
      value: p.providerId,
    }));
  }, [data]);

  const fetchStats = useCallback(async (ym: string, pv?: string) => {
    setLoading(true);
    try {
      const json = await getMonthlyTokenStats(ym, pv);
      setData(json);

      // 展开为平铺列表
      const rows: TokenStatRow[] = [];
      for (const p of json.byProvider || []) {
        for (const m of p.models || []) {
          rows.push({
            providerId: p.providerId,
            modelId: m.modelId,
            promptTokens: m.promptTokens,
            completionTokens: m.completionTokens,
            cacheHitTokens: m.cacheHitTokens,
            cacheMissTokens: m.cacheMissTokens,
            cacheHitRate: m.cacheHitRate,
            totalCost: m.totalCost,
            requestCount: m.requestCount,
          });
        }
      }
      setFlatRows(rows);
    } catch {
      message.error('获取统计数据失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void fetchStats(yearMonth, providerFilter);
  }, [yearMonth, providerFilter, fetchStats]);

  const handleRebuild = async () => {
    setRebuilding(true);
    try {
      const result = await rebuildTokenEvents(yearMonth);
      message.success(`重建完成：创建 ${result.eventsCreated} 条明细，跳过 ${result.skippedDuplicates} 条重复`);
      void fetchStats(yearMonth, providerFilter);
    } catch {
      message.error('重建失败');
    } finally {
      setRebuilding(false);
    }
  };

  const columns: ProColumns<TokenStatRow>[] = [
    { title: 'Provider', dataIndex: 'providerId', key: 'providerId', width: 120 },
    { title: '模型', dataIndex: 'modelId', key: 'modelId', width: 160 },
    {
      title: 'Prompt Tokens',
      dataIndex: 'promptTokens',
      key: 'promptTokens',
      width: 120,
      render: (_, r) => fmtTokens(r.promptTokens),
    },
    {
      title: '缓存命中',
      dataIndex: 'cacheHitTokens',
      key: 'cacheHitTokens',
      width: 120,
      render: (_, r) => fmtTokens(r.cacheHitTokens),
    },
    {
      title: '缓存未命中',
      dataIndex: 'cacheMissTokens',
      key: 'cacheMissTokens',
      width: 120,
      render: (_, r) => fmtTokens(r.cacheMissTokens),
    },
    {
      title: '缓存命中率',
      dataIndex: 'cacheHitRate',
      key: 'cacheHitRate',
      width: 110,
      render: (_, r) => {
        const pct = r.cacheHitRate * 100;
        const color = pct > 80 ? '#22c55e' : pct > 50 ? '#eab308' : '#ef4444';
        return <span style={{ color, fontWeight: 600 }}>{fmtRate(r.cacheHitRate)}</span>;
      },
    },
    {
      title: 'Completion Tokens',
      dataIndex: 'completionTokens',
      key: 'completionTokens',
      width: 140,
      render: (_, r) => fmtTokens(r.completionTokens),
    },
    {
      title: '请求次数',
      dataIndex: 'requestCount',
      key: 'requestCount',
      width: 100,
    },
    {
      title: '费用 (USD)',
      dataIndex: 'totalCost',
      key: 'totalCost',
      width: 120,
      render: (_, r) => fmtCost(r.totalCost),
    },
    {
      title: '费用 (RMB)',
      key: 'costRmb',
      width: 120,
      render: (_, r) => `¥${(r.totalCost * 7.2).toFixed(4)}`,
    },
  ];

  return (
    <PageContainer
      title="Token 消耗统计"
      subTitle="按月查看各 LLM 服务商和模型的 Token 用量、缓存命中率及费用"
      extra={[
        <Select
          key="provider"
          allowClear
          placeholder="筛选 Provider"
          style={{ width: 160 }}
          options={providerOptions}
          value={providerFilter}
          onChange={(v) => setProviderFilter(v)}
        />,
        <Button
          key="rebuild"
          icon={<ReloadOutlined />}
          loading={rebuilding}
          onClick={handleRebuild}
        >
          重建统计
        </Button>,
        <DatePicker
          key="month"
          picker="month"
          value={dayjs(yearMonth)}
          onChange={(d) => { if (d) setYearMonth(d.format('YYYY-MM')); }}
          allowClear={false}
        />,
      ]}
    >
      {/* 汇总卡片 */}
      <Row gutter={16} style={{ marginBottom: 16 }}>
        <Col xs={24} sm={12} md={6}>
          <Card loading={loading}>
            <Statistic
              title="总 Token 消耗"
              value={fmtTokens((data?.totalPromptTokens ?? 0) + (data?.totalCompletionTokens ?? 0))}
              prefix={<ThunderboltOutlined />}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6}>
          <Card loading={loading}>
            <Statistic
              title="缓存命中率"
              value={data ? fmtRate(data.cacheHitRate) : '0%'}
              prefix={<BarChartOutlined />}
              valueStyle={{
                color:
                  (data?.cacheHitRate ?? 0) > 0.8 ? '#22c55e'
                  : (data?.cacheHitRate ?? 0) > 0.5 ? '#eab308'
                  : '#ef4444',
              }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6}>
          <Card loading={loading}>
            <Statistic
              title="总费用"
              value={data ? `$${data.totalCost.toFixed(4)}` : '$0'}
              prefix={<DollarOutlined />}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6}>
          <Card loading={loading}>
            <Statistic
              title="请求次数"
              value={data?.totalRequests ?? 0}
              prefix={<ApiOutlined />}
            />
          </Card>
        </Col>
      </Row>

      {/* 明细表 */}
      <ProTable<TokenStatRow>
        headerTitle={`${yearMonth} 明细`}
        loading={loading}
        columns={columns}
        dataSource={flatRows}
        rowKey={(r) => `${r.providerId}:${r.modelId}`}
        search={false}
        pagination={false}
        toolBarRender={false}
      />

      <Typography.Paragraph type="secondary" style={{ marginTop: 16 }}>
        费用计算公式：缓存命中 × 缓存命中单价 + 缓存未命中 × 输入单价 + 输出 × 输出单价（USD）
        <br />
        人民币按固定汇率 7.2 换算，仅供参考。
      </Typography.Paragraph>
    </PageContainer>
  );
};

export default TokenStatsPage;
