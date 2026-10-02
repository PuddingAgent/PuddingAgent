# PuddingPlatformAdmin Chat 路由体积门禁决策 Plan（2026-09-19）

## 1. 现状与证据

本 Plan 只讨论 `Source/PuddingPlatformAdmin` 的 Chat 路由首屏 chunk，不涉及后端或其他路由。门禁脚本从 source map 中定位包含 `src/pages/chat/index.tsx` 的 chunk，按 JS 文件字节数检查；同时检查同步入口总量、`common-async` 泄漏，以及三个已登记的惰性源是否仍落在 Chat/common chunk。当前常量为 `maxChatRouteChunkBytes = 496 * 1024 = 507904`（`Source/PuddingPlatformAdmin/scripts/check-chat-bundle-budget.cjs:8-16`），同步入口上限为 `1536 * 1024`（同文件 `:4`）。

HEAD `2cf63bab` 的父代理实测门禁输出为：`sync=1375324 chat=507842 common=196016`；Chat 预算只剩 **62 B**。压缩卡改动前为 `chat=505576`，新卡片净增 2687 B、已有瘦身回收 421 B。故下一次功能性改动几乎必然越过当前门禁。

历史决策不是“永久冻结 480 KB”：2026-09-16 因 P0-FE Goal 面板变更后 `chat=492929`，超过 480 KB 1409 B，门禁上调到整数 0.5 MB 以继续拦截重依赖回归；原文同时明确登记后续优化刀：仅在详情 Popover 使用的 `GoalStepsPanel` 应改为 `React.lazy`，移出首屏后再收紧上限（`scripts/check-chat-bundle-budget.cjs:9-16`）。

Map 逐来源归因覆盖率 99.9%，Chat chunk 共 147 个来源（129 个 Chat 源、16 个 antd 图标、2 个 thinking-orbs 源）。重点数字：`useChatState.ts` 22225 B、`useSessionEventProjection.ts` 22183 B、`IntentConsole.tsx` 17286 B、`MessageList.tsx` 16555 B、`chatStateUtils.ts` 14642 B、`index.tsx` 14582 B、`GoalStepsPanel.tsx` 11729 B、`AgentMessageBubble.tsx` 11597 B、`MessageQueueDropdown.tsx` 10428 B、`ContextUsageRing.tsx` 6288 B、`SessionSidebar.tsx` 6042 B、`EditablePlanCard.tsx` 5997 B、`CommandPalette.tsx` 9179 B、`GoalBanner.tsx` 9316 B；`thinking-orbs` 两文件合计 14445 B。`components/` 小计 232420 B。以上均为简报实测值，未重跑构建。

## 2. 候选清单（实际使用点核实）

判定规则：只有“模块本身不在首屏必经渲染/输入链，且可由条件交互触发”的项目才进入路线 B；测试环境必须沿用 `process.env.NODE_ENV === 'test' ? require(...) : React.lazy(...)`，避免 Jest 因 Suspense 异步化抖动。字节数仅采用简报 3.1 已实测值；不在 top30 的项目明确标为“未单列”，不虚构数字。

| 模块（map 归因） | 实际使用点（文件:行） | 首屏/罕见路径判定 | 惰性化判据、预计回收与风险 |
|---|---|---|---|
| `GoalStepsPanel.tsx`（11729 B） | `components/GoalBanner.tsx:29` 静态 import；`:334` 渲染 | **罕见路径**：GoalBanner 外层常驻，但步骤面板位于详情 `Popover` 内容内；不是首屏必经 | 简报已明确为上游后续优化刀；可按既有模式改 lazy。理论上限约 11729 B，实际以构建前后差值为准。风险是 Popover 首次打开的 chunk 延迟和测试 Suspense。优先级 P0。 |
| `CommandPalette.tsx`（9179 B） | `components/ComposerTextInput.tsx:26-29` import；`:289` 始终渲染组件 | **不是可安全罕见路径**：命令面板显示受 `/` 条件控制，但组件挂在每次 Composer 首屏渲染的输入链；不能仅因 UI 条件显示就认定可移出 | 可技术上拆分，但会把输入链/首次输入交互变成异步边界；预计回收不应承诺（理论 9179 B）。风险最高：首屏输入、键盘选择和测试均受影响；不列入第一阶段。 |
| `MessageQueueDropdown.tsx`（10428 B） | `components/IntentConsole.tsx:52` import；`:782` 渲染 | **当前首屏输入链**：队列入口位于 IntentConsole/Composer；是否展开是低频条件，但组件本身静态挂载 | 只有把打开事件与组件加载一起重构才可拆；理论 10428 B，实际待差值测量。风险是发送队列/停止/拖拽交互首开延迟与请求瀑布；不作为第一刀。 |
| `EditablePlanCard.tsx`（5997 B） | `components/MessageList.tsx:48` import；`:1197` 条件渲染 | **条件路径但在消息列表主渲染链**：仅有计划消息时出现，不是所有首屏必现；是否值得拆取决于计划消息频率 | 可按消息类型在 `MessageList` 外围 lazy；理论 5997 B，实际需差值。风险是历史计划卡首屏出现时闪烁、交互与 Jest mock；列为 P2。 |
| `SessionSidebar.tsx`（6042 B） | `components/ChatLayout.tsx:16-18` import；`:134` 常驻渲染 | **首屏必经**：ChatLayout 每次渲染 SessionSidebar；不可归为罕见路径 | 惰性化会损害布局首帧和会话选择，预计回收不应承诺。明确不做。 |
| `GoalBanner.tsx`（9316 B） | `components/ChatMain.tsx:46` import；`:586` 常驻渲染 | **首屏必经**：ChatMain Header 中常驻；其内部 Modal/Popover 的罕见性不能把整个 Banner 拆出 | 不拆整个模块。可单独拆其重型详情子块，但那等同 GoalStepsPanel/后续 Modal 细分；理论 9316 B不可直接计为回收。 |
| `ApprovalCard.tsx`（简报 3.1 未单列） | `components/MessageList.tsx:45` import；`:1173` 条件渲染；测试直接 import `ApprovalCard.test.tsx:3` | **消息数据条件路径**，不是每轮必现，但归属于 MessageList 主链；测试直接 import 要保留 test 同步分支 | 可拆但需在有审批消息时验证无闪烁；字节数未单列，预计回收待 source-map 差值，不可在本 Plan 虚报。风险是审批决策入口延迟；P2/暂缓。 |
| `RecentlyDeniedPanel.tsx`（简报 3.1 未单列） | `components/AutoReviewIndicator.tsx:10` import；`:150` 放在 `Popover` content；测试直接 import `RecentlyDeniedPanel.test.tsx:6` | **明确罕见路径**：只在 Auto-review 指示器点击 Popover 时展示；但 AutoReviewIndicator 静态持有 import | 可沿 test require + production lazy 拆；字节数未单列，预计回收待差值。风险是 Popover 首开延迟，低于 GoalSteps；P1 候选。 |
| `ModelRetryRow.tsx`（简报 3.1 未单列） | `components/AgentMessageBubble.tsx:32` import；`:685` 仅在 `processItems`/运行状态相关条件下渲染；测试由 `AgentMessageBubble.test.tsx:174-175` mock | **条件路径但接近消息主链**：普通消息不显示，失败/重试过程显示；静态 import 位于高频 AgentMessageBubble | 可拆但需保证重试入口首开可用；字节未单列，回收待差值。风险是失败消息交互延迟和复杂测试 mock；P2。 |
| `presentation/renderers/*` | `presentation/PresentationRegistry.ts:7-12` 静态 import Generic/Terminal/Diff/Read/Search/Web；实际挂载由 `components/execution-flow/ToolCallRow.tsx:24-25,326-329` 的 `resolveRenderer` 决定 | **条件数据路径**：仅工具调用带 presentation 时出现；但 renderer 注册表静态加载五类，属于消息执行流的可选分支 | 可把注册表改成按 kind 的异步加载，但需改变 `resolveRenderer`/ToolCallRow 契约并处理 fallback；简报未给逐文件字节归因，预计回收必须实测。风险是工具卡首开、并发 chunk 和 Suspense；P3，不作为当前门禁止血。 |
| `thinking-orbs`（14445 B） | `components/execution-flow/TurnStatusOrb.tsx:1-2,12` import；`:69` 渲染；由 `TurnStatus.tsx:13,163` 使用；依赖源为简报归因中的两文件 | **高频状态路径**：TurnStatus 在消息执行/状态展示中使用，不能按“动效可选”直接移出首屏 | 可考虑自绘 CSS/轻量 SVG/canvas 替代，但不是简单 lazy；替换成本和视觉回归高。理论最多约 14445 B，但需保持 pending/theme/phase 行为并实测。路线 B 不是本期首选；风险是动画与可访问性回归。 |

候选核实结论：简报要求的“罕见路径”中，`GoalStepsPanel`、`RecentlyDeniedPanel` 最符合；`EditablePlanCard`、`ApprovalCard`、`ModelRetryRow` 是数据条件路径；`CommandPalette`、`MessageQueueDropdown`、`SessionSidebar`、`GoalBanner`、`thinking-orbs` 仍在首屏必经或高频链，不应被简单归入罕见 UI。未发现“候选实际首屏必经”与简报已知事实冲突；上述边界是对简报数字的细化。

## 3. 四路线对比

| 路线 | 可腾出的字节 | 对拦截回归能力的影响 | 实施成本 | 风险 | 是否需用户决策 | 可逆性 |
|---|---:|---|---|---|---|---|
| A 再基线化 | 具体档位建议 **+4 KB**（新上限 512000 B，余量约 4158 B）；备选 +16 KB（523? 以当前 507842 B 计余量约 16384 B）或 +32 KB（约 32768 B） | +4 KB 仍能拦截单次约 4 KB 以上回归；+16/+32 KB 会放过相应规模的重依赖回归，且掩盖 GoalSteps 等可优化项 | 低：改一个脚本常量及注释，验证一次构建 | 预算继续膨胀、回归被合法化；+32 KB 风险明显 | **是** | 高：恢复旧常量即可 |
| B 路由内瘦身 | 首刀 GoalSteps 实测回收 11,710 B（chat 507842 → 496132，见 §9）；加 RecentlyDenied/条件卡片后可能更多，但未单列项必须以差值确认 | 增强拦截能力；移出真实首屏重依赖后可把门禁收紧到 492–496 KB 区间 | 中高：逐组件拆分、测试同步分支、chunk/交互验证 | 首次打开延迟、Suspense、chunk 请求增加；错误拆分会伤害首屏 | 否（实现本身不需改门禁；最终新阈值需决策） | 高：每个原子提交可回滚 |
| C 混合（推荐） | 先回收 GoalSteps 实测 11,710 B；然后只给短期安全垫 **+4 KB**，目标上限建议 512000 B；若实测回收充分，最终可再收紧到 **496*1024=507904 B** 或更低 | 先拆掉已知历史技术债，同时 +4 KB 只牺牲约 4 KB 回归敏感度；比 +16/+32 KB 更保守 | 中：一项 UI 惰性化 + 一项显式定档 + 完整回归 | 需处理 Popover 首开和两阶段门禁决策；若不验证差值，目标可能不准确 | **仅最后定档需用户决策** | 高：先回滚惰性化，再恢复旧阈值 |
| D 不做 | 0 B | 当前 62 B 余量等价于几乎没有新增回归容忍度；下一次功能改动直接 exit 1，门禁无法服务迭代 | 0 | 开发阻塞、团队被迫临时改常量，且继续保留已知 GoalSteps 首屏负担 | 否，但实际会把决策推迟为事故 | 表面高，实际不可持续 |

注：+16 KB 的新上限应按当前常量直接加 `16*1024`，即 524288 B；+32 KB 为 540672 B。表中“523?”不作为数字依据，实施时禁止采用该笔误。推荐档位为 C 的短期 +4 KB，即 512000 B，而非 +16/+32 KB。

## 4. 推荐结论

明确推荐 **路线 C：先做 GoalStepsPanel 惰性化，再由用户最后决策只增加 +4 KB 的短期安全垫**。理由是：当前问题不是单纯“预算太小”，而是已有上游决策指出的 11729 B 罕见 Popover 负担仍在首屏；先移除它，既能恢复约 11.7 KB 的结构性余量，又能把门禁继续用于拦截重依赖回归。+4 KB 仅覆盖小幅并行变更，不会像 +16/+32 KB 那样把大型依赖误拉回 Chat 首屏后仍视为通过。

不推荐 A 单独先调大：它最快但把已知可修复问题转化为预算债务，且无法改善首屏；不推荐 B 立即广泛拆分所有候选：多数候选实际处于输入、侧栏或消息高频链，误拆会制造首开延迟，收益没有 source-map 差值证据；不推荐 D：62 B 余量会使正常功能迭代变成门禁事故，团队最终仍要临时调档，而且失去有意回归检测窗口。

最终阈值不是本 Plan 擅自决定：在瘦身构建确认后，用户应在“保守维持 507904 B”“+4 KB 到 512000 B”“进一步收紧到不高于 496 KB”三者中决策。建议默认选 **512000 B 仅作为过渡档**，并登记在后续构建数据稳定后收紧。

## 5. 分步实施计划

每步均为一个可独立提交的原子任务；本 Plan 阶段不执行源码、常量修改和构建。

### Step 0：建立改动前证据快照
- 目标：锁定当前 `chat=507842`、`sync=1375324`、`common=196016` 及 147 来源归因。
- 文件：不改源码；仅使用 `scripts/check-chat-bundle-budget.cjs` 与简报中的已有日志/归因结果。
- 验收命令：由父代理执行既定 `npm run build > ..\\..\\temp\\chat-budget-before.log 2>&1`，再检查 `[chat-bundle-budget]`。
- 期望：`chat=507842`（允许构建环境仅产生可解释差异）；当前门禁通过，余量 62 B。

### Step 1：仅拆 GoalStepsPanel
- 目标：让生产环境 GoalStepsPanel 走 lazy 独立 chunk，测试环境同步 require；保持详情 Popover 内容、错误降级、加载行为不变。
- 文件：`src/pages/chat/components/GoalBanner.tsx`，必要时新增同目录 loader 小段；对应 `GoalBanner`/`GoalStepsPanel` 测试仅为保持同步契约而调整。
- 验收命令：`npx jest src/pages/chat/components/GoalBanner.test.tsx src/pages/chat/components/GoalStepsPanel.test.tsx`；随后 `npm run build` 和门禁脚本；使用 source-map 归因命令确认来源归属。
- 期望：`chat` 至 **496132 B**（已由父代理探针构建实测，见 §9；对应余量 11,772 B）；`GoalStepsPanel.tsx` 不在 chat/common，独立 chunk 可定位；其余四项门禁仍通过。不得因预测值偏差擅自改阈值。

### Step 2：对 GoalSteps 首开与测试契约做回归锁
- 目标：验证 Popover 首次打开加载、错误/空态、测试环境不出现 Suspense 异步抖动。
- 文件：`GoalBanner.test.tsx`、`GoalStepsPanel.test.tsx`（只补行为断言，不改生产语义）。
- 验收命令：同 Step 1 的 Jest；检查 test 分支使用同步 require；浏览器手工/既有 UI smoke 检查“打开详情→步骤内容/失败提示可见”。
- 期望：chat 数字与 Step 1 相同，目标区间 495–498 KB；相关测试用例数不减少，既有已知失败不得新增。

### Step 3：评估低风险第二批（可选，独立提交）
- 目标：只在需要更大余量时评估 `RecentlyDeniedPanel`，不得把 CommandPalette、MessageQueueDropdown、SessionSidebar、GoalBanner 整体拆出。
- 文件：`AutoReviewIndicator.tsx` 及 `RecentlyDeniedPanel.tsx`，对应测试。
- 验收命令：相关 Jest；`npm run build`；source-map 检查该源是否独立 chunk，且 common 禁止项/已有 deferredChatSources 不变。
- 期望：chat 在 Step 2 数字基础上再降低“实测差值”；由于 3.1 未单列字节，**不得预设固定数字**，只要求 `chat <= Step 2 chat` 且无同步入口回归。若首开延迟或 chunk 膨胀不合格，跳过该步。

### Step 4：用户决策后最后定档（最后一步）
- 目标：在 Step 1/3 的真实差值稳定后，才调整 `maxChatRouteChunkBytes`；不把预算调整混在代码拆分提交中。
- 文件：`scripts/check-chat-bundle-budget.cjs`；禁止触碰其他门禁常量。
- 验收命令：`npm run build`；确认脚本 exit 0、source-map deferred/common 检查通过，并记录 chat 与目标差值。
- 期望：若采用推荐过渡档，常量为 `500 * 1024 = 512000 B`，`chat <= 512000 B` 且建议保留至少 8 KB 余量；若用户选择维持旧档，则 `chat <= 507904 B`；若收紧，则以用户批准的新目标为准。此步骤的数值必须来自用户决策，不得由实施者默认放宽。

## 6. 验收标准

1. `npm run build` exit 0，且门禁输出中的 `chat` 不超过最终批准目标；`sync` 不超过 1572864 B。
2. `GoalStepsPanel.tsx` 在 source map 中不属于 Chat 初始 chunk，也不属于 `common-async`；独立 chunk 可定位。既有 `CheckpointTimelinePanel`、`ContextMenu`、`workspace-tasks/index.tsx` 三项检查继续通过。
3. `common-async` 不含 `src/pages/workspace-tasks/`；Chat chunk 仍可由脚本通过 `src/pages/chat/index.tsx` 唯一定位。
4. Goal Banner 详情 Popover 首次打开有可见 loading/内容/错误降级，不出现白屏；关闭后再次打开行为正确。
5. 相关 Jest 用例数量不减少；测试环境采用同步 require，不因 Suspense 异步挂载产生新增失败。简报列出的既有失败（AgentMessageBubble 2 例、IntentConsole 2 例）不计为本次回归，但不得新增同类失败。
6. 不把首屏必经链错误拆分：输入框、侧栏、GoalBanner 外壳、MessageQueueDropdown 的常用队列入口保持交互可用；如评估第二批，必须证明首开交互延迟没有不可接受回归。
7. 每个源码/常量改动都单独构建并记录 `chat=`；最终报告保存“改动前、每个原子步骤后、最终定档后”的数字与 source-map 归属。
8. 以父代理的真实构建差值为准，不用 map 单来源估算替代验收；理论回收值只用于排序。

## 7. 风险与回滚

- **首帧空白/抖动**：Popover 首次打开可能等待独立 chunk。保留加载占位和错误边界；若用户感知为白屏，立即回滚 GoalSteps lazy，或改为仅在打开事件后预取而不阻断 Popover。
- **测试 Suspense 抖动**：若生产 lazy 代码无 test require 分支，Jest 会出现异步断言失败。回滚到既有同步 import，按 `process.env.NODE_ENV === 'test' ? require(...) : React.lazy(...)` 修正后再提交。
- **chunk 数量膨胀/请求瀑布**：每个 lazy 候选会增加请求；只允许首刀 GoalSteps，第二批必须用浏览器网络面板和构建产物确认没有串行瀑布。
- **mako 切块不生效**：用 `node temp\\chat-chunk-sources.cjs` 检查源归属；若 `GoalStepsPanel.tsx` 仍在 Chat chunk 或 common，任务失败，不得宣称回收，直接回滚并检查 import 是否仍被静态路径保留。
- **阈值调整削弱回归拦截**：+16/+32 KB 不是默认方案；任何调档必须记录旧值、新值和对应能拦截的回归规模。推荐过渡档 +4 KB 也应在后续稳定后收紧，而不是永久放宽。
- **条件模块误判为罕见**：CommandPalette、MessageQueueDropdown、SessionSidebar、GoalBanner 外壳实际在首屏链；若拆分导致输入/导航延迟，按模块原子回滚，不扩大范围。
- **未单列来源的估算误差**：ApprovalCard、RecentlyDeniedPanel、ModelRetryRow、renderers 未在 top30 中单列；不得用文件体积或目录小计冒充可回收字节，必须由前后构建差值确认。
- **依赖替换风险**：thinking-orbs 虽有约 14445 B 归因，但属于高频状态动效；替换需单独视觉/可访问性验收，本期不与门禁定档混合。

回滚顺序：先回滚第二批候选（若有），再回滚 GoalSteps lazy，最后才回滚/恢复门禁常量；每次回滚都重新运行一次门禁并保存 `chat=`。

## 8. 停止条件

本 Plan 已按简报第六节完成八个部分并写入指定文件。按任务约束，本阶段不修改任何源码或门禁常量、不执行 git 提交、不重跑构建，也不修改 `Docs/IndepthCoding-Guide` 或 `Docs/claude-reviews-claude`。候选使用点已通过 `search_grep`/`file_read` 实际核实；未发现“GoalStepsPanel 并非 Popover 罕见路径”或其他会推翻简报关键决策的冲突。后续实施必须由父代理复核本文件、执行真实构建并在用户批准后进行最后定档。

## 9. 父代理探针实测补充（2026-09-19，探针改动已还原、未入库）

Step 1 的「理论上限」已由父代理用真实构建测出确定值（临时改造 `GoalBanner.tsx` → 构建 → `git checkout` 还原）：

| 指标 | 探针前（HEAD `2cf63bab`） | GoalStepsPanel 惰性化后 | 差值 |
|---|---:|---:|---:|
| Chat 路由 chunk | 507,842 B | **496,132 B** | **−11,710 B** |
| 同步入口总量 sync | 1,375,324 B | 1,375,364 B | +40 B（上限 1,572,864 B，宽裕） |
| common-async | 196,016 B | 196,016 B | 0（未泄漏进 common） |
| 门禁结果 | ok（余量 62 B） | ok（**余量 11,772 B**） | — |

方法与判据：按仓库既有惰性化写法（`process.env.NODE_ENV === 'test' ? require('./GoalStepsPanel').default : React.lazy(loadGoalStepsPanel)`，
外加 `React.Suspense` 轻量 fallback）改造 `components/GoalBanner.tsx:29` 与 `:334`；构建后读门禁输出，
再用 `node temp\chat-chunk-sources.cjs` 复核 chat chunk 成员：`GoalStepsPanel.tsx` 已不在 chat chunk，
`common-async` 仍为 196,016 B。map 归因预估 11,729 B 与实测 11,710 B 相差 19 B（0.16%）——
说明「map 逐来源归因」可作为可靠排序依据，但**验收仍以构建差值为准**。

据此得出的决策数字（只做 Step 1 的前提下）：

- 维持现阈值 507,904 B：余量 62 B → **11,772 B**；
- 若另加过渡档 +4 KB（512,000 B）：余量 → **15,868 B**；
- Step 2（第二批候选）在 Step 1 之后**不再是止血必需**：11,772 B 已足够容纳一次普通功能性改动，
  建议确有需要时再做，并按实测差值验收。

因此：**路线 B 的首刀可独立解除当前的「门禁危机」，无需先动阈值**；阈值问题降级为
「是否给未来并行改动预留安全垫」的独立决策。据此，路线 A（先调阈值）在任何情况下都不应作为第一步。
——本节由父代理复核时补入，上文中凡与本节数字冲突处，以本节为准。
