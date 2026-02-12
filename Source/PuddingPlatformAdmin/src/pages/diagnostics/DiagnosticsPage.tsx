// ─── DiagnosticsPage：诊断仪表盘入口 (ADR-023 Phase 2) ──────────
import { PageContainer } from '@ant-design/pro-components';
import {
  Card,
  Col,
  Row,
  Statistic,
  Tag,
  Typography,
  Spin,
  Alert,
  Progress,
  Space,
  Badge,
} from 'antd';
import {
  CheckCircleOutlined,
  CloseCircleOutlined,
  WarningOutlined,
  QuestionCircleOutlined,
  ThunderboltOutlined,
  ApiOutlined,
  ClockCircleOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import React, { useCallback, useEffect, useState } from 'react';
import { history } from '@umijs/max';
import {
  getComponentHealth,
  getEventStats,
} from './api';
import type { ComponentHealthItem, EventStats } from './types';

const { Text, Title } = Typography;

/** 健康状态 → 颜色映射 */
const healthColor: Record<string, string> = {
  healthy: '#22c55e',
  degraded: '#f59e0b',
  failing: '#ef4444',
  unknown: '#9ca3af',
};

/** 健康状态 → 图标映射 */
const healthIcon: Record<string, React.ReactNode> = {
  healthy: <CheckCircleOutlined />,
  degraded: <WarningOutlined />,
  failing: <CloseCircleOutlined />,
  unknown: <QuestionCircleOutlined />,
};

const DiagnosticsPage: React.FC = () => {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [components, setComponents] = useState<ComponentHealthItem[]>([]);
  const [eventStats, setEventStats] = useState<EventStats | null>(null);

  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [compData, evtData] = await Promise.all([
        getComponentHealth(),
        getEventStats(),
      ]);
      setComponents(compData || []);
      setEventStats(evtData);
    } catch (e: any) {
      setError(e?.message || '获取诊断数据失败');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  /** 计算总体健康状态 */
  const overallStatus = (() => {
    if (components.length === 0) return 'unknown';
    if (components.some((c) => c.status === 'failing')) return 'failing';
    if (components.some((c) => c.status === 'degraded')) return 'degraded';
    if (components.every((c) => c.status === 'healthy')) return 'healthy';
    return 'unknown';
  })();

  return (
    <PageContainer
      header={{
        title: '诊断概览',
        extra: [
          <Text key="refresh" type="secondary" onClick={fetchData} style={{ cursor: 'pointer' }}>
            <ReloadOutlined spin={loading} /> 刷新
          </Text>,
        ],
      }}
    >
      {error && (
        <Alert message={error} type="error" showIcon closable style={{ marginBottom: 16 }} />
      )}

      <Spin spinning={loading}>
        {/* ── 总体状态卡片 ── */}
        <Row gutter={[16, 16]} style={{ marginBottom: 24 }}>
          <Col xs={24} sm={12} md={6}>
            <Card>
              <Statistic
                title="系统状态"
                valueRender={() => (
                  <Tag
                    color={overallStatus === 'healthy' ? 'green' : overallStatus === 'degraded' ? 'gold' : 'red'}
                    data-testid={`status-badge-${overallStatus}`}
                  >
                    {overallStatus === 'healthy' ? '健康' : overallStatus === 'degraded' ? '降级' : '异常'}
                  </Tag>
                )}
              />
            </Card>
          </Col>
          <Col xs={24} sm={12} md={6}>
            <Card>
              <Statistic title="活跃组件" value={components.length} prefix={<ApiOutlined />} />
            </Card>
          </Col>
          <Col xs={24} sm={12} md={6}>
            <Card
              hoverable
              onClick={() => history.push('/diagnostics/timeline')}
            >
              <Statistic
                title="运行时时间线"
                value={eventStats ? eventStats.byStatus.reduce((a, b) => a + b.count, 0) : 0}
                prefix={<ClockCircleOutlined />}
                suffix="条记录"
              />
            </Card>
          </Col>
          <Col xs={24} sm={12} md={6}>
            <Card
              hoverable
              onClick={() => history.push('/diagnostics/subagent-runs')}
            >
              <Statistic
                title="子代理运行"
                value="查看"
                prefix={<ThunderboltOutlined />}
              />
            </Card>
          </Col>
        </Row>

        {/* ── 组件健康卡片网格 ── */}
        <Title level={5} style={{ marginBottom: 16 }}>组件健康状态</Title>
        <Row gutter={[16, 16]}>
          {components.map((comp) => (
            <Col xs={24} sm={12} lg={8} key={comp.component}>
              <Card
                size="small"
                title={
                  <Space>
                    <Badge color={healthColor[comp.status]} />
                    <span>{comp.component}</span>
                    <Tag
                      color={
                        comp.status === 'healthy' ? 'green' :
                        comp.status === 'degraded' ? 'gold' :
                        comp.status === 'failing' ? 'red' : 'default'
                      }
                      data-testid={`status-badge-${comp.status}`}
                    >
                      {comp.status === 'healthy' ? '健康' :
                       comp.status === 'degraded' ? '降级' :
                       comp.status === 'failing' ? '故障' : '未知'}
                    </Tag>
                  </Space>
                }
              >
                <Row gutter={8}>
                  <Col span={8}>
                    <Statistic
                      title="成功"
                      value={comp.succeededCount}
                      valueStyle={{ color: '#22c55e', fontSize: 20 }}
                    />
                  </Col>
                  <Col span={8}>
                    <Statistic
                      title="失败"
                      value={comp.failedCount}
                      valueStyle={{ color: comp.failedCount > 0 ? '#ef4444' : undefined, fontSize: 20 }}
                    />
                  </Col>
                  <Col span={8}>
                    <Statistic
                      title="重试"
                      value={comp.retriedCount}
                      valueStyle={{ color: comp.retriedCount > 0 ? '#f59e0b' : undefined, fontSize: 20 }}
                    />
                  </Col>
                </Row>
                <Progress
                  percent={
                    comp.startedCount > 0
                      ? Math.round((comp.succeededCount / comp.startedCount) * 100)
                      : 100
                  }
                  success={{ percent: comp.startedCount > 0 ? Math.round((comp.succeededCount / comp.startedCount) * 100) : 100 }}
                  status={comp.status === 'failing' ? 'exception' : comp.status === 'degraded' ? 'active' : 'success'}
                  size="small"
                  style={{ marginTop: 8 }}
                />
                {comp.lastError && (
                  <Text type="danger" ellipsis style={{ fontSize: 12 }}>
                    最近错误: {comp.lastError}
                  </Text>
                )}
              </Card>
            </Col>
          ))}
          {!loading && components.length === 0 && (
            <Col span={24}>
              <Text type="secondary">暂无组件数据</Text>
            </Col>
          )}
        </Row>

        {/* ── 事件统计摘要 ── */}
        {eventStats && (
          <>
            <Title level={5} style={{ marginTop: 24, marginBottom: 16 }}>事件统计</Title>
            <Row gutter={[16, 16]}>
              <Col xs={24} md={12}>
                <Card size="small" title="按状态分布">
                  {eventStats.byStatus.map(({ status, count }) => (
                    <Row key={status} style={{ marginBottom: 8 }}>
                      <Col span={16}>
                        <Tag color={status === 'succeeded' ? 'green' : status === 'failed' ? 'red' : 'blue'}>
                          {status}
                        </Tag>
                      </Col>
                      <Col span={8}>
                        <Statistic value={count} valueStyle={{ fontSize: 18 }} />
                      </Col>
                    </Row>
                  ))}
                </Card>
              </Col>
              <Col xs={24} md={12}>
                <Card size="small" title="按组件分布">
                  {eventStats.byComponent.slice(0, 10).map(({ component, count }) => (
                    <Row key={component} style={{ marginBottom: 8 }}>
                      <Col span={16}>
                        <Text ellipsis style={{ maxWidth: 150 }}>{component}</Text>
                      </Col>
                      <Col span={8}>
                        <Statistic value={count} valueStyle={{ fontSize: 18 }} />
                      </Col>
                    </Row>
                  ))}
                </Card>
              </Col>
            </Row>
          </>
        )}
      </Spin>
    </PageContainer>
  );
};

export default DiagnosticsPage;
