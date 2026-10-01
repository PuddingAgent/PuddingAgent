import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ArrowLeftOutlined, CloseOutlined, InfoCircleOutlined } from '@ant-design/icons';
import { Alert, Avatar, Button, Col, Collapse, Drawer, Form, Modal, Row, Select, Space, Spin, Tooltip, Typography } from 'antd';
import {
  ProForm,
  ProFormDigit,
  ProFormSelect,
  ProFormSwitch,
  ProFormText,
  ProFormTextArea,
} from '@ant-design/pro-components';
import type { FormInstance } from 'antd';
import useBreakpoint from 'antd/es/grid/hooks/useBreakpoint';
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
import AgentPromptCatalog from './AgentPromptCatalog';
import AgentPromptEditor from './AgentPromptEditor';
import { useWorkspaceAgentStyles } from './workbenchStyles';
import {
  PROMPT_DOCUMENT_FIELDS,
  collectDirtyFields,
  collectDirtyPromptDocuments,
  collectDirtySections,
  findPromptDocument,
  summarizeValidationErrors,
  WORKSPACE_AGENT_SECTION_FIELDS,
  type AgentFormSnapshot,
} from './promptDocuments';

const { Text } = Typography;

const ROLE_OPTIONS = [
  { label: '服务型 (Service)', value: 'Service' },
  { label: '任务型 (Task)', value: 'Task' },
  { label: '审计型 (Audit)', value: 'Audit' },
  { label: '自定义 (Custom)', value: 'Custom' },
];

const ROLE_LABELS = new Map(ROLE_OPTIONS.map((option) => [option.value, option.label]));

const WORKSPACE_AGENT_SECTIONS: SettingsSectionMeta[] = [
  { key: 'basic', label: '基础信息', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.basic },
  { key: 'capabilities', label: '能力与 Skill', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.capabilities },
  { key: 'prompts', label: '角色与 Prompt', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.prompts },
  { key: 'models', label: '模型与记忆', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.models },
  { key: 'smartModels', label: 'Smart 子代理', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.smartModels },
  { key: 'guardrails', label: '执行护栏', fieldNames: WORKSPACE_AGENT_SECTION_FIELDS.guardrails },
];

/** 工作台需要比对的全部字段：覆盖所有分区与隐藏字段。 */
const DRAFT_FIELDS: string[] = Array.from(
  new Set(Object.values(WORKSPACE_AGENT_SECTION_FIELDS).flat()),
);

const GRID_COLUMNS_DEFAULT = '184px minmax(0, 1fr)';
const GRID_COLUMNS_PROMPTS = '184px 220px minmax(0, 1fr)';

export type WorkspaceAgentFormValues =
  CreateWorkspaceAgentRequest & UpdateWorkspaceAgentRequest;

export interface WorkspaceAgentSettingsDrawerProps {
  open: boolean;
  editMode: boolean;
  form: FormInstance<WorkspaceAgentFormValues>;
  /** 当前工作区名称，仅用于身份栏展示。 */
  workspaceName?: string;
  /** 是否仍在从服务端加载完整配置；加载完成前禁止编辑与保存。 */
  loading?: boolean;
  /**
   * 基线数据版本号。
   *
   * 父组件每次把新的服务端数据或模板快照写入表单后必须递增：工作台以它作为
   * 「重新取快照」的信号，而不是猜测 effect 执行顺序（表单值写入与子组件首次
   * effect 的先后并不稳定）。
   */
  dataVersion?: number;
  /**
   * 保存所有分区。返回 true 表示配置已写入（工作台会刷新基线），false 表示失败。
   *
   * 调用方不要在此回调内关闭工作台：关闭时机由工作台按草稿状态决定。
   */
  onSave: (values: WorkspaceAgentFormValues) => Promise<boolean>;
  onClose: () => void;
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

/**
 * 工作区 Agent 编辑工作台。
 *
 * 设计依据：Docs/Features/Agent-Settings-Redesign-2026-10-01.md
 * 结构：顶部身份与保存状态 → 左侧设置导航 → Prompt 文档目录 → 大面积正文编辑器。
 *
 * 一期不改变字段含义与保存合同：仍然一次保存整个 Agent 表单，仍然复用既有分区组件。
 */
const WorkspaceAgentSettingsDrawer: React.FC<WorkspaceAgentSettingsDrawerProps> = ({
  open,
  editMode,
  form,
  workspaceName,
  loading = false,
  dataVersion = 0,
  onSave,
  onClose,
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
  const { styles: sharedStyles } = useStyles();
  const { styles, cx } = useWorkspaceAgentStyles();
  const screens = useBreakpoint();
  const bodyRef = useRef<HTMLDivElement>(null);

  const [activeSection, setActiveSection] = useState<AgentTemplateSectionKey>('basic');
  const [activeDocument, setActiveDocument] = useState<string>(PROMPT_DOCUMENT_FIELDS[0]);
  const [baseline, setBaseline] = useState<AgentFormSnapshot | null>(null);
  const [errorSections, setErrorSections] = useState<Set<AgentTemplateSectionKey>>(new Set());
  const [errorCounts, setErrorCounts] = useState<Record<string, number>>({});
  const [errorDocuments, setErrorDocuments] = useState<Set<string>>(new Set());
  const [saving, setSaving] = useState(false);
  const [saveFailed, setSaveFailed] = useState(false);
  const [closeConfirming, setCloseConfirming] = useState(false);

  // ── 草稿与基线 ────────────────────────────────────────────────
  // 逐字段比较内容快照，而不是 isFieldsTouched：内容改回原值后修改标记必须消失。
  // useWatch([]) 取整个表单值。preserve 让回调拿到包含未挂载字段的全量 store：
  // Prompt 文档字段只由受控编辑器写入、不渲染 Form.Item，默认的 getFieldsValue()
  // 不会返回它们，脏值判断会漏掉 Prompt 改动。
  const draftValues = Form.useWatch([], { form, preserve: true }) as unknown as
    | AgentFormSnapshot
    | undefined;
  const dirtyFields = useMemo(
    () => (baseline ? collectDirtyFields(draftValues ?? {}, baseline, DRAFT_FIELDS) : []),
    [baseline, draftValues],
  );
  const dirtySections = useMemo(() => collectDirtySections(dirtyFields), [dirtyFields]);
  const dirtyDocuments = useMemo(() => collectDirtyPromptDocuments(dirtyFields), [dirtyFields]);
  const dirtyCount = dirtyFields.length;
  const isDirty = dirtyCount > 0;
  const errorCount = useMemo(
    () => Object.values(errorCounts).reduce((total, value) => total + value, 0),
    [errorCounts],
  );

  const takeSnapshot = useCallback(
    (): AgentFormSnapshot => ({ ...(form.getFieldsValue(true) as AgentFormSnapshot) }),
    [form],
  );

  // 打开工作台或父组件灌入新数据时重新取基线。
  useEffect(() => {
    if (open && !loading) setBaseline(takeSnapshot());
  }, [open, loading, dataVersion, takeSnapshot]);

  useEffect(() => {
    if (open) return;
    setActiveSection('basic');
    setActiveDocument(PROMPT_DOCUMENT_FIELDS[0]);
    setBaseline(null);
    setErrorSections(new Set());
    setErrorCounts({});
    setErrorDocuments(new Set());
    setSaveFailed(false);
    setCloseConfirming(false);
  }, [open]);

  // ── 保存 ──────────────────────────────────────────────────────

  const focusField = (field: string) => {
    window.setTimeout(() => {
      form.scrollToField(field, { behavior: 'smooth', block: 'center' });
    }, 0);
  };

  const handleSave = useCallback(async (): Promise<boolean> => {
    if (saving || loading || !baseline) return false;
    setSaving(true);
    setSaveFailed(false);
    try {
      const values = await form.validateFields();
      setErrorSections(new Set());
      setErrorCounts({});
      setErrorDocuments(new Set());
      const ok = await onSave(values);
      if (!ok) {
        setSaveFailed(true);
        return false;
      }
      // 保存成功后刷新基线并留在当前文档，不自动关闭工作台。
      setBaseline(takeSnapshot());
      // 新增模式没有可继续编辑的实体（再次保存会重复创建），成功后返回列表。
      if (!editMode) onClose();
      return true;
    } catch (error: any) {
      const errorFields = error?.errorFields ?? [];
      if (errorFields.length === 0) {
        // 非校验失败（例如 onSave 外抛异常）：保留草稿并提示重试。
        setSaveFailed(true);
        return false;
      }
      const summary = summarizeValidationErrors(errorFields);
      setErrorSections(summary.sections);
      setErrorCounts(summary.countsBySection);
      setErrorDocuments(summary.promptDocuments);
      if (summary.firstSection) setActiveSection(summary.firstSection);
      if (summary.firstField) {
        const document = findPromptDocument(summary.firstField);
        if (document) setActiveDocument(document.field);
        focusField(summary.firstField);
      }
      return false;
    } finally {
      setSaving(false);
    }
  }, [baseline, editMode, form, loading, onClose, onSave, saving, takeSnapshot]);

  const handleBack = useCallback(() => {
    if (!isDirty) {
      onClose();
      return;
    }
    setCloseConfirming(true);
  }, [isDirty, onClose]);

  // Ctrl+S / Cmd+S 保存并阻止浏览器另存为；仅在工作台内生效。
  useEffect(() => {
    const node = bodyRef.current;
    if (!node || !open) return;
    const handler = (event: Event) => {
      const keyboardEvent = event as KeyboardEvent;
      if (!(keyboardEvent.ctrlKey || keyboardEvent.metaKey)) return;
      if (keyboardEvent.key.toLowerCase() !== 's') return;
      event.preventDefault();
      if (isDirty) void handleSave();
    };
    node.addEventListener('keydown', handler);
    return () => node.removeEventListener('keydown', handler);
  }, [handleSave, isDirty, open]);

  // ── 身份栏 ────────────────────────────────────────────────────

  const name = (draftValues?.name as string | undefined) ?? '';
  const avatarId = draftValues?.avatarId as string | undefined;
  const role = (draftValues?.role as string | undefined) ?? '';
  const avatar = avatars.find((item) => item.avatarId === String(avatarId ?? ''));
  const sourceTemplateId = String(draftValues?.sourceTemplateId ?? '').replace(/^global:/, '');
  const sourceTemplateName = sourceTemplateId
    ? templates.find((item) => item.templateId === sourceTemplateId)?.name ?? sourceTemplateId
    : undefined;

  const findAvatar = (avatarValue: unknown) =>
    avatars.find((item) => item.avatarId === String(avatarValue));

  const identityMeta = [
    workspaceName || undefined,
    role ? ROLE_LABELS.get(role) ?? role : undefined,
    sourceTemplateName ? `创建自：${sourceTemplateName}` : '未使用来源模板',
  ]
    .filter(Boolean)
    .join(' · ');

  /**
   * 抽屉宽度分两种模式。
   *
   * 「角色与 Prompt」要装下目录 + 大面积正文，必须接近全屏；其余分区只有少量字段，
   * 用全屏宽度只会把内容顶到左边、右侧留一大片死区（截图实测约 30–45% 宽度被浪费）。
   * 所以普通分区收窄到 1040px，宽屏下不再有无内容的空白。
   */
  const useWideCanvas = activeSection === 'prompts';
  const width = useWideCanvas
    ? screens.xl
      ? '94vw'
      : '100%'
    : screens.xl
      ? 1040
      : '100%';
  const editorDisabled = saving || loading || !baseline;

  const statusNode = errorCount > 0 ? (
    <span className={cx(styles.saveStatus, styles.saveStatusError)}>
      <span className={cx(styles.statusDot, 'dot-error')} />
      {`校验未通过 · ${errorCount} 项`}
    </span>
  ) : isDirty ? (
    <span className={cx(styles.saveStatus, styles.saveStatusDirty)}>
      <span className={cx(styles.statusDot, 'dot-dirty')} />
      {`未保存 · ${dirtyCount} 项`}
    </span>
  ) : (
    <span className={styles.saveStatus}>
      <span className={styles.statusDot} />
      {baseline ? '无未保存更改' : '加载中…'}
    </span>
  );

  const badges = useMemo(() => {
    const result: Partial<Record<AgentTemplateSectionKey, number>> = {};
    for (const section of errorSections) result[section] = errorCounts[section] ?? 1;
    return result;
  }, [errorCounts, errorSections]);

  const markedSections = useMemo(() => {
    const result = new Set<AgentTemplateSectionKey>(dirtySections);
    for (const section of errorSections) result.delete(section);
    return result;
  }, [dirtySections, errorSections]);

  const handlePromptChange = useCallback(
    (next: string) => {
      form.setFieldValue(activeDocument as never, next as never);
    },
    [activeDocument, form],
  );

  const currentPromptValue = (draftValues?.[activeDocument] as string | undefined) ?? '';
  const baselinePromptValue = (baseline?.[activeDocument] as string | undefined) ?? '';

  // ── 分区内容 ──────────────────────────────────────────────────

  const renderDocumentPane = () => (
    <AgentPromptEditor
      field={activeDocument}
      value={currentPromptValue}
      baselineValue={baselinePromptValue}
      errorText={errorDocuments.has(activeDocument) ? '这份内容校验未通过，请检查。' : undefined}
      readOnly={editorDisabled}
      dirtyFields={dirtyDocuments}
      errorFields={errorDocuments}
      onChange={handlePromptChange}
      onSwitchDocument={setActiveDocument}
    />
  );

  const renderActiveSection = () => {
    if (activeSection === 'prompts') return renderDocumentPane();

    return (
      <div className={styles.formScroll}>
        <div className={styles.formColumn}>
          <section hidden={activeSection !== 'basic'} data-section-id="basic">
            <div className={styles.sectionHeading}>
              <div className={styles.sectionTitle}>基础信息</div>
              <div className={styles.sectionHint}>名称、角色与启用状态决定列表与运行入口的展示。</div>
            </div>

            <Row gutter={16}>
              <Col xs={24} sm={12}>
                <ProFormText
                  name="name"
                  label="Agent 名称"
                  rules={[{ required: true, message: '请输入 Agent 名称' }]}
                />
              </Col>
              <Col xs={24} sm={12}>
                <ProFormSelect
                  name="role"
                  label="角色类型"
                  options={ROLE_OPTIONS}
                  rules={[{ required: true, message: '请选择角色类型' }]}
                  fieldProps={getAgentTemplateSelectPopupProps(sharedStyles.selectPopup)}
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
              fieldProps={getAgentTemplateSelectPopupProps(sharedStyles.selectPopup, {
                allowClear: false,
                onChange: onSourceTemplateChange,
              })}
              extra="创建时复制模板配置快照，之后独立修改；编辑模式下不可更换。"
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
                {...getAgentTemplateSelectPopupProps(sharedStyles.selectPopup)}
                options={avatars.map((item) => ({
                  label: item.name,
                  value: item.avatarId,
                }))}
                optionRender={(option) => {
                  const item = findAvatar(option.value);
                  return item ? (
                    <Space size={8}>
                      <Avatar size={22} src={item.url} />
                      <span>{item.name}</span>
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
              onGrantChange={onGrantChange}
              onSkillChange={onSkillChange}
              defaultCapIds={defaultCapIds}
              grantCapabilities={grantCapabilities}
              capabilityFieldName="selectedCapabilityIds"
              skillFieldName="skillPackageIds"
            />
          </div>

          <div hidden={activeSection !== 'models'} className={styles.modelSectionHost}>
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

          <section hidden={activeSection !== 'smartModels'} data-section-id="smartModels">
            <div className={styles.sectionHeading}>
              <div className={styles.sectionTitle}>Smart 子代理模型</div>
              <div className={styles.sectionHint}>留空表示该角色回退到子代理默认模型策略。</div>
            </div>
            <SmartRoleModelFields />
          </section>

          <section hidden={activeSection !== 'guardrails'} data-section-id="guardrails">
            <div className={styles.sectionHeading}>
              <div className={styles.sectionTitle}>执行护栏</div>
              <div className={styles.sectionHint}>这里的上限仍受平台安全上限约束。</div>
            </div>
            <div className={styles.fieldGrid}>
              <ProFormDigit
                name="maxRounds"
                label="最大轮次"
                min={1}
                max={1000}
                fieldProps={{ addonAfter: '轮' }}
                extra="一次任务允许的 Agent 循环轮数。"
              />
              <ProFormDigit
                name="maxElapsedSeconds"
                label="最大耗时"
                min={10}
                max={86400}
                fieldProps={{ addonAfter: '秒' }}
                extra="86400 秒等于 24 小时；平台安全上限仍会生效。"
              />
              <ProFormDigit
                name="maxToolCallsTotal"
                label="最大工具调用"
                min={1}
                max={500}
                fieldProps={{ addonAfter: '次' }}
                extra="包含主 Agent 与当前执行链中的工具调用。"
              />
            </div>
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
    );
  };

  const isPromptSection = activeSection === 'prompts';
  const bodyStyle = isPromptSection
    ? { gridTemplateColumns: GRID_COLUMNS_PROMPTS }
    : { gridTemplateColumns: GRID_COLUMNS_DEFAULT };
  const ready = !loading && baseline !== null;

  return (
    <Drawer
      open={open}
      width={width}
      mask={false}
      keyboard
      className={styles.drawer}
      onClose={handleBack}
      aria-label={editMode ? '编辑 Agent 工作台' : '新增 Agent 工作台'}
    >
      <ProForm
        form={form}
        submitter={false}
        layout="vertical"
        disabled={saving}
        className={sharedStyles.settingsForm}
      >
        <div className={styles.workbench} ref={bodyRef} tabIndex={-1}>
          <header className={styles.header}>
            <div className={styles.headerGroup}>
              <Button
                type="text"
                className={styles.headerBack}
                icon={<ArrowLeftOutlined />}
                onClick={handleBack}
              >
                返回列表
              </Button>
              {/* 抽屉头被隐藏（身份栏取代了它），这里补回一个可见的关闭入口。 */}
              <Tooltip title="关闭（Esc）">
                <Button
                  type="text"
                  className={styles.headerClose}
                  icon={<CloseOutlined />}
                  onClick={handleBack}
                  aria-label="关闭 Agent 编辑工作台"
                />
              </Tooltip>
            </div>

            <div className={styles.identity}>
              <Avatar size={44} src={avatar?.url} style={{ flexShrink: 0 }}>
                {name.trim().charAt(0) || '新'}
              </Avatar>
              <div className={styles.identityCopy}>
                <div className={styles.identityName}>
                  {name.trim() || (editMode ? '未命名 Agent' : '新增 Agent')}
                </div>
                <div className={styles.identityMeta}>{identityMeta}</div>
              </div>
            </div>

            <div className={styles.headerActions}>
              {statusNode}
              <Tooltip
                title={
                  editMode && !isDirty
                    ? '当前没有未保存的更改'
                    : '保存表示配置已写入；正在执行的任务会在下一次读取配置时生效。'
                }
              >
                <Button
                  /* 无修改时降级为次要按钮：长期停在灰紫的禁用态会被误读成「不能保存」。 */
                  type={isDirty || !editMode ? 'primary' : 'default'}
                  loading={saving}
                  disabled={!ready || (!isDirty && editMode)}
                  onClick={() => void handleSave()}
                >
                  {saving
                    ? '保存中…'
                    : isDirty
                      ? '保存所有更改'
                      : editMode
                        ? '已保存'
                        : '创建 Agent'}
                </Button>
              </Tooltip>
            </div>
          </header>

          {(saveFailed || errorCount > 0) && (
            <div className={styles.bannerStack}>
              {saveFailed && (
                <Alert
                  showIcon
                  closable
                  type="error"
                  onClose={() => setSaveFailed(false)}
                  message="保存失败，草稿已保留"
                  description="配置未写入。请检查网络或服务状态后重试，不需要重新输入内容。"
                  style={{ marginBottom: 8 }}
                />
              )}
              {errorCount > 0 && (
                <Alert
                  showIcon
                  type="warning"
                  message={`有 ${errorCount} 项内容校验未通过`}
                  description="出错的分区与文档已标记，已自动切换到第一个错误所在位置。"
                  style={{ marginBottom: 8 }}
                />
              )}
            </div>
          )}

          {ready ? (
            <div className={styles.body} style={bodyStyle}>
              <AgentTemplateSettingsNav
                className={styles.sectionNav}
                activeSection={activeSection}
                errorSections={errorSections}
                markedSections={markedSections}
                badges={badges}
                sections={WORKSPACE_AGENT_SECTIONS}
                onNavigate={setActiveSection}
              />
              {isPromptSection && (
                <AgentPromptCatalog
                  activeField={activeDocument}
                  dirtyFields={dirtyDocuments}
                  errorFields={errorDocuments}
                  onSelect={setActiveDocument}
                />
              )}
              <div className={styles.pane}>{renderActiveSection()}</div>
            </div>
          ) : (
            <div className={styles.loadingPlaceholder}>
              <Spin size="small" />
              正在加载 Agent 完整配置…
            </div>
          )}

          <footer className={styles.footer}>
            {/* 只保留一行：左侧「来源模板不受影响」已由字段说明覆盖，右侧「生效时机」已进保存按钮 Tooltip。
                原来的两句长文案会与应用底部状态栏挤在同一行。 */}
            <span className={styles.footerMeta}>
              <InfoCircleOutlined />
              配置只作用于当前 Agent。
            </span>
            <span className={styles.footerMeta}>Ctrl+S 保存 · Esc 关闭</span>
          </footer>
        </div>
      </ProForm>

      <Modal
        open={closeConfirming}
        title="放弃未保存的修改？"
        okText="保存并返回"
        cancelText="继续编辑"
        confirmLoading={saving}
        onCancel={() => setCloseConfirming(false)}
        onOk={async () => {
          const ok = await handleSave();
          if (ok) {
            setCloseConfirming(false);
            onClose();
          }
        }}
        footer={(_, { OkBtn, CancelBtn }) => (
          <>
            <Button
              danger
              onClick={() => {
                setCloseConfirming(false);
                onClose();
              }}
            >
              放弃修改
            </Button>
            <CancelBtn />
            <OkBtn />
          </>
        )}
      >
        <p>关闭后，本次对 Agent 配置的修改不会保存。</p>
        <p style={{ marginBottom: 0 }}>
          <Text type="secondary">
            当前有 {dirtyCount} 项未保存修改
            {dirtyDocuments.size > 0 ? `（其中 ${dirtyDocuments.size} 份 Prompt 文档）` : ''}。
          </Text>
        </p>
      </Modal>
    </Drawer>
  );
};

export default WorkspaceAgentSettingsDrawer;
