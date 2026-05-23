import React from 'react';
import { Form, Tag, Transfer } from 'antd';
import type { CapabilityDto, SkillPackageDto } from '@/services/platform/api';
import { useStyles } from '../styles';

export interface CapabilitySkillSectionProps {
  id: string;
  capabilities: CapabilityDto[];
  skillPackages: SkillPackageDto[];
  grantTargetKeys: string[];
  skillTargetKeys: string[];
  onGrantChange: (keys: string[]) => void;
  onSkillChange: (keys: string[]) => void;
  defaultCapIds: string[];
  grantCapabilities: CapabilityDto[];
}

const CapabilitySkillSection: React.FC<CapabilitySkillSectionProps> = ({
  id,
  capabilities,
  skillPackages,
  grantTargetKeys,
  skillTargetKeys,
  onGrantChange,
  onSkillChange,
  defaultCapIds,
  grantCapabilities,
}) => {
  const { styles } = useStyles();

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>能力与 Skill</div>

      <div style={{ display: 'flex', gap: 12, marginBottom: 16, flexWrap: 'wrap' }}>
        <Tag color="green">{defaultCapIds.length} 项默认能力</Tag>
        <Tag color={grantTargetKeys.length > 0 ? 'orange' : 'default'}>
          高权限授权: {grantTargetKeys.length}
        </Tag>
        <Tag color={skillTargetKeys.length > 0 ? 'blue' : 'default'}>
          Skill: {skillTargetKeys.length} 项
        </Tag>
      </div>

      {/* 默认能力（预设勾选，只读展示） */}
      <Form.Item label="默认能力" help="始终可用，无需授权（只读、记忆、子代理等）">
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
          {capabilities
            .filter((c) => !c.requiresShellExecution && !c.requiresFileWrite)
            .map((c) => (
              <Tag key={c.capabilityId} color="green" style={{ fontSize: 11, opacity: 0.85 }}>
                {c.name}
              </Tag>
            ))}
        </div>
      </Form.Item>

      {/* 高权限能力（Transfer 穿梭框 + 搜索） */}
      <Form.Item
        label="高权限能力"
        help="需要显式授权：Shell 执行、文件写入、Python 等（右侧为已授权）"
      >
        <Transfer
          dataSource={grantCapabilities.map((c) => ({
            key: c.capabilityId,
            title: c.name,
            description: c.toolName,
          }))}
          titles={['可选', '已授权']}
          targetKeys={grantTargetKeys}
          onChange={(nextKeys) => {
            onGrantChange(nextKeys as string[]);
          }}
          showSearch
          filterOption={(inputValue, item) =>
            item.title.toLowerCase().includes(inputValue.toLowerCase()) ||
            (item.description ?? '').toLowerCase().includes(inputValue.toLowerCase())
          }
          render={(item) => (
            <span>
              {item.title}
              <span style={{ fontSize: 10, color: '#888', marginLeft: 6 }}>{item.description}</span>
            </span>
          )}
          listStyle={{ width: 260, height: 280 }}
          style={{ width: '100%' }}
        />
      </Form.Item>

      {/* SKILL 包选择（Transfer + 搜索）— 仅保留一个输入形态，删除 Checkbox.Group 冗余 */}
      <Form.Item label="SKILL 包选择" help="选择 Agent 可用的 Skill 包（右侧为已选）">
        <Transfer
          dataSource={skillPackages.map((s) => ({
            key: s.skillPackageId,
            title: s.name,
            description: `v${s.version}`,
          }))}
          titles={['可选', '已选']}
          targetKeys={skillTargetKeys}
          onChange={(nextKeys) => {
            onSkillChange(nextKeys as string[]);
          }}
          showSearch
          filterOption={(inputValue, item) =>
            item.title.toLowerCase().includes(inputValue.toLowerCase()) ||
            (item.description ?? '').toLowerCase().includes(inputValue.toLowerCase())
          }
          render={(item) => (
            <span>
              {item.title}
              <Tag style={{ fontSize: 10, marginLeft: 4 }}>v{item.description}</Tag>
            </span>
          )}
          listStyle={{ width: 260, height: 240 }}
          style={{ width: '100%' }}
        />
      </Form.Item>
    </section>
  );
};

export default CapabilitySkillSection;
