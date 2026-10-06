import { Button, Card, Empty, Progress, Space, Typography } from 'antd';
import * as React from 'react';
import { useCallback, useState } from 'react';
import { formatBytes, getDatabaseSpace } from './api';
import type { StorageDatabaseSpace } from './types';

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
 * 数字口径：文件级总量精确（页统计 + WAL/SHM）；按数据类的字节由每表**精确行数 × 抽样平均行长**得出
 * （或 dbstat 可用时按 B 树精确）。跨类的**相对占比**可用 —— 同一把尺子量所有表。
 * 按用户要求，界面上**不标注「估算」**字样。
 * </p>
 */
export const DatabaseSpacePanel: React.FC = () => {
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
          {spaces.map((space) => (
            <DatabaseSpaceBlock key={space.key} space={space} />
          ))}
        </Space>
      ) : null}
    </Card>
  );
};

const DatabaseSpaceBlock: React.FC<{ space: StorageDatabaseSpace }> = ({ space }) => {
  if (!space.exists) {
    return (
      <div>
        <Typography.Text strong>{space.displayName}</Typography.Text>{' '}
        <Typography.Text type="secondary">库不存在</Typography.Text>
      </div>
    );
  }

  const classified = space.dataClasses ?? [];
  const total = classified.reduce((sum, item) => sum + item.bytes, 0);

  return (
    <div>
      <Space size={8} align="baseline" wrap>
        <Typography.Text strong>{space.displayName}</Typography.Text>
        <Typography.Text type="secondary">{formatBytes(space.fileBytes)}</Typography.Text>
        {space.perTableAvailable ? null : (
          // 只说明"按表怎么来的"这件事本身；不写「估算」字样（用户要求）。
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            按数据类聚合
          </Typography.Text>
        )}
      </Space>

      {classified.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有可归类的数据" />
      ) : (
        <Space direction="vertical" size={2} style={{ width: '100%', marginTop: 4 }}>
          {classified.map((item) => {
            const percent = total > 0 ? Math.round((item.bytes / total) * 1000) / 10 : 0;
            return (
              <div key={item.targetId}>
                <Space size={8} style={{ width: '100%' }} align="baseline" wrap>
                  <Typography.Text style={{ minWidth: 140, display: 'inline-block' }}>
                    {item.displayName}
                  </Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12, minWidth: 90, display: 'inline-block' }}>
                    {formatBytes(item.bytes)}
                  </Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12, minWidth: 56, display: 'inline-block' }}>
                    {percent}%
                  </Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {item.tables?.length ?? 0} 张表
                  </Typography.Text>
                </Space>
                <Progress
                  percent={percent}
                  showInfo={false}
                  size="small"
                  status={item.targetId === 'unclassified' ? 'normal' : 'active'}
                />
              </div>
            );
          })}
        </Space>
      )}
    </div>
  );
};
