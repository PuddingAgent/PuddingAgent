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
| `src/pages/orchestration/index.tsx` | `/orchestration` 入口；ComfyUI 式画布优先布局：Graph/Run 紧凑控制条、顶部“运行”与悬浮工作台；发现/模板新建/受约束删除 Graph、节点/边/Input/Hook authoring，并把组件设置/输出委托给组件 UI 注册表；运行始终固定已保存 Revision |
| `src/pages/orchestration/ManualRunModal.tsx` | 按当前 Revision Graph Inputs 生成类型化运行表单；展示固定 revisionId，提交后切入 Run 投影，内容草稿未保存时禁止运行 |
| `src/pages/orchestration/manualRun.ts` | 手动运行 requestId 与 content/json/number/boolean `OrchestrationValueEnvelope` 构造纯函数 |
| `src/pages/orchestration/ImageGenerateNodeSettings.tsx` | 图片生成节点的 mode/size/provider/model/watermark 配置检查器；不持有 provider secret |
| `src/pages/orchestration/SubAgentNodeSettings.tsx` | SubAgent 节点的角色、Agent 模板和精确 provider/model 路由设置；目录加载失败时仍允许编辑现有值，不持有 secret |
| `src/pages/orchestration/componentUiRegistry.tsx` | 组件自有输入/输出 UI 注册表；SubAgent 负责 `result` 文本，图片生成/展示组件负责各自 `images` Artifact 渲染，未知组件保持通用摘要/引用降级 |
| `src/pages/orchestration/HttpHookPanel.tsx` | HTTP Hook 编辑与调试说明；冻结 catalog descriptor，配置 payload path → Graph Input，并为已保存 Revision 生成 Admin Bearer endpoint/body/cURL，不回显 token |
| `src/pages/orchestration/httpHookTriggers.ts` | Webhook trigger 草稿增删/启停、descriptor version/hash 冻结及显式 Revision endpoint 编译纯函数 |
| `src/pages/orchestration/api.ts` | 登录态 Graph create/delete、显式 Revision manual Run、Graph/Run/catalog/revision/layout GET/PUT/events 与 image Artifact 客户端；S1 增加 validateOrchestrationDraft 与 putOrchestrationRevision；按路径段转义 revisionId，并以 `afterSequence` + `Last-Event-ID` 消费可恢复 SSE |
| `src/pages/orchestration/graphViewModel.ts` | 无副作用的 DAG 分层布局；投影 catalog 强类型端口、组件类型/workspace 与完整节点 outputs，把 control/data edge 映射到对应 React Flow handle |
| `src/pages/orchestration/OrchestrationComponentNode.tsx` | React Flow 自定义节点；显示运行状态、control/data 端口，并调用组件 UI 注册表渲染该组件自己的运行输出，不拥有 executable schema |
| `src/pages/orchestration/edgeEditor.ts` | S2 纯边编辑层；解析 handle，镜像后端 dataType/MIME/cardinality/delivery 兼容性，拒绝自环/环/重复/单值端口多来源，并构造/修改/删除 control/data edge |
| `src/pages/orchestration/EdgeInspector.tsx` | 选中边的只读端点/映射、受限 condition 修改和删除；注册表谓词只读，不接受任意脚本或字符串表达式 |
| `src/pages/orchestration/graphInputs.ts` | Graph Input 增删改、引用清单、删除时清理节点引用与节点端口 binding 的不可变纯函数 |
| `src/pages/orchestration/GraphInputsPanel.tsx` | Graph Input 契约面板；编辑 dataType/MIME/cardinality/delivery/激活必填，并在删除前显示受影响节点引用 |
| `src/pages/orchestration/NodeGraphInputBindings.tsx` | 按 catalog 输入端口过滤兼容 Graph Input，写入 `graphInputBindings`；单值端口已有 data edge 时阻止第二来源 |
| `src/pages/orchestration/graphManagement.ts` | 新建表单默认值/模板请求规范化与删除门禁；默认选择最小 image-generation 模板，任何存在 durable Run 的 Graph 在前端即禁止删除，后端仍重复校验 |
| `src/pages/orchestration/layoutEditor.ts` | 把受控画布节点与 viewport 编译为布局 CAS 请求；新建从 L1 开始，更新严格递增，并保留本切片不编辑的尺寸/父组/折叠元数据；识别 409 冲突 |
| `src/pages/orchestration/revisionEditor.ts` | S1 Revision 草稿/构建/删除纯函数：catalog→节点（含 Gate evaluator 形状）、节点校验、删节点同步删边、下一 Revision 预览、409 草稿保留/服务端切换、Layout 防误写；inputs/triggers 参与 dirty 与冲突 diff |
| `src/pages/orchestration/types.ts` | 与 `pudding.agent-orchestration/v2` Web JSON 对齐的前端契约；包含 validation/revision CAS、Graph Input/Trigger、data binding 与受治理 edge predicate |
| `src/pages/orchestration/*.test.ts` | SSE、API 路径、DAG/强类型 handle、布局/Revision CAS、Graph 生命周期、edge 构造/兼容/环拒绝、Graph Input 引用清理与 dirty/diff 行为测试 |
| `config/routes.ts` | 注册管理菜单 `/orchestration`；继续保持 system config 为最后一个可见顶级菜单 |

画布采用 `@xyflow/react`，固定高度容器内开放平移、缩放、选择、节点拖拽和 graph 模式的 catalog 端口连线；页面默认把完整宽高交给画布，Inspector、Graph Inputs、HTTP Hook、Events 按需以悬浮层覆盖，打开/收起都不重建画布或丢失 viewport。`deleteKeyCode={null}` 关闭画布裸删除键，节点/边删除必须走检查器以同步定义与引用。运行状态刷新只更新节点外观，不覆盖未保存坐标。保存提交全部节点坐标与当前 viewport，并以 `expectedCurrentLayoutRevision` 做 CAS；409 时保留本地状态，只有用户明确确认才重新加载。Executable definition、GraphLayout 与 Run 投影保持三层独立。

## Chat 性能热路径

| 文件 | 职责与性能边界 |
|------|----------------|
| `src/pages/chat/client/chatClientStore.ts` | 会话/状态缓存；相同状态轮询必须短路，不重复写缓存或通知订阅者 |
| `src/pages/chat/components/MessageList.tsx` | 将历史消息与 active run 快照投影为稳定消息行；active run 无法匹配现有 Turn 时追加到当前消息流末端；直接渲染 `MessageRow`，并只给当前主代理行附加有界委派摘要 |
| `src/pages/chat/styles/messageStyleContext.tsx` | 消息树样式边界；`MessageList` 注册一次聚合 Chat 样式并通过 Context 共享，消息叶子不得重复调用 `useChatStyles` |
| `src/pages/chat/components/MessageRow.tsx` | 单消息渲染与语义 memo 边界；投影重建等价对象时保持历史行不提交，正文或过程事件变化仍立即更新 |
| `src/pages/chat/components/MessageProcessSummary.tsx` | 思考/工具过程摘要；折叠时不得构建完整 rounds、trace chips 和展示项 |
| `src/pages/chat/components/MessageItem.tsx` | 消息文本轻量壳；立即显示纯文本 fallback，并异步加载 Markdown 增强器 |
| `src/pages/chat/components/MarkdownBlock.tsx` | ReactMarkdown、KaTeX、HTML parser 和 Prism 的独立按需 chunk |
| `src/pages/chat/reducer/subAgentReducer.ts` | 子代理事件与状态快照的统一投影；`budget_exhausted` 是可恢复终态，任何终态进入后不得被迟到事件降级；`subagent.llm.completed.reasoning_preview` 作为实际“模型推理”展示，旧字符数占位不再生成 |
| `src/pages/chat/components/ChatMain.tsx` | Chat 主壳；完整子代理卡片只进入托盘坞，主消息仅接收 active count/时间锚点等父级委派摘要；运行检查器仅在存在卡片或显式打开时加载 |
| `src/pages/chat/components/HistorySearchModal.tsx` | 历史搜索弹窗；只有 `historyModalOpen` 时才挂载并触发异步 chunk |
| `src/pages/chat/components/AgentMessageBubble.tsx` | Agent 消息气泡；首 Token 前并列展示主代理当前活动与最近推理摘要，无事件时展示真实等待阶段；子代理内部过程不得进入主消息；操作栏在首次 hover 后才实例化 |
| `src/pages/chat/components/SubAgentActivityDock.tsx` | 子代理任务、工具、轮次和输出详情的唯一运行时入口；Agent-first 路由由 `useChatState` 回退到已解析 `mainSessionId` 绑定卡片；预算耗尽以“运行结束/预算已用尽”异常终态展示 |
| `src/pages/chat/components/IntentConsole.tsx` | Composer；摄像头弹窗只在用户打开视觉输入时加载和挂载 |
| `src/pages/chat/viewport/messageProjection.ts` | 将 MessageList 已组装的权威消息顺序转换为虚拟行；不得按 active run 的原始启动时间二次排序，避免长任务状态回跳到历史顶部 |
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

编排 Layout/Revision/Typed Edge/Graph Input/HTTP Hook/Manual Run/组件 UI 定向 Jest：`src/pages/orchestration` 10 suites / 57 tests；生产构建必须生成 `/orchestration/index.html`。仓库全量 `tsc --noEmit` 有既有非编排基线错误时，必须另行确认输出中 `src/pages/orchestration` 命中为 0，不能把全量失败描述为通过。
