import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Avatar, Button, Col, Drawer, Form, Row, Select, Space, Typography } from 'antd';
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

const SECTION_FIELDS: Record<AgentTemplateSectionKey, string[]> = {
  basic: ['name', 'role', 'description', 'heartbeatPrompt', 'sourceTemplateId', 'avatarId', 'isEnabled'],
  capabilities: ['selectedCapabilityIds', 'skillPackageIds'],
  prompts: [
    'systemPrompt',
    'soulMdContent',
    'agentsMdContent',
    'toolsMdContent',
    'bootstrapMdContent',
    'memoryMdContent',
    'userPromptTemplate',
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
    'explorerModel',
    'researcherModel',
    'plannerModel',
    'reviewerModel',
    'developerModel',
    'deployerModel',
    'testerModel',
  ],
  guardrails: ['maxRounds', 'maxElapsedSeconds', 'maxToolCallsTotal', 'containerImage'],
};

export type WorkspaceAgentFormValues =
  CreateWorkspaceAgentRequest & UpdateWorkspaceAgentRequest;

export interface WorkspaceAgentSettingsDrawerProps {
  open: boolean;
  editMode: boolean;
  form: FormInstance<WorkspaceAgentFormValues>;
  onClose: () => void;
  onSave: () => Promise<void>;
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
  const { styles } = useStyles();
  const contentRef = useRef<HTMLDivElement>(null);
  const [activeSection, setActiveSection] = useState<AgentTemplateSectionKey>('basic');
  const [errorSections, setErrorSections] = useState<Set<AgentTemplateSectionKey>>(new Set());
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (open) {
      setActiveSection('basic');
      setErrorSections(new Set());
    }
  }, [open]);

  const scrollToSection = useCallback((key: AgentTemplateSectionKey) => {
    const content = contentRef.current;
    const section = content?.querySelector<HTMLElement>(`[data-section-id="${key}"]`);
    if (content && section) {
      content.scrollTo({
        top: section.offsetTop - content.offsetTop,
        behavior: 'smooth',
      });
    }
  }, []);

  useEffect(() => {
    if (!open) return;
    const content = contentRef.current;
    if (!content) return;
    const handleScroll = () => {
      const scrollTop = content.scrollTop + 80;
      let current: AgentTemplateSectionKey = 'basic';
      content.querySelectorAll<HTMLElement>('[data-section-id]').forEach((section) => {
        if (section.offsetTop - content.offsetTop <= scrollTop) {
          current = section.dataset.sectionId as AgentTemplateSectionKey;
        }
      });
      setActiveSection(current);
    };
    content.addEventListener('scroll', handleScroll, { passive: true });
    return () => content.removeEventListener('scroll', handleScroll);
  }, [open]);

  const handleSave = async () => {
    try {
      setSaving(true);
      await form.validateFields();
      await onSave();
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
        scrollToSection(firstSection);
      }
    } finally {
      setSaving(false);
    }
  };

  const findAvatar = (avatarId: unknown) =>
    avatars.find((avatar) => avatar.avatarId === String(avatarId));

  return (
    <Drawer
      title={editMode ? '编辑 Agent' : '新增 Agent'}
      open={open}
      width={960}
      className={styles.drawer}
      onClose={onClose}
      extra={
        <Button type="primary" loading={saving} onClick={handleSave}>
          保 存
        </Button>
      }
    >
      <ProForm form={form} submitter={false} layout="vertical" className={styles.settingsForm}>
        <div className={styles.settingsLayout}>
          <AgentTemplateSettingsNav
            activeSection={activeSection}
            errorSections={errorSections}
            onNavigate={(key) => {
              setActiveSection(key);
              scrollToSection(key);
            }}
          />

          <div ref={contentRef} className={styles.settingsContent}>
            <section data-section-id="basic" className={styles.section}>
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

              <ProFormTextArea
                name="heartbeatPrompt"
                label="心跳提示词"
                rows={3}
                placeholder="Agent 空闲心跳时收到的提示词；留空使用默认提示词"
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

            <CapabilitySkillSection
              id="capabilities"
              capabilities={capabilities}
              skillPackages={skillPackages}
              grantTargetKeys={grantTargetKeys}
              skillTargetKeys={skillTargetKeys}
              onGrantChange={onGrantChange}
              onSkillChange={onSkillChange}
              defaultCapIds={defaultCapIds}
              grantCapabilities={grantCapabilities}
              capabilityFieldName="selectedCapabilityIds"
              skillFieldName="skillPackageIds"
            />

            <section data-section-id="prompts" className={styles.section}>
              <div className={styles.sectionTitle}>角色定义</div>
              <ProFormTextArea
                name="systemPrompt"
                label="系统提示词"
                rows={8}
                placeholder="定义 Agent 的核心职责、能力边界和行为准则"
              />
              <ProFormTextArea name="soulMdContent" label="SOUL.md - 人设与边界" rows={4} />
              <ProFormTextArea name="agentsMdContent" label="AGENTS.md - 协作规范" rows={5} />
              <ProFormTextArea name="toolsMdContent" label="TOOLS.md - 工具约定" rows={4} />
              <ProFormTextArea name="bootstrapMdContent" label="BOOTSTRAP.md - 首次引导" rows={4} />
              <ProFormTextArea name="memoryMdContent" label="MEMORY.md - 记忆策略" rows={4} />
              <ProFormTextArea
                name="userPromptTemplate"
                label="用户 Prompt 模板"
                rows={3}
                placeholder="可选，支持 {{variable}} 占位符"
              />
            </section>

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

            <div style={{ marginTop: -28, marginBottom: 24 }}>
              <SmartRoleModelFields />
            </div>

            <section data-section-id="guardrails" className={styles.section}>
              <div className={styles.sectionTitle}>执行护栏</div>
              <Row gutter={16}>
                <Col xs={24} sm={12} md={8}>
                  <ProFormDigit name="maxRounds" label="最大轮次" min={1} max={1000} />
                </Col>
                <Col xs={24} sm={12} md={8}>
                  <ProFormDigit name="maxElapsedSeconds" label="最大耗时(秒)" min={10} max={86400} />
                </Col>
                <Col xs={24} sm={12} md={8}>
                  <ProFormDigit name="maxToolCallsTotal" label="最大工具调用" min={1} max={500} />
                </Col>
              </Row>
              <ProFormText
                name="containerImage"
                label="运行环境"
                placeholder="宿主模式暂不使用，留空即可"
              />
            </section>
          </div>
        </div>
      </ProForm>
    </Drawer>
  );
};

export default WorkspaceAgentSettingsDrawer;
