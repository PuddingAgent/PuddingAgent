import { PageContainer, ProDescriptions, ProTable } from '@ant-design/pro-components';
import type { ProColumns } from '@ant-design/pro-components';
import { Badge, Drawer, Space, Tag, Typography } from 'antd';
import React, { useState } from 'react';
import { getAgentTemplate, listAgentTemplates, type AgentTemplateDefinition } from '@/services/platform/api';

const { Text } = Typography;

const templateTypeConfig = {
  Service: { color: 'blue', label: '服务型' },
  Task: { color: 'green', label: '任务型' },
  Audit: { color: 'orange', label: '审计型' },
};

const AgentTemplatePage: React.FC = () => {
  const [drawerVisible, setDrawerVisible] = useState(false);
  const [selectedTemplate, setSelectedTemplate] = useState<AgentTemplateDefinition | null>(null);

  const handleViewDetail = async (templateId: string) => {
    const detail = await getAgentTemplate(templateId);
    setSelectedTemplate(detail);
    setDrawerVisible(true);
  };

  const columns: ProColumns<AgentTemplateDefinition>[] = [
    {
      title: 'Template ID',
      dataIndex: 'templateId',
      copyable: true,
      width: 180,
      ellipsis: true,
    },
    {
      title: '名称',
      dataIndex: 'name',
      render: (_, record) => (
        <Space>
          <span style={{ fontWeight: 500 }}>{record.name}</span>
        </Space>
      ),
    },
    {
      title: '描述',
      dataIndex: 'description',
      ellipsis: true,
      search: false,
    },
    {
      title: '类型',
      dataIndex: 'templateType',
      width: 100,
      render: (_, record) => {
        const cfg = templateTypeConfig[record.templateType] ?? { color: 'default', label: record.templateType };
        return <Tag color={cfg.color}>{cfg.label}</Tag>;
      },
    },
    {
      title: '技能数',
      search: false,
      width: 80,
      render: (_, record) => <Tag>{record.skillIds.length}</Tag>,
    },
    {
      title: '首选模型',
      search: false,
      ellipsis: true,
      width: 140,
      render: (_, record) => (
        <Text type="secondary">{record.runtime?.preferredModel ?? '平台默认'}</Text>
      ),
    },
    {
      title: '上下文限制',
      search: false,
      width: 100,
      render: (_, record) => (
        record.runtime ? <Tag>{record.runtime.maxContextTokens.toLocaleString()} tokens</Tag> : '—'
      ),
    },
    {
      title: '操作',
      valueType: 'option',
      width: 80,
      render: (_, record) => [
        <a key="detail" onClick={() => handleViewDetail(record.templateId)}>
          详情
        </a>,
      ],
    },
  ];

  return (
    <PageContainer
      header={{
        title: 'Agent 模板',
        subTitle: '查看平台注册的所有 Agent 蓝图定义',
      }}
    >
      <ProTable<AgentTemplateDefinition>
        rowKey="templateId"
        columns={columns}
        request={async () => {
          const data = await listAgentTemplates();
          return { data, success: true, total: data.length };
        }}
        search={false}
        pagination={{ pageSize: 20 }}
        options={{ reload: true, density: true }}
        cardBordered
      />

      <Drawer
        title={selectedTemplate ? `${selectedTemplate.name} 详情` : ''}
        open={drawerVisible}
        onClose={() => setDrawerVisible(false)}
        width={520}
      >
        {selectedTemplate && (
          <ProDescriptions
            dataSource={selectedTemplate}
            column={1}
            columns={[
              { title: 'Template ID', dataIndex: 'templateId' },
              { title: '名称', dataIndex: 'name' },
              { title: '描述', dataIndex: 'description' },
              {
                title: '类型',
                render: () => {
                  const cfg = templateTypeConfig[selectedTemplate.templateType];
                  return <Tag color={cfg.color}>{cfg.label}</Tag>;
                },
              },
              {
                title: '能力权限',
                render: () => (
                  <Space wrap>
                    {selectedTemplate.capability?.allowShellExecution && <Tag color="red">Shell 执行</Tag>}
                    {selectedTemplate.capability?.allowFileWrite && <Tag color="orange">文件写入</Tag>}
                    {selectedTemplate.capability?.allowNetworkAccess && <Tag color="blue">网络访问</Tag>}
                    {!selectedTemplate.capability && <Text type="secondary">无额外权限</Text>}
                  </Space>
                ),
              },
              {
                title: '技能列表',
                render: () => (
                  <Space wrap>
                    {selectedTemplate.skillIds.length === 0
                      ? <Text type="secondary">无</Text>
                      : selectedTemplate.skillIds.map(s => <Tag key={s}>{s}</Tag>)}
                  </Space>
                ),
              },
              {
                title: '首选模型',
                render: () => selectedTemplate.runtime?.preferredModel ?? '平台默认',
              },
              {
                title: '最大上下文',
                render: () => selectedTemplate.runtime
                  ? `${selectedTemplate.runtime.maxContextTokens.toLocaleString()} tokens`
                  : '—',
              },
              {
                title: '每会话最大轮次',
                render: () => selectedTemplate.runtime?.maxTurnsPerSession?.toString() ?? '—',
              },
            ]}
          />
        )}
      </Drawer>
    </PageContainer>
  );
};

export default AgentTemplatePage;
