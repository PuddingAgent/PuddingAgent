import React from 'react';
import { Col, Form, Row, Select, Avatar, Space, Typography } from 'antd';
import {
  ProFormText,
  ProFormTextArea,
  ProFormSelect,
  ProFormSwitch,
  ProFormDigit,
} from '@ant-design/pro-components';
import type { AgentAvatarDto } from '@/services/platform/api';
import { useStyles } from '../styles';
import type { AgentTemplateScope } from '../types';

const { Text } = Typography;

const ROLES = [
  { label: '服务型 (Service)', value: 'Service' },
  { label: '任务型 (Task)', value: 'Task' },
  { label: '审计型 (Audit)', value: 'Audit' },
  { label: '自定义 (Custom)', value: 'Custom' },
];

export interface BasicSectionProps {
  id: string;
  scope: AgentTemplateScope;
  builtIn?: boolean;
  avatars: AgentAvatarDto[];
  workspaces?: { label: string; value: string }[];
  globalTemplates?: { label: string; value: string }[];
  onBaseGlobalTemplateChange?: (templateId?: string) => void;
}

const BasicSection: React.FC<BasicSectionProps> = ({
  id,
  scope,
  builtIn,
  avatars,
  workspaces,
  globalTemplates,
  onBaseGlobalTemplateChange,
}) => {
  const { styles } = useStyles();

  const renderAvatarSelectItem = (avatar: AgentAvatarDto, compact = false) => (
    <Space size={8} align="center" style={{ minWidth: 0 }}>
      <Avatar size={compact ? 20 : 24} src={avatar.url} />
      <span style={{ fontWeight: 500, whiteSpace: 'nowrap' }}>{avatar.name}</span>
      {!compact && avatar.recommendedUse && (
        <Text type="secondary" ellipsis style={{ fontSize: 12 }}>
          {avatar.recommendedUse}
        </Text>
      )}
    </Space>
  );

  const findAvatar = (avatarId: unknown) => avatars.find((a) => a.avatarId === String(avatarId));

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>基础信息</div>

      {scope === 'workspace' && workspaces && (
        <ProFormSelect
          name="workspaceId"
          label="所属工作区"
          rules={[{ required: true }]}
          options={workspaces}
          disabled={builtIn}
        />
      )}

      {scope === 'workspace' && globalTemplates && (
        <ProFormSelect
          name="baseGlobalTemplateId"
          label="继承自全局模板"
          options={globalTemplates}
          placeholder="不选则创建独立模板"
          fieldProps={{
            allowClear: true,
            onChange: onBaseGlobalTemplateChange,
          }}
        />
      )}

      {/* 模板 ID — 整行 */}
      <ProFormText
        name="templateId"
        label="模板 ID"
        rules={[
          { required: true },
          { pattern: /^[a-z0-9-]+$/, message: '仅允许小写字母、数字、连字符' },
        ]}
        disabled={!!builtIn}
        placeholder="如 code-reviewer"
      />

      {/* 名称 + 角色类型 两列并排 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormText name="name" label="名称" rules={[{ required: true }]} />
        </Col>
        <Col span={12}>
          <ProFormSelect
            name="role"
            label="角色类型"
            options={ROLES}
            rules={[{ required: true }]}
          />
        </Col>
      </Row>

      {/* 描述 — 整行 */}
      <ProFormTextArea name="description" label="描述" rows={2} />

      {/* 头像 — 整行 */}
      <Form.Item
        name="avatarId"
        label="头像"
        rules={[{ required: true, message: '请选择头像' }]}
      >
        <Select
          placeholder="选择系统头像"
          loading={avatars.length === 0}
          options={avatars.map((a) => ({
            label: a.name,
            value: a.avatarId,
          }))}
          optionRender={(option) => {
            const avatar = findAvatar(option.value);
            if (!avatar) return option.label;
            return renderAvatarSelectItem(avatar);
          }}
          labelRender={(option) => {
            const avatar = findAvatar(option.value);
            if (!avatar) return option.label;
            return renderAvatarSelectItem(avatar, true);
          }}
        />
      </Form.Item>

      {/* 启用 + 排序权重 两列并排 */}
      <Row gutter={16}>
        <Col span={12}>
          <ProFormSwitch name="isEnabled" label="启用" />
        </Col>
        <Col span={12}>
          <ProFormDigit name="sortOrder" label="排序权重" min={0} />
        </Col>
      </Row>
    </section>
  );
};

export default BasicSection;
