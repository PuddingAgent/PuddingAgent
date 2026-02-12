import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Drawer, Form } from 'antd';
import { ProForm } from '@ant-design/pro-components';
import type { FormInstance } from 'antd';
import { useStyles } from './styles';
import AgentTemplateSettingsNav from './AgentTemplateSettingsNav';
import BasicSection from './sections/BasicSection';
import CapabilitySkillSection from './sections/CapabilitySkillSection';
import PromptPersonaSection from './sections/PromptPersonaSection';
import ModelMemorySection from './sections/ModelMemorySection';
import GuardrailSection from './sections/GuardrailSection';
import {
  type AgentTemplateSectionKey,
  type AgentTemplateScope,
  findSectionByField,
  collectErrorSections,
} from './types';
import type {
  AgentAvatarDto,
  CapabilityDto,
  GlobalAgentTemplateDto,
  LlmModelDto,
  LlmProviderDto,
  SkillPackageDto,
} from '@/services/platform/api';

export interface AgentTemplateSettingsDrawerProps {
  open: boolean;
  mode: 'create' | 'edit';
  scope: AgentTemplateScope;
  title: string;
  builtIn?: boolean;
  form: FormInstance<any>;
  onClose: () => void;
  onSave: () => Promise<void>;

  providers: LlmProviderDto[];
  models: LlmModelDto[];
  memoryModels: LlmModelDto[];
  loadingModels: boolean;
  loadingMemoryModels: boolean;
  embeddingModels: LlmModelDto[];
  loadingEmbeddingModels: boolean;
  capabilities: CapabilityDto[];
  skillPackages: SkillPackageDto[];
  avatars?: AgentAvatarDto[];
  workspaces?: { label: string; value: string }[];
  globalTemplates?: { label: string; value: string }[];

  grantTargetKeys: string[];
  skillTargetKeys: string[];
  setGrantTargetKeys: (keys: string[]) => void;
  setSkillTargetKeys: (keys: string[]) => void;

  onProviderChange: (providerId: string) => void | Promise<void>;
  onMemoryProviderChange: (providerId: string) => void | Promise<void>;
  onEmbeddingProviderChange: (providerId: string) => void | Promise<void>;
  onBaseGlobalTemplateChange?: (templateId?: string) => void | Promise<void>;

  /** 默认能力 ID 列表（始终预设，不展示在 Transfer 中） */
  defaultCapIds: string[];
  /** 高权限能力列表 */
  grantCapabilities: CapabilityDto[];
}

const AgentTemplateSettingsDrawer: React.FC<AgentTemplateSettingsDrawerProps> = ({
  open,
  mode,
  scope,
  title,
  builtIn,
  form,
  onClose,
  onSave,
  providers,
  models,
  memoryModels,
  loadingModels,
  loadingMemoryModels,
  embeddingModels,
  loadingEmbeddingModels,
  capabilities,
  skillPackages,
  avatars = [],
  workspaces,
  globalTemplates,
  grantTargetKeys,
  skillTargetKeys,
  setGrantTargetKeys,
  setSkillTargetKeys,
  onProviderChange,
  onMemoryProviderChange,
  onEmbeddingProviderChange,
  onBaseGlobalTemplateChange,
  defaultCapIds,
  grantCapabilities,
}) => {
  const { styles } = useStyles();
  const contentRef = useRef<HTMLDivElement>(null);
  const [activeSection, setActiveSection] = useState<AgentTemplateSectionKey>('basic');
  const [errorSections, setErrorSections] = useState<Set<AgentTemplateSectionKey>>(new Set());
  const [saving, setSaving] = useState(false);

  // 打开/关闭时重置状态
  useEffect(() => {
    if (open) {
      setActiveSection('basic');
      setErrorSections(new Set());
    }
  }, [open]);

  // 滚动到指定 section（容器内滚动）
  const scrollToSection = useCallback((key: AgentTemplateSectionKey) => {
    const content = contentRef.current;
    if (!content) return;
    const el = content.querySelector<HTMLElement>(`[data-section-id="${key}"]`);
    if (el) {
      const top = el.offsetTop - content.offsetTop;
      content.scrollTo({ top, behavior: 'smooth' });
    }
  }, []);

  const handleNavigate = useCallback(
    (key: AgentTemplateSectionKey) => {
      setActiveSection(key);
      scrollToSection(key);
    },
    [scrollToSection],
  );

  // 滚动监听：当前视图中的 section（绑定到内容容器）
  useEffect(() => {
    if (!open) return;
    const content = contentRef.current;
    if (!content) return;

    const handleScroll = () => {
      const scrollTop = content.scrollTop + 80; // 偏移导航头高度
      const sections = content.querySelectorAll<HTMLElement>('[data-section-id]');
      let current: AgentTemplateSectionKey = 'basic';
      sections.forEach((el) => {
        const id = el.getAttribute('data-section-id') as AgentTemplateSectionKey;
        if (el.offsetTop - content.offsetTop <= scrollTop) {
          current = id;
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
      const values = await form.validateFields();
      // ADR-034：如果 avatarId 为空，补默认头像
      if (!values.avatarId && avatars.length > 0) {
        values.avatarId = avatars[0].avatarId;
      }
      await onSave();
    } catch (error: any) {
      // 错误定位：找到第一个错误字段所在分组，滚动过去
      const firstErrorField = error?.errorFields?.[0]?.name?.[0];
      if (firstErrorField) {
        const section = findSectionByField(String(firstErrorField));
        setActiveSection(section.key);
        scrollToSection(section.key);
        setErrorSections(collectErrorSections(error.errorFields ?? []));
      }
      throw error; // 让调用方处理
    } finally {
      setSaving(false);
    }
  };

  return (
    <Drawer
      title={title}
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
      {builtIn && (
        <Alert
          className={styles.drawerAlert}
          type="warning"
          message="这是系统内置模板，不允许修改模板 ID，但可以编辑其他字段。"
          showIcon
        />
      )}

      <ProForm form={form} submitter={false} layout="vertical" className={styles.settingsForm}>
        <div className={styles.settingsLayout}>
          <AgentTemplateSettingsNav
            activeSection={activeSection}
            errorSections={errorSections}
            onNavigate={handleNavigate}
          />
          <div ref={contentRef} className={styles.settingsContent}>
            <BasicSection
              id="basic"
              scope={scope}
              builtIn={builtIn}
              avatars={avatars}
              workspaces={workspaces}
              globalTemplates={globalTemplates?.map((g) => ({ label: g.label, value: g.value }))}
              onBaseGlobalTemplateChange={onBaseGlobalTemplateChange}
            />

            <CapabilitySkillSection
              id="capabilities"
              capabilities={capabilities}
              skillPackages={skillPackages}
              grantTargetKeys={grantTargetKeys}
              skillTargetKeys={skillTargetKeys}
              onGrantChange={(keys) => {
                setGrantTargetKeys(keys);
                // 合并默认能力 + 高权限能力 → selectedCapabilityIds
                const merged = [...defaultCapIds, ...keys];
                form.setFieldsValue({ selectedCapabilityIds: merged });
              }}
              onSkillChange={(keys) => {
                setSkillTargetKeys(keys);
                form.setFieldsValue({ selectedSkillPackageIds: keys });
              }}
              defaultCapIds={defaultCapIds}
              grantCapabilities={grantCapabilities}
            />

            <PromptPersonaSection id="prompts" />

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

            <GuardrailSection id="guardrails" />
          </div>
        </div>
      </ProForm>
    </Drawer>
  );
};

export default AgentTemplateSettingsDrawer;
