import { Button, Card, Empty, Progress, Space, Tag, Tooltip, Typography } from 'antd';
import * as React from 'react';
import { useCallback, useState } from 'react';
import { formatBytes, getDatabaseSpace } from './api';
import type { StorageDatabaseSpace, StorageInventoryDatabase } from './types';
import { computeUsageBreakdown, resolveFreeBytes } from './usageBreakdown';
import type { UsageRow } from './usageBreakdown';

/** 区块里每个库的分母说明（A/B 两条口径要写在界面上，不能只写在代码注释里）。 */
const DENOMINATOR_NOTE = '各占比以该库文件大小为分母';

/** 跨库口径说明（C）。 */
const CROSS_DATABASE_NOTE =
  '各库独立归一：百分比与进度条都是「该项 ÷ 该库文件大小」，100% 表示该库已被这张表完整覆盖；' +
  '因此跨库的进度条长度不能用来比较体量，比体量请看绝对值。';

/** 面板入参：只把快照里已存在的三件事实（库大小口径 / 空闲页 / 顶部合计）接进来，不新增接口。 */
export interface DatabaseSpacePanelProps {
  /** 快照里的数据库元数据：只为取空闲页（freelist）与库大小口径；可以不传。 */
  databases?: readonly StorageInventoryDatabase[];
  /** 顶部「Pudding 数据约」的合计，用来解释各库相加为何小于它；可以不传。 */
  headlineTotalBytes?: number | null;
}

/**
 * 「数据库占用（按数据类）」面板。
 *
 * <p>
 * 为什么是**按钮触发**而不是随页面自动加载：这几个数字要对每个库逐表取行数并对有界样本求和，
 * 在 GB 级库上需要可感知的时间。自动加载会让每次进存储页都付这个代价，因此做成显式动作
 * —— 与服务端 `GET /api/admin/storage/database-space` 的定位一致（overview 那条热路径明确不触发扫描）。
 * </p>
 *
 * <p>
 * 数字口径（见 `usageBreakdown.ts`）：文件级总量精确（主文件 + WAL + SHM）；按数据类的字节由每表
 * 精确行数 × 抽样平均行长得出（或 dbstat 可用时按 B 树精确）。**每个库的表都补齐到该库文件大小**
 * ——缺的那部分是索引页与 freelist，用兜底行如实列出，而不是让「各子项之和」自己漂着。
 * </p>
 */
export const DatabaseSpacePanel: React.FC<DatabaseSpacePanelProps> = ({
  databases,
  headlineTotalBytes,
}) => {
  const [spaces, setSpaces] = useState<StorageDatabaseSpace[] | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setSpaces(await getDatabaseSpace());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : '读取数据库占用失败');
    } finally {
      setLoading(false);
    }
  }, []);

  const measuredTotal = (spaces ?? []).reduce((sum, item) => sum + (item.fileBytes ?? 0), 0);
  const headlineGap =
    headlineTotalBytes && headlineTotalBytes > 0 ? headlineTotalBytes - measuredTotal : null;

  return (
    <Card
      size="small"
      title="数据库占用（按数据类）"
      extra={
        <Space size={8}>
          <Button size="small" loading={loading} onClick={() => void load()}>
            {spaces ? '重新测量' : '查看数据库占用'}
          </Button>
        </Space>
      }
    >
      {error ? (
        <Typography.Text type="danger">{error}</Typography.Text>
      ) : null}

      {!spaces && !error ? (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          点击「查看数据库占用」按数据类测量各库占用（逐表取行数，GB 级库需要几秒）。
        </Typography.Text>
      ) : null}

      {spaces?.length ? (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {CROSS_DATABASE_NOTE}
          </Typography.Text>

          {spaces.map((space) => (
            <DatabaseSpaceBlock key={space.key} space={space} databases={databases} />
          ))}

          {headlineGap === null ? null : (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              这些库合计 {formatBytes(measuredTotal)}（主文件 + WAL + 共享内存）｜顶部「Pudding 数据约」{' '}
              {formatBytes(headlineTotalBytes ?? 0)}｜
              {headlineGap >= 0
                ? `差额 ${formatBytes(headlineGap)} 是顶部合计里的非数据库文件（如全文检索索引目录）与未测量的库`
                : `各库合计比顶部合计多 ${formatBytes(-headlineGap)}（两处测量时点不同）`}
            </Typography.Text>
          )}
        </Space>
      ) : null}
    </Card>
  );
};

const DatabaseSpaceBlock: React.FC<{
  space: StorageDatabaseSpace;
  databases?: readonly StorageInventoryDatabase[];
}> = ({ space, databases }) => {
  if (!space.exists) {
    return (
      <div>
        <Typography.Text strong>{space.displayName}</Typography.Text>{' '}
        <Typography.Text type="secondary">库不存在</Typography.Text>
      </div>
    );
  }

  const breakdown = computeUsageBreakdown(space, { freeBytes: resolveFreeBytes(space, databases) });

  return (
    <div>
      <Space size={8} align="baseline" wrap>
        <Typography.Text strong>{space.displayName}</Typography.Text>
        <Tooltip title="该库主文件 + WAL + 共享内存；与总览表「大小」列同一口径">
          <Typography.Text type="secondary">{formatBytes(breakdown.denominatorBytes)}</Typography.Text>
        </Tooltip>
        {breakdown.denominatorIsFallback ? (
          <Tooltip title="库文件大小未取到，百分比改用 页大小 × 页数 作分母">
            <Tag color="orange">分母为页数推算</Tag>
          </Tooltip>
        ) : null}
        {breakdown.estimated ? (
          <Tooltip title="按数据类的字节由「每表精确行数 × 抽样平均行长」推算，不含索引页">
            <Tag color="gold">估算</Tag>
          </Tooltip>
        ) : null}
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {DENOMINATOR_NOTE}
        </Typography.Text>
      </Space>

      {breakdown.rows.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有可归类的数据" />
      ) : (
        <Space direction="vertical" size={2} style={{ width: '100%', marginTop: 4 }}>
          {breakdown.rows.map((row) => (
            <UsageRowLine key={row.targetId} row={row} />
          ))}
        </Space>
      )}

      {breakdown.closed ? null : (
        <Typography.Text type="warning" style={{ fontSize: 12, display: 'block', marginTop: 4 }}>
          {breakdown.overflowBytes > 0
            ? `各数据类与空闲页之和超出该库文件大小 ${formatBytes(breakdown.overflowBytes)}（数据类为估算，或两处测量时点不同）`
            : '各部分之和与库文件大小未闭合：该库没有取到页统计'}
        </Typography.Text>
      )}
    </div>
  );
};

/** 一行占用：名称 / 字节 / 百分比 / 表数 / 进度条。 */
const UsageRowLine: React.FC<{ row: UsageRow }> = ({ row }) => {
  const label = (
    <Typography.Text style={{ minWidth: 180, display: 'inline-block' }}>{row.displayName}</Typography.Text>
  );

  return (
    <div>
      <Space size={8} style={{ width: '100%' }} align="baseline" wrap>
        {row.hint ? <Tooltip title={row.hint}>{label}</Tooltip> : label}
        <Typography.Text type="secondary" style={{ fontSize: 12, minWidth: 96, display: 'inline-block' }}>
          {row.estimated || row.inferred ? '≈' : ''}
          {formatBytes(row.bytes)}
        </Typography.Text>
        <Typography.Text type="secondary" style={{ fontSize: 12, minWidth: 56, display: 'inline-block' }}>
          {row.percent}%
        </Typography.Text>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {row.kind === 'class'
            ? `${row.tableCount} 张表`
            : row.kind === 'free'
              ? '可复用'
              : '按差值推算'}
        </Typography.Text>
      </Space>
      <Progress
        percent={row.percent}
        showInfo={false}
        size="small"
        status={row.kind === 'class' ? 'active' : 'normal'}
      />
    </div>
  );
};
