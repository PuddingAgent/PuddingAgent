import { PageContainer } from '@ant-design/pro-components';
import { history, useIntl } from '@umijs/max';
import { Button, Card, Space, Typography } from 'antd';
import React from 'react';

const Admin: React.FC = () => {
  const intl = useIntl();
  return (
    <PageContainer
      title="Pudding Console"
      content={intl.formatMessage({
        id: 'pages.admin.subPage.title',
        defaultMessage: 'Pudding Console',
      })}
    >
      <Card>
        <Space direction="vertical" size={16}>
          <Typography.Title level={3} style={{ margin: 0 }}>
            Pudding 的设置与管理抽屉
          </Typography.Title>
          <Typography.Paragraph type="secondary" style={{ margin: 0, maxWidth: 720 }}>
            这里用于管理 Agent、场景、技能、模型资源与运行时配置。日常使用请回到独立对话页，让后台保持低频、安静、可靠。
          </Typography.Paragraph>
          <Button type="primary" onClick={() => history.push('/chat')}>
            返回对话
          </Button>
        </Space>
      </Card>
    </PageContainer>
  );
};

export default Admin;
