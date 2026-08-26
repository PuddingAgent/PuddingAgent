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

## 主代理服务商余额徽标（多服务商计费展示适配器，2026-08-24）

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/chat/utils/providerBilling.ts` | 展示适配器注册表 `{id,match,displayName,fallbackCurrencySymbol}` + `resolveBillingAdapter`/`currencySymbolFor`（CNY→¥/USD→$）；providerId 未命中不渲染徽标；新服务商在此加一项即可 |
| `src/pages/chat/hooks/useProviderBalance.ts` | 余额拉取：providerId 变化即取 + 5min 低频轮询（`usePollingLoader` 页面隐藏自动暂停）+ 手动 `refresh`；任何失败静默降级为 `balance=undefined` + `errorText`，不抛错 |
| `src/pages/chat/components/ProviderBalanceIndicator.tsx` | 品牌图标（DeepSeek/Mimo 内联 SVG）+ `¥xx.xx` 徽标；`detail` prop 进 Tooltip 第二行（错误原因/刷新提示） |
| `src/pages/chat/components/GoalBanner.tsx` + `hooks/useGoal.ts` | ADR-074 G1 Goal 状态条：服务端投影（GET /api/v1/conversations/{id}/goal）驱动 objective/phase/iteration 与 pause/resume/cancel；终态隐藏控件；`api.ts` 含 `getConversationGoal`/`executeGoalCommand`；CommandPalette 有 /goal 提示；5 项 jest 通过 |
| `src/pages/chat/components/ChatMain.tsx` | 挂载点：头部 `extraActions` 首位，`selectedAgent.preferredProviderId` 命中适配器才渲染；点击徽标手动刷新 |
| `src/services/platform/api.ts` | `getLlmProviderBalance` → `GET /api/llm/providers/{id}/balance`；`LlmProviderBalanceDto`/`LlmBalanceInfoDto` 类型 |

后端查询适配器注册表（`ILlmBalanceProvider`/`DeepSeekLlmBalanceProvider`）见 `PuddingPlatform/code_map.md` 提供商配置节；完整设计与扩展步骤见 `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md`。

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
| `src/pages/orchestration/EdgeInspector.tsx` | 选中边的端点/映射展示、受限 condition 修改与删除、data binding 可编辑且稳定 round-trip；谓词为字段级可编辑（PredicatePicker，B5-3a），仍不接受任意脚本或字符串表达式 |
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

## Chat 流式 UI（deepseek-harness 对齐，2026-08-23）

参考 `E:\github\deepseek\deepseek-harness`（dsh-0.1.1-rc.2）流式 UI 设计 + `Docs/deepseek-harness-message-card-alignment-2026-08-14.md`；保留 Pudding 色板/头像身份，采用 harness 的信息架构与流式渲染模式。行为链质感演进记录见 `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md`；2026-08-25 后续施工以 `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` + ADR-079 为权威合同。

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/chat/components/MarkdownBlock.tsx` | `preprocessMarkdown` 只做逐行 `` `` ``→``` 归一；历史「管道行收集 + 标题拆 \|」hack 会破坏 GFM 表格（分隔行与正文合并/空行被吞→整表降级为 `<p>` 原文），已删除 |
| `src/pages/chat/hooks/useTypewriterStreaming.ts` | `findStableMarkdownBoundary`（已导出）表格感知：前导 `\|` 识别（不要求尾管道）、分隔行到达才允许整表提交、半截表头滞留 live 段、表格 run 中间不提交 |
| `src/pages/chat/components/MessageItem.tsx` | 流式尾段含块级/强调语法（`\|`、反引号、`#`、列表、`>*_~`、链接）时走 ReactMarkdown 渲染（不再以纯文本 span 显示原始管道/星号）；纯文本尾段保留打字机 span + 墨迹光标（零解析路径） |
| `src/pages/chat/components/AgentMessageBubble.tsx` | 行序 TurnStatus → TurnContentStream（正文 ⇄ 行为组单一内容流）→ ModelRetryRow → 错误行 → actions/StatsLine；投影一旦含 TextBlock 就不再渲染第二个 answer bubble，`answerMarkdown` 仅作复制/TTS/无 canonical 正文节点时的旧记录兜底；TurnStatus 有 canonical 投影时直接消费 `deriveTurnStatusFromProjection`，turn 终态渲染 TurnStatsLine |
| `src/pages/chat/hooks/useSessionEventProjection.ts` | `message.content.appended` 到达即翻转 status=streaming（首个回答增量 = 已知事实「正在生成回答」，状态条与正文不再各说各话）；`enqueueDelta` 携带入队基准长度 |
| `src/pages/chat/hooks/useSessionEventBuffers.ts` | 回答增量缓冲 `{delta, baseLength}`；flush 经 `applyBufferedDeltaToTurn` 按基准位置幂等应用——基准漂移（activeRun 快照竞态）丢弃缓冲，杜绝直播期间正文整段重复 |
| `src/pages/chat/components/IntentConsole.tsx` | Composer §6.2：Sandbox/Auto-review 收敛进设置 Popover（活动态角标浮出）；执行偏好/权限/语音/发送保持直达 |
| `src/pages/chat/styles/agent.styles.ts` | `agentBubbleNew` 平铺化：透明背景/无边框阴影/15px 1.75（对齐 harness 助手无气泡全宽正文）；`agentActiveOutputSurface` 退化为兼容类名 |
| `src/pages/chat/styles/user.styles.ts` | `userBubbleNew` 右侧 22px 圆角胶囊（harness 数值）：accent 淡染、无边框无阴影 |
| `src/pages/chat/styles/execution-flow.styles.ts` | 行式 chrome：28px 行高、16px leading、14/22 标题（secondary 档）、2×2 分隔点（caption 档）、22px 展开缩进、TurnStatus 文字 shimmer；行为链升级新增：`rowSweep` 运行行扫光（reduced-motion 降级）、`duration` 折叠行尾部计量槽、`reasoningChip` 段时长 chip、`statsLine/statsDot` 终态计量行、`timelineList` 交错时间线容器 |
| `src/pages/chat/styles/markdown.styles.ts` | 表格仅横向分隔线（th 加粗下边线/td 弱底线/无竖线网格，对齐 harness MarkdownText）；代码块圆角 12px、行内 code 圆角 6px |
| `src/pages/chat/components/messageTurnMerge.test.ts` | BUG2 守卫单测：终态不回退、同 messageId 不追加第二卡 |
| `src/pages/chat/components/MessageItem.streaming.test.tsx` | BUG1 单测：完整表格渲染 `<table>`、流式尾段表格走 markdown、纯文本尾段保留打字机 |
| `src/pages/chat/projections/turnContentBlocks.ts` | 把 canonical nodes 解释为 TextBlock ⇄ ActivityGroupBlock；每个最大连续非正文区间一组，不重排；组 key 锚定首节点，摘要/运行态/Stats 纯派生 |
| `src/pages/chat/components/execution-flow/TurnContentStream.tsx` | AgentTurnCard 唯一内容渲染器；按 ADR-079 逆向寻找最后 ActivityGroup，不能把“整个块流最后一块”当最新行为组；只有尾部开放正文段走打字机 |
| `src/pages/chat/components/execution-flow/ActivityGroup.tsx` / `TextSegmentView.tsx` / `useDisclosureRegistry.ts` | ADR-079 目标：默认 owner 在会话内唯一（最新 Agent 回合最后行为组），最终正文不关闭；owner 转移柔和收起并在动画后卸载成员 DOM；用户 tri-state override 粘性；封闭正文段/历史组按可见语义 memo |
| `src/pages/chat/components/execution-flow/ReasoningDisclosureRow.tsx` | 多段推理行；ADR-079 要求 ActivityGroup 展开态使用 `inline-full` 完整 canonical reasoning 换行且无二级 disclosure，standalone 折叠摘要模式仍可保留；hooks 必须先于空 payload 早退 |
| `src/pages/chat/components/execution-flow/ToolCallRow.tsx` | 完成态耗时/非零 exit code 上折叠行尾部（caption 灰 tabular-nums，error 染红）；presentation 卡经 `resolveRenderer` 分派（P3 五类已注册） |
| `src/pages/chat/components/execution-flow/TurnStatsLine.tsx` | turn 终态计量行：`N 段思考 · M 工具 · 3m01s · 4.2k tokens`；缺失项省略不伪造，全缺失不渲染 |
| `src/pages/chat/presentation/PresentationRegistry.ts` | kind→renderer 分派表：generic/terminal/diff/read/search/web 已注册，delegation/job 回落 Generic；禁止按 toolName 分支 |
| `src/pages/chat/presentation/renderers/*.tsx` | 五类专用 renderer + `rendererKit`（卡片家族：banner + 224px 内容窗口、圆角 radius-md、meta 契约字段优先、payload 回退解析、payload 即调用参数 JSON 时不重复展示正文）；diff 解析 +/−/@@ 着色与增删计数，search 命中数组有界 20 条 |
| `src/pages/chat/utils/formatDuration.ts` | `formatDurationMs`（123ms/1.2s/1m03s，缺失 null 不伪造）+ `formatTokenCount`（4.2k tokens）；计量 chip/工具行耗时/StatsLine 共用 |
| `src/pages/chat/client/featureFlag.ts` | `isExecutionFlowProjectionEnabled` 默认开启（P2 转正：live turn 消费 canonical 投影交错）；localStorage `pudding-exec-flow-proj==='0'` 为逃生门；历史/无投影 turn 走路径 A adapter |
| `src/pages/chat/projections/executionFlowProjector.ts` | `MessageNode` = 一个连续正文段，任何非 content 节点事件切段；`message.completed.reply` 只在整 turn 无 content delta 时创建兜底正文，绝不覆盖已有段文本；空段过滤、reasoning/tool/delegation 仍按 canonical sequence 投影 |
| `src/pages/chat/projections/turnSurfaceStore.ts` / `client/types.ts` | bootstrap/activeRun/live 三源按 eventId 幂等合流；`ProcessSummaryItem.sequence` 必填且运行时校验，缺失 fail closed，禁止数组下标伪造跨源顺序 |
| `src/pages/chat/hooks/useTurnSurfaceStore.ts` | 可见 turn 懒水合调度器：最多 2 个并发槽，任一完成继续排空队列；可见性由 MessageRow 的滚动容器 IntersectionObserver（600px 预取）注册，组件挂载不再等同可见；会话切换丢弃迟到响应，同轮失败项跳过并在下一服务端投影重试 |
| `src/pages/chat/utils/chatStateUtils.ts` | `resolveTerminalAssistantMarkdown` 分叉兜底：current 与终态 reply 完全分叉且无后缀衔接时返回 reply（服务端 canonical），不再整段拼接（旧实现任何一次流内偏差都会让正文显示两遍） |
| `src/pages/chat/hooks/useTypewriterStreaming.ts` | stale-stable 守卫：`stableTextRef` 镜像已提交前缀，`text` 不再以其开头（快照/终态改写）时整体重置 stable/live 游标，杜绝 stale stable + 新 live 同段双渲染 |
| `src/pages/chat/components/execution-flow/TurnStatus.tsx` | 单行运行态（唯一 aria-live）；leading 槽渲染阶段墨球 TurnStatusOrb（pending/五阶段 → breathing/connecting/working/solving/weaving/composing） |
| `src/pages/chat/components/execution-flow/TurnStatusOrb.tsx` | thinking-orbs 20px 单色墨球包装：阶段映射 + `data-pudding-theme` 显式主题绑定（MutationObserver 跟随）；全局仅 TurnStatus 一颗动画，其余行保持静态 StateDot + 扫光（不喧宾夺主） |
| `src/pages/chat/components/ComposerTextInput.tsx` | 输入叶子组件（输入框卡顿修复）：textarea+草稿态+IME 组合守卫+「/」命令面板全部下沉，按键只重渲染本叶子（memo）；非组合逐键 lift、组合期不 lift（compositionEnd 一次性）、`lastLiftedRef` 自 lift 回显抑制（父级滞后 prop 不误判为外部改写）；ref API（setValue/getValue/focus）供语音转写/组图提示词/清空复用 |
| `src/pages/chat/components/IntentConsole.tsx` | Composer 壳：不再持有草稿态/面板态（下沉叶子），仅订阅低频事件（focus 变化、hasText 空↔非空翻转）；外部改写走 textInputRef.setValue；发送门控用 composerHasText |
| `src/pages/chat/components/ChatMain.tsx` | `handlePinnedQuote` 用 inputValueRef 消除 inputValue 依赖（回调身份稳定，MessageList 的 React.memo 不再被逐键 lift 击穿） |
| `src/pages/chat/types.ts` | `buildMessageBlocks` 仅滤除 `subagent_progress`（托盘坞承载）；spawned/completed 父级委派事实保留进主消息（DelegationRow 路径 A 数据源） |
| `src/pages/chat/styles/message.styles.ts` | AgentTurnCard 宽屏最大 750px（720px 内容列 + 外壳），消除右侧卡内空白；正常流保留真实高度，不使用 `content-visibility` remembered intrinsic size；消息操作条为透明图标行，CurrentActivityPanel 委派大卡退役 |
| `src/pages/chat/components/MarkdownBlock.tsx` | `preprocessMarkdown` 增量：正文 emoji run 包 `<span data-md-emoji>`（0.95em 收敛，围栏代码/行内 code 跳过；fence 状态跟踪） |
| `src/pages/chat/viewport/useMessageViewportRuntime.ts` | 吸底阈值 `BOTTOM_THRESHOLD_PX`=24；scrollTop 单一写入者/instant snap/上滚停跟随；虚拟化权重同时计入消息正文、过程项和已水合 canonical render weight；follow effect 依赖 `totalSize`，ResizeObserver auto 模式在底部阈值内收敛 |
| `src/pages/chat/viewport/executionFlowRenderWeight.ts` | 递归计算 reasoning/tool/delegation/message canonical 节点的结构与文本渲染成本，使 DOM 很重但消息数较少的会话提前进入虚拟化 |
| `src/pages/chat/components/AgentMessageBubble.tsx` | 流式打字机不再覆盖 tick/maxLag（40/48 滞后余量过小致 bursts 式蹦出）；交给 hook 的 B2 自适应（24ms tick、速率追踪、拥堵降速、分档 charsPerTick） |
| `src/pages/chat/components/IncrementalMarkdown.tsx` | 流式 markdown 增量渲染（对齐 harness IncrementalMarkdownParser 架构）：围栏外空行切块 + 冻结块 memo 缓存（key=偏移:长度），提交只重解析尾部块，长文流式从 O(n·parse) 降为 O(tail)；`splitMarkdownBlocks` 纯函数（fence 内空行保护、连续空行跳过、前缀偏移） |
| `src/pages/chat/components/MessageItem.tsx` | append 风格：stable 走 IncrementalMarkdown；live 尾段一律消费打字机推进的 visibleLiveText（原实现含语法尾段整段渲染 liveText 造成"瞬跳块+逐打文字"混合节奏），markdown 对未完成结构按前缀渐进渲染 |
| `src/pages/chat/hooks/useTypewriterStreaming.ts` | append 节奏：基础步长跟随流速率（ratePerTick，visible≈到达速率，clamp 2..24），滞后分档仅作追赶兜底；adaptiveMaxLag 收紧 [24,120]（只作平滑余量，避免长落后追赶爆发） |
| `src/pages/chat/styles/layout.styles.ts` | `messageListShell` 增加 relative：底部滚动控制簇（回到底部/贴底跟随）锚定消息区右下角，天然位于 composer 上方（对齐 Hermes measured-composer 锚定语义） |

## Chat 性能热路径

| 文件 | 职责与性能边界 |
|------|----------------|
| `src/pages/chat/client/chatClientStore.ts` | 会话/状态缓存；相同状态轮询必须短路，不重复写缓存或通知订阅者 |
| `src/pages/chat/components/MessageList.tsx` | 将历史消息与 active run 快照投影为稳定消息行；把已水合 turn 的 execution-flow render weight 附到 viewport item；active run 无法匹配现有 Turn 时追加到当前消息流末端；直接渲染 `MessageRow`，并只给当前主代理行附加有界委派摘要；保留终态守卫、同 messageId 原地更新与三源去重 |
| `src/pages/chat/styles/messageStyleContext.tsx` | 消息树样式边界；`MessageList` 注册一次聚合 Chat 样式并通过 Context 共享，消息叶子不得重复调用 `useChatStyles` |
| `src/pages/chat/components/MessageRow.tsx` | 单消息渲染与语义 memo 边界；Agent 行通过根滚动容器 IntersectionObserver 在 600px 预取区注册可见 turn，避免初次挂载批量水合全部历史；投影重建等价对象时保持历史行不提交 |
| `src/pages/chat/components/MessageProcessSummary.tsx` | 思考/工具过程摘要；折叠时不得构建完整 rounds、trace chips 和展示项 |
| `../../Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | Chat 执行流目标设计：同一 assistant turn 内用 TurnStatus、ReasoningDisclosureRow、ToolCallRow、DelegationRow 分层呈现，按 toolCallId 配对并复用实时/历史 projector |
| `src/pages/chat/components/MessageItem.tsx` | 消息文本轻量壳；立即显示纯文本 fallback，并异步加载 Markdown 增强器 |
| `src/pages/chat/components/MarkdownBlock.tsx` | ReactMarkdown、KaTeX、HTML parser 和 Prism 的独立按需 chunk |
| `src/pages/chat/reducer/subAgentReducer.ts` | 子代理事件与状态快照的统一投影；即使页面漏收 `created/started`，也会按状态接口的 canonical `runId` 重建缺失运行；`budget_exhausted` 是可恢复终态，任何终态进入后不得被迟到事件降级；`subagent.llm.completed.reasoning_preview` 作为实际“模型推理”展示，旧字符数占位不再生成 |
| `src/pages/chat/components/ChatMain.tsx` | Chat 主壳；消息与当前 Agent 保持首屏关键路径，余额、Goal、会话推断在首帧 idle 后启动；任务看板、Checkpoint、历史搜索、开发面板和子代理检查器保持动态模块，并在入口 hover/focus 时意图预取；完整子代理卡片只进入托盘坞，主消息仅接收有界父级委派摘要 |
| `src/pages/chat/index.tsx` | Chat 路由装配；消息右键菜单只有右键触发时才下载并挂载，不得把 ContextMenu 拉回首始 chunk |
| `src/pages/chat/hooks/useInitialIdleReady.ts` | 首帧后辅助工作调度边界；优先 `requestIdleCallback(timeout=1200)`，旧 WebView2 回退到不超过 250ms 的 timer，并提供卸载取消 |
| `src/pages/chat/components/HistorySearchModal.tsx` | 历史搜索弹窗；只有 `historyModalOpen` 时才挂载并触发异步 chunk |
| `src/pages/chat/components/AgentMessageBubble.tsx` | Agent 消息气泡；首 Token 前用一条 compact reasoning disclosure 与 canonical ToolCallRow 连续展示主代理轨迹，不再重复渲染 thinking/tool 活动大卡；无事件时才显示等待占位，子代理内部过程不得进入主消息 |
| `src/pages/chat/components/ReasoningPreview.tsx` | DeepSeek Harness 风格思考 disclosure；折叠态只显示最新可见推理摘要，点击后展示完整 model-visible reasoning，不伪造隐藏思维链 |
| `src/pages/chat/components/ToolCallRow.tsx` | 24px 工具轨迹行；call/result 只按 canonical `toolCallId` 精确配对，IN/OUT 按需展开，不按工具名或到达顺序兼容猜测 |
| `src/pages/chat/hooks/useSessionEventReplay.ts` | 会话 bootstrap/gap replay 与子代理状态校正；进入会话后始终立即并低频读取状态接口，不能以本地已有 active run 为轮询前提 |
| `src/pages/chat/components/SubAgentActivityDock.tsx` | 子代理任务、推理、工具、轮次和输出详情的唯一入口；选中 run 后分页读取归档并用同一 reducer 投影恢复历史时间线/终态统计，实时运行每 3 秒追平；Agent-first 路由回退 `mainSessionId` 绑定卡片 |
| `src/pages/chat/components/IntentConsole.tsx` | Composer；摄像头弹窗只在用户打开视觉输入时加载和挂载 |
| `src/pages/chat/hooks/useMessageInteractionQueue.ts` | Composer 活动消息队列投影；只请求并保留 queued/delivering/retrying，终态 delivery 留在持久化审计/诊断入口，不进入常驻输入区 |
| `src/pages/chat/components/MessageQueueDropdown.tsx` | 活动消息队列紧凑入口；无活动项时不渲染，有活动项时默认收起并可按需展开详情 |
| `src/pages/chat/viewport/messageProjection.ts` | 将 MessageList 已组装的权威消息顺序转换为虚拟行；不得按 active run 的原始启动时间二次排序，避免长任务状态回跳到历史顶部 |
| `src/pages/chat/viewport/useMessageViewportRuntime.ts` | 虚拟列表、锚点与贴底；scroll 状态未变化时不得触发 React commit，首屏稳定轮询应尽快结束 |
| `src/utils/perfEventRuntime.ts` | 首屏常驻的轻量性能事件缓冲；不得反向静态导入完整诊断模块 |
| `src/utils/debug.ts` | 完整性能快照、观察器和诊断建议；仅由 perf/debug 模式或开发面板异步加载 |
| `scripts/check-chat-bundle-budget.cjs` / `package.json` | `npm run build` 后的 Chat 体积门禁；同步脚本 ≤1536 KiB、Chat 路由 ≤480 KiB，并验证任务看板/Checkpoint/ContextMenu 未回流首载共同块 |

## 工作空间与静态资源边界

| 文件 | 职责与边界 |
|------|------------|
| `src/pages/workspace/index.tsx` | 工作空间列表；进入操作直接打开带 `workspaceId` 的 Chat，新建后进入管理设置，不再加载 2D Studio |
| `src/utils/workspaceNavigation.ts` | 工作空间入口在最近访问或唯一工作空间时解析到 Chat；不再生成 Studio 深链 |
| `public/assets/` | 只保留 Web 运行时实际需要的静态资源；`images/me.png` 是当前用户的 Pudding 默认头像，Agent 精灵素材已迁出到 `../PuddingDesktop/Assets/AgentSprites/` |

## 用户头像

| 文件 | 职责与边界 |
|------|------------|
| `src/components/UserAvatarUpload.tsx` | 受控头像上传组件（Upload + 正方形裁剪 Canvas 512px）；props 为 `userId`/`avatarUrl`/`onUploaded`，不读写 `@@initialState`；上传走 `updateUserAvatar`（`POST /api/users/{userId}/avatar`，字段 `file`，仅 PNG/JPG/WebP ≤5MiB），成功只回调，是否同步顶栏由页面决定 |
| `src/pages/user-management/index.tsx` | 编辑抽屉顶部头像区域（仅编辑态显示）；头像为“独立即时保存项”（确认即上传，不随保存/取消回滚）；`onUploaded` 更新 `editingUser.avatar` 并刷新列表，仅当被编辑用户是当前登录用户时同步全局 `currentUser.avatar` |
| `src/pages/workspace/[id]/index.tsx` | 工作空间概览已移除“我的头像”区块；头像入口只在用户管理编辑抽屉 |

后端契约见 `PuddingPlatform/Controllers/Api/UserAvatarApiController.cs`：`POST /api/users/{userId}/avatar`（为自己上传需登录，为他人上传需 Admin）；`AppUserDto.Avatar` 携带现有头像；`/api/currentUser` 从 `AppUsers.Avatar` 投影，空值回退 `/admin/assets/images/me.png`。

后端对应入口：`PuddingPlatform/Services/AgentChat/AgentConversationProjectionService.cs` 首屏只返回最近 20 条消息；active run 返回最近 64 条可见过程明细，同时用 `processSummary` 保留全量计数。`PuddingPlatform/Controllers/Api/SessionEventsController.cs` 的 bootstrap 子代理事件快照上限为 500 条。

全局壳：`src/app.tsx` 只保留所有路由真正共享的认证、主题和 request；管理端 ProLayout、全局操作和开发态 `SettingDrawer` 必须留在异步 `AdminLayout`。不要仅为减小 `umi.js` 启用 `granularChunks`；必须合计 HTML 同步引用的 framework chunk 与 Chat 路由首始 chunk，确认真实首载字节和请求顺序确有改善。

2026-08-26 生产构建基线：同步 HTML 脚本 1,375,324 bytes，Chat 路由 454,635 bytes，`common-async` 187,591 bytes；从 Chat 触发的任务看板 41,670 bytes、开发面板 48,405 bytes、历史搜索 9,988 bytes、Checkpoint 4,941 bytes、ContextMenu 4,604 bytes 均为独立异步块。该数据是构建产物证据，不等同于已部署 WebView2 的真实网络/交互验收。

## 测试

编排 Layout/Revision/Typed Edge/Graph Input/HTTP Hook/Manual Run/组件 UI 定向 Jest：`src/pages/orchestration` 10 suites / 57 tests；生产构建必须生成 `/orchestration/index.html`。仓库全量 `tsc --noEmit` 有既有非编排基线错误时，必须另行确认输出中 `src/pages/orchestration` 命中为 0，不能把全量失败描述为通过。

余额徽标定向 Jest：`providerBilling.test.ts` / `useProviderBalance.test.ts` / `ProviderBalanceIndicator.test.tsx` 共 3 suites / 12 tests（注册表匹配矩阵、拉取/降级/手动刷新、'—'/¥xx.xx/detail Tooltip）。
