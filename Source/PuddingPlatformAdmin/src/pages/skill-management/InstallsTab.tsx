import { Button, Input, message, Space, Tag, Typography } from 'antd';
import { PlusOutlined, SearchOutlined, ThunderboltOutlined } from '@ant-design/icons';
import React, { useCallback, useRef, useState } from 'react';
import { ProTable } from '@ant-design/pro-components';
import type { ActionType, ProColumns } from '@ant-design/pro-components';
import type { HubSkillInstallDto, HubSkillSummaryDto } from '@/services/platform/api';
import { listHubInstalls, listHubSkills } from '@/services/platform/api';
import { compareVersions, errText, formatDateTime, HubEmpty } from './skillHubShared';
import { CheckHubUpdatesModal, RegisterHubInstallModal } from './SkillWriteModals';

const { Text } = Typography;

/**
 * 安装台账 Tab：技能 / Agent 实例 / 工作区 / 已装版本 / 是否落后最新 / 安装时间。
 * 「全部更新」只提示不代执行（人类决策，由各 Agent 自行更新）。
 */
const InstallsTab: React.FC = () => {
  const tableRef = useRef<ActionType | undefined>(undefined);
  const latestVersionRef = useRef<Map<string, string>>(new Map());
  const [agentFilter, setAgentFilter] = useState('');

  // 写路径入口（2026-09-21）：登记安装 / 检查更新
  const [registerOpen, setRegisterOpen] = useState(false);
  const [updatesOpen, setUpdatesOpen] = useState(false);

  const handleUpdateAllHint = useCallback((): void => {
    message.info(
      '「全部更新」仅为提醒：系统不会代为执行，请各 Agent 实例通过 skill_hub 工具的 check_updates / install 自行完成升级。',
      6,
    );
  }, []);

  const columns: ProColumns<HubSkillInstallDto>[] = [
    {
      title: '技能',
      dataIndex: 'skillId',
      render: (_: unknown, record: HubSkillInstallDto) => (
        <Text code style={{ fontSize: 12 }}>
          {record.skillId}
        </Text>
      ),
    },
    { title: 'Agent 实例', dataIndex: 'agentInstanceId', ellipsis: true },
    {
      title: '工作区',
      dataIndex: 'workspaceId',
      render: (v: React.ReactNode) => v || <Text type="secondary">-</Text>,
    },
    {
      title: '已装版本',
      dataIndex: 'installedVersion',
      width: 110,
      render: (_: unknown, record: HubSkillInstallDto) => <Tag color="blue">v{record.installedVersion}</Tag>,
    },
    {
      title: '是否落后最新',
      key: 'outdated',
      width: 120,
      render: (_: unknown, record: HubSkillInstallDto) => {
        const latest = latestVersionRef.current.get(record.skillId);
        if (!latest) return <Text type="secondary">未知</Text>;
        return compareVersions(record.installedVersion, latest) < 0 ? (
          <Tag color="orange">落后（最新 v{latest}）</Tag>
        ) : (
          <Tag color="green">最新</Tag>
        );
      },
    },
    {
      title: '安装人',
      dataIndex: 'installedBy',
      width: 140,
      ellipsis: true,
    },
    {
      title: '安装时间',
      dataIndex: 'installedAt',
      width: 170,
      render: (_: unknown, record: HubSkillInstallDto) => (
        <Text style={{ fontSize: 12 }}>{formatDateTime(record.installedAt)}</Text>
      ),
    },
  ];

  return (
    <div>
      <ProTable<HubSkillInstallDto>
        actionRef={tableRef}
        rowKey={(record) =>
          `${record.skillId}@@${record.agentInstanceId}@@${record.installedVersion}@@${record.installedAt}`
        }
        columns={columns}
        request={async (params) => {
          try {
            const [installs, skills] = await Promise.all([
              listHubInstalls({
                agentInstanceId: agentFilter.trim() || undefined,
                page: params.current ?? 1,
                pageSize: params.pageSize ?? 20,
              }),
              listHubSkills({ page: 1, pageSize: 500 }),
            ]);
            latestVersionRef.current = new Map<string, string>(
              (skills ?? []).map((s: HubSkillSummaryDto) => [s.skillId, s.latestVersion]),
            );
            return { data: installs ?? [], success: true };
          } catch (e) {
            message.error(`加载安装台账失败：${errText(e)}`);
            return { data: [], success: false };
          }
        }}
        search={false}
        options={false}
        pagination={{ defaultPageSize: 20, showSizeChanger: false }}
        locale={{ emptyText: <HubEmpty description="暂无安装记录：Agent 通过 skill_hub 安装技能后会在此登记台账" /> }}
        headerTitle={
          <Space size={8} wrap>
            <Input.Search
              allowClear
              placeholder="按 Agent 实例 ID 过滤"
              style={{ width: 260 }}
              value={agentFilter}
              onChange={(e) => setAgentFilter(e.target.value)}
              onSearch={() => tableRef.current?.reload()}
            />
          </Space>
        }
        toolBarRender={() => [
          <Button
            key="register"
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => setRegisterOpen(true)}
          >
            登记安装
          </Button>,
          <Button key="checkUpdates" icon={<SearchOutlined />} onClick={() => setUpdatesOpen(true)}>
            检查更新
          </Button>,
          <Button key="updateAll" icon={<ThunderboltOutlined />} onClick={handleUpdateAllHint}>
            全部更新（提示）
          </Button>,
          <Button key="reload" onClick={() => tableRef.current?.reload()}>
            刷新
          </Button>,
        ]}
      />

      {/* 写路径：登记安装台账 / 检查待更新清单（不新增依赖，仅 antd 组件） */}
      <RegisterHubInstallModal
        open={registerOpen}
        onClose={() => setRegisterOpen(false)}
        onRegistered={() => tableRef.current?.reload()}
      />
      <CheckHubUpdatesModal open={updatesOpen} onClose={() => setUpdatesOpen(false)} />
    </div>
  );
};

export default InstallsTab;
