import React, { useEffect, useState } from 'react';
import { Alert, App, Avatar, Button, Col, Collapse, Drawer, Form, Row, Select, Space, Typography } from 'antd';
import {
  ProForm,
  ProFormDigit,
  ProFormSelect,
  ProFormSwitch,
  ProFormText,
  ProFormTextArea,
} from '@ant-design/pro-components';
import type { FormInstance } from 'antd';
import AgentTemplateSettingsNav from '../../agent-template-settings/AgentTemplateSettingsNav';
import CapabilitySkillSection from '../../agent-template-settings/sections/CapabilitySkillSection';
import ModelMemorySection from '../../agent-template-settings/sections/ModelMemorySection';
import { getAgentTemplateSelectPopupProps } from '../../agent-template-settings/selectPopup';
import { useStyles } from '../../agent-template-settings/styles';
import type { AgentTemplateSectionKey } from '../../agent-template-settings/types';
import type { SettingsSectionMeta } from '../../agent-template-settings/types';
import type {
  AgentAvatarDto,
  CapabilityDto,
  CreateWorkspaceAgentRequest,
  GlobalAgentTemplateDto,
  LlmModelDto,
  LlmProviderDto,
  SkillPackageDto,
  UpdateWorkspaceAgentRequest,
} from '@/services/platform/api';
import SmartRoleModelFields from './SmartRoleModelFields';

const { Text } = Typography;

const ROLE_OPTIONS = [
  { label: '服务型 (Service)', value: 'Service' },
  { label: '任务型 (Task)', value: 'Task' },
  { label: '审计型 (Audit)', value: 'Audit' },
  { label: '自定义 (Custom)', value: 'Custom' },
];

const WORKSPACE_AGENT_SECTIONS: SettingsSectionMeta[] = [
  { key: 'basic', label: '基础信息', fieldNames: ['name', 'role', 'description', 'sourceTemplateId', 'avatarId', 'isEnabled'] },
  { key: 'capabilities', label: '能力与 Skill', fieldNames: ['selectedCapabilityIds', 'skillPackageIds'] },
  { key: 'prompts', label: '角色与 Prompt', fieldNames: ['systemPrompt', 'heartbeatPrompt', 'soulMdContent', 'agentsMdContent', 'toolsMdContent', 'bootstrapMdContent', 'memoryMdContent', 'userPromptTemplate'] },
  { key: 'models', label: '模型与记忆', fieldNames: ['preferredProviderId', 'preferredModelId', 'memoryLlmProviderId', 'memoryLlmModelId', 'embeddingProviderId', 'embeddingModelId', 'memorySearchMode', 'reasoningEffort'] },
  { key: 'smartModels', label: 'Smart 子代理', fieldNames: ['explorerModel', 'researcherModel', 'plannerModel', 'reviewerModel', 'developerModel', 'deployerModel', 'testerModel'] },
  { key: 'guardrails', label: '执行护栏', fieldNames: ['maxReplyTokens', 'maxRounds', 'maxElapsedSeconds', 'maxToolCallsTotal', 'containerImage'] },
];

const SECTION_FIELDS: Record<AgentTemplateSectionKey, string[]> = {
  basic: ['name', 'role', 'description', 'sourceTemplateId', 'avatarId', 'isEnabled'],
  capabilities: ['selectedCapabilityIds', 'skillPackageIds'],
  prompts: [
    'systemPrompt',
    'soulMdContent',
    'agentsMdContent',
    'toolsMdContent',
    'bootstrapMdContent',
    'memoryMdContent',
    'userPromptTemplate',
    'heartbeatPrompt',
  ],
  models: [
    'preferredProviderId',
    'preferredModelId',
    'memoryLlmProviderId',
    'memoryLlmModelId',
    'embeddingProviderId',
    'embeddingModelId',
    'memorySearchMode',
    'reasoningEffort',
  ],
  smartModels: ['explorerModel', 'researcherModel', 'plannerModel', 'reviewerModel', 'developerModel', 'deployerModel', 'testerModel'],
  guardrails: ['maxReplyTokens', 'maxRounds', 'maxElapsedSeconds', 'maxToolCallsTotal', 'containerImage'],
};

export type WorkspaceAgentFormValues =
  CreateWorkspaceAgentRequest & UpdateWorkspaceAgentRequest;

export interface WorkspaceAgentSettingsDrawerProps {
  open: boolean;
  editMode: boolean;
  form: FormInstance<WorkspaceAgentFormValues>;
  onClose: () => void;
  onSave: (values: WorkspaceAgentFormValues) => Promise<void>;
  onSourceTemplateChange: (templateId?: string) => void | Promise<void>;
  templates: GlobalAgentTemplateDto[];
  selectedTemplate?: GlobalAgentTemplateDto;
  avatars: AgentAvatarDto[];
  providers: LlmProviderDto[];
  models: LlmModelDto[];
  memoryModels: LlmModelDto[];
  embeddingModels: LlmModelDto[];
  loadingModels: boolean;
  loadingMemoryModels: boolean;
  loadingEmbeddingModels: boolean;
  onProviderChange: (providerId: string) => void | Promise<void>;
  onMemoryProviderChange: (providerId: string) => void | Promise<void>;
  onEmbeddingProviderChange: (providerId: string) => void | Promise<void>;
  capabilities: CapabilityDto[];
  skillPackages: SkillPackageDto[];
  defaultCapIds: string[];
  grantCapabilities: CapabilityDto[];
  grantTargetKeys: string[];
  skillTargetKeys: string[];
  onGrantChange: (keys: string[]) => void;
  onSkillChange: (keys: string[]) => void;
}

const WorkspaceAgentSettingsDrawer: React.FC<WorkspaceAgentSettingsDrawerProps> = ({
  open,
  editMode,
  form,
  onClose,
  onSave,
  onSourceTemplateChange,
  templates,
  selectedTemplate,
  avatars,
  providers,
  models,
  memoryModels,
  embeddingModels,
  loadingModels,
  loadingMemoryModels,
  loadingEmbeddingModels,
  onProviderChange,
  onMemoryProviderChange,
  onEmbeddingProviderChange,
  capabilities,
  skillPackages,
  defaultCapIds,
  grantCapabilities,
  grantTargetKeys,
  skillTargetKeys,
  onGrantChange,
  onSkillChange,
}) => {
  const { modal } = App.useApp();
  const { styles } = useStyles();
  const [activeSection, setActiveSection] = useState<AgentTemplateSectionKey>('basic');
  const [errorSections, setErrorSections] = useState<Set<AgentTemplateSectionKey>>(new Set());
  const [saving, setSaving] = useState(false);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    if (open) {
      setActiveSection('basic');
      setErrorSections(new Set());
      setDirty(false);
    }
  }, [open]);

  const handleSave = async () => {
    try {
      setSaving(true);
      const values = await form.validateFields();
      await onSave(values);
      setDirty(false);
    } catch (error: any) {
      const errorFields = error?.errorFields ?? [];
      const sections = new Set<AgentTemplateSectionKey>();
      for (const field of errorFields) {
        const fieldName = String(field.name?.[0]);
        const section = (Object.keys(SECTION_FIELDS) as AgentTemplateSectionKey[])
          .find((key) => SECTION_FIELDS[key].includes(fieldName)) ?? 'basic';
        sections.add(section);
      }
      setErrorSections(sections);
      const firstSection = sections.values().next().value as AgentTemplateSectionKey | undefined;
      if (firstSection) {
        setActiveSection(firstSection);
      }
    } finally {
      setSaving(false);
    }
  };

  const requestClose = () => {
    if (!dirty) {
      onClose();
      return;
    }
    modal.confirm({
      title: '放弃未保存的修改？',
      content: '关闭后，本次对 Agent 配置的修改不会保存。',
      okText: '放弃修改',
      okButtonProps: { danger: true },
      cancelText: '继续编辑',
      onOk: onClose,
    });
  };

  const markSectionChanged = (changedValues: Record<string, unknown>) => {
    setDirty(true);
    const changedField = Object.keys(changedValues)[0];
    if (!changedField) return;
    const section = (Object.keys(SECTION_FIELDS) as AgentTemplateSectionKey[])
      .find((key) => SECTION_FIELDS[key].includes(changedField));
    if (!section) return;
    setErrorSections((current) => {
      const next = new Set(current);
      next.delete(section);
      return next;
    });
  };

  const findAvatar = (avatarId: unknown) =>
    avatars.find((avatar) => avatar.avatarId === String(avatarId));

  return (
    <Drawer
      title={editMode ? '编辑 Agent' : '新增 Agent'}
      open={open}
      width={960}
      className={styles.drawer}
      onClose={requestClose}
      extra={
        <Button type="primary" loading={saving} onClick={handleSave}>
          {dirty ? '保存更改' : '保存'}
        </Button>
      }
    >
      <ProForm
        form={form}
        submitter={false}
        layout="vertical"
        className={styles.settingsForm}
        onValuesChange={markSectionChanged}
      >
        <div className={styles.settingsLayout}>
          <AgentTemplateSettingsNav
            activeSection={activeSection}
            errorSections={errorSections}
            sections={WORKSPACE_AGENT_SECTIONS}
            onNavigate={setActiveSection}
          />

          <div className={styles.settingsContent}>
            <section hidden={activeSection !== 'basic'} data-section-id="basic" className={styles.section}>
              <div className={styles.sectionTitle}>基础信息</div>

              <Row gutter={16}>
                <Col span={12}>
                  <ProFormText
                    name="name"
                    label="Agent 名称"
                    rules={[{ required: true, message: '请输入 Agent 名称' }]}
                  />
                </Col>
                <Col span={12}>
                  <ProFormSelect
                    name="role"
                    label="角色类型"
                    options={ROLE_OPTIONS}
                    rules={[{ required: true, message: '请选择角色类型' }]}
                    fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup)}
                  />
                </Col>
              </Row>

              <ProFormTextArea
                name="description"
                label="实例职责"
                rows={3}
                placeholder="描述这个 Agent 在当前工作区负责什么"
              />

              <ProFormSelect
                name="sourceTemplateId"
                label="来源模板"
                disabled={editMode}
                options={templates.map((template) => ({
                  label: `${template.name} (${template.templateId})`,
                  value: `global:${template.templateId}`,
                }))}
                fieldProps={getAgentTemplateSelectPopupProps(styles.selectPopup, {
                  allowClear: false,
                  onChange: onSourceTemplateChange,
                })}
                extra="模板只在创建时提供初始快照；Agent 创建后独立演进。"
              />

              {selectedTemplate && (
                <Alert
                  showIcon
                  type="info"
                  message={`模板快照：${selectedTemplate.name} · ${selectedTemplate.role}`}
                  description={
                    <Text type="secondary">
                      模型：{selectedTemplate.preferredModelId || '平台默认'} ·
                      记忆：{selectedTemplate.memorySearchMode || 'deep'}
                    </Text>
                  }
                  style={{ marginBottom: 16 }}
                />
              )}

              <Form.Item name="avatarId" label="头像">
                <Select
                  allowClear
                  placeholder="选择 Agent 头像"
                  {...getAgentTemplateSelectPopupProps(styles.selectPopup)}
                  options={avatars.map((avatar) => ({
                    label: avatar.name,
                    value: avatar.avatarId,
                  }))}
                  optionRender={(option) => {
                    const avatar = findAvatar(option.value);
                    return avatar ? (
                      <Space size={8}>
                        <Avatar size={22} src={avatar.url} />
                        <span>{avatar.name}</span>
                      </Space>
                    ) : option.label;
                  }}
                />
              </Form.Item>

              <ProFormSwitch name="isEnabled" label="启用" />
            </section>

            <div hidden={activeSection !== 'capabilities'}>
              <CapabilitySkillSection
                id="capabilities"
                capabilities={capabilities}
                skillPackages={skillPackages}
                grantTargetKeys={grantTargetKeys}
                skillTargetKeys={skillTargetKeys}
                onGrantChange={(keys) => {
                  setDirty(true);
                  onGrantChange(keys);
                }}
                onSkillChange={(keys) => {
                  setDirty(true);
                  onSkillChange(keys);
                }}
                defaultCapIds={defaultCapIds}
                grantCapabilities={grantCapabilities}
                capabilityFieldName="selectedCapabilityIds"
                skillFieldName="skillPackageIds"
              />
            </div>

            <section hidden={activeSection !== 'prompts'} data-section-id="prompts" className={styles.section}>
              <div className={styles.sectionTitle}>角色与 Prompt</div>
              <ProFormTextArea
                name="systemPrompt"
                label="系统提示词"
                rows={7}
                placeholder="定义 Agent 的核心职责、能力边界和行为准则"
              />
              <ProFormTextArea
                name="userPromptTemplate"
                label="用户 Prompt 模板"
                rows={3}
                placeholder="可选，支持 {{variable}} 占位符"
              />
              <Collapse
                size="small"
                items={[
                  {
                    key: 'heartbeat',
                    label: 'heartbeatPrompt.md · 心跳恢复流程',
                    children: (
                      <ProFormTextArea
                        name="heartbeatPrompt"
                        rows={8}
                        fieldProps={{ showCount: true }}
                        placeholder="Agent 空闲心跳时收到的提示词；留空使用默认提示词"
                      />
                    ),
                  },
                  {
                    key: 'soul',
                    label: 'SOUL.md · 人设与边界',
                    children: <ProFormTextArea name="soulMdContent" rows={8} fieldProps={{ showCount: true }} />,
                  },
                  {
                    key: 'agents',
                    label: 'AGENTS.md · 协作规范',
                    children: <ProFormTextArea name="agentsMdContent" rows={10} fieldProps={{ showCount: true }} />,
                  },
                  {
                    key: 'tools',
                    label: 'TOOLS.md · 工具约定',
                    children: <ProFormTextArea name="toolsMdContent" rows={8} fieldProps={{ showCount: true }} />,
                  },
                  {
                    key: 'bootstrap',
                    label: 'BOOTSTRAP.md · 首次引导',
                    children: <ProFormTextArea name="bootstrapMdContent" rows={8} fieldProps={{ showCount: true }} />,
                  },
                  {
                    key: 'memory',
                    label: 'MEMORY.md · 记忆策略',
                    children: <ProFormTextArea name="memoryMdContent" rows={8} fieldProps={{ showCount: true }} />,
                  },
                ]}
              />
            </section>

            <div hidden={activeSection !== 'models'}>
              <ModelMemorySection
                id="models"
                providers={providers}
                models={models}
                memoryModels={memoryModels}
                loadingModels={loadingModels}
                loadingMemoryModels={loadingMemoryModels}
                onProviderChange={onProviderChange}
                onMemoryProviderChange={onMemoryProviderChange}
                embeddingModels={embeddingModels}
                loadingEmbeddingModels={loadingEmbeddingModels}
                onEmbeddingProviderChange={onEmbeddingProviderChange}
              />
            </div>

            <section hidden={activeSection !== 'smartModels'} data-section-id="smartModels" className={styles.section}>
              <div className={styles.sectionTitle}>Smart 子代理模型</div>
              <SmartRoleModelFields onChanged={() => setDirty(true)} />
            </section>

            <section hidden={activeSection !== 'guardrails'} data-section-id="guardrails" className={styles.section}>
              <div className={styles.sectionTitle}>执行护栏</div>
              <Row gutter={16}>
                <Col xs={24} sm={12}>
                  <ProFormDigit
                    name="maxReplyTokens"
                    label="最大回复 Token"
                    min={256}
                    max={131072}
                    fieldProps={{ addonAfter: 'tokens' }}
                    extra="单次回复的输出上限，不包含输入上下文。"
                  />
                </Col>
                <Col xs={24} sm={12}>
                  <ProFormDigit
                    name="maxRounds"
                    label="最大轮次"
                    min={1}
                    max={1000}
                    fieldProps={{ addonAfter: '轮' }}
                    extra="一次任务允许的 Agent 循环轮数。"
                  />
                </Col>
                <Col xs={24} sm={12}>
                  <ProFormDigit
                    name="maxElapsedSeconds"
                    label="最大耗时"
                    min={10}
                    max={86400}
                    fieldProps={{ addonAfter: '秒' }}
                    extra="86400 秒等于 24 小时；平台安全上限仍会生效。"
                  />
                </Col>
                <Col xs={24} sm={12}>
                  <ProFormDigit
                    name="maxToolCallsTotal"
                    label="最大工具调用"
                    min={1}
                    max={500}
                    fieldProps={{ addonAfter: '次' }}
                    extra="包含主 Agent 与当前执行链中的工具调用。"
                  />
                </Col>
              </Row>
              <Collapse
                size="small"
                items={[
                  {
                    key: 'advanced-runtime',
                    label: '高级运行环境',
                    children: (
                      <ProFormText
                        name="containerImage"
                        label="容器镜像"
                        placeholder="宿主模式暂不使用，留空即可"
                      />
                    ),
                  },
                ]}
              />
            </section>
          </div>
        </div>
      </ProForm>
    </Drawer>
  );
};

export default WorkspaceAgentSettingsDrawer;
