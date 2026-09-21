import { PageContainer } from '@ant-design/pro-components';
import { Tabs } from 'antd';
import React from 'react';
import OverviewTab from './OverviewTab';
import SkillsTab from './SkillsTab';
import EvoMapTab from './EvoMapTab';
import InstallsTab from './InstallsTab';
import EventsTab from './EventsTab';
import LegacySkillPackages from './LegacySkillPackages';

/**
 * SKILL Hub 管理台（六 Tab）：
 * ① 概览 ② 技能库 ③ EVO MAP 进化图谱 ④ 安装台账 ⑤ 事件审计 ⑥ 技能包（旧，zip 上传功能原样保留）。
 * 契约：Docs/Features/SKILL-Hub技能中心与EVO-MAP设计方案-2026-09-21.md §7（6 Tab 以表格为准）。
 * `menu.skillManagement` i18n 键保持不变。
 */
const SkillManagementPage: React.FC = () => {
  const items = [
    { key: 'overview', label: '概览', children: <OverviewTab /> },
    { key: 'skills', label: '技能库', children: <SkillsTab /> },
    { key: 'evomap', label: 'EVO MAP', children: <EvoMapTab /> },
    { key: 'installs', label: '安装台账', children: <InstallsTab /> },
    { key: 'events', label: '事件审计', children: <EventsTab /> },
    {
      key: 'legacy',
      label: '技能包（旧）',
      children: (
        <div>
          <div style={{ marginBottom: 12, fontSize: 13, color: 'rgba(0,0,0,0.45)' }}>
            原「上传 zip 管理 SKILL 包」功能（供 Agent 模板选包），整套保留于此 Tab。
          </div>
          <LegacySkillPackages />
        </div>
      ),
    },
  ];

  return (
    <PageContainer
      title="SKILL Hub 管理台"
      subTitle="技能中心 · 技能库 · EVO MAP 进化图谱 · 安装台账 · 事件审计 · 技能包（旧）"
    >
      <Tabs defaultActiveKey="overview" items={items} destroyInactiveTabPane={false} />
    </PageContainer>
  );
};

export default SkillManagementPage;
