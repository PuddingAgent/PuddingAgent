import { ApiOutlined, AppstoreOutlined, SafetyCertificateOutlined } from '@ant-design/icons';
import { Button, Checkbox, Empty, Form, Input, Modal, Radio, Space, Tag, Typography } from 'antd';
import React, { useMemo, useState } from 'react';
import type { CapabilityDto, SkillPackageDto } from '@/services/platform/api';
import { useStyles } from '../styles';

const { Text } = Typography;

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
  capabilityFieldName?: string;
  skillFieldName?: string;
}

type GrantModalKind = 'capability' | 'skill';
type CapabilityFilter = 'all' | 'shell' | 'file' | 'network';

const HiddenFormField: React.FC = () => null;

const normalize = (value?: string | null) => (value ?? '').trim().toLowerCase();

const matchesQuery = (query: string, ...values: Array<string | undefined | null>) => {
  const q = normalize(query);
  if (!q) return true;
  return values.some((value) => normalize(value).includes(q));
};

const dedupeKeys = (keys: string[]) => Array.from(new Set(keys));

const renderCapabilityPermissionTags = (item: CapabilityDto) => (
  <Space size={4} wrap>
    {item.requiresShellExecution && <Tag color="volcano">Shell</Tag>}
    {item.requiresFileWrite && <Tag color="blue">文件写入</Tag>}
    {item.requiresNetworkAccess && <Tag color="purple">网络</Tag>}
    {!item.requiresShellExecution && !item.requiresFileWrite && !item.requiresNetworkAccess && (
      <Tag color="green">低风险</Tag>
    )}
  </Space>
);

const GrantChip: React.FC<{
  label: string;
  code?: string;
  color?: string;
  closable?: boolean;
  onClose?: () => void;
}> = ({ label, code, color = 'default', closable, onClose }) => (
  <Tag
    color={color}
    closable={closable}
    onClose={(event) => {
      event.preventDefault();
      onClose?.();
    }}
    style={{ marginInlineEnd: 0, padding: '2px 7px', lineHeight: 1.7 }}
  >
    <Space size={4}>
      <span>{label}</span>
      {code && <Text type="secondary" style={{ fontSize: 11 }}>{code}</Text>}
    </Space>
  </Tag>
);

const ResourcePickerModal: React.FC<{
  kind: GrantModalKind;
  open: boolean;
  capabilities: CapabilityDto[];
  skillPackages: SkillPackageDto[];
  selectedKeys: string[];
  onCancel: () => void;
  onApply: (keys: string[]) => void;
}> = ({
  kind,
  open,
  capabilities,
  skillPackages,
  selectedKeys,
  onCancel,
  onApply,
}) => {
  const [draftKeys, setDraftKeys] = useState<string[]>(selectedKeys);
  const [query, setQuery] = useState('');
  const [capabilityFilter, setCapabilityFilter] = useState<CapabilityFilter>('all');

  React.useEffect(() => {
    if (open) {
      setDraftKeys(selectedKeys);
      setQuery('');
      setCapabilityFilter('all');
    }
  }, [open, selectedKeys]);

  const selectedSet = useMemo(() => new Set(draftKeys), [draftKeys]);

  const capabilityItems = useMemo(() => {
    return capabilities.filter((item) => {
      if (!matchesQuery(query, item.name, item.toolName, item.capabilityId, item.description)) return false;
      if (capabilityFilter === 'shell') return item.requiresShellExecution;
      if (capabilityFilter === 'file') return item.requiresFileWrite;
      if (capabilityFilter === 'network') return item.requiresNetworkAccess;
      return true;
    });
  }, [capabilities, capabilityFilter, query]);

  const skillItems = useMemo(() => {
    return skillPackages.filter((item) =>
      matchesQuery(query, item.name, item.skillPackageId, item.version, item.description),
    );
  }, [query, skillPackages]);

  const visibleKeys = kind === 'capability'
    ? capabilityItems.map((item) => item.capabilityId)
    : skillItems.map((item) => item.skillPackageId);
  const allVisibleSelected = visibleKeys.length > 0 && visibleKeys.every((key) => selectedSet.has(key));

  const toggleOne = (key: string, checked: boolean) => {
    setDraftKeys((current) => {
      if (checked) return dedupeKeys([...current, key]);
      return current.filter((item) => item !== key);
    });
  };

  const toggleVisible = (checked: boolean) => {
    setDraftKeys((current) => {
      if (!checked) return current.filter((key) => !visibleKeys.includes(key));
      return dedupeKeys([...current, ...visibleKeys]);
    });
  };

  const title = kind === 'capability' ? '选择高权限工具' : '选择 Skill 包';
  const description = kind === 'capability'
    ? '只展示需要显式授权的工具。默认工具会自动随模板可用。'
    : '选择这个 Agent 模板可使用的 Skill 包。';

  return (
    <Modal
      title={title}
      open={open}
      width={760}
      onCancel={onCancel}
      destroyOnHidden
      footer={null}
    >
      <Text type="secondary" style={{ display: 'block', marginBottom: 12 }}>
        {description}
      </Text>
      <Space wrap style={{ width: '100%', justifyContent: 'space-between', marginBottom: 12 }}>
        <Input.Search
          allowClear
          placeholder={kind === 'capability' ? '搜索工具名、toolId、描述' : '搜索 Skill 名称、ID、版本'}
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          style={{ width: 300 }}
        />
        {kind === 'capability' && (
          <Radio.Group
            size="small"
            value={capabilityFilter}
            onChange={(event) => setCapabilityFilter(event.target.value)}
          >
            <Radio.Button value="all">全部</Radio.Button>
            <Radio.Button value="shell">Shell</Radio.Button>
            <Radio.Button value="file">文件</Radio.Button>
            <Radio.Button value="network">网络</Radio.Button>
          </Radio.Group>
        )}
      </Space>

      <div
        style={{
          border: '1px solid rgba(92, 74, 58, 0.14)',
          borderRadius: 8,
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            height: 42,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '0 12px',
            borderBottom: '1px solid rgba(92, 74, 58, 0.12)',
            background: '#fffefa',
          }}
        >
          <Checkbox
            checked={allVisibleSelected}
            indeterminate={!allVisibleSelected && visibleKeys.some((key) => selectedSet.has(key))}
            onChange={(event) => toggleVisible(event.target.checked)}
          >
            当前筛选 {visibleKeys.length} 项
          </Checkbox>
          <Text type="secondary">已选 {draftKeys.length} 项</Text>
        </div>

        <div style={{ maxHeight: 360, overflowY: 'auto', background: '#fffefa' }}>
          {kind === 'capability' && capabilityItems.length === 0 && (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有匹配的工具" style={{ margin: '48px 0' }} />
          )}
          {kind === 'skill' && skillItems.length === 0 && (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有匹配的 Skill" style={{ margin: '48px 0' }} />
          )}

          {kind === 'capability' && capabilityItems.map((item) => (
            <div
              key={item.capabilityId}
              style={{
                display: 'grid',
                gridTemplateColumns: '24px minmax(0, 1fr)',
                gap: 8,
                padding: '10px 12px',
                borderBottom: '1px solid rgba(92, 74, 58, 0.08)',
              }}
            >
              <Checkbox
                aria-label={item.name}
                checked={selectedSet.has(item.capabilityId)}
                onChange={(event) => toggleOne(item.capabilityId, event.target.checked)}
              />
              <div style={{ minWidth: 0 }}>
                <Space size={6} wrap>
                  <Text strong>{item.name}</Text>
                  <Text code style={{ fontSize: 11 }}>{item.toolName}</Text>
                  {renderCapabilityPermissionTags(item)}
                </Space>
                {item.description && (
                  <Text type="secondary" style={{ display: 'block', fontSize: 12, marginTop: 4 }}>
                    {item.description}
                  </Text>
                )}
              </div>
            </div>
          ))}

          {kind === 'skill' && skillItems.map((item) => (
            <div
              key={item.skillPackageId}
              style={{
                display: 'grid',
                gridTemplateColumns: '24px minmax(0, 1fr)',
                gap: 8,
                padding: '10px 12px',
                borderBottom: '1px solid rgba(92, 74, 58, 0.08)',
              }}
            >
              <Checkbox
                aria-label={item.name}
                checked={selectedSet.has(item.skillPackageId)}
                onChange={(event) => toggleOne(item.skillPackageId, event.target.checked)}
              />
              <div style={{ minWidth: 0 }}>
                <Space size={6} wrap>
                  <Text strong>{item.name}</Text>
                  <Tag>v{item.version}</Tag>
                  <Text code style={{ fontSize: 11 }}>{item.skillPackageId}</Text>
                </Space>
                {item.description && (
                  <Text type="secondary" style={{ display: 'block', fontSize: 12, marginTop: 4 }}>
                    {item.description}
                  </Text>
                )}
              </div>
            </div>
          ))}
        </div>
      </div>

      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
        <Button onClick={onCancel}>取消</Button>
        <Button data-testid="resource-picker-apply" type="primary" onClick={() => onApply(draftKeys)}>
          应用
        </Button>
      </div>
    </Modal>
  );
};

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
  capabilityFieldName = 'selectedCapabilityIds',
  skillFieldName = 'selectedSkillPackageIds',
}) => {
  const { styles } = useStyles();
  const [activePicker, setActivePicker] = useState<GrantModalKind | null>(null);

  const defaultCapabilities = useMemo(
    () => capabilities.filter((item) => defaultCapIds.includes(item.capabilityId)),
    [capabilities, defaultCapIds],
  );
  const selectedGrantCapabilities = useMemo(
    () => grantCapabilities.filter((item) => grantTargetKeys.includes(item.capabilityId)),
    [grantCapabilities, grantTargetKeys],
  );
  const selectedSkills = useMemo(
    () => skillPackages.filter((item) => skillTargetKeys.includes(item.skillPackageId)),
    [skillPackages, skillTargetKeys],
  );

  const previewDefaults = defaultCapabilities.slice(0, 6);

  return (
    <section id={id} data-section-id={id} className={styles.section}>
      <div className={styles.sectionTitle}>能力与 Skill</div>

      <Form.Item name={capabilityFieldName} hidden>
        <HiddenFormField />
      </Form.Item>
      <Form.Item name={skillFieldName} hidden>
        <HiddenFormField />
      </Form.Item>

      <div style={{ display: 'flex', gap: 8, marginBottom: 14, flexWrap: 'wrap' }}>
        <Tag icon={<SafetyCertificateOutlined />} color="green">
          默认能力 {defaultCapIds.length}
        </Tag>
        <Tag icon={<ApiOutlined />} color={grantTargetKeys.length > 0 ? 'orange' : 'default'}>
          高权限授权 {grantTargetKeys.length}
        </Tag>
        <Tag icon={<AppstoreOutlined />} color={skillTargetKeys.length > 0 ? 'blue' : 'default'}>
          Skill {skillTargetKeys.length}
        </Tag>
      </div>

      <div style={{ display: 'grid', gap: 14 }}>
        <div>
          <div className={styles.inlineLabel}>默认能力</div>
          <Space size={[4, 6]} wrap>
            {previewDefaults.map((item) => (
              <GrantChip key={item.capabilityId} label={item.name} code={item.toolName} color="green" />
            ))}
            {defaultCapabilities.length > previewDefaults.length && (
              <Tag style={{ marginInlineEnd: 0 }}>+{defaultCapabilities.length - previewDefaults.length}</Tag>
            )}
          </Space>
        </div>

        <div>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              gap: 12,
              marginBottom: 6,
            }}
          >
            <div className={styles.inlineLabel} style={{ marginBottom: 0 }}>
              高权限工具
            </div>
            <Button size="small" icon={<ApiOutlined />} onClick={() => setActivePicker('capability')}>
              添加/管理
            </Button>
          </div>
          {selectedGrantCapabilities.length > 0 ? (
            <Space size={[4, 6]} wrap>
              {selectedGrantCapabilities.map((item) => (
                <GrantChip
                  key={item.capabilityId}
                  label={item.name}
                  code={item.toolName}
                  color="orange"
                  closable
                  onClose={() => onGrantChange(grantTargetKeys.filter((key) => key !== item.capabilityId))}
                />
              ))}
            </Space>
          ) : (
            <Text type="secondary">未授予高权限工具</Text>
          )}
        </div>

        <div>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              gap: 12,
              marginBottom: 6,
            }}
          >
            <div className={styles.inlineLabel} style={{ marginBottom: 0 }}>
              Skill 包
            </div>
            <Button size="small" icon={<AppstoreOutlined />} onClick={() => setActivePicker('skill')}>
              添加/管理
            </Button>
          </div>
          {selectedSkills.length > 0 ? (
            <Space size={[4, 6]} wrap>
              {selectedSkills.map((item) => (
                <GrantChip
                  key={item.skillPackageId}
                  label={item.name}
                  code={`v${item.version}`}
                  color="blue"
                  closable
                  onClose={() => onSkillChange(skillTargetKeys.filter((key) => key !== item.skillPackageId))}
                />
              ))}
            </Space>
          ) : (
            <Text type="secondary">未选择 Skill 包</Text>
          )}
        </div>
      </div>

      <ResourcePickerModal
        kind="capability"
        open={activePicker === 'capability'}
        capabilities={grantCapabilities}
        skillPackages={skillPackages}
        selectedKeys={grantTargetKeys}
        onCancel={() => setActivePicker(null)}
        onApply={(keys) => {
          onGrantChange(keys);
          setActivePicker(null);
        }}
      />
      <ResourcePickerModal
        kind="skill"
        open={activePicker === 'skill'}
        capabilities={grantCapabilities}
        skillPackages={skillPackages}
        selectedKeys={skillTargetKeys}
        onCancel={() => setActivePicker(null)}
        onApply={(keys) => {
          onSkillChange(keys);
          setActivePicker(null);
        }}
      />
    </section>
  );
};

export default CapabilitySkillSection;
