import { PageContainer } from '@ant-design/pro-components';
import { AppstoreOutlined, DatabaseOutlined, ThunderboltOutlined, UserOutlined } from '@ant-design/icons';
import { history } from '@umijs/max';
import { Button, Card, Col, Row, Space, Typography } from 'antd';
import React from 'react';
import { buildWorkspacePath, buildWorkspaceSettingsPath } from '@/utils/workspaceNavigation';

const adminQuickEntries = [
  {
    title: '工作区管理',
    description: '进入默认工作区的后台设置、成员和 Agent 配置。',
    icon: <AppstoreOutlined />,
    path: buildWorkspaceSettingsPath('default'),
  },
  {
    title: 'LLM 资源池',
    description: '维护服务商、模型、API Key 和调用协议。',
    icon: <ThunderboltOutlined />,
    path: '/llm-resource-pool',
  },
  {
    title: '组织权限',
    description: '管理用户、团队和权限角色。',
    icon: <UserOutlined />,
    path: '/system-config/user-management',
  },
  {
    title: '记忆图书馆',
    description: '查看工作区、Agent 和知识来源的记忆资产。',
    icon: <DatabaseOutlined />,
    path: '/memory-library',
  },
];

const Admin: React.FC = () => {
  return (
    <PageContainer
      title="后台管理首页"
      content="集中管理 Pudding 的资源、权限、运行配置和系统诊断。"
    >
      <Row gutter={[16, 16]}>
        {adminQuickEntries.map((entry) => (
          <Col key={entry.path} xs={24} sm={12} xl={6}>
            <Card
              hoverable
              style={{ height: '100%' }}
              onClick={() => history.push(entry.path)}
            >
              <Space align="start" size={12}>
                <span style={{ color: 'var(--ant-color-primary)', fontSize: 20 }}>
                  {entry.icon}
                </span>
                <Space direction="vertical" size={6}>
                  <Typography.Text strong>{entry.title}</Typography.Text>
                  <Typography.Text type="secondary" style={{ fontSize: 13 }}>
                    {entry.description}
                  </Typography.Text>
                </Space>
              </Space>
            </Card>
          </Col>
        ))}
      </Row>
      <Space style={{ marginTop: 20 }}>
        <Button type="primary" onClick={() => history.push(buildWorkspacePath())}>
          进入用户工作空间
        </Button>
        <Button onClick={() => history.push('/diagnostics/overview')}>
          查看系统诊断
        </Button>
      </Space>
    </PageContainer>
  );
};

export default Admin;
