# Pudding 前端 Workspace First 改造设计

## 背景

当前前端的实际入口仍以 Chat 为中心：`/` 和 `/welcome` 最终进入 `/chat`，用户会直接面对单个 Agent 的对话界面。新的产品模型已经发生变化：Workspace 是用户的工作场所，Workspace 内包含多个 Agent；Chat 只是用户选中某个 Agent 后的对话详情页。

下一阶段前端改造的目标是把用户主路径从“打开一个 Chat”调整为“进入一个工作空间，观察多个 Agent，再选择一个 Agent 发起任务或对话”。这能让工作室像真实的协作场所，而不是后台管理台或单助手聊天页。

## 设计目标

- 用户首次进入产品时看到的是工作空间或工作室，而不是后台管理菜单。
- Workspace 成为主导航实体，Agent 是 Workspace 内的成员，Chat 是 Agent 的会话详情。
- `/pudding/workspaces/{workspaceId}` 成为用户日常工作台，展示像素风格办公室和多个 Agent 的实时状态。
- `/chat` 保持强对话能力，但必须能通过面包屑稳定回到当前 Workspace。
- 管理能力和用户工作流分离，普通用户路径避免使用后台管理语义。
- 所有页面跳转都可以通过 URL 恢复，支持浏览器刷新、复制链接和深链进入。

## 推荐信息架构

### 用户侧页面

| 页面 | 角色 | 路径 | 说明 |
| --- | --- | --- | --- |
| 工作空间入口 | 用户首屏/空间选择 | `/pudding/workspaces` | 展示用户可访问的 Workspace、最近活跃、Agent 数量、进入工作室入口。 |
| 工作室 | 用户主工作台 | `/pudding/workspaces/{workspaceId}` | 像素风格房间，展示 Workspace 内多个 Agent 和状态。 |
| Agent 工作区深链 | 用户主工作台内选中 Agent | `/pudding/workspaces/{workspaceId}/{agentId}` | 恢复指定 Workspace 和 Agent 的工作室上下文。 |
| Agent 对话 | 单 Agent 任务详情 | `/chat?workspaceId={workspaceId}&agentId={agentId}&sessionId={sessionId}` | 处理消息流、任务过程、上下文、历史会话。 |
| Agent 管理入口 | Workspace 内成员管理 | 工作室抽屉或后续 `/pudding/workspaces/{workspaceId}/agents` | 查看 Agent 头像、能力、状态、启停和基础设置。 |
| 用户设置 | 个人偏好 | `/settings` | 主题、语言、默认 Workspace、通知偏好。 |

### 管理侧页面

| 页面组 | 建议路径 | 说明 |
| --- | --- | --- |
| 后台首页 | `/admin` | 后台管理的默认入口，聚合常用管理页面。 |
| 工作区后台设置 | `/admin/workspace/default` | 工作区管理菜单的默认落点；`/admin/workspace` 只作为跳转入口。 |
| 模型和资源 | `/admin/llm-resource-pool` | LLM 资源池、模型配置、服务商资源。 |
| 模板和能力 | `/admin/global-agent-template`、`/admin/capability-management`、`/admin/skill-management` | 全局模板、能力、技能配置。 |
| 组织和权限 | `/admin/user-management`、`/admin/team-management`、`/admin/role-management` | 用户、团队、角色。 |
| 诊断和运行时 | `/admin/diagnostics/*`、`/admin/runtime-management` | 系统诊断、运行时状态、调试面板。 |

这不是一次性搬迁所有管理页面的要求。第一阶段只需要在用户主路径上隐藏后台感，后续逐步把管理页面归入 `/admin` 或设置入口。

## 用户进入规则

入口决策应封装成一个独立的前端路由决策函数，避免散落在页面组件里。

1. 未登录用户进入 `/`：跳转 `/user/login`。
2. 已登录用户进入 `/`：
   - 如果本地有最近访问的 `workspaceId` 且仍可访问，跳转 `/pudding/workspaces/{workspaceId}`。
   - 否则如果只有一个可用 Workspace，跳转 `/pudding/workspaces/{workspaceId}`。
   - 否则跳转 `/pudding/workspaces`。
3. 已登录用户进入 `/welcome`：按 `/` 的同一规则处理。
4. 用户直接访问 `/chat`：
   - 如果 URL 带 `workspaceId` 和 `agentId`，按深链恢复 Chat。
   - 如果缺少 `workspaceId`，先按默认 Workspace 决策补齐。
   - 如果缺少 `agentId`，选择该 Workspace 内第一个可用 Agent。
   - 如果没有可用 Agent，返回工作室并展示空状态。
5. 用户直接访问 `/workspace-studio`：
   - 如果缺少 `workspaceId`，按最近访问或默认 Workspace 补齐。
   - 如果指定的 Workspace 不存在或无权限，跳转 `/pudding/workspaces` 并显示不可访问提示。
   - 该路径仅作为旧链接兼容入口，新链接应使用 `/pudding/workspaces/{workspaceId}`。

## 跳转规则

### 全局规则

- Pudding 标识点击后回到用户入口，不进入后台页面。
- 普通用户 Workspace 和 Agent 上下文优先使用 `/pudding/workspaces/{workspaceId}/{agentId}` 路径段表达；Chat 会话继续使用查询参数表达 `sessionId`。
- 页面切换不得丢失 Workspace 上下文。
- 进入 Chat 前必须明确 Workspace 和 Agent。

### Workspace 页面

| 用户动作 | 目标 |
| --- | --- |
| 点击 Workspace 卡片主体 | `/pudding/workspaces/{workspaceId}` |
| 点击“进入工作室” | `/pudding/workspaces/{workspaceId}` |
| 点击“进入对话” | `/chat?workspaceId={workspaceId}&agentId={defaultAgentId}` |
| 新建 Workspace 成功 | `/pudding/workspaces/{newWorkspaceId}` |
| 删除当前最近 Workspace | 清理最近访问记录，留在 `/pudding/workspaces` |

### Workspace Studio 页面

| 用户动作 | 目标 |
| --- | --- |
| 点击 Agent 精灵 | 选中 Agent，更新 URL 为 `/pudding/workspaces/{workspaceId}/{agentId}` |
| 双击 Agent 或点击“进入对话” | `/chat?workspaceId={workspaceId}&agentId={agentId}` |
| 切换 Workspace | `/pudding/workspaces/{nextWorkspaceId}`，清空旧 `agentId` |
| 点击面包屑 Pudding | `/pudding/workspaces` |
| 点击面包屑 Workspace | 保持当前 `/pudding/workspaces/{workspaceId}` |
| 点击 Agent 设置 | 打开工作室内抽屉，或进入后续 `/pudding/workspaces/{workspaceId}/agents?agentId={agentId}` |

### Chat 页面

| 用户动作 | 目标 |
| --- | --- |
| 点击面包屑 Pudding | `/pudding/workspaces` |
| 点击面包屑 Workspace | `/pudding/workspaces/{workspaceId}/{agentId}` |
| 切换 Agent | `/chat?workspaceId={workspaceId}&agentId={nextAgentId}`，并重置当前会话 |
| 点击“工作室”按钮 | `/pudding/workspaces/{workspaceId}/{agentId}` |
| 打开历史会话 | `/chat?workspaceId={workspaceId}&agentId={agentId}&sessionId={sessionId}` |

## 体验设计

### Workspace 入口页

Workspace 入口页不是管理列表，而是“我有哪些工作场所”。默认视图应优先卡片化展示每个 Workspace 的当前状态，但桌面端可以保留表格作为密集模式。每个 Workspace 至少展示名称、描述、Agent 数量、最近活跃、运行状态和主按钮“进入工作室”。

如果用户只有一个 Workspace，可以不经过入口页。入口页仍作为全局切换和创建空间的地方存在。

### Workspace Studio 工作室

工作室是产品的主体验。像素风格房间负责表达“多个 Agent 同处一个工作空间”，但画面不是装饰，应承担状态表达：

- `working`：有活跃会话、当前 Chat 正在处理、子 Agent 运行中。
- `resting`：可用但暂无任务。
- `sleeping`：禁用、冻结或不可用。
- `playing`：预留给空闲娱乐、非任务活动或用户后续定义的轻状态。
- `error`：后续扩展，用于运行失败、资源不可用、配置异常。

选中 Agent 后，右侧或底部焦点面板展示 Agent 名称、状态、当前活动、最近会话、进入对话和设置入口。Agent 精灵只负责第一层识别，具体任务信息放在焦点面板，避免画面拥挤。

### Chat 页面

Chat 页面继续承担深度交互：消息流、过程预览、上下文、停止、导出、历史会话。它不再是产品首页，因此顶部需要更明确的上下文：

`Pudding / {workspaceName} / {agentName}`

Chat 中的 Workspace 和 Agent 切换仍可保留，但切换行为必须更新 URL，并且和工作室页面选择保持一致。

### 空状态和异常

- 没有 Workspace：展示创建 Workspace 的明确入口。
- Workspace 没有 Agent：展示创建或添加 Agent 的入口，并解释当前工作室为空。
- Agent 不可用：保留在工作室内展示为 sleeping，不默认隐藏。
- 精灵图加载失败：使用默认 Pudding 精灵图。
- 后端接口失败：页面保留结构，局部显示错误，不整页崩溃。

## 视觉和交互原则

- 保持 Quiet Local Intelligence：温暖、中性、克制、稳定。
- 工作台是工具，不做营销页，不放大段说明文案。
- 工作室像素画面应有明确边界和稳定尺寸，避免加载后跳动。
- 所有图标按钮需要有 tooltip 或 aria-label。
- 动画只表达状态，不干扰阅读；尊重 `prefers-reduced-motion`。
- 移动端不强行缩小完整办公室：可以改为横向滚动画面和下方 Agent 列表。
- 不把卡片套卡片；信息分区用全宽 band 或简单面板。

## 数据和状态模型

前端需要一个统一的 Workspace 上下文模型：

```ts
interface WorkspaceRouteContext {
  workspaceId?: string;
  agentId?: string;
  sessionId?: string;
}
```

建议新增或整理以下工具函数：

```ts
buildWorkspacePath(): string
buildWorkspaceStudioPath(context: Pick<WorkspaceRouteContext, 'workspaceId' | 'agentId'>): string
buildChatPath(context: WorkspaceRouteContext): string
parseWorkspaceRouteContext(search: string): WorkspaceRouteContext
resolveDefaultWorkspace(workspaces, recentWorkspaceId): string | undefined
resolveDefaultAgent(agents, requestedAgentId): string | undefined
```

最近访问记录使用 localStorage 保存，只保存 ID，不保存服务端对象：

```ts
interface RecentWorkspaceVisit {
  workspaceId: string;
  agentId?: string;
  visitedAt: number;
}
```

## 分阶段实施计划

### 阶段 1：路由和入口收敛

- 把 `/` 和 `/welcome` 从固定跳 `/chat` 改为 Workspace First 决策。
- 建立统一 path builder 和 route context parser。
- Chat、Workspace、Studio 共用同一套路由生成规则。
- 为入口决策、path builder、query parser 写单元测试。

验收标准：刷新、复制链接、直接打开 URL 都能恢复到正确 Workspace 和 Agent。

### 阶段 2：Workspace 页面用户化

- 将 `/pudding/workspaces` 的主文案从管理语义改为工作空间入口。
- 默认主按钮改为“进入工作室”。
- 卡片上展示 Agent 数量和最近活跃。
- 新建 Workspace 成功后直接进入工作室。

验收标准：普通用户不会感觉自己进入后台列表，而是在选择工作场所。

### 阶段 3：Workspace Studio 强化

- 增强 Agent 焦点面板。
- 增加状态汇总：工作中、休息、睡觉、异常数量。
- 完善多 Agent 布局，超过 8 个 Agent 时稳定排列。
- 为移动端提供横向滚动和列表 fallback。

验收标准：用户不进入 Chat 也能判断当前 Workspace 里谁在工作、谁可用、下一步能做什么。

### 阶段 4：Chat 和 Studio 闭环

- Chat 顶部改为完整面包屑。
- 从 Chat 返回 Studio 时保留选中 Agent。
- Chat 切换 Agent 后同步 URL。
- 打开历史会话时 URL 带 `sessionId`。

验收标准：Studio 和 Chat 之间来回切换不会丢失上下文。

### 阶段 5：用户区和管理区分离

- 用户主路径隐藏后台管理入口。
- 管理类页面逐步迁移或归类到 `/admin`。
- 保留必要的设置入口，但不把资源池、模板、权限暴露为默认主导航。

验收标准：普通用户路径聚焦 Workspace、Agent、Chat；管理员能力仍可访问但不干扰日常工作流。

### 阶段 6：体验 QA

- 检查 375、768、1024、1440 宽度。
- 检查键盘焦点、图标按钮 aria-label、tooltip。
- 检查精灵图加载失败兜底。
- 检查低动效偏好。
- 检查登录、刷新、无权限、空数据、接口失败。

验收标准：核心页面在桌面和移动端都稳定，不出现遮挡、跳动、空白首屏或死路跳转。

## 测试策略

- 单元测试：
  - route context parser。
  - path builder。
  - 默认 Workspace/Agent 决策。
  - Workspace Studio Agent 状态映射。
- 组件测试：
  - Workspace 页面点击进入工作室。
  - Studio 点击 Agent 更新 URL。
  - Chat 面包屑返回 Studio。
- 浏览器验证：
  - `/`
  - `/pudding/workspaces`
  - `/pudding/workspaces/default`
  - `/pudding/workspaces/default/{agentId}`
  - `/workspace-studio?workspaceId=default`（旧链接兼容）
  - `/chat?workspaceId=default&agentId={agentId}`
  - 缺失或错误 query 参数的恢复路径。

## 开放问题

- `playing` 状态是否由真实业务事件驱动，还是仅作为空闲动画状态。
- Agent 设置是先做工作室抽屉，还是独立 `/pudding/workspaces/{id}/agents` 页面。
- 普通用户和管理员的导航菜单是否需要完全分离成两套 layout。
- 最近访问记录是否需要服务端同步，还是先使用本地 localStorage。

## 推荐下一步

先实施阶段 1 和阶段 2。它们会建立正确的用户进入路径和跳转规则，同时不会大规模改动 Chat 流式消息逻辑。完成后再增强 Studio 的实时状态和多 Agent 体验。
