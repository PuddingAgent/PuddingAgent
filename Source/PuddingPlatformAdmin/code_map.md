# PuddingPlatformAdmin CodeMAP

> React 管理前端 | Ant Design Pro v6 · React 19 · UmiJS

## 技术栈

| 项 | 值 |
|------|------|
| 框架 | Ant Design Pro v6 / React 19 / UmiJS |
| 包管理 | pnpm |
| 测试 | Jest |

## 目录

| 目录 | 用途 |
|------|------|
| `src/` | 前端源码 |
| `config/` | Umi 配置 |
| `public/` | 静态资源 |
| `mock/` | 模拟数据 |
| `tests/` | 测试 |
| `e2e/` | 端到端测试 |
| `scripts/` | 构建脚本 |
| `docker/` | Docker 配置 |
| `dist/` | 构建产物 |

## 关键文件

| 文件 | 用途 |
|------|------|
| `package.json` | 依赖（3KB） |
| `tsconfig.json` | TypeScript 配置 |
| `biome.json` | 代码格式化 |
| `jest.config.ts` | 测试配置 |
| `Dockerfile` | Docker 构建 |

## Token 统计

| 文件 | 用途 |
|------|------|
| `src/pages/stats/tokens/index.tsx` | 月度 Provider/Model Token 页面；显示本地网关或旧投影数据源；趋势图使用整列命中区和受控 HTML tooltip，稳定显示分项 Token、缓存命中率与请求数 |
| `src/services/platform/api.ts` | `MonthlyTokenStatsResponse.dataSource` 区分 `local_gateway` 与 `legacy_projection` |

## LLM 资源池

| 文件 | 用途 |
|------|------|
| `src/pages/llm-resource-pool/index.tsx` | Provider 与模型管理；协议只在模型新增/编辑表单选择，支持 Chat Completions、Responses、Anthropic Messages |
| `src/pages/llm-resource-pool/providerTemplates.ts` | Provider 模板及模型级协议默认值；OpenCode Go 模板含同一 Provider 下三种协议的五个模型 |
| `src/services/platform/api.ts` | Provider DTO 不含协议；Model DTO/Upsert 必须携带协议 |

## 路由与壳层加载边界

| 文件 | 职责与加载边界 |
|------|----------------|
| `config/config.ts` | Umi 基础配置；不得重新启用全局 `layout` 插件，否则 Chat 会重新承担管理壳运行时 |
| `config/routes.ts` | 将 `/chat`、登录、Bootstrap 和工作空间列表等独立体验与 `adminRoutes` 分组；管理路由统一挂到异步 `AdminLayout` 父路由；已移除 Workspace Studio 深链与旧入口 |
| `src/app.tsx` | 全应用认证、初始状态、主题和 request；不得静态导入管理壳的 ProLayout、头像区或 SettingDrawer |
| `src/layouts/AdminLayout/index.tsx` | 仅管理路由加载的过渡壳；当前保留 ProLayout 菜单能力，后续可在不影响 Chat 首载的前提下替换为 PuddingAdminShell |
| `src/layouts/AdminLayout/menuIcons.ts` | 将 `routes.ts` 的语义图标名转换为真实 Ant Design React 图标；只在异步管理壳执行，避免菜单显示 `home/appstore` 文字或把图标依赖带回 Chat 主包 |

## Agent 编排布局编辑器

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/orchestration/index.tsx` | `/orchestration` 入口；发现、新建和受约束删除 Graph，预览 Revision，提供 DAG、节点检查器、事件时间线及节点拖拽/viewport 保存；S1 增加节点 CRUD、保存 Revision（validate + PUT CAS）、409 冲突保留草稿与显式 reload/diff；不提交运行命令 |
| `src/pages/orchestration/api.ts` | 登录态 Graph create/delete、Graph/Run/catalog/revision/layout GET/PUT/events 客户端；S1 增加 validateOrchestrationDraft 与 putOrchestrationRevision；按路径段转义 revisionId，并以 `afterSequence` + `Last-Event-ID` 消费可恢复 SSE |
| `src/pages/orchestration/graphViewModel.ts` | 无副作用的 DAG 分层布局；优先使用已保存节点坐标，对缺失节点自动布局，并区分 control/data edge |
| `src/pages/orchestration/graphManagement.ts` | 新建表单默认值/请求规范化与删除门禁；任何存在 durable Run 的 Graph 在前端即禁止删除，后端仍重复校验 |
| `src/pages/orchestration/layoutEditor.ts` | 把受控画布节点与 viewport 编译为布局 CAS 请求；新建从 L1 开始，更新严格递增，并保留本切片不编辑的尺寸/父组/折叠元数据；识别 409 冲突 |
| `src/pages/orchestration/revisionEditor.ts` | S1 Revision 草稿/构建/删除纯函数：catalog→节点（kind/executor/hash 冻结）、本地节点校验（HumanInput 可无 executor；SubAgent 必须 role/template/route）、删节点同步删边与最后节点门禁、下一 Revision 预览（revision/parent/id）、409 冲突提取与草稿保留、保存成功后切换到服务端 Revision、草稿存在时阻止布局写入旧 base Revision、只读冲突 diff 摘要 |
| `src/pages/orchestration/types.ts` | 与 `pudding.agent-orchestration/v2` Web JSON 对齐的前端契约；S1 增加 validation issue/result、validate 与 revision write DTO、revision conflict 事实 |
| `src/pages/orchestration/*.test.ts` | SSE 分块/坏帧隔离、Revision 路径、DAG 布局/边语义、布局 CAS、Graph 新建请求和删除门禁，以及 §5.2 十项 Revision Editor 行为测试 |
| `config/routes.ts` | 注册管理菜单 `/orchestration`；继续保持 system config 为最后一个可见顶级菜单 |

画布采用 `@xyflow/react`，固定高度容器内开放平移、缩放、节点选择和节点拖拽；保持 `nodesConnectable=false`，并以 `deleteKeyCode={null}` 关闭画布删除键（节点删除必须走检查器，保证与定义/边同步）。运行状态刷新只更新节点外观，不覆盖未保存坐标。保存提交全部节点坐标与当前 viewport，并以 `expectedCurrentLayoutRevision` 做 CAS；409 时保留本地状态，只有用户明确确认才重新加载。Executable definition、GraphLayout 与 Run 投影保持三层独立。

## Chat 性能热路径

| 文件 | 职责与性能边界 |
|------|----------------|
| `src/pages/chat/client/chatClientStore.ts` | 会话/状态缓存；相同状态轮询必须短路，不重复写缓存或通知订阅者 |
| `src/pages/chat/components/MessageList.tsx` | 将历史消息与 active run 快照投影为稳定消息行；直接渲染 `MessageRow`，不再为每行重建单元素 `ChatTurn[]` |
| `src/pages/chat/styles/messageStyleContext.tsx` | 消息树样式边界；`MessageList` 注册一次聚合 Chat 样式并通过 Context 共享，消息叶子不得重复调用 `useChatStyles` |
| `src/pages/chat/components/MessageRow.tsx` | 单消息渲染与语义 memo 边界；投影重建等价对象时保持历史行不提交，正文或过程事件变化仍立即更新 |
| `src/pages/chat/components/MessageProcessSummary.tsx` | 思考/工具过程摘要；折叠时不得构建完整 rounds、trace chips 和展示项 |
| `src/pages/chat/components/MessageItem.tsx` | 消息文本轻量壳；立即显示纯文本 fallback，并异步加载 Markdown 增强器 |
| `src/pages/chat/components/MarkdownBlock.tsx` | ReactMarkdown、KaTeX、HTML parser 和 Prism 的独立按需 chunk |
| `src/pages/chat/components/ChatMain.tsx` | Chat 主壳；子代理运行检查器仅在存在运行卡片或显式打开时加载 |
| `src/pages/chat/components/HistorySearchModal.tsx` | 历史搜索弹窗；只有 `historyModalOpen` 时才挂载并触发异步 chunk |
| `src/pages/chat/components/AgentMessageBubble.tsx` | Agent 消息气泡；每条消息不得预挂载关闭状态的会话诊断 Drawer 或操作栏，操作栏在首次 hover 后才实例化 |
| `src/pages/chat/components/IntentConsole.tsx` | Composer；摄像头弹窗只在用户打开视觉输入时加载和挂载 |
| `src/pages/chat/viewport/useMessageViewportRuntime.ts` | 虚拟列表、锚点与贴底；scroll 状态未变化时不得触发 React commit，首屏稳定轮询应尽快结束 |
| `src/utils/perfEventRuntime.ts` | 首屏常驻的轻量性能事件缓冲；不得反向静态导入完整诊断模块 |
| `src/utils/debug.ts` | 完整性能快照、观察器和诊断建议；仅由 perf/debug 模式或开发面板异步加载 |

## 工作空间与静态资源边界

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/workspace/index.tsx` | 工作空间列表；进入操作直接打开带 `workspaceId` 的 Chat，新建后进入管理设置，不再加载 2D Studio |
| `src/utils/workspaceNavigation.ts` | 工作空间入口在最近访问或唯一工作空间时解析到 Chat；不再生成 Studio 深链 |
| `public/assets/` | 只保留 Web 运行时实际需要的静态资源；`images/me.png` 是当前用户的 Pudding 默认头像，Agent 精灵素材已迁出到 `../PuddingDesktop/Assets/AgentSprites/` |

后端对应入口：`PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs` 首屏只返回最近 20 条消息；active run 返回最近 64 条可见过程明细，同时用 `processSummary` 保留全量计数。`PuddingPlatform/Controllers/Api/SessionEventsController.cs` 的 bootstrap 子代理事件快照上限为 500 条。

全局壳：`src/app.tsx` 只保留所有路由真正共享的认证、主题和 request；管理端 ProLayout、全局操作和开发态 `SettingDrawer` 必须留在异步 `AdminLayout`。不要仅为减小 `umi.js` 启用 `granularChunks`；必须合计 HTML 同步引用的 framework chunk 与 Chat 路由首始 chunk，确认真实首载字节和请求顺序确有改善。

## 测试

编排 Layout Editor 与 S1 Revision Editor 定向 Jest：`src/pages/orchestration` 5 suites / 24 tests（基线 12 + §5.2 十项 S1 行为 12）；生产构建必须生成 `/orchestration/index.html`。
