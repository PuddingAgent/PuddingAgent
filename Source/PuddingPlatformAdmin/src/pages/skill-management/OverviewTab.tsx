import { Button, Card, Col, Progress, Row, Statistic, Table, message } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import type { HubSkillStatsDto, HubSkillSummaryDto } from '@/services/platform/api';
import { getSkillHubStats } from '@/services/platform/api';
import {
  ALL_EVO_ACTIONS,
  HubEmpty,
  evoActionTag,
  errText,
  formatDateTime,
  renderSkillStatusTag,
} from './skillHubShared';

/** 概览 Tab：指标卡 7 项 + 进化动作分布 + Top 安装榜（无数据时展示友好空态） */
const OverviewTab: React.FC = () => {
  const [stats, setStats] = useState<HubSkillStatsDto | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const data = await getSkillHubStats();
      setStats(data ?? null);
    } catch (e) {
      setStats(null);
      message.error(`加载概览失败：${errText(e)}`);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const metrics: { title: string; value: number | undefined }[] = [
    { title: '技能总数', value: stats?.totalSkills },
    { title: '活跃', value: stats?.activeSkills },
    { title: '已退役', value: stats?.retiredSkills },
    { title: '版本总数', value: stats?.totalVersions },
    { title: '安装总数', value: stats?.totalInstalls },
    { title: '覆盖 Agent 数', value: stats?.distinctAgents },
    { title: '已进化技能数', value: stats?.evolvedSkills },
  ];

  const actionCounts = new Map<string, number>(
    (stats?.evolutionActionCounts ?? []).map((c) => [c.action, c.count]),
  );
  const maxActionCount = Math.max(1, ...(stats?.evolutionActionCounts ?? []).map((c) => c.count));

  const topColumns = [
    {
      title: '技能',
      dataIndex: 'name',
      render: (_: unknown, record: HubSkillSummaryDto) => (
        <span>
          {record.name}{' '}
          <code style={{ fontSize: 11 }}>{record.skillId}</code>
        </span>
      ),
    },
    { title: '最新版本', dataIndex: 'latestVersion', width: 100 },
    {
      title: '状态',
      dataIndex: 'status',
      width: 90,
      render: (status: string) => renderSkillStatusTag(status),
    },
    { title: '装机 Agent 数', dataIndex: 'installCount', width: 120 },
  ];

  return (
    <div>
      <Card
        size="small"
        title="核心指标"
        extra={
          <Button size="small" icon={<ReloadOutlined />} onClick={() => void load()} loading={loading}>
            刷新
          </Button>
        }
      >
        {stats ? (
          <Row gutter={[16, 16]}>
            {metrics.map((m) => (
              <Col xs={12} sm={8} lg={6} xxl={3} key={m.title}>
                <Statistic title={m.title} value={m.value ?? 0} />
              </Col>
            ))}
          </Row>
        ) : loading ? (
          <HubEmpty description="概览加载中…" />
        ) : (
          <HubEmpty description="暂无概览数据：Hub 技能库为空或服务尚未就绪，待技能发布后此处将展示 7 项核心指标" />
        )}
      </Card>

      <Card size="small" title="进化动作分布" style={{ marginTop: 16 }}>
        {stats && (stats.evolutionActionCounts ?? []).length > 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {ALL_EVO_ACTIONS.map((action) => {
              const count = actionCounts.get(action) ?? 0;
              return (
                <Row key={action} gutter={12} align="middle">
                  <Col span={3}>{evoActionTag(action)}</Col>
                  <Col span={17}>
                    <Progress
                      percent={Math.round((count / maxActionCount) * 100)}
                      showInfo={false}
                      size="small"
                    />
                  </Col>
                  <Col span={4} style={{ textAlign: 'right' }}>
                    <span style={{ fontSize: 12 }}>{count} 次</span>
                  </Col>
                </Row>
              );
            })}
          </div>
        ) : (
          <HubEmpty description="暂无进化动作记录：技能发布新版本（patch/split/compress 等）后此处将展示动作分布" />
        )}
      </Card>

      <Card size="small" title="Top 安装榜" style={{ marginTop: 16 }}>
        {stats && (stats.topInstalled ?? []).length > 0 ? (
          <Table<HubSkillSummaryDto>
            rowKey="skillId"
            size="small"
            columns={topColumns}
            dataSource={stats.topInstalled}
            pagination={false}
          />
        ) : (
          <HubEmpty description="暂无安装数据：Agent 安装技能后此处将展示安装榜（统计时间可参考下方生成时间）" />
        )}
        {stats && (
          <div style={{ marginTop: 8, fontSize: 12, color: 'rgba(0,0,0,0.45)' }}>
            统计生成时间：{formatDateTime(stats.generatedAt)}
          </div>
        )}
      </Card>
    </div>
  );
};

export default OverviewTab;
