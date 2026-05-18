// ── TokenStatsPage：Token 消耗统计页面（ADR-018）────
import { PageContainer, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import {
  Card,
  Col,
  DatePicker,
  Row,
  Space,
  Statistic,
  Typography,
  message,
} from 'antd';
import {
  BarChartOutlined,
  DollarOutlined,
  ThunderboltOutlined,
  ApiOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
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

interface MonthlyStatsResponse {
  yearMonth: string;
  totalPromptTokens: number;
  totalCompletionTokens: number;
  totalCacheHitTokens: number;
  totalCacheMissTokens: number;
  cacheHitRate: number;
  totalCost: number;
  totalRequests: number;
  byProvider: {
    providerId: string;
    promptTokens: number;
    completionTokens: number;
    cacheHitTokens: number;
    cacheMissTokens: number;
    cacheHitRate: number;
    totalCost: number;
    requestCount: number;
    models: {
      modelId: string;
      promptTokens: number;
      completionTokens: number;
      cacheHitTokens: number;
      cacheMissTokens: number;
      cacheHitRate: number;
      totalCost: number;
      requestCount: number;
    }[];
  }[];
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
  const [loading, setLoading] = useState(false);
  const [data, setData] = useState<MonthlyStatsResponse | null>(null);
  const [flatRows, setFlatRows] = useState<TokenStatRow[]>([]);

  const fetchStats = useCallback(async (ym: string) => {
    setLoading(true);
    try {
      const res = await fetch(`/api/stats/tokens/monthly?yearMonth=${ym}`, {
        headers: { 'Authorization': `Bearer ${localStorage.getItem('token') || ''}` },
      });
      if (!res.ok) {
        message.error('获取统计数据失败');
        return;
      }
      const json: MonthlyStatsResponse = await res.json();
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
    void fetchStats(yearMonth);
  }, [yearMonth, fetchStats]);

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
