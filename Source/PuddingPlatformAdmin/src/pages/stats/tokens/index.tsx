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
  Tag,
  Typography,
  message,
} from 'antd';
import {
  BarChartOutlined,
  MoneyCollectOutlined,
  ReloadOutlined,
  ThunderboltOutlined,
  ApiOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import {
  getContextLayerTokenStats,
  getMonthlyTokenStats,
  getTokenStatsSeries,
  rebuildTokenEvents,
  type ContextLayerTokenStatsLayer,
  type MonthlyTokenStatsResponse,
  type TokenSeriesPoint,
  type TokenStatsSeriesResponse,
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
  inputCost: number;
  cacheHitCost: number;
  outputCost: number;
  totalCost: number;
  requestCount: number;
}

interface ContextLayerAnalysisRow {
  layerName: string;
  layerOrder: number;
  layerRole?: string;
  calls: number;
  tokenCount: number;
  tokenShare: number;
  avgTokens: number;
  p95Tokens: number;
  estimatedHitTokens: number;
  estimatedMissTokens: number;
  cacheHitRate: number | null;
  changeCount: number;
  changeRate: number;
  distinctHashes: number;
  changeReasons: Array<{ reason: string; count: number }>;
  impactScore: number;
}

const fmtTokens = (n: number): string => {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
};

const ALL_PROVIDER_VALUE = '__all_provider__';
const ALL_MODEL_VALUE = '__all_model__';
const SUMMARY_CARD_STYLE: React.CSSProperties = { height: '100%', width: '100%' };
const SUMMARY_CARD_BODY_STYLE: React.CSSProperties = {
  minHeight: 92,
  display: 'flex',
  alignItems: 'center',
};
const SUMMARY_VALUE_STYLE: React.CSSProperties = { whiteSpace: 'nowrap' };

const fmtCostRmb = (rmbCost: number): string => `¥${rmbCost.toFixed(4)}`;

const fmtRate = (rate: number): string => `${(rate * 100).toFixed(1)}%`;

const normalizeChangeReasons = (
  reasons: ContextLayerTokenStatsLayer['changeReasons'],
): Array<{ reason: string; count: number }> => {
  if (!reasons) return [];
  const entries = Array.isArray(reasons)
    ? reasons.map((item) => [item.reason, item.count] as const)
    : Object.entries(reasons);
  return entries
    .filter(([reason, count]) => reason && typeof count === 'number' && count > 0)
    .map(([reason, count]) => ({ reason, count }));
};

const buildContextLayerAnalysisRows = (
  layers: ContextLayerTokenStatsLayer[],
): ContextLayerAnalysisRow[] => {
  const groups = new Map<string, ContextLayerAnalysisRow>();

  for (const layer of layers) {
    const key = `${layer.layerName}:${layer.layerRole ?? ''}`;
    const existing = groups.get(key) ?? {
      layerName: layer.layerName,
      layerOrder: layer.layerOrder,
      layerRole: layer.layerRole,
      calls: 0,
      tokenCount: 0,
      tokenShare: 0,
      avgTokens: 0,
      p95Tokens: 0,
      estimatedHitTokens: 0,
      estimatedMissTokens: 0,
      cacheHitRate: null,
      changeCount: 0,
      changeRate: 0,
      distinctHashes: 0,
      changeReasons: [],
      impactScore: 0,
    };

    existing.calls += layer.calls;
    existing.layerOrder = Math.min(existing.layerOrder, layer.layerOrder);
    existing.tokenCount += layer.tokenCount;
    existing.tokenShare += layer.tokenShare ?? 0;
    existing.p95Tokens = Math.max(existing.p95Tokens, layer.p95Tokens ?? 0);
    existing.estimatedHitTokens += layer.estimatedHitTokens ?? 0;
    existing.estimatedMissTokens += layer.estimatedMissTokens ?? 0;
    existing.changeCount += layer.changeCount ?? 0;
    existing.distinctHashes += layer.distinctHashes ?? 0;

    const reasonMap = new Map(existing.changeReasons.map((item) => [item.reason, item.count]));
    for (const reason of normalizeChangeReasons(layer.changeReasons)) {
      reasonMap.set(reason.reason, (reasonMap.get(reason.reason) ?? 0) + reason.count);
    }
    existing.changeReasons = Array.from(reasonMap.entries())
      .map(([reason, count]) => ({ reason, count }))
      .sort((a, b) => b.count - a.count);

    groups.set(key, existing);
  }

  return Array.from(groups.values())
    .map((row) => {
      const cacheTokens = row.estimatedHitTokens + row.estimatedMissTokens;
      const cacheHitRate = cacheTokens > 0 ? row.estimatedHitTokens / cacheTokens : null;
      const changeRate = row.calls > 0 ? row.changeCount / row.calls : 0;
      const avgTokens = row.calls > 0 ? row.tokenCount / row.calls : 0;
      const cachePenalty = cacheHitRate === null ? 0 : (1 - cacheHitRate) * 0.35;
      const impactScore = row.tokenCount > 0
        ? (row.tokenShare * 0.45) + cachePenalty + (changeRate * 0.2)
        : 0;

      return {
        ...row,
        avgTokens,
        cacheHitRate,
        changeRate,
        impactScore,
      };
    })
    .sort((a, b) => {
      if (a.layerOrder !== b.layerOrder) return a.layerOrder - b.layerOrder;
      const nameCompare = a.layerName.localeCompare(b.layerName);
      if (nameCompare !== 0) return nameCompare;
      return b.tokenCount - a.tokenCount;
    });
};

const impactTag = (score: number, tokenCount: number) => {
  if (tokenCount <= 0) return <Tag>未参与</Tag>;
  if (score >= 0.3) return <Tag color="red">高影响</Tag>;
  if (score >= 0.16) return <Tag color="orange">中影响</Tag>;
  return <Tag color="green">稳定</Tag>;
};

const formatUnknownLabel = (value: string, label: string) =>
  value === 'unknown' ? `${label} (unknown)` : value;

const SERIES_SEGMENTS = [
  { key: 'cacheMissTokens', label: '输入（未命中缓存）', color: '#3b82f6' },
  { key: 'cacheHitTokens', label: '输入（命中缓存）', color: '#8fd3f4' },
  { key: 'completionTokens', label: '输出', color: '#1677ff' },
] as const;

const BreakdownPanel: React.FC<{
  tokenItems: Array<{ label: string; value: string; color: string }>;
  costItems: Array<{ label: string; value: string; color: string }>;
}> = ({ tokenItems, costItems }) => {
  const renderSection = (title: string, items: Array<{ label: string; value: string; color: string }>) => (
    <div>
      <Typography.Text strong>{title}</Typography.Text>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
          gap: 12,
          marginTop: 10,
        }}
      >
        {items.map((item) => (
          <div
            key={`${title}:${item.label}`}
            style={{
              minWidth: 0,
              borderLeft: `3px solid ${item.color}`,
              paddingLeft: 10,
            }}
          >
            <Typography.Text
              type="secondary"
              style={{ display: 'block', fontSize: 12, whiteSpace: 'nowrap' }}
            >
              {item.label}
            </Typography.Text>
            <Typography.Text
              strong
              style={{ display: 'block', fontSize: 15, whiteSpace: 'nowrap' }}
            >
              {item.value}
            </Typography.Text>
          </div>
        ))}
      </div>
    </div>
  );

  return (
    <Card
      title="消耗构成"
      styles={{ body: { padding: 16 } }}
      style={{ marginBottom: 16 }}
    >
      <Row gutter={[24, 16]}>
        <Col xs={24} lg={12}>{renderSection('Token', tokenItems)}</Col>
        <Col xs={24} lg={12}>{renderSection('费用', costItems)}</Col>
      </Row>
    </Card>
  );
};

const CostBreakdown: React.FC<{
  cacheHitCost: number;
  inputCost: number;
  outputCost: number;
}> = ({ cacheHitCost, inputCost, outputCost }) => (
  <Space direction="vertical" size={0}>
    <Typography.Text style={{ fontSize: 12 }}>
      命中：{fmtCostRmb(cacheHitCost)}
    </Typography.Text>
    <Typography.Text style={{ fontSize: 12 }}>
      未命中：{fmtCostRmb(inputCost)}
    </Typography.Text>
    <Typography.Text style={{ fontSize: 12 }}>
      输出：{fmtCostRmb(outputCost)}
    </Typography.Text>
  </Space>
);

const sumSeriesPoint = (point: TokenSeriesPoint) =>
  point.cacheMissTokens + point.cacheHitTokens + point.completionTokens;

const TokenSeriesChart: React.FC<{
  title: string;
  data: TokenSeriesPoint[];
  labelFormat: (period: string, index: number) => string;
}> = ({ title, data, labelFormat }) => {
  const maxValue = Math.max(...data.map(sumSeriesPoint), 1);
  const width = 720;
  const height = 230;
  const padding = { top: 20, right: 24, bottom: 38, left: 52 };
  const plotWidth = width - padding.left - padding.right;
  const plotHeight = height - padding.top - padding.bottom;
  const slotWidth = plotWidth / Math.max(data.length, 1);
  const barWidth = Math.max(4, Math.min(18, slotWidth * 0.54));

  return (
    <Card
      title={title}
      styles={{ body: { padding: 16 } }}
      data-testid="token-series-chart"
      extra={
        <Space size={12} wrap>
          {SERIES_SEGMENTS.map((segment) => (
            <Space key={segment.key} size={4}>
              <span
                style={{
                  width: 10,
                  height: 10,
                  borderRadius: 3,
                  background: segment.color,
                  display: 'inline-block',
                }}
              />
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {segment.label}
              </Typography.Text>
            </Space>
          ))}
        </Space>
      }
    >
      <svg
        role="img"
        aria-label={`${title} Token 图表`}
        viewBox={`0 0 ${width} ${height}`}
        style={{ width: '100%', height: 260, display: 'block' }}
      >
        {[0, 0.5, 1].map((ratio) => {
          const y = padding.top + plotHeight - plotHeight * ratio;
          return (
            <g key={ratio}>
              <line
                x1={padding.left}
                x2={width - padding.right}
                y1={y}
                y2={y}
                stroke="rgba(92, 74, 58, 0.16)"
              />
              <text x={8} y={y + 4} fill="#756b5f" fontSize={12}>
                {fmtTokens(maxValue * ratio)}
              </text>
            </g>
          );
        })}

        {data.map((point, index) => {
          const x = padding.left + slotWidth * index + (slotWidth - barWidth) / 2;
          let yCursor = padding.top + plotHeight;
          const segments = SERIES_SEGMENTS.map((segment) => {
            const value = point[segment.key];
            const segmentHeight = (value / maxValue) * plotHeight;
            yCursor -= segmentHeight;
            return {
              ...segment,
              value,
              x,
              y: yCursor,
              height: segmentHeight,
            };
          });

          return (
            <g key={point.period}>
              <title>
                {`${point.period}
输入（未命中缓存）：${point.cacheMissTokens.toLocaleString()} tokens
输入（命中缓存）：${point.cacheHitTokens.toLocaleString()} tokens
输出：${point.completionTokens.toLocaleString()} tokens
请求次数：${point.requestCount.toLocaleString()}`}
              </title>
              {segments.map((segment) => (
                <rect
                  key={segment.key}
                  x={segment.x}
                  y={segment.y}
                  width={barWidth}
                  height={Math.max(segment.height, segment.value > 0 ? 1 : 0)}
                  rx={2}
                  fill={segment.color}
                />
              ))}
              {(index === 0 || index === data.length - 1 || data.length <= 12) && (
                <text
                  x={x + barWidth / 2}
                  y={height - 12}
                  textAnchor="middle"
                  fill="#756b5f"
                  fontSize={12}
                >
                  {labelFormat(point.period, index)}
                </text>
              )}
            </g>
          );
        })}
      </svg>
    </Card>
  );
};

const TokenStatsPage: React.FC = () => {
  const [yearMonth, setYearMonth] = useState<string>(dayjs().format('YYYY-MM'));
  const [providerFilter, setProviderFilter] = useState<string | undefined>();
  const [modelFilter, setModelFilter] = useState<string | undefined>();
  const [rebuilding, setRebuilding] = useState(false);
  const [loading, setLoading] = useState(false);
  const [data, setData] = useState<MonthlyTokenStatsResponse | null>(null);
  const [series, setSeries] = useState<TokenStatsSeriesResponse | null>(null);
  const [contextLayers, setContextLayers] = useState<ContextLayerTokenStatsLayer[]>([]);
  const [flatRows, setFlatRows] = useState<TokenStatRow[]>([]);
  const [optionRows, setOptionRows] = useState<TokenStatRow[]>([]);

  const flattenRows = useCallback((json: MonthlyTokenStatsResponse): TokenStatRow[] => {
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
          inputCost: m.inputCost,
          cacheHitCost: m.cacheHitCost,
          outputCost: m.outputCost,
          totalCost: m.totalCost,
          requestCount: m.requestCount,
        });
      }
    }
    return rows;
  }, []);

  const providerOptions = React.useMemo(() => {
    const providers = Array.from(new Set(optionRows.map((row) => row.providerId))).sort();
    return [
      { label: '全部 Provider', value: ALL_PROVIDER_VALUE },
      ...providers.map((providerId) => ({
        label: formatUnknownLabel(providerId, '未知 Provider'),
        value: providerId,
      })),
    ];
  }, [optionRows]);

  const modelOptions = React.useMemo(() => {
    const rows = providerFilter
      ? optionRows.filter((row) => row.providerId === providerFilter)
      : optionRows;
    const models = Array.from(new Set(rows.map((row) => row.modelId))).sort();
    return [
      { label: '全部模型', value: ALL_MODEL_VALUE },
      ...models.map((modelId) => ({
        label: formatUnknownLabel(modelId, '未知模型'),
        value: modelId,
      })),
    ];
  }, [optionRows, providerFilter]);

  const fetchStats = useCallback(async (ym: string, pv?: string, mv?: string) => {
    setLoading(true);
    try {
      const shouldRefreshOptions = Boolean(pv || mv);
      const monthStart = dayjs(ym).startOf('month');
      const monthEnd = dayjs(ym).endOf('month');
      const [json, seriesJson, contextLayerJson, allJson] = await Promise.all([
        getMonthlyTokenStats(ym, pv, mv),
        getTokenStatsSeries(ym, pv, mv),
        getContextLayerTokenStats({
          from: monthStart.toISOString(),
          to: monthEnd.toISOString(),
          providerId: pv,
          modelId: mv,
        }),
        shouldRefreshOptions ? getMonthlyTokenStats(ym) : Promise.resolve(null),
      ]);
      setData(json);
      setSeries(seriesJson);
      setContextLayers(contextLayerJson.layers || []);
      setFlatRows(flattenRows(json));
      if (allJson) {
        setOptionRows(flattenRows(allJson));
      } else if (!pv && !mv) {
        setOptionRows(flattenRows(json));
      }
    } catch {
      message.error('获取统计数据失败');
      setSeries(null);
      setContextLayers([]);
    } finally {
      setLoading(false);
    }
  }, [flattenRows]);

  useEffect(() => {
    void fetchStats(yearMonth, providerFilter, modelFilter);
  }, [yearMonth, providerFilter, modelFilter, fetchStats]);

  const handleRebuild = async () => {
    setRebuilding(true);
    try {
      const result = await rebuildTokenEvents(yearMonth);
      message.success(`重建完成：删除 ${result.eventsDeleted ?? 0} 条旧明细，创建 ${result.eventsCreated} 条明细，跳过 ${result.skippedDuplicates} 条重复`);
      void fetchStats(yearMonth, providerFilter, modelFilter);
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
      title: '费用构成 (RMB)',
      key: 'costBreakdown',
      width: 150,
      render: (_, r) => (
        <CostBreakdown
          cacheHitCost={r.cacheHitCost}
          inputCost={r.inputCost}
          outputCost={r.outputCost}
        />
      ),
    },
    {
      title: '总费用 (RMB)',
      dataIndex: 'totalCost',
      key: 'totalCost',
      width: 120,
      render: (_, r) => fmtCostRmb(r.totalCost),
    },
  ];

  const contextAnalysisRows = React.useMemo(
    () => buildContextLayerAnalysisRows(contextLayers),
    [contextLayers],
  );

  const contextLayerColumns: ProColumns<ContextLayerAnalysisRow>[] = [
    {
      title: '层',
      dataIndex: 'layerName',
      key: 'layerName',
      width: 220,
      render: (_, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{r.layerName}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            #{r.layerOrder} / {r.calls.toLocaleString()} 次调用
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '职责',
      dataIndex: 'layerRole',
      key: 'layerRole',
      width: 150,
      render: (_, r) => <Tag>{r.layerRole || 'unknown'}</Tag>,
    },
    {
      title: '影响',
      dataIndex: 'impactScore',
      key: 'impactScore',
      width: 110,
      render: (_, r) => impactTag(r.impactScore, r.tokenCount),
    },
    {
      title: 'Token 压力',
      dataIndex: 'tokenCount',
      key: 'tokenCount',
      width: 180,
      render: (_, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{fmtTokens(r.tokenCount)}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            占比 {fmtRate(r.tokenShare)} / 均值 {fmtTokens(Math.round(r.avgTokens))} / P95 {fmtTokens(Math.round(r.p95Tokens))}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '缓存表现',
      dataIndex: 'cacheHitRate',
      key: 'cacheHitRate',
      width: 170,
      render: (_, r) => {
        if (r.cacheHitRate === null) {
          return (
            <Space direction="vertical" size={0}>
              <Typography.Text type="secondary">无缓存样本</Typography.Text>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                命中 {fmtTokens(r.estimatedHitTokens)} / 未命中 {fmtTokens(r.estimatedMissTokens)}
              </Typography.Text>
            </Space>
          );
        }

        return (
          <Space direction="vertical" size={0}>
            <Typography.Text
              strong
              style={{
                color: r.cacheHitRate > 0.9 ? '#22c55e' : r.cacheHitRate > 0.65 ? '#eab308' : '#ef4444',
              }}
            >
              {fmtRate(r.cacheHitRate)}
            </Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              命中 {fmtTokens(r.estimatedHitTokens)} / 未命中 {fmtTokens(r.estimatedMissTokens)}
            </Typography.Text>
          </Space>
        );
      },
    },
    {
      title: '变化',
      dataIndex: 'changeRate',
      key: 'changeRate',
      width: 150,
      render: (_, r) => (
        <Space direction="vertical" size={0}>
          <Typography.Text strong>{fmtRate(r.changeRate)}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {r.changeCount.toLocaleString()} 次 / {r.distinctHashes.toLocaleString()} hashes
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: '主要原因',
      dataIndex: 'changeReasons',
      key: 'changeReasons',
      width: 220,
      render: (_, r) => {
        const reasons = r.changeReasons.slice(0, 2);
        if (reasons.length === 0) return <Typography.Text type="secondary">-</Typography.Text>;
        return (
          <Space wrap size={[4, 4]}>
            {reasons.map((item) => (
              <Tag key={item.reason}>{item.reason}: {item.count}</Tag>
            ))}
          </Space>
        );
      },
    },
  ];

  const totalTokens = (data?.totalCacheHitTokens ?? 0)
    + (data?.totalCacheMissTokens ?? 0)
    + (data?.totalCompletionTokens ?? 0);

  return (
    <PageContainer
      title="Token 消耗统计"
      subTitle="按月查看各 LLM 服务商和模型的 Token 用量、缓存命中率及费用"
      extra={[
        <Space key="filters" wrap size={8}>
          <Select
            data-testid="token-provider-filter"
            style={{ width: 180 }}
            popupMatchSelectWidth={false}
            styles={{ popup: { root: { minWidth: 180 } } }}
            options={providerOptions}
            value={providerFilter ?? ALL_PROVIDER_VALUE}
            onChange={(v) => {
              const nextProvider = v === ALL_PROVIDER_VALUE ? undefined : v;
              setProviderFilter(nextProvider);
              setModelFilter(undefined);
            }}
          />
          <Select
            data-testid="token-model-filter"
            style={{ width: 180 }}
            popupMatchSelectWidth={false}
            styles={{ popup: { root: { minWidth: 180 } } }}
            options={modelOptions}
            value={modelFilter ?? ALL_MODEL_VALUE}
            onChange={(v) => setModelFilter(v === ALL_MODEL_VALUE ? undefined : v)}
          />
          <Button
            icon={<ReloadOutlined />}
            loading={rebuilding}
            onClick={handleRebuild}
          >
            重建统计
          </Button>
          <DatePicker
            picker="month"
            value={dayjs(yearMonth)}
            onChange={(d) => { if (d) setYearMonth(d.format('YYYY-MM')); }}
            allowClear={false}
          />
        </Space>,
      ]}
    >
      {/* 汇总卡片 */}
      <Row gutter={[16, 16]} align="stretch" style={{ marginBottom: 16 }}>
        <Col xs={24} sm={12} md={6} style={{ display: 'flex' }}>
          <Card loading={loading} style={SUMMARY_CARD_STYLE} styles={{ body: SUMMARY_CARD_BODY_STYLE }}>
            <Statistic
              title="Token 消耗"
              value={fmtTokens(totalTokens)}
              prefix={<ThunderboltOutlined />}
              valueStyle={SUMMARY_VALUE_STYLE}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6} style={{ display: 'flex' }}>
          <Card loading={loading} style={SUMMARY_CARD_STYLE} styles={{ body: SUMMARY_CARD_BODY_STYLE }}>
            <Statistic
              title="缓存命中率"
              value={data ? fmtRate(data.cacheHitRate) : '0%'}
              prefix={<BarChartOutlined />}
              valueStyle={{
                ...SUMMARY_VALUE_STYLE,
                color:
                  (data?.cacheHitRate ?? 0) > 0.8 ? '#22c55e'
                  : (data?.cacheHitRate ?? 0) > 0.5 ? '#eab308'
                  : '#ef4444',
              }}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6} style={{ display: 'flex' }}>
          <Card loading={loading} style={SUMMARY_CARD_STYLE} styles={{ body: SUMMARY_CARD_BODY_STYLE }}>
            <Statistic
              title="总费用"
              value={data ? fmtCostRmb(data.totalCost) : '¥0'}
              prefix={<MoneyCollectOutlined />}
              valueStyle={SUMMARY_VALUE_STYLE}
            />
          </Card>
        </Col>
        <Col xs={24} sm={12} md={6} style={{ display: 'flex' }}>
          <Card loading={loading} style={SUMMARY_CARD_STYLE} styles={{ body: SUMMARY_CARD_BODY_STYLE }}>
            <Statistic
              title="请求次数"
              value={data?.totalRequests ?? 0}
              prefix={<ApiOutlined />}
              valueStyle={SUMMARY_VALUE_STYLE}
            />
          </Card>
        </Col>
      </Row>

      <BreakdownPanel
        tokenItems={[
          {
            label: '命中输入',
            value: fmtTokens(data?.totalCacheHitTokens ?? 0),
            color: '#8fd3f4',
          },
          {
            label: '未命中输入',
            value: fmtTokens(data?.totalCacheMissTokens ?? 0),
            color: '#3b82f6',
          },
          {
            label: '输出',
            value: fmtTokens(data?.totalCompletionTokens ?? 0),
            color: '#1677ff',
          },
        ]}
        costItems={[
          {
            label: '命中输入',
            value: fmtCostRmb(data?.cacheHitCost ?? 0),
            color: '#8fd3f4',
          },
          {
            label: '未命中输入',
            value: fmtCostRmb(data?.inputCost ?? 0),
            color: '#3b82f6',
          },
          {
            label: '输出',
            value: fmtCostRmb(data?.outputCost ?? 0),
            color: '#1677ff',
          },
        ]}
      />

      <Row gutter={[16, 16]} style={{ marginBottom: 16 }}>
        <Col xs={24} lg={12}>
          <TokenSeriesChart
            title="按月趋势"
            data={series?.monthly ?? []}
            labelFormat={(period) => dayjs(`${period}-01`).format('M月')}
          />
        </Col>
        <Col xs={24} lg={12}>
          <TokenSeriesChart
            title="按日趋势"
            data={series?.daily ?? []}
            labelFormat={(period) => dayjs(period).format('M-D')}
          />
        </Col>
      </Row>

      <Typography.Title level={4} style={{ marginTop: 0 }}>
        上下文分层缓存分析
      </Typography.Title>
      <Typography.Paragraph type="secondary">
        按上下文层统计 Token 占比、估算缓存命中率和内容变化率，用于定位哪些层最影响前缀缓存稳定性。
      </Typography.Paragraph>
      <ProTable<ContextLayerAnalysisRow>
        headerTitle="上下文分层影响分析"
        loading={loading}
        columns={contextLayerColumns}
        dataSource={contextAnalysisRows}
        rowKey={(r) => `${r.layerName}:${r.layerRole ?? ''}`}
        search={false}
        pagination={false}
        toolBarRender={false}
        scroll={{ x: 1200 }}
        style={{ marginBottom: 16 }}
      />

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
        费用计算公式：缓存命中 × 缓存命中单价 + 缓存未命中 × 输入单价 + 输出 × 输出单价。
        <br />
        价格和费用全链路以 RMB 为标准口径，单位为人民币/百万 tokens。
      </Typography.Paragraph>
    </PageContainer>
  );
};

export default TokenStatsPage;
