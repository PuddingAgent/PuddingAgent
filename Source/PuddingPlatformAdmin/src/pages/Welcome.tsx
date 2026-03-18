import {
  ApiOutlined,
  BranchesOutlined,
  ClusterOutlined,
  RobotOutlined,
  ThunderboltOutlined,
} from '@ant-design/icons';
import { PageContainer, ProCard, StatisticCard } from '@ant-design/pro-components';
import { useModel } from '@umijs/max';
import { Badge, Col, List, Row, Space, Tag, theme, Typography } from 'antd';
import React from 'react';

const { Text } = Typography;

const recentActivities = [
  {
    id: 1,
    agent: 'CodeReviewer',
    action: '完成代码审查任务 #PR-421',
    time: '2 分钟前',
    status: 'success' as const,
  },
  {
    id: 2,
    agent: 'TaskPlanner',
    action: '新建执行计划，分配 3 个子任务',
    time: '5 分钟前',
    status: 'processing' as const,
  },
  {
    id: 3,
    agent: 'DataAnalyst',
    action: '数据集分析完成，生成报告',
    time: '12 分钟前',
    status: 'success' as const,
  },
  {
    id: 4,
    agent: 'Monitor',
    action: '检测到中心锁竞争异常事件',
    time: '18 分钟前',
    status: 'error' as const,
  },
  {
    id: 5,
    agent: 'Summarizer',
    action: '生成会话上下文摘要',
    time: '30 分钟前',
    status: 'default' as const,
  },
];

const systemServices = [
  { name: 'PuddingRuntime', status: 'success' as const, label: '运行中' },
  { name: 'PuddingController', status: 'success' as const, label: '运行中' },
  { name: 'PuddingGateway', status: 'success' as const, label: '运行中' },
  { name: 'PuddingMemoryEngine', status: 'warning' as const, label: '降级运行' },
  { name: 'CoordinationBus', status: 'success' as const, label: '运行中' },
];

const Welcome: React.FC = () => {
  const { token } = theme.useToken();
  const { initialState } = useModel('@@initialState');
  const userName = initialState?.currentUser?.name ?? '管理员';

  return (
    <PageContainer
      title="平台概览"
      subTitle={`欢迎回来，${userName}`}
      extra={
        <Tag color="processing" style={{ marginLeft: 8 }}>
          v1.0 · 开发预览
        </Tag>
      }
    >
      {/* ── 统计卡片行 ─────────────────────────────────── */}
      <StatisticCard.Group style={{ marginBottom: 24 }}>
        <StatisticCard
          statistic={{
            title: '智能体',
            value: 12,
            icon: (
              <RobotOutlined
                style={{
                  fontSize: 28,
                  color: token.colorPrimary,
                  background: token.colorPrimaryBg,
                  padding: 8,
                  borderRadius: token.borderRadius,
                }}
              />
            ),
            description: (
              <Text type="secondary" style={{ fontSize: 12 }}>
                已注册 12 个智能体
              </Text>
            ),
          }}
        />
        <StatisticCard.Divider />
        <StatisticCard
          statistic={{
            title: '活跃会话',
            value: 5,
            icon: (
              <ApiOutlined
                style={{
                  fontSize: 28,
                  color: '#52c41a',
                  background: '#f6ffed',
                  padding: 8,
                  borderRadius: token.borderRadius,
                }}
              />
            ),
            description: (
              <Text type="secondary" style={{ fontSize: 12 }}>
                实时活跃
              </Text>
            ),
          }}
        />
        <StatisticCard.Divider />
        <StatisticCard
          statistic={{
            title: '工作空间',
            value: 3,
            icon: (
              <ClusterOutlined
                style={{
                  fontSize: 28,
                  color: '#faad14',
                  background: '#fffbe6',
                  padding: 8,
                  borderRadius: token.borderRadius,
                }}
              />
            ),
            description: (
              <Text type="secondary" style={{ fontSize: 12 }}>
                含 1 个默认空间
              </Text>
            ),
          }}
        />
        <StatisticCard.Divider />
        <StatisticCard
          statistic={{
            title: '今日事件',
            value: 847,
            trend: 'up',
            icon: (
              <ThunderboltOutlined
                style={{
                  fontSize: 28,
                  color: '#ff4d4f',
                  background: '#fff1f0',
                  padding: 8,
                  borderRadius: token.borderRadius,
                }}
              />
            ),
            description: (
              <Text type="secondary" style={{ fontSize: 12 }}>
                较昨日 +12%
              </Text>
            ),
          }}
        />
      </StatisticCard.Group>

      {/* ── 内容区 ──────────────────────────────────────── */}
      <Row gutter={[24, 24]}>
        {/* 最近活动 */}
        <Col xs={24} lg={15}>
          <ProCard title="最近活动" bordered headerBordered>
            <List
              itemLayout="horizontal"
              dataSource={recentActivities}
              renderItem={(item) => (
                <List.Item>
                  <List.Item.Meta
                    avatar={
                      <Badge
                        status={item.status}
                        style={{ marginTop: 6, marginRight: 4 }}
                      />
                    }
                    title={
                      <Space size={8}>
                        <Text
                          strong
                          style={{ color: token.colorPrimary }}
                        >
                          {item.agent}
                        </Text>
                        <Text>{item.action}</Text>
                      </Space>
                    }
                    description={
                      <Text
                        type="secondary"
                        style={{ fontSize: 12 }}
                      >
                        {item.time}
                      </Text>
                    }
                  />
                </List.Item>
              )}
            />
          </ProCard>
        </Col>

        {/* 右侧列：快速操作 + 系统服务 */}
        <Col xs={24} lg={9}>
          <ProCard
            title="快速操作"
            bordered
            headerBordered
            style={{ marginBottom: 24 }}
          >
            <Space direction="vertical" style={{ width: '100%' }} size={12}>
              <ProCard
                hoverable
                size="small"
                style={{ cursor: 'pointer' }}
                onClick={() =>
                  (window.location.href = '/admin/sub-page')
                }
              >
                <Space>
                  <RobotOutlined style={{ color: token.colorPrimary }} />
                  <Text>管理智能体</Text>
                </Space>
              </ProCard>
              <ProCard
                hoverable
                size="small"
                style={{ cursor: 'pointer' }}
                onClick={() => (window.location.href = '/list')}
              >
                <Space>
                  <ClusterOutlined style={{ color: '#faad14' }} />
                  <Text>工作空间列表</Text>
                </Space>
              </ProCard>
              <ProCard
                hoverable
                size="small"
                style={{ cursor: 'pointer' }}
                onClick={() => (window.location.href = '/list')}
              >
                <Space>
                  <BranchesOutlined style={{ color: '#52c41a' }} />
                  <Text>查看会话记录</Text>
                </Space>
              </ProCard>
            </Space>
          </ProCard>

          <ProCard title="系统服务" bordered headerBordered>
            <Space direction="vertical" style={{ width: '100%' }} size={10}>
              {systemServices.map((svc) => (
                <div
                  key={svc.name}
                  style={{
                    display: 'flex',
                    justifyContent: 'space-between',
                    alignItems: 'center',
                  }}
                >
                  <Text style={{ fontSize: 13 }}>{svc.name}</Text>
                  <Badge status={svc.status} text={svc.label} />
                </div>
              ))}
            </Space>
          </ProCard>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default Welcome;
