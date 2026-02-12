# Admin Console 去 Ant Design Pro 化代码级设计方案

> 日期：2026-05-23  
> 范围：`Source/PuddingPlatformAdmin` 的 Console 壳层、通用组件、`/admin/workspace` 样板页  
> ADR：[37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR](../07架构/37ADR-036AdminConsole去AntDesignPro化与Pudding设计语言统一ADR.md)

---

## 1. 目标

把当前 Admin Console 从 Ant Design Pro 模板外观改为 Pudding 自有设计语言，同时保留 Ant Design 的表单、表格、弹窗等工程能力。

本方案面向 Dev 施工，目标是让第一轮改造可落地、可验收、可继续推广：

1. 生产环境不再显示 Ant Design Pro footer、水印、模板链接和 SettingDrawer；
2. 建立 Pudding Admin 设计 token 和组件 wrapper；
3. 将 `/admin/workspace` 迁移为样板页；
4. 给后续管理页迁移提供明确文件边界、组件 API、验收命令和静态扫描规则。

---

## 2. 当前代码事实

### 2.1 模板痕迹

| 文件 | 现状 | 处理 |
|------|------|------|
| `Source/PuddingPlatformAdmin/package.json` | `name`、`description`、`repository` 仍是 Ant Design Pro | 后续模板清理阶段改为 Pudding metadata。 |
| `Source/PuddingPlatformAdmin/src/app.tsx` | `footerRender`、`waterMarkProps`、`links`、`SettingDrawer` 暴露 Pro 默认能力 | 第一阶段 hardening。 |
| `Source/PuddingPlatformAdmin/src/components/Footer/index.tsx` | 渲染 `DefaultFooter`，包含 Ant Design Pro 链接 | 删除或替换为 Pudding 版权组件；生产环境默认不渲染。 |
| `Source/PuddingPlatformAdmin/config/defaultSettings.ts` | 使用 ProLayout `mix`、`navTheme`、`colorPrimary` | 暂保留必要配置，逐步降为 shell data。 |
| `Source/PuddingPlatformAdmin/src/pages/workspace/index.tsx` | 使用 `PageContainer`、`ProTable`、Card inline style | 样板页迁移。 |

### 2.2 可复用基础

| 文件 | 可复用点 |
|------|----------|
| `src/global.style.ts` | 已有 Pudding runtime tokens、动画、dark theme hook。 |
| `src/components/ThemeMode/index.tsx` | 已有 light/dark/system 主题管理和 AntD ConfigProvider。 |
| `src/components/GlobalActions/index.tsx` | 已抽出 Console / Chat 共用全局操作。 |
| `src/pages/chat/styles.ts` | 已体现暖纸面、低饱和紫色、克制圆角的方向。 |

---

## 3. 文件结构

第一轮施工新增和修改以下文件。

```text
Source/PuddingPlatformAdmin/src/
├── app.tsx                                      # 修改：去除 Pro footer/watermark/links/常驻 SettingDrawer
├── global.style.ts                             # 修改：增加 Pudding Admin tokens 和 Pro 覆盖
├── components/
│   ├── Footer/index.tsx                        # 修改或废弃：不再渲染 Ant Pro footer
│   ├── PuddingAdminShell/
│   │   ├── index.tsx                           # 新增：Console shell
│   │   └── styles.ts                           # 新增：shell 样式
│   ├── PuddingPageHeader/
│   │   ├── index.tsx                           # 新增：页面标题/描述/操作区
│   │   └── styles.ts                           # 新增
│   ├── PuddingToolbar/
│   │   ├── index.tsx                           # 新增：搜索/筛选/视图切换
│   │   └── styles.ts                           # 新增
│   ├── PuddingDataTable/
│   │   ├── index.tsx                           # 新增：antd Table wrapper
│   │   └── styles.ts                           # 新增
│   ├── PuddingStatusBadge/
│   │   ├── index.tsx                           # 新增：统一状态 badge
│   │   └── styles.ts                           # 新增
│   └── PuddingEntityCard/
│       ├── index.tsx                           # 新增：实体卡片辅助视图
│       └── styles.ts                           # 新增
└── pages/
    └── workspace/
        ├── index.tsx                           # 修改：样板页迁移
        └── styles.ts                           # 新增：页面局部布局

Source/PuddingPlatformAdmin/e2e/
├── admin-console-branding.spec.ts              # 新增：模板痕迹和截图 smoke
└── admin-workspace-responsive.spec.ts          # 新增：响应式和可访问性 smoke

Source/PuddingPlatformAdmin/scripts/
└── scan-ant-pro-template.cjs                    # 新增：静态模板痕迹扫描
```

---

## 4. 设计 Token

### 4.1 `global.style.ts` 增量

在 `:root` 中补充 admin tokens。不要删除已有 runtime tokens；Admin 和 Chat 共享部分色彩，但使用独立语义名。

```ts
injectGlobal`
  :root {
    --pudding-admin-bg: #f5f0e8;
    --pudding-admin-bg-subtle: #ede5d9;
    --pudding-admin-surface: #fafaf7;
    --pudding-admin-surface-muted: #f2eee7;
    --pudding-admin-border: rgba(92, 74, 58, 0.12);
    --pudding-admin-border-strong: rgba(92, 74, 58, 0.2);
    --pudding-admin-text: #1a1a2e;
    --pudding-admin-text-muted: #5c4a3a;
    --pudding-admin-accent: #7c3aed;
    --pudding-admin-accent-soft: rgba(124, 58, 237, 0.08);
    --pudding-admin-success: #4f7f58;
    --pudding-admin-warning: #b7791f;
    --pudding-admin-danger: #b42318;
    --pudding-admin-radius: 8px;
    --pudding-admin-shadow-low: 0 1px 6px rgba(0, 0, 0, 0.04);
  }

  [data-pudding-theme='dark'] {
    --pudding-admin-bg: #0b1020;
    --pudding-admin-bg-subtle: #111827;
    --pudding-admin-surface: #172033;
    --pudding-admin-surface-muted: #1f2937;
    --pudding-admin-border: rgba(167, 139, 250, 0.18);
    --pudding-admin-border-strong: rgba(167, 139, 250, 0.28);
    --pudding-admin-text: #f8fafc;
    --pudding-admin-text-muted: #cbd5e1;
    --pudding-admin-accent: #a78bfa;
    --pudding-admin-accent-soft: rgba(167, 139, 250, 0.12);
    --pudding-admin-success: #86efac;
    --pudding-admin-warning: #facc15;
    --pudding-admin-danger: #fca5a5;
    --pudding-admin-shadow-low: 0 1px 8px rgba(0, 0, 0, 0.28);
  }
`;
```

### 4.2 AntD ConfigProvider 对齐

在 `ThemeProviderContainer` 中同步 AntD token。

```ts
const themeConfig = useMemo(
  () => ({
    cssVar: true,
    algorithm: isDark ? antdTheme.darkAlgorithm : antdTheme.defaultAlgorithm,
    token: {
      colorPrimary: isDark ? '#a78bfa' : '#7c3aed',
      colorBgLayout: isDark ? '#0b1020' : '#f5f0e8',
      colorBgContainer: isDark ? '#172033' : '#fafaf7',
      colorBorder: isDark ? 'rgba(167, 139, 250, 0.18)' : 'rgba(92, 74, 58, 0.12)',
      colorText: isDark ? '#f8fafc' : '#1a1a2e',
      colorTextSecondary: isDark ? '#cbd5e1' : '#5c4a3a',
      borderRadius: 8,
      borderRadiusLG: 8,
      borderRadiusXL: 12,
      controlHeight: 36,
      controlHeightSM: 30,
    },
  }),
  [isDark],
);
```

---

## 5. Shell Hardening

### 5.1 `app.tsx` 修改

目标：生产环境不再显示 Pro footer、水印、模板链接和常驻 SettingDrawer。

建议改法：

```tsx
const showDevTools = isDev && new URLSearchParams(window.location.search).has('debug');

export const layout: RunTimeLayoutConfig = ({ initialState, setInitialState }) => {
  return {
    actionsRender: () => [
      <PuddingGlobalActions
        key="global-actions"
        variant="pro-layout"
        setInitialState={setInitialState}
      />,
    ],
    avatarProps: {
      src: initialState?.currentUser?.avatar,
      title: <AvatarName />,
      render: (_, avatarChildren) => (
        <AvatarDropdown dropdownTrigger={['click']}>{avatarChildren}</AvatarDropdown>
      ),
    },
    footerRender: false,
    waterMarkProps: undefined,
    links: [],
    menuHeaderRender: undefined,
    childrenRender: (children) => (
      <>
        {children}
        {showDevTools && (
          <SettingDrawer
            disableUrlParams
            enableDarkTheme
            settings={initialState?.settings}
            onSettingChange={(settings) => {
              setInitialState((preInitialState) => ({
                ...preInitialState,
                settings,
              }));
            }}
          />
        )}
      </>
    ),
    ...initialState?.settings,
  };
};
```

说明：

- `footerRender: false` 是第一阶段最小变更；
- `waterMarkProps` 必须移除，不显示用户水印；
- `links` 必须为空，不能显示 OpenAPI/Ant Pro 模板链接；
- `SettingDrawer` 只允许 `?debug=1` 且 dev 环境显示。

### 5.2 `Footer/index.tsx` 处理

第一阶段可以保留文件但不再由 layout 使用。若仍有页面直接 import，则替换为 Pudding 版权组件：

```tsx
const Footer: React.FC = () => (
  <footer aria-label="Pudding Console footer" className="pudding-console-footer">
    <span>Pudding Console</span>
  </footer>
);
```

生产普通页面默认不需要 footer。

---

## 6. 通用组件设计

### 6.1 `PuddingPageHeader`

职责：替代 `PageContainer` header。标题和说明保持紧凑，右侧承载主操作。

```tsx
export interface PuddingPageHeaderProps {
  title: React.ReactNode;
  description?: React.ReactNode;
  eyebrow?: React.ReactNode;
  actions?: React.ReactNode;
  meta?: React.ReactNode;
}

export const PuddingPageHeader: React.FC<PuddingPageHeaderProps> = ({
  title,
  description,
  eyebrow,
  actions,
  meta,
}) => (
  <section className={styles.header}>
    <div className={styles.copy}>
      {eyebrow && <div className={styles.eyebrow}>{eyebrow}</div>}
      <h1 className={styles.title}>{title}</h1>
      {description && <p className={styles.description}>{description}</p>}
      {meta && <div className={styles.meta}>{meta}</div>}
    </div>
    {actions && <div className={styles.actions}>{actions}</div>}
  </section>
);
```

样式约束：

- `h1` 20-24px；
- description 13-14px；
- header padding 桌面 24px 28px 12px，移动端 16px；
- actions 换行时不压缩标题。

### 6.2 `PuddingToolbar`

职责：承载搜索、筛选、视图切换、刷新、批量动作。

```tsx
export interface PuddingToolbarProps {
  leading?: React.ReactNode;
  filters?: React.ReactNode;
  actions?: React.ReactNode;
}

export const PuddingToolbar: React.FC<PuddingToolbarProps> = ({ leading, filters, actions }) => (
  <div className={styles.toolbar}>
    <div className={styles.leading}>{leading}</div>
    <div className={styles.filters}>{filters}</div>
    <div className={styles.actions}>{actions}</div>
  </div>
);
```

样式约束：

- 高度不固定，最小 44px；
- 移动端垂直堆叠；
- `Input.Search` 宽度桌面 280-360px，移动端 100%；
- 视图切换使用 `Segmented`，不要继续使用 solid `Radio.Button` 的 Pro 蓝紫块感。

### 6.3 `PuddingDataTable`

职责：封装 antd Table 默认样式和空状态，避免裸 ProTable 默认外观。

```tsx
export interface PuddingDataTableProps<T extends object> {
  rowKey: string | ((record: T) => string);
  columns: ColumnsType<T>;
  dataSource: T[];
  loading?: boolean;
  emptyText?: React.ReactNode;
  pagination?: TablePaginationConfig | false;
}

export function PuddingDataTable<T extends object>({
  rowKey,
  columns,
  dataSource,
  loading,
  emptyText,
  pagination,
}: PuddingDataTableProps<T>) {
  return (
    <div className={styles.tableSurface}>
      <Table<T>
        rowKey={rowKey}
        columns={columns}
        dataSource={dataSource}
        loading={loading}
        pagination={pagination}
        locale={{ emptyText: emptyText ?? '暂无数据' }}
        size="middle"
      />
    </div>
  );
}
```

样式约束：

- surface 使用 `--pudding-admin-surface`；
- border 使用 `--pudding-admin-border`；
- 表头背景 `--pudding-admin-surface-muted`；
- hover 背景 `--pudding-admin-accent-soft` 的低透明版本；
- 不使用 ProTable 默认 `options={{ density: true }}`。

### 6.4 `PuddingStatusBadge`

职责：统一业务状态，不使用高饱和 Tag。

```tsx
export type PuddingStatusTone = 'success' | 'warning' | 'danger' | 'neutral' | 'accent';

export interface PuddingStatusBadgeProps {
  tone: PuddingStatusTone;
  children: React.ReactNode;
}

export const PuddingStatusBadge: React.FC<PuddingStatusBadgeProps> = ({ tone, children }) => (
  <span className={classNames(styles.badge, styles[tone])}>
    <span className={styles.dot} aria-hidden="true" />
    {children}
  </span>
);
```

状态映射：

```ts
const getWorkspaceStatus = (workspace: WorkspaceWithPermDto) => {
  if (workspace.isFrozen) return { tone: 'danger' as const, label: '已冻结' };
  if (!workspace.isEnabled) return { tone: 'neutral' as const, label: '已停用' };
  return { tone: 'success' as const, label: '运行中' };
};
```

### 6.5 `PuddingEntityCard`

职责：移动端和少量实体摘要辅助视图。桌面默认不把卡片作为首选。

```tsx
export interface PuddingEntityCardProps {
  title: React.ReactNode;
  description?: React.ReactNode;
  status?: React.ReactNode;
  meta?: Array<{ label: React.ReactNode; value: React.ReactNode }>;
  actions?: React.ReactNode;
}
```

样式约束：

- border radius 8px；
- 不使用大面积 blur；
- 不使用 glow；
- actions 固定在底部，避免卡片高度跳动。

---

## 7. `/admin/workspace` 样板页设计

### 7.1 页面结构

```tsx
const WorkspacePage: React.FC = () => (
  <App>
    <WorkspaceManager />
  </App>
);

const WorkspaceManager: React.FC = () => {
  return (
    <main className={styles.page}>
      <PuddingPageHeader
        title="场景"
        description="管理 AI 助手运行上下文、成员入口和默认会话空间。"
        actions={
          <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
            新建场景
          </Button>
        }
      />

      <PuddingToolbar
        leading={
          <Input.Search
            allowClear
            placeholder="搜索名称或场景 ID"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
          />
        }
        filters={
          <Select
            value={statusFilter}
            onChange={setStatusFilter}
            options={statusOptions}
            aria-label="按状态筛选场景"
          />
        }
        actions={
          <Segmented
            value={viewMode}
            onChange={(value) => setViewMode(value as ViewMode)}
            options={[
              { label: '表格', value: 'table', icon: <TableOutlined /> },
              { label: '卡片', value: 'card', icon: <AppstoreOutlined /> },
            ]}
          />
        }
      />

      {viewMode === 'table' ? renderTable() : renderCards()}
      {renderCreateModal()}
    </main>
  );
};
```

### 7.2 默认视图

桌面端默认 `table`，移动端可以默认 `card`。

```ts
const getInitialViewMode = (): ViewMode => {
  if (typeof window !== 'undefined' && window.matchMedia('(max-width: 767px)').matches) {
    return 'card';
  }
  return 'table';
};
```

### 7.3 表格列

```tsx
const columns: ColumnsType<WorkspaceWithPermDto> = [
  {
    title: '场景',
    dataIndex: 'name',
    render: (_, record) => (
      <div className={styles.nameCell}>
        <span className={styles.name}>{record.name}</span>
        {record.workspaceId === 'default' && (
          <PuddingStatusBadge tone="accent">内置</PuddingStatusBadge>
        )}
        {record.description && <span className={styles.description}>{record.description}</span>}
      </div>
    ),
  },
  {
    title: '状态',
    width: 112,
    render: (_, record) => {
      const status = getWorkspaceStatus(record);
      return <PuddingStatusBadge tone={status.tone}>{status.label}</PuddingStatusBadge>;
    },
  },
  {
    title: '成员',
    dataIndex: 'memberCount',
    width: 88,
    align: 'right',
    render: (value) => value ?? 0,
  },
  {
    title: '场景 ID',
    dataIndex: 'workspaceId',
    width: 220,
    render: (value) => <Typography.Text code copyable>{value}</Typography.Text>,
  },
  {
    title: '创建时间',
    dataIndex: 'createdAt',
    width: 160,
    render: (value) => dayjs(value).format('YYYY-MM-DD'),
  },
  {
    title: '操作',
    key: 'actions',
    width: 132,
    render: (_, record) => (
      <Space size={4}>
        <Tooltip title="进入 Chat">
          <Button
            aria-label={`进入 ${record.name} Chat`}
            icon={<EnterOutlined />}
            onClick={() => history.push(`/workspace/${record.workspaceId}`)}
          />
        </Tooltip>
        <Popconfirm
          title="确认删除此场景？"
          description="此操作不可恢复，内置场景无法删除。"
          onConfirm={() => handleDelete(record)}
          okText="删除"
          cancelText="取消"
          okButtonProps={{ danger: true }}
        >
          <Tooltip title={record.workspaceId === 'default' ? '内置场景不可删除' : '删除'}>
            <Button
              aria-label={`删除 ${record.name}`}
              danger
              icon={<DeleteOutlined />}
              disabled={record.workspaceId === 'default'}
            />
          </Tooltip>
        </Popconfirm>
      </Space>
    ),
  },
];
```

### 7.4 搜索与筛选

```ts
const filteredWorkspaces = useMemo(() => {
  const normalizedQuery = query.trim().toLowerCase();
  return workspaces.filter((workspace) => {
    const matchesQuery =
      !normalizedQuery ||
      workspace.name?.toLowerCase().includes(normalizedQuery) ||
      workspace.workspaceId?.toLowerCase().includes(normalizedQuery);

    const status = getWorkspaceStatus(workspace).tone;
    const matchesStatus = statusFilter === 'all' || statusFilter === status;

    return matchesQuery && matchesStatus;
  });
}, [query, statusFilter, workspaces]);
```

筛选项：

```ts
const statusOptions = [
  { value: 'all', label: '全部状态' },
  { value: 'success', label: '运行中' },
  { value: 'danger', label: '已冻结' },
  { value: 'neutral', label: '已停用' },
];
```

### 7.5 空状态

```tsx
const emptyText = (
  <div className={styles.emptyState}>
    <div className={styles.emptyTitle}>暂无场景</div>
    <div className={styles.emptyDescription}>创建一个场景后，就可以为不同工作上下文配置 Agent。</div>
    <Button type="primary" icon={<PlusOutlined />} onClick={openCreateModal}>
      新建场景
    </Button>
  </div>
);
```

不要使用 AntD 默认 illustration 作为主视觉；空状态保持文字和单个操作即可。

---

## 8. 表单与弹窗规范

### 8.1 新建场景 Modal

保留现有业务逻辑，调整文案、宽度和验证提示。

```tsx
<Modal
  title="新建场景"
  open={createOpen}
  onOk={handleCreate}
  onCancel={() => setCreateOpen(false)}
  confirmLoading={createLoading}
  okText="创建"
  cancelText="取消"
  width={520}
  destroyOnClose
>
  <Form form={form} layout="vertical" className={styles.createForm}>
    <Form.Item
      name="name"
      label="名称"
      rules={[
        { required: true, message: '请输入场景名称' },
        { max: 128, message: '最多 128 个字符' },
      ]}
    >
      <Input autoFocus placeholder="例如：默认工作空间" />
    </Form.Item>

    <Form.Item
      name="description"
      label="描述"
      extra="用于帮助成员理解这个场景的用途。"
    >
      <Input.TextArea placeholder="可选" rows={3} maxLength={512} showCount />
    </Form.Item>
  </Form>
</Modal>
```

### 8.2 提交状态

- 创建中禁用确认按钮；
- 创建成功后关闭 modal 并刷新；
- 如果系统没有可用 team，保留现有错误：`创建失败：系统尚未初始化可用分组`；
- 表单验证错误不弹全局 message。

---

## 9. 静态扫描门禁

新增 `scripts/scan-ant-pro-template.cjs`。

```js
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const blocked = [
  'Ant Design Pro',
  'Powered by Ant Design',
  'Powered by Ant Desgin',
  'github.com/ant-design/ant-design-pro',
  'pro.ant.design',
];

const allowList = new Set([
  path.normalize('scripts/scan-ant-pro-template.cjs'),
]);

function walk(dir) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (['node_modules', 'dist', '.umi', '.git'].includes(entry.name)) return [];
      return walk(fullPath);
    }
    if (!/\.(ts|tsx|js|jsx|less|css|md|json)$/.test(entry.name)) return [];
    return [fullPath];
  });
}

const failures = [];

for (const file of walk(root)) {
  const relative = path.relative(root, file);
  if (allowList.has(path.normalize(relative))) continue;
  const text = fs.readFileSync(file, 'utf8');
  for (const phrase of blocked) {
    if (text.includes(phrase)) {
      failures.push(`${relative}: contains "${phrase}"`);
    }
  }
}

if (failures.length > 0) {
  console.error(failures.join('\n'));
  process.exit(1);
}
```

`package.json` 增加：

```json
{
  "scripts": {
    "lint:branding": "node scripts/scan-ant-pro-template.cjs"
  }
}
```

注意：第一阶段如果 README/package metadata 还没清理，可以临时加入 allowList，但生产代码和页面组件不能豁免。

---

## 10. Playwright 验收

### 10.1 模板痕迹

`e2e/admin-console-branding.spec.ts`：

```ts
import { expect, test } from '@playwright/test';

test('admin workspace does not expose Ant Design Pro branding', async ({ page }) => {
  await page.goto('/admin/workspace');

  await expect(page.getByText('Ant Design Pro')).toHaveCount(0);
  await expect(page.getByText('Powered by Ant Design')).toHaveCount(0);
  await expect(page.getByText('Powered by Ant Desgin')).toHaveCount(0);
  await expect(page.locator('a[href*="pro.ant.design"]')).toHaveCount(0);
  await expect(page.locator('a[href*="github.com/ant-design/ant-design-pro"]')).toHaveCount(0);
});
```

### 10.2 响应式截图

`e2e/admin-workspace-responsive.spec.ts`：

```ts
import { expect, test } from '@playwright/test';

const viewports = [
  { width: 375, height: 812 },
  { width: 768, height: 1024 },
  { width: 1024, height: 768 },
  { width: 1440, height: 900 },
];

for (const viewport of viewports) {
  test(`workspace page renders at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await page.goto('/admin/workspace');

    await expect(page.getByRole('heading', { name: '场景' })).toBeVisible();
    await expect(page.getByRole('button', { name: /新建场景/ })).toBeVisible();
    await expect(page.locator('body')).not.toHaveCSS('overflow-x', 'scroll');
  });
}
```

### 10.3 可访问性 smoke

同一 spec 增加：

```ts
test('icon actions have accessible names', async ({ page }) => {
  await page.goto('/admin/workspace');

  const enterButtons = page.getByRole('button', { name: /进入 .* Chat/ });
  await expect(enterButtons.first()).toBeVisible();

  const deleteButtons = page.getByRole('button', { name: /删除/ });
  await expect(deleteButtons.first()).toBeVisible();
});
```

---

## 11. 施工任务切分

### Task 1：Shell hardening

文件：

- 修改 `Source/PuddingPlatformAdmin/src/app.tsx`
- 修改或停用 `Source/PuddingPlatformAdmin/src/components/Footer/index.tsx`

验收：

```powershell
cd Source\PuddingPlatformAdmin
npm run tsc
npm run biome:lint
```

浏览器检查：

- 打开 `/admin/workspace`；
- 页面底部不显示 Ant Design Pro footer；
- 页面不显示水印；
- 普通 URL 不显示 SettingDrawer；
- `?debug=1` 且 dev 环境可以显示 SettingDrawer。

### Task 2：Admin tokens

文件：

- 修改 `Source/PuddingPlatformAdmin/src/global.style.ts`
- 修改 `Source/PuddingPlatformAdmin/src/components/ThemeMode/index.tsx`

验收：

- light/dark 主题切换后背景、surface、文字、边框均来自 Pudding token；
- Chat 页面不发生明显回归；
- Console 页面不再出现浅蓝模板底色。

### Task 3：新增 wrapper components

文件：

- 新增 `PuddingPageHeader`
- 新增 `PuddingToolbar`
- 新增 `PuddingDataTable`
- 新增 `PuddingStatusBadge`
- 新增 `PuddingEntityCard`

验收：

- 组件 API 与本方案一致；
- 所有 icon-only 操作接收 `aria-label`；
- 所有样式引用 token，不写散落品牌色。

### Task 4：迁移 `/admin/workspace`

文件：

- 修改 `Source/PuddingPlatformAdmin/src/pages/workspace/index.tsx`
- 新增 `Source/PuddingPlatformAdmin/src/pages/workspace/styles.ts`

验收：

- 默认桌面视图是表格；
- 卡片视图可切换；
- 搜索和状态筛选生效；
- 新建、进入 Chat、删除业务逻辑保持不变；
- 默认工作空间不可删除；
- 空状态、加载状态、错误 message 保持可读。

### Task 5：增加门禁

文件：

- 新增 `Source/PuddingPlatformAdmin/scripts/scan-ant-pro-template.cjs`
- 修改 `Source/PuddingPlatformAdmin/package.json`
- 新增 `Source/PuddingPlatformAdmin/e2e/admin-console-branding.spec.ts`
- 新增 `Source/PuddingPlatformAdmin/e2e/admin-workspace-responsive.spec.ts`

验收：

```powershell
cd Source\PuddingPlatformAdmin
npm run lint:branding
npm run test:e2e -- admin-console-branding.spec.ts
npm run test:e2e -- admin-workspace-responsive.spec.ts
```

---

## 12. 后续页面迁移顺序

`/admin/workspace` 验收后，按风险和复用程度迁移：

1. `/admin/team-management`：和 workspace 同属实体管理，复用表格/卡片模式；
2. `/admin/user-management`、`/admin/role-management`：验证复杂操作列和权限状态；
3. `/admin/global-agent-template`、`/admin/workspace-agent-template`：验证表单、头像、模型配置；
4. `/admin/keyvault`、`/admin/llm-resource-pool`：验证敏感信息、密钥遮蔽、风险操作；
5. `/admin/diagnostics/*`、`/admin/stats/tokens`：验证数据密集和图表类页面。

每迁移一个页面都必须：

- 替换 `PageContainer`；
- 去掉页面级 inline style；
- 使用 wrapper components；
- 补充最小 Playwright smoke；
- 通过 `lint:branding`。

---

## 13. 非目标

本轮不做：

- 重写 Chat；
- 更换 UI 技术栈；
- 删除所有 `@ant-design/pro-components` 依赖；
- 新增后端 API；
- 修改 Workspace 创建/删除数据契约；
- 一次性迁移所有页面。

---

## 14. 完成定义

第一轮完成必须满足：

1. `/admin/workspace` 视觉上不再像 Ant Design Pro 模板页；
2. 生产环境无 Ant Pro footer、水印、模板链接、常驻 SettingDrawer；
3. `PuddingPageHeader`、`PuddingToolbar`、`PuddingDataTable`、`PuddingStatusBadge` 可被其他页面复用；
4. light/dark 主题下截图可读；
5. 375、768、1024、1440 viewport 无布局破裂；
6. 静态扫描和 E2E smoke 可以阻断模板痕迹回归；
7. Dev 可以按本文件继续迁移后续管理页，不需要重新解释设计语言。
