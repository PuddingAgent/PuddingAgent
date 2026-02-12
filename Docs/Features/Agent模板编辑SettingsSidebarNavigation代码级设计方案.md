# Agent 模板编辑 Settings Sidebar Navigation 代码级设计方案

> 日期：2026-05-23
> 范围：`Source/PuddingPlatformAdmin`
> ADR：`Docs/07架构/41ADR-040Agent模板编辑SettingsSidebarNavigationADR.md`

## 1. 当前代码事实

主要入口：

- `Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx`
- `Source/PuddingPlatformAdmin/src/pages/workspace-agent-template/index.tsx`
- `Source/PuddingPlatformAdmin/src/pages/workspace/[id]/index.tsx`

当前 Global 模板页中，编辑抽屉使用：

```tsx
<Drawer width={600}>
  <ProForm form={form} submitter={false} layout="vertical">
    ...
  </ProForm>
</Drawer>
```

问题点：

1. 表单字段过长，所有配置线性堆叠。
2. `selectedSkillPackageIds` 同时存在 Transfer 和 Checkbox.Group 两种输入，容易造成状态不一致。
3. Global / Workspace 模板页字段结构高度相似，但未共享。
4. 没有分组级错误定位。

## 2. 文件结构

新增：

```text
Source/PuddingPlatformAdmin/src/pages/agent-template-settings/
├── AgentTemplateSettingsDrawer.tsx
├── AgentTemplateSettingsNav.tsx
├── sections/
│   ├── BasicSection.tsx
│   ├── CapabilitySkillSection.tsx
│   ├── PromptPersonaSection.tsx
│   ├── ModelMemorySection.tsx
│   └── GuardrailSection.tsx
├── types.ts
└── styles.ts
```

修改：

```text
Source/PuddingPlatformAdmin/src/pages/global-agent-template/index.tsx
Source/PuddingPlatformAdmin/src/pages/workspace-agent-template/index.tsx
Source/PuddingPlatformAdmin/src/pages/workspace/[id]/index.tsx
```

## 3. 组件职责

### `AgentTemplateSettingsDrawer`

负责抽屉布局、保存按钮、Form 容器、分组滚动和错误定位。

Props：

```ts
export type AgentTemplateScope = 'global' | 'workspace';

export interface AgentTemplateSettingsDrawerProps<TValues> {
  open: boolean;
  mode: 'create' | 'edit';
  scope: AgentTemplateScope;
  title: string;
  builtIn?: boolean;
  form: FormInstance<TValues>;
  onClose: () => void;
  onSave: () => Promise<void>;

  providers: LlmProviderDto[];
  models: LlmModelDto[];
  memoryModels: LlmModelDto[];
  capabilities: CapabilityDto[];
  skillPackages: SkillPackageDto[];
  avatars?: AgentAvatarDto[];
  workspaces?: WorkspaceDto[];
  globalTemplates?: GlobalAgentTemplateDto[];

  grantTargetKeys: string[];
  skillTargetKeys: string[];
  setGrantTargetKeys: (keys: string[]) => void;
  setSkillTargetKeys: (keys: string[]) => void;

  onProviderChange: (providerId: string) => void | Promise<void>;
  onMemoryProviderChange: (providerId: string) => void | Promise<void>;
  onBaseGlobalTemplateChange?: (templateId?: string) => void | Promise<void>;
}
```

布局：

```tsx
<Drawer width={960} className={styles.drawer}>
  <ProForm form={form} submitter={false} layout="vertical">
    <div className={styles.settingsLayout}>
      <AgentTemplateSettingsNav ... />
      <div ref={contentRef} className={styles.settingsContent}>
        <BasicSection id="basic" ... />
        <CapabilitySkillSection id="capabilities" ... />
        <PromptPersonaSection id="prompts" ... />
        <ModelMemorySection id="models" ... />
        <GuardrailSection id="guardrails" ... />
      </div>
    </div>
  </ProForm>
</Drawer>
```

### `AgentTemplateSettingsNav`

负责展示分组、active 状态、错误状态。

```ts
export interface SettingsSectionMeta {
  key: AgentTemplateSectionKey;
  label: string;
  description?: string;
  fieldNames: string[];
}

export type AgentTemplateSectionKey =
  | 'basic'
  | 'capabilities'
  | 'prompts'
  | 'models'
  | 'guardrails';
```

导航项状态：

```ts
type SectionStatus = 'normal' | 'active' | 'error';
```

## 4. Section Registry

在 `types.ts` 中定义分组和字段映射：

```ts
export const AGENT_TEMPLATE_SECTIONS: SettingsSectionMeta[] = [
  {
    key: 'basic',
    label: '基础信息',
    fieldNames: [
      'workspaceId',
      'baseGlobalTemplateId',
      'templateId',
      'name',
      'role',
      'description',
      'avatarId',
      'avatarEmoji',
      'isEnabled',
      'sortOrder',
    ],
  },
  {
    key: 'capabilities',
    label: '能力与 Skill',
    fieldNames: ['selectedCapabilityIds', 'selectedSkillPackageIds'],
  },
  {
    key: 'prompts',
    label: 'Prompt 与个性',
    fieldNames: [
      'systemPrompt',
      'personaPrompt',
      'toolsDescription',
      'bootstrapTemplate',
      'userPromptTemplate',
    ],
  },
  {
    key: 'models',
    label: '模型与记忆',
    fieldNames: [
      'preferredProviderId',
      'preferredModelId',
      'memoryLlmProviderId',
      'memoryLlmModelId',
      'memorySearchMode',
      'reasoningEffort',
    ],
  },
  {
    key: 'guardrails',
    label: '执行护栏',
    fieldNames: [
      'maxRounds',
      'maxElapsedSeconds',
      'maxToolCallsTotal',
      'containerImage',
      'maxContextTokens',
      'maxReplyTokens',
    ],
  },
];
```

## 5. 错误定位

`handleSave` 保持在页面层，但保存前捕获 `validateFields` 的错误：

```ts
try {
  const values = await form.validateFields();
  await save(values);
} catch (error) {
  const firstErrorField = error?.errorFields?.[0]?.name?.[0];
  if (firstErrorField) {
    const section = findSectionByField(firstErrorField);
    setActiveSection(section.key);
    scrollToSection(section.key);
    setErrorSections(collectErrorSections(error.errorFields));
  }
  throw error;
}
```

辅助函数：

```ts
export function findSectionByField(fieldName: string): SettingsSectionMeta {
  return (
    AGENT_TEMPLATE_SECTIONS.find((section) =>
      section.fieldNames.includes(fieldName),
    ) ?? AGENT_TEMPLATE_SECTIONS[0]
  );
}
```

## 6. 具体分组内容

### `BasicSection`

Global 显示：

- `templateId`
- `name`
- `role`
- `description`
- `avatarId`
- `isEnabled`
- `sortOrder`

Workspace 显示：

- `workspaceId`
- `templateId`
- `name`
- `baseGlobalTemplateId`
- `role`
- `description`
- `avatarEmoji` 或后续统一为 `avatarId`
- `isEnabled`
- `sortOrder`

### `CapabilitySkillSection`

保留：

- 默认能力只读 Tag 列表
- 高权限能力 Transfer
- Skill 包 Transfer

删除：

- `SKILL 包选择（多选）` 的 Checkbox.Group 重复输入

Transfer 宽度改为自适应：

```tsx
listStyle={{ width: 260, height: 280 }}
```

在 960px 抽屉下，两栏 Transfer 可正常展示。

### `PromptPersonaSection`

包含：

- `systemPrompt`
- `personaPrompt`
- `toolsDescription`
- `bootstrapTemplate`
- `userPromptTemplate`

文本域高度：

- systemPrompt: 8 rows
- persona/tools: 4 rows
- bootstrap: 5 rows
- userPromptTemplate: 3 rows

### `ModelMemorySection`

包含：

- 主模型服务商
- 主模型
- 潜意识模型服务商
- 潜意识模型
- 记忆搜索模式
- 推理深度

主模型和潜意识模型分别使用两列 Row：

```tsx
<Row gutter={16}>
  <Col span={12}>provider</Col>
  <Col span={12}>model</Col>
</Row>
```

### `GuardrailSection`

包含：

- maxRounds
- maxElapsedSeconds
- maxToolCallsTotal
- containerImage
- maxContextTokens
- maxReplyTokens

数字字段使用三列布局，但在小屏降为单列。

## 7. 样式要求

`styles.ts` 使用 `antd-style`，遵循现有 Pudding Admin token。

关键样式：

```ts
export const useStyles = createStyles(({ token, css }) => ({
  drawer: css`
    .ant-drawer-body {
      padding: 0;
    }
  `,
  settingsLayout: css`
    display: grid;
    grid-template-columns: 184px minmax(0, 1fr);
    min-height: calc(100vh - 56px);
  `,
  settingsNav: css`
    position: sticky;
    top: 0;
    align-self: start;
    height: calc(100vh - 56px);
    padding: 16px 12px;
    border-right: 1px solid ${token.colorBorderSecondary};
    background: ${token.colorBgContainer};
  `,
  settingsContent: css`
    min-width: 0;
    padding: 20px 24px 32px;
    overflow: auto;
  `,
  section: css`
    padding-bottom: 28px;
    margin-bottom: 24px;
    border-bottom: 1px solid ${token.colorBorderSecondary};
  `,
  sectionTitle: css`
    margin-bottom: 16px;
    font-size: 15px;
    font-weight: 600;
  `,
}));
```

窄屏：

```css
@media (max-width: 768px) {
  grid-template-columns: 1fr;
}
```

窄屏时导航切换为顶部 sticky 横向滚动。

## 8. 页面接入

### Global 页面

从 `global-agent-template/index.tsx` 删除 Drawer 内联表单，替换为：

```tsx
<AgentTemplateSettingsDrawer
  scope="global"
  open={formDrawer}
  mode={editItem ? 'edit' : 'create'}
  title={editItem ? '编辑 Agent 模板' : '创建 Agent 模板'}
  builtIn={editItem?.isBuiltIn}
  form={form}
  onClose={() => setFormDrawer(false)}
  onSave={handleSave}
  providers={providers}
  models={models}
  memoryModels={memoryModels}
  capabilities={capabilities}
  skillPackages={skillPackages}
  avatars={avatars}
  grantTargetKeys={grantTargetKeys}
  skillTargetKeys={skillTargetKeys}
  setGrantTargetKeys={setGrantTargetKeys}
  setSkillTargetKeys={setSkillTargetKeys}
  onProviderChange={handleProviderChange}
  onMemoryProviderChange={handleMemoryProviderChange}
/>
```

### Workspace 页面

同样替换 Drawer 内联表单，额外传入：

```tsx
scope="workspace"
workspaces={workspaces}
globalTemplates={globalTemplates}
onBaseGlobalTemplateChange={handleGlobalTemplateChange}
```

## 9. 测试与验收

最低验证：

```powershell
cd Source/PuddingPlatformAdmin
npm run tsc
npm run lint
```

建议增加前端测试：

- 打开 Global Agent 模板编辑抽屉，断言存在导航项 `基础信息`、`能力与 Skill`、`Prompt 与个性`、`模型与记忆`、`执行护栏`。
- 点击 `模型与记忆` 后，对应 section 可见。
- 清空必填 `name` 保存，导航 `基础信息` 显示错误状态。
- 页面中只存在一个 `selectedSkillPackageIds` 输入形态，不再重复展示 Checkbox 版本。

## 10. 非目标

本次不做：

- 后端 DTO 改造。
- 局部保存。
- 草稿保存。
- 多步骤创建向导。
- Agent 模板权限模型重构。
