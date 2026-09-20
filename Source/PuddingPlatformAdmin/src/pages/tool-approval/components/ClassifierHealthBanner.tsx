// ── 安全分类器健康明显提示横幅（安全分类器方案 v2 §8.2 / §10 D7，切片 S6b-2）──
// 服务端权威原则：本组件只消费 GET /api/classifier-health 快照，
// 不做任何本地健康推断/持久化；请求失败显式显示"未知/获取失败"，绝不回退为健康。
// unknown（从未探测）与 configured=false（健康面未接线）一律按"未知"处理，不得当作健康。
import { Alert, Collapse, Table, Tag, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  getClassifierHealth,
  type ClassifierHealthItemDto,
  type ClassifierHealthSnapshotDto,
  type ClassifierHealthState,
} from '@/services/platform/api';

/** 默认轮询间隔：显式定时刷新（组件卸载即停），可经 props 覆盖或禁用。 */
export const CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS = 30_000;

// 健康档位中文文案单一事实源：`Record<ClassifierHealthState, string>` 由编译器强制完备——
// 联合类型新增成员（后端新增档位）而这里没补 ⇒ `tsc` 直接报错，不会静默漏同步。
export const HEALTH_LABELS: Record<ClassifierHealthState, string> = {
  unknown: '未知',
  healthy: '健康',
  degraded: '降级',
  unavailable: '不可用',
};

// 档位 → antd Tag 语义色单一事实源（同样编译器强制完备）。
export const HEALTH_TAG_COLOR: Record<ClassifierHealthState, string> = {
  unknown: 'default',
  healthy: 'success',
  degraded: 'warning',
  unavailable: 'error',
};

export type ClassifierBannerKind = 'hidden' | 'error' | 'warning';

export interface ClassifierBannerDecision {
  kind: ClassifierBannerKind;
  headline: string;
  description: string;
}

/** 获取失败（含未登录 401 等异常）：显式未知态，绝不当作健康。 */
export const FETCH_ERROR_DECISION: ClassifierBannerDecision = {
  kind: 'warning',
  headline: '分类器健康状态获取失败，当前状态未知',
  description:
    '管理端暂时无法读取分类器健康快照。未知不等于健康：非白名单命中的工具调用' +
    '可能被延迟或挂起、转人工审批，不会静默放行。请稍后重试或检查服务端日志。',
};

/**
 * 档位聚合（纯函数，服务端权威）。优先级：未接线 > 无分类器 > unavailable >
 * degraded > unknown > 全部 healthy（不显示横幅，避免长期稳态噪声削弱告警）。
 */
export function resolveBannerDecision(
  snapshot: ClassifierHealthSnapshotDto,
): ClassifierBannerDecision {
  const classifiers = snapshot.classifiers ?? [];

  if (!snapshot.configured) {
    return {
      kind: 'warning',
      headline: '分类器健康面未接线，健康状态未知',
      description:
        '服务端尚未注册分类器健康面，管理端无法获知真实健康档位。未知不等于健康：' +
        '非白名单命中的工具调用可能被延迟或挂起、转人工审批，不会静默放行。',
    };
  }

  if (classifiers.length === 0) {
    return {
      kind: 'warning',
      headline: '未注册任何安全分类器，健康状态未知',
      description:
        '服务端健康面已接线但没有分类器实例。未知不等于健康：非白名单命中的工具调用' +
        '可能被延迟或挂起、转人工审批，不会静默放行。',
    };
  }

  const countBy = (health: ClassifierHealthState) =>
    classifiers.filter((item) => item.health === health).length;

  const unavailableCount = countBy('unavailable');
  if (unavailableCount > 0) {
    return {
      kind: 'error',
      headline: `安全分类器不可用（${unavailableCount} 个分类器不可用）`,
      description:
        '分类器不可用期间，非白名单命中的工具调用将被延迟或挂起、等待人工审批，' +
        '不会静默放行。请立即检查分类器服务与配置。',
    };
  }

  const degradedCount = countBy('degraded');
  if (degradedCount > 0) {
    return {
      kind: 'warning',
      headline: `安全分类器降级运行（${degradedCount} 个分类器降级）`,
      description:
        '部分分类器健康异常，安全审批可能变慢或退化为人工流程。非白名单命中的工具调用' +
        '不会被静默放行。请关注下方明细。',
    };
  }

  const unknownCount = countBy('unknown');
  if (unknownCount > 0) {
    return {
      kind: 'warning',
      headline: `部分分类器健康状态未知（${unknownCount} 个尚未探测）`,
      description:
        '以下分类器从未完成健康探测，档位未知。未知不等于健康：非白名单命中的工具调用' +
        '可能被延迟或挂起、转人工审批，不会静默放行。',
    };
  }

  return { kind: 'hidden', headline: '', description: '' };
}

const formatTime = (value?: string) =>
  value ? new Date(value).toLocaleString() : '从未探测';

const formatLatency = (value?: number) =>
  typeof value === 'number' ? `${Math.round(value)} ms` : '-';

const DETAIL_COLUMNS: ColumnsType<ClassifierHealthItemDto> = [
  {
    title: '分类器',
    dataIndex: 'classifierId',
    render: (_, record) => (
      <Typography.Text code ellipsis style={{ maxWidth: 220 }}>
        {record.classifierId}
      </Typography.Text>
    ),
  },
  {
    title: '健康状态',
    dataIndex: 'health',
    width: 100,
    render: (_, record) => (
      <Tag color={HEALTH_TAG_COLOR[record.health]}>{HEALTH_LABELS[record.health]}</Tag>
    ),
  },
  {
    title: '连续失败',
    dataIndex: 'consecutiveFailures',
    width: 90,
  },
  {
    title: '最近探测',
    dataIndex: 'lastCheckedAtUtc',
    width: 170,
    render: (_, record) => formatTime(record.lastCheckedAtUtc),
  },
  {
    title: '最近延迟',
    dataIndex: 'lastLatencyMs',
    width: 90,
    render: (_, record) => formatLatency(record.lastLatencyMs),
  },
  {
    title: '说明',
    dataIndex: 'detail',
    ellipsis: true,
    render: (_, record) => record.detail || '-',
  },
];

export interface ClassifierHealthBannerProps {
  /** 健康快照获取函数；默认消费只读 API，测试可注入。 */
  fetchHealth?: () => Promise<ClassifierHealthSnapshotDto>;
  /** 轮询间隔毫秒数；默认 30s，传非正数禁用轮询。卸载即清理。 */
  refreshIntervalMs?: number;
}

const ClassifierHealthBanner: React.FC<ClassifierHealthBannerProps> = ({
  fetchHealth = getClassifierHealth,
  refreshIntervalMs = CLASSIFIER_HEALTH_REFRESH_INTERVAL_MS,
}) => {
  const [snapshot, setSnapshot] = useState<ClassifierHealthSnapshotDto | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const inFlightRef = useRef(false);

  const refresh = useCallback(async () => {
    if (inFlightRef.current) return;
    inFlightRef.current = true;
    try {
      const next = await fetchHealth();
      setSnapshot(next);
      setLoadFailed(false);
    } catch {
      // 服务端权威：请求失败 ⇒ 显式未知态，不沿用旧快照装作健康。
      setLoadFailed(true);
    } finally {
      inFlightRef.current = false;
    }
  }, [fetchHealth]);

  useEffect(() => {
    void refresh();
    if (refreshIntervalMs <= 0) {
      return undefined;
    }
    const timer = window.setInterval(() => {
      void refresh();
    }, refreshIntervalMs);
    return () => window.clearInterval(timer);
  }, [refresh, refreshIntervalMs]);

  // 首次拉取尚未返回时不渲染，避免闪烁。
  if (!snapshot && !loadFailed) {
    return null;
  }

  const decision = loadFailed
    ? FETCH_ERROR_DECISION
    : resolveBannerDecision(snapshot as ClassifierHealthSnapshotDto);
  if (decision.kind === 'hidden') {
    return null;
  }

  const detailItems =
    !loadFailed && (snapshot as ClassifierHealthSnapshotDto).classifiers.length > 0
      ? [
          {
            key: 'classifier-health-detail',
            label: '分类器健康明细',
            children: (
              <Table<ClassifierHealthItemDto>
                size="small"
                rowKey="classifierId"
                columns={DETAIL_COLUMNS}
                dataSource={(snapshot as ClassifierHealthSnapshotDto).classifiers}
                pagination={false}
              />
            ),
          },
        ]
      : [];

  return (
    <Alert
      role="alert"
      data-testid="classifier-health-banner"
      data-banner-kind={decision.kind}
      type={decision.kind}
      showIcon
      message={decision.headline}
      description={
        <div>
          <Typography.Paragraph style={{ marginBottom: detailItems.length ? 8 : 0 }}>
            {decision.description}
          </Typography.Paragraph>
          {detailItems.length > 0 ? (
            <Collapse ghost size="small" items={detailItems} />
          ) : null}
        </div>
      }
      style={{ marginBottom: 16 }}
    />
  );
};

export default ClassifierHealthBanner;
