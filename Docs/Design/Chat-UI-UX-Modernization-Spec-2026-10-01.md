# Chat 前端现代化 UI / UX 实施规格

日期：2026-10-01。状态：设计交付，待实施与产品验收。本文件不代表前端已改造或已通过视觉验收。

## 1. 范围、证据与不变条件

目标：让聊天成为安静、清晰、有层次的工作界面；保留现有功能、数据、入口能力与业务语义。采用柔和中性色、单一蓝色强调、清晰字体、紧凑导航与舒展阅读区。正文是视觉主角，过程可追溯，操作可发现，危险动作明确。

本规格基于当前源码静态审阅，不是运行页面截图审计。尺寸和颜色是目标设计值；实际效果必须按第 12 节截图、键盘、真实交互验收。没有以静态源码证明服务端能力可用。

源码根为 `Source/PuddingPlatformAdmin/src/pages/chat/`，以下相对源码路径均相对此根：

| 已审阅事实 | 实施含义 |
|---|---|
| `index.tsx` 组装 `useChatState`、Agent 客户端与 TurnSurface，兼有架构开关 | 保留两条当前受支持的投影路径，不能趁换皮删除回退 |
| `components/ChatLayout.tsx` 传递工作区、Agent、会话、队列、权限、Checkpoint 等回调 | 作为功能保留清单；搬入口仍调用同一回调 |
| `ChatMain.tsx` 已有 WorkspaceNavigationHeader、任务看板、余额、历史搜索、自动朗读、Checkpoint、开发面板、Goal 与子代理区域 | 只调整视觉分组及响应式，继续保留懒加载 |
| `SessionSidebar.tsx` 是 Agent 通讯录加可展开的会话历史 | 不改成只有会话列表，不把 Agent 主会话选择改成创建新会话 |
| `IntentConsole.tsx` 承载运行中补充、队列、图片、语音、技能与执行偏好 | 主输入入口在此；不能只修改 `InputArea.tsx` 就宣称完成 |
| `ComposerTextInput.tsx` 有叶子草稿、IME 守卫与命令面板 | 保留草稿所有权和低频上报，避免恢复整树逐键渲染 |
| `ComposerActionMenu.tsx` 普通附件、思考强度为“即将开放”；图片/摄像头按能力门控 | 保留禁用与原因，不能画成可用按钮；执行偏好不等于思考强度 |
| `MessageList.tsx` 接 viewport runtime、审批卡、计划卡、转录模式与专注模式 | 保留滚动锚点、历史加载、审批和折叠语义 |
| `MarkdownBlock.tsx` 经异步边界加载 GFM、公式与 HTML 渲染依赖 | 保留首屏文本回退和当前渲染能力；安全审核另列门禁 |
| `styles.ts` 聚合 `styles/*.styles.ts`；布局已有 CSS 变量、模糊背景，动画已有弹跳、光晕、扫描等定义 | 在现有样式系统内收敛，先查实际引用再移除死样式；不额外引入 UI 框架 |

架构遵循 [Shell / Web / Core ADR](../Features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md)。Chat 留在 Web，WinUI 仅 Shell，Core 独立进程。设计不新增 API、数据库迁移、原生聊天或授权捷径。历史设计参考 [消息卡设计](../message-card-ui-design-2026-08-13.md)、[行为链质量升级](../chat-ui-behavior-chain-quality-upgrade-2026-08-23.md)；冲突时保留当前业务事实源，本文件决定外观目标，不覆盖业务协议。

## 2. 总体布局与信息层级

```text
┌ Agent / 会话导航 264 ┬ 工作区导航 / 当前 Agent       看板 搜索 快照 更多 ┐
│ 搜索 Agent 或会话   │ Goal 摘要 / 连接异常（条件出现，非永久占位）        │
│ Agent 通讯录        │                                                │
│ 当前 Agent 高亮     │          消息与执行轨迹：最大宽 880              │
│ 历史会话展开区      │          正文 → 过程摘要 → 可展开明细             │
│ 新任务              │                              子代理活动入口     │
│                     │ 普通 / 详细 / 摘要   专注     回到最新（条件出现）│
│                     │ 待发图片 / 技能 chips / 队列摘要                  │
│                     │ 输入草稿                                         │
│                     │ +  执行偏好 权限   麦克风  补充当前任务 停止 发送 │
│                     │ 运行状态 / 上下文摘要 / 详情                      │
└─────────────────────┴─────────────────────────────────────────────────┘
```

这是结构示意，右侧子代理停靠区与开发面板复用既有组件，宽屏可展开，不能永久挤占正文。系统标题栏不在 Web 内重画。

- 桌面 ≥1280 CSS px：左栏 264，顶部 56；阅读区最大 880，左右至少 24，输入区同宽；右侧详情仅展开时占 320，中心不足 640 时转抽屉。
- 920–1279：左栏 232 或手动收起；顶部工作区用紧凑标签但仍可切换；右侧全部抽屉。
- 600–919：左栏为覆盖式抽屉，顶部保留展开、Agent、任务看板及更多；工作区切换、历史搜索、快照移入更多。当前 `headerSwitchSelect` 在窄屏隐藏，必须提供替代入口后再隐藏。
- <600：边距 12，输入区 12；顶部 Agent 名称省略，更多可看完整名称；队列、状态详情用抽屉。按钮点击区至少 44×44，允许工具行换行，不允许页面水平滚动。
- 各尺寸保持顶部、输入区可见，消息区作为主滚动容器；短高度 <640 时压缩装饰与摘要，textarea 上限为可用高度的 30%，最低两行。不得用 fixed 输入框覆盖消息。
- 使用容器可用高度，优先父级 `height:100%; min-height:0`；浏览器独立页面可用 `100dvh`。不得将 Shell 标题栏高度写成固定补偿值。
- 抽屉打开不卸载消息列表/输入草稿；关闭恢复触发器焦点。折叠导航仅改变可见性，不触发选择 Agent 或会话。

## 3. 视觉系统：字体、色彩、间距、控件

默认浅色，跟随当前产品主题入口；深色同等验收。主题源复用应用 ThemeProvider / ConfigProvider；不要新建独立 localStorage 开关与原主题冲突。

| Token | 浅色 | 深色 | 用途 |
|---|---|---|---|
| bg | #F7F8FA | #11151B | 背景 |
| surface | #FFFFFF | #1A2029 | 卡片、输入区、弹层 |
| surface-muted | #EEF1F5 | #242C37 | hover、代码容器 |
| text | #182230 | #E8EDF4 | 正文 |
| text-muted | #526174 | #A8B5C7 | 时间、次级信息 |
| border | #D8DEE8 | #445166 | 容器分隔 |
| accent | #2458D3 | #91B3FF | 链接、选中、主操作 |
| accent-soft | #EAF0FF | #243657 | 选中底色 |
| success / warning / danger | #157347 / #8A5700 / #B42318 | #75D6A4 / #F0C36A / #FF9E99 | 状态文字与图标 |

色值是候选 token；验收必须测实际背景及透明度合成后的对比度。正文 ≥4.5:1，大字 ≥3:1，操作边界、焦点、状态图形 ≥3:1。禁用项除视觉变淡外必须说明禁用原因。

字体：`'Segoe UI Variable', 'Segoe UI', 'Microsoft YaHei UI', system-ui, sans-serif`；代码：`'Cascadia Code', Consolas, monospace`。不依赖外网下载字体。正文 15px/1.75，输入 15px/1.6；控件/导航 14px/1.4；次级信息 12px/1.5；消息标题 16px/1.5 600；Markdown h1/h2/h3 24/20/17px。正文不使用细于 400 的字重，中文不添加字距。数字统计用 tabular-nums，时间避免不断改变宽度。

间距基于 4：4/8/12/16/24/32。主消息间隔 24，同回合过程块间隔 8，卡片 padding 16，输入 padding 12–16。控件圆角 8、卡片 12、输入容器 16，chip 6；不要所有区域都胶囊化。默认容器 1px 边框；仅浮层使用轻阴影，正文无光晕。

图标统一现有 Ant Design 图标，16/18px，线条与基线统一。普通按钮高 36，图标按钮点击区 36，触屏 44。主发送按钮实色；停止为中性轮廓加明确方形图标与文字；删除在菜单末组危险色。不得把“停止”画成红色删除动作。

## 4. 功能与入口保留矩阵

以下每行都必须保留；桌面/窄屏位置分别验收。操作名按当前文案可微调，但含义不可改变。

| 功能 / 当前组件 | 新位置及交互 | 回归重点 |
|---|---|---|
| 工作区导航 / `WorkspaceNavigationHeader`、工作区 Select | 顶部左区；窄屏顶部更多 → 工作区 | 禁用工作区仍禁用；创建入口仍可达；不泄漏跨工作区草稿 |
| Agent / `SessionSidebar` | 左栏通讯录，头像 28、名称一行、状态文字；选中用底色+左线 | 工作中、冻结、停用、异常与离线不可都显示“在线”；保留未读 |
| 新任务、历史会话、搜索 / `SessionSidebar` | 左栏顶区；历史作为当前 Agent 下折叠区 | 新任务不是切 Agent；重命名、归档、删除仍有菜单 |
| 全文历史搜索 / `HistorySearchModal` | 顶部搜索；窄屏更多 | 与通讯录本地过滤明确区分；结果定位仍走既有 viewport |
| 任务看板 / `TaskBoardModal` | 顶部“任务”文字按钮，窄屏仍常驻 | 打开后回到当前会话，既有权限不变 |
| Checkpoint / `CheckpointTimelinePanel` | 顶部快照；抽屉 | 还原、分叉、删除、清空均保留；还原标记可关闭 |
| 自动朗读 / `useAutoTts`，通知音 | 顶部更多 → 声音，当前开关显示选中 | 不新增重复音效；只按现有完成/通知逻辑播放 |
| 余额 / `ProviderBalanceIndicator` | 顶部轻量数字与详情；窄屏更多 | 刷新保留，错误/未知不显示 0；按现有币种 |
| 普通/详细/摘要 / `TranscriptModeSwitch` | 阅读区下缘独立“显示”栏 | normal/verbose/summary 值不变，只影响展示 |
| 专注 / `FocusViewToggle` | 同“显示”栏开关 | 每回合摘要与逐条展开仍可用，不改变转录数据 |
| 消息操作 / `MessageActions`、`ContextMenu` | 消息底部复制、固定、更多；用户保留重新输入，助手保留朗读等现有动作 | 右键等价入口；删除、重跑按当前角色能力；固定引用仍可插入草稿 |
| 固定消息 / `PinnedMessageButton` | 显示栏固定入口及数量 | 展开、引用、总结等既有操作保留 |
| Markdown / `MarkdownBlock`、`IncrementalMarkdown` | 正文阅读层 | 代码、表格、公式、HTML、制品图片都可渲染；不改消息原文 |
| reasoning、工具树、委派 / `execution-flow/*` | 正文邻近一行过程摘要 → 展开分层轨迹 | 事件顺序、callId、投影一致；非伪造进度 |
| 子代理 / `SubAgentActivityDock` | 右侧停靠入口，窄屏抽屉 | 主代理仅有界摘要，子代理内部过程仍在 dock |
| 审批 / `ApprovalCard`、`RecentlyDeniedPanel` | 当前相关消息内常显；拒绝历史从状态详情可达 | 允许/拒绝/范围不变，执行中防重复提交 |
| 计划 / `EditablePlanCard` | 消息内卡片，不并入装饰性过程摘要 | 编辑、保存及当前支持动作保持原回调 |
| Goal / `GoalBanner`、相关 Goal 组件 | 顶部条件摘要，详情沿用现有入口 | 只展示当前投影，不由计时器判完成，不复活历史废弃状态机 |
| `+` / `ComposerActionMenu` | 输入区左下；添加到本轮与会话动作分组 | 图片、摄像头按能力门控，普通附件/思考强度仍即将开放，导出保留 |
| 技能 / `SkillPalette`、技能管理、pendingSkills | + 子面板及原管理入口；输入上方 chips | 可搜索、选择、移除；仅在发送时走原追加逻辑 |
| 命令与提及 / `CommandPalette`、`MentionPalette` | textarea 附近弹层 | `/`、提及触发和键盘顺序保持，IME 不误触发 |
| 语音与图片 / `IntentConsole`、`ImagePreviewOverlay` | 输入下缘麦克风；图片上缘缩略图 | 录音、转写、权限失败、移除、预览保留；不发送未确认图片 |
| 权限 / `PermissionModeSelector` | 输入下缘有文字标签，窄屏折入“执行设置” | 展示当前真实模式，切换失败回退，不默认升权限 |
| 执行偏好 / `IntentConsole` | 输入下缘“执行偏好” | 与权限分离，既有选项和值不变 |
| 队列 / `MessageQueueDropdown` | 输入上方“待发送 N” | 编辑、删除、立即发送、steer、排序、取消全部保留；拖拽有上下移键盘替代 |
| 运行中补充、停止、发送 / `IntentConsole` | 输入右下；运行时同时显示停止和发送/排队，补充为独立动作 | 停止当前不清队列；取消全部仍明确；补充不等于新消息排队 |
| 状态 / `ComposerStatusDetails`、各 token、上下文、压缩、索引、潜意识指标 | 输入下缘一行概览 → 状态详情，warning/error 自动露出 | 统计缺失显示“未知”；保留 Token、命中率、记忆等详细信息 |
| 开发者模式 / `DevPanel`、benchmark、会话时间线 | 顶部更多 → 开发者；开启后按原条件露出面板及时间线入口 | Context、Subconscious、Perf、Benchmark 等原 tabs 不删；保留诊断懒加载 |

实施前再对 `ChatMain`、`IntentConsole` 的所有 render 分支做一次清单核对；新增或漏列的已存在入口补进此矩阵后才能搬移。暂不可用功能不能以删除入口完成“简洁化”。

## 5. 消息、富文本与执行轨迹设计

助手消息左对齐、无大气泡底色：角色/Agent 小标题、正文、过程、操作分层。用户消息保留浅强调气泡，最大宽 78%（窄屏 92%），按原时间顺序显示。长用户消息采用显式“展开全文”，原文复制/重新输入不读截断后的 DOM。头像不随每段流式输出重复插入。

正文段落 margin-bottom 12，列表层级缩进 20；块引用左线 3px、背景 muted、padding 12；超长 URL 使用 overflow-wrap，代码不强制断词。链接有下划线或 hover 下划线加明确色，键盘可聚焦；沿用既有链接路由、安全策略和 Desktop 浏览器桥。

代码块：顶部语言标签+复制，等宽 13px/1.6，最大高 480，内部横向滚动；复制成功显示“已复制”1.5s，失败显示“复制失败，重试”。保留代码内容换行，复制读原始字符串。表格只让表格容器横向滚动；长公式可横滚；不要把整个消息设 overflow:hidden 截断内容。图片保留比例、最大宽 100%、加载占位与错误重试；预览支持 Escape、焦点恢复，加载失败不清空消息。

流式输出按实际事件即时显示，不用逐字打字机重放，不人为延迟消息。稳定内容不得每个 chunk 重排全树或重复入场动画。未闭合 fenced code / 公式按既有增量渲染策略回退，闭合后升级；不要为了美观修改原文或吞掉行。重型 Markdown 保留异步加载、纯文本 fallback，避免 fallback 与最终渲染同时被屏幕阅读器朗读。

过程行示例：`工具执行中 · 读取文件 · 2 项活动   展开`；完成后：`过程 · 3 次工具调用 · 1 个子代理   展开`。无可靠数量时省略数字。错误活动常显错误摘要与查看详情，不静默折叠；审批需求常显操作卡。normal 折叠明细，verbose 展开，summary 保留摘要；专注模式覆盖视觉密度但不清 disclosure 状态。不得把 reasoning 改成伪造的“思考步骤”。

工具行：图标+工具名+简短目标+状态+耗时，参数与结果分别可展开；大结果显示截断提示和既有查看入口，不丢原始数据。委派区保留 Agent 名、任务摘要、状态和进入 dock 入口。展开状态用稳定 turnId / 活动标识，不能数组 index 作 key；新的 chunk 不重置展开/文本选择。主题变化也要检查 `MarkdownBlock` memo 与 styles 引用的更新，避免正文颜色滞留旧主题。

## 6. 状态与反馈规则

以下是展示词汇，映射当前 projection / hooks，**不是新增业务状态机或后端枚举**。

| 状态 | 视觉与文案 | 操作 |
|---|---|---|
| 无工作区 / Agent | 安静插画或现有空态的小图标，“选择工作区 / Agent 开始” | 明确选择或创建，发送禁用且有原因 |
| 历史加载 | 3 行中性 skeleton，保留顶部与输入结构 | 不把正在加载误画为无消息 |
| 就绪 | 静态状态点+“就绪” | 发送 |
| 提交中 | 按钮“发送中”，局部 spinner | 防重复提交，保留草稿直至受理 |
| 执行 / 输出中 | 文本“执行中/输出中”，仅一个轻量动效 | 停止、排队、补充当前任务各自可达 |
| 等待审批 / 用户输入 | amber 图标与明确等待原因 | 审批卡常显，不用 spinner 冒充处理 |
| 停止请求已提交 | “正在停止”直到权威事件返回 | 暂停重复停止，不提前显示完成 |
| 成功 / 已停止 | 静态图标和终态文字 | 完成仅短暂提示，不持续闪烁 |
| 失败 | danger 图标、简短原因、详情 | 按实际支持提供重试，不一键自动重放有副作用工具 |
| 断线 / 重连 | 顶部“连接中断，正在重连”，保留已收消息 | 不清草稿，不显示假完成；受理未知时不盲目重发 |
| 上下文接近上限 / 压缩 | 实际百分比+提示，压缩时明确文案 | 保留详情；不把缺失统计置 0，不估算模型结果 |

错误至少分网络、权限、能力不支持、渲染失败、执行失败，用户可理解的摘要与诊断详情分离。toast 用于短暂复制/保存反馈；影响继续工作的错误放 inline，不只闪过。审批、停止、删除等按钮 pending 状态不依赖动画完成来解除。

## 7. 输入区与微交互

输入容器 resting 为 1px border，hover 稍加深，focus-within 为强调边框与 2px 外圈；避免整块持续发光。textarea 两行起、最多八行（短窗口再限制），内容多时内部滚动。placeholder 根据真实状态显示“给当前 Agent 发消息”或“继续输入，可排队或补充当前任务”。不替代真实 label。

- Enter/Shift+Enter、Ctrl/Cmd+Enter 继续由现有事件处理链决定；保留运行中 Ctrl/Cmd+Enter 的补充语义。若增加新快捷键，先检查 Shell、浏览器与现有命令冲突。
- 中文 composition 中 Enter 只确认候选，不发送；`ComposerTextInput` 保留 `isComposing` 与原守卫，外部语音转写/固定引用更新仍同步叶子草稿。
- 发送、排队、补充按钮文案由现有运行状态决定；补充拒绝时保留草稿并解释，发送失败保留可重试内容。取消录音不清原草稿。
- 图片 chip 为 56×56+文件名可查，删除点击区 24（触屏 44）；移除后释放预览资源按原生命周期处理。技能 chip 标签+删除，长名省略但有完整名称提示。
- `+` 打开后焦点落首个可用项，技能子面板可点击/键盘打开，不能只 hover；Escape 先关闭子面板，再关闭父弹层，回到 `+`。上/下导航，子面板返回保持原项目焦点。
- queue 数量必须显式显示；“取消全部”与“停止当前执行”分开。拖拽重排只作视觉反馈，最终顺序仍用原回调更新；网络错误回退原顺序。
- 菜单选择立即给反馈但不提前声明业务成功；禁用图标仍提供原因说明文本。hover 与 focus-visible 效果等价，触屏操作栏常显。
- 重命名聚焦选中文本，Enter 保存、Escape 取消；归档保留当前反馈；删除按既有确认路径，确认内显示对象名并默认焦点在取消。不得用气泡里的双击替代删除确认。

## 8. 动画与特效预算

| 交互 | 动效规范 |
|---|---|
| hover / focus / 按压 | 色彩/透明度 120ms，按压可 1px 位移；正文无缩放 |
| 新消息首次出现 | opacity 0→1、translateY 4→0，160ms；历史/重连/虚拟列表复挂不播放 |
| 菜单 / popover | opacity+translateY 4px，140ms；方向依靠现有定位 |
| 抽屉 | 200ms translateX，遮罩 opacity；结束不改变焦点语义 |
| 折叠 | 图标旋转 120ms；内容不做 max-height:2000px 扫描，长内容即时展开 |
| 工作中 | 单个状态小点 opacity 0.6→1，1.6s；静态文字保留 |
| 成功 | 静态勾，允许一次 120ms 淡入 |

收敛现有弹跳、blur、发光、glitch、全背景扫描与逐字符效果；保留状态识别能力，原球体/粒子装饰可作为小型空态标识或静态图形，不能删除其附带功能/点击入口。隐藏标签页、不可见区域停止循环动画；动画不作为业务计时器。优先 transform/opacity，不持续动画阴影、滤镜或高度。

`prefers-reduced-motion` 下禁用装饰循环、位移和 smooth scroll，保留静态状态词与必要 loading 语义。系统高对比模式下移除依赖透明背景的区分，使用系统色边框与焦点。

## 9. 示例代码与接入细节

下面是拟实施模式，不是本次新增产品代码。CSS 属性适用于现有样式层，React 示例通过 props 调用原功能；集成时遵循仓库 lint 与既有 ThemeProvider，不复制第二套业务逻辑。

### 9.1 统一 token 与作用域

在现有 `--pudding-chat-*` 变量定义处更新值；先用 `rg -- '--pudding-chat-bg' src` 查真源，避免多个定义竞争。不要全局覆盖 `.ant-btn`。

```css
/* 添加到现有主题真源；主题属性名在集成时接应用真实主题源 */
.chatTheme {
  --pudding-chat-bg: #f7f8fa;
  --pudding-chat-surface: #fff;
  --pudding-chat-surface-muted: #eef1f5;
  --pudding-chat-text: #182230;
  --pudding-chat-text-muted: #526174;
  --pudding-chat-border: #d8dee8;
  --pudding-chat-accent: #2458d3;
  --pudding-chat-accent-soft: #eaf0ff;
  --chat-font: 'Segoe UI Variable', 'Segoe UI', 'Microsoft YaHei UI', system-ui, sans-serif;
  font-family: var(--chat-font);
  color: var(--pudding-chat-text);
}
.chatTheme[data-theme='dark'] {
  --pudding-chat-bg: #11151b;
  --pudding-chat-surface: #1a2029;
  --pudding-chat-surface-muted: #242c37;
  --pudding-chat-text: #e8edf4;
  --pudding-chat-text-muted: #a8b5c7;
  --pudding-chat-border: #445166;
  --pudding-chat-accent: #91b3ff;
  --pudding-chat-accent-soft: #243657;
}
.chatTheme :focus-visible {
  outline: 2px solid var(--pudding-chat-accent);
  outline-offset: 2px;
}
```

Ant Design portal 不一定在 `.chatTheme` 下。Select、Tooltip、Modal、Drawer 应沿用当前 popup 容器机制或主题 token 传递；为 popup 增加明确作用域 class，验证深色弹层不会变回白色。仅在不破坏定位/裁剪时改变 popup container。

### 9.2 保留 antd-style 分模块结构

```tsx
// 拟加入 styles/layout.styles.ts 的样式片段
import { createStyles } from 'antd-style';
export const useReadingLayoutStyles = createStyles(() => ({
  center: {
    flex: 1, minWidth: 0, minHeight: 0,
    display: 'flex', flexDirection: 'column',
  },
  scrollRegion: { flex: 1, minHeight: 0, overflowY: 'auto' },
  readingColumn: {
    width: '100%', maxWidth: 880, marginInline: 'auto',
    paddingInline: 24, boxSizing: 'border-box',
    '@media (max-width: 599px)': { paddingInline: 12 },
  },
  composer: {
    flexShrink: 0, border: '1px solid var(--pudding-chat-border)',
    borderRadius: 16, background: 'var(--pudding-chat-surface)',
    '&:focus-within': {
      borderColor: 'var(--pudding-chat-accent)',
      boxShadow: '0 0 0 2px var(--pudding-chat-accent-soft)',
    },
  },
}));
```

集成时合入现有 `useLayoutStyles` / `useComposerStyles`；不要同时制造两个滚动容器。`MessageList` 的 ref 仍绑定实际滚动节点，虚拟列表测量节点保持原关系。改变 padding 后跑 viewport 锚点回归。

### 9.3 微控件：只表达状态与回调

```tsx
import { Button, Tooltip } from 'antd';
import { SearchOutlined } from '@ant-design/icons';

type SearchActionProps = { onOpen: () => void; busy: boolean };
export function HistorySearchAction({ onOpen, busy }: SearchActionProps) {
  return (
    <Tooltip title={busy ? '正在加载历史' : '搜索历史消息'}>
      <span>
        <Button type="text" icon={<SearchOutlined />} disabled={busy}
          aria-label="搜索历史消息" onClick={onOpen} />
      </span>
    </Tooltip>
  );
}
```

把 `onOpen` 接到 `ChatMain` 原打开搜索动作；不在按钮组件调用 API。图标通过 aria-label 命名，Tooltip 不作为唯一可访问名称。忙碌是否应禁止搜索由现有能力决定，此例 busy 仅用于展示禁用模式，不新增业务限制。

### 9.4 折叠过程：稳定身份与可访问语义

```tsx
import React, { useId } from 'react';
type ProcessDisclosureProps = {
  open: boolean; summary: string; onToggle: () => void;
  children: React.ReactNode;
};
export function ProcessDisclosure(p: ProcessDisclosureProps) {
  const regionId = useId();
  return (
    <section>
      <button type="button" aria-expanded={p.open}
        aria-controls={regionId} onClick={p.onToggle}>
        {p.summary}<span aria-hidden="true"> {p.open ? '⌃' : '⌄'}</span>
      </button>
      <div id={regionId} hidden={!p.open}>{p.children}</div>
    </section>
  );
}
```

在现有 `ExecutionDisclosureRow` / `ReasoningDisclosureRow` 中采用此语义，展开状态仍由 disclosure registry 管理。本例保留 DOM 适合小内容；大工具结果继续按现有懒水合策略渲染，不能为了 hidden 一次性挂载所有历史内容。状态记录不得驻留临时行组件而在虚拟化复挂时丢失。

### 9.5 动效可关闭，入场由事件决定

```css
@keyframes chat-message-enter {
  from { opacity: 0; transform: translateY(4px); }
  to { opacity: 1; transform: translateY(0); }
}
.chatMessage[data-enter='true'] {
  animation: chat-message-enter 160ms ease-out both;
}
.chatActions { opacity: 0; transition: opacity 120ms ease; }
.chatMessage:hover .chatActions,
.chatMessage:focus-within .chatActions { opacity: 1; }
@media (hover: none) { .chatActions { opacity: 1; } }
@media (prefers-reduced-motion: reduce) {
  .chatMessage[data-enter='true'] { animation: none; }
  .chatActions { transition: none; }
  .chatScrollRegion { scroll-behavior: auto; }
}
@media (forced-colors: active) {
  .chatMessage, .chatComposer { border: 1px solid CanvasText; }
  .chatTheme :focus-visible { outline-color: Highlight; }
}
```

`data-enter` 只在会话内首次接收的新消息短时设置，从现有事件标识派生；历史加载、缓存回放、重连重复事件、虚拟化复挂都为 false，动画结束清除。操作栏保留空间，opacity 不导致布局跳动；键盘聚焦使其显示。不得把带焦点元素设 aria-hidden。

### 9.6 测试示例：功能不能因视觉迁移变义

```tsx
// Jest / Testing Library；示例组件与 fixtures 在实施时使用真实 IntentConsole
const stop = jest.fn();
// render(<IntentConsole {...existingFixture} loading onStop={stop} />);
// await user.click(screen.getByRole('button', { name: '停止当前执行' }));
// expect(stop).toHaveBeenCalledTimes(1);
// expect(screen.getByText('待发送 2')).toBeVisible();
// expect(clearQueue).not.toHaveBeenCalled();
```

这里有意只给测试断言模式，不提供虚构的完整 fixture 或声称可直接运行。使用现有 `IntentConsole.test.tsx` 的完整 props 构造。IME 用 `ComposerTextInput.test.tsx` 的 composition 序列扩展，主题/缩放/布局在浏览器测试验证，不能用 jsdom 尺寸断言代替。

## 10. 性能、滚动、可访问性与安全

滚动沿用 `viewport/useMessageViewportRuntime.ts` 的 intent 和测量。贴底时跟随输出；用户向上阅读时暂停跟随并显示“回到最新”，不得被任何新 chunk 抢回底部。历史加载保持顶部可见消息的相对位置；图片/公式晚加载、过程展开时复测高度并维持锚点。仅显式回到底部触发跳转。不要另写全局 `scrollIntoView` effect 与原 runtime 竞争。

性能预算为待验收目标：相同机器、同一构建模式、同一消息 fixture，改造后输入延迟 p95 ≤50ms 且相对基线回退 ≤10%；滚动压力场景 p95 帧间隔 ≤33ms，新增装饰不造成连续 >100ms 主线程任务。使用浏览器 trace 测输入事件到绘制，不能拿 React render 时间代替端到端输入延迟。未达绝对目标时不能只凭“比以前快”通过，须单列性能问题处理。

分别测试 1000 回合历史、长代码/表格/公式、10 分钟连续流式、子代理活动与工具大结果。保留 `MessageRow` memo、viewport 渲染预算、纯文本 fallback、DevPanel 与 Markdown 懒加载和 bundle budget。依赖新增必须说明压缩包增量，不能把诊断或公式渲染放入首屏同步包。不得使用全文 JSON.stringify 作每个 chunk 的 memo 比较。

键盘顺序：导航 → 顶部动作 → 消息可交互区 → 显示栏 → 输入与操作；无正数 tabindex。弹层使用已有 Ant Design focus 管理，Escape 关闭当前顶层，焦点回触发器。状态用文字+图标+颜色，选中使用 aria-current（页面/导航适用）或现有控件 selected 语义，不混用。

状态宣布区域只用一个 `role=status`/polite live region，播报低频“开始执行 / 等待审批 / 已停止 / 失败”，不朗读每个 token、耗时和队列排序。紧急需行动的错误可 assertive，避免多区域重复播报。200% 缩放所有功能可达；400% 按窄屏布局重排。系统高对比、减少动画与屏幕阅读器各做一次人工检查。

美化不扩大 `rehypeRaw` 等现有渲染信任范围；检查现有 HTML、链接、图片、制品 URL 与 iframe 的安全处理，有缺口登记独立修复并阻止相应危险场景验收。对脚本、事件属性、危险 scheme 使用测试文本验证；不为了保留显示效果允许脚本执行。审批卡不能被消息 HTML 伪装成应用授权按钮，外部内容与应用控件视觉/DOM 边界清晰。

## 11. 实施切片与文件责任

| 切片 | 修改点 | 可单独交付证据 |
|---|---|---|
| P0 清单与基线 | 跑当前入口场景并保存截图/trace 到 `temp/test-out`；补全第 4 节矩阵 | 每项入口的当前位置、能力、回调；浅深色基线 |
| P1 主题和布局 | 现有 CSS 变量真源、`styles.ts`、`layout/sidebar/composer/message/markdown/status/animations.styles.ts` | 无业务改动；三种窗口截图、popup 同主题、缩放 |
| P2 导航和入口 | `ChatMain`、`SessionSidebar`、菜单与现有 panels | 第 4 节逐项前后可达性；窄屏无功能丢失 |
| P3 消息和轨迹 | `MessageRow/Item/Actions`、`execution-flow`、Markdown 样式 | 普通/详细/摘要/专注、工具/审批/子代理/固定回归 |
| P4 输入和状态 | `IntentConsole`、`ComposerTextInput`、技能/队列/状态详情 | IME、图片/语音、停止/排队/补充/权限矩阵 |
| P5 产品验收 | 浏览器 E2E、perf、WebView2 真实窗口 | 第 12 节签字记录，问题清零或明确阻断 |

不必新增独立程序集或抽象设计系统包。若确需抽组件，先在独立边界测试，遵循[组件化交付规程](../Conventions/组件化交付规程.md)，视觉切片不要顺手重构客户端状态层。每个已验证原子任务精确暂存并提交；保持旧回调契约。不得整体合并 B。

## 12. 验收门禁：通过是什么

验收记录必须包含 commit、浏览器/WebView2 runtime、Windows 版本、DPI、viewport CSS 尺寸、主题、数据 fixture、实际结果与截图/trace 路径。文档完成不等于产品通过。

| ID | 操作与数据 | 必须通过的结果 |
|---|---|---|
| V01 | 1440×900、1024×768、768×720、390×844；浅/深两色 | 正文/输入同中心线；阅读宽度符合 §2；无页面横滚/遮挡；窄屏所有矩阵入口可达 |
| V02 | 1280×720 100/150/200% DPI；浏览器 200/400% 缩放 | 不截断按钮文字/弹层，输入法候选不遮主操作，菜单可滚动 |
| V03 | 测正文、次级文字、链接、选中、focus、审批/警告卡 | 实际合成色达到 §3 对比度，状态不只靠颜色 |
| F01 | 逐行执行 §4 矩阵；能力可用与不可用两套 Agent | 原操作仍走原回调与结果；附件/思考强度不误启用；图片/摄像头门控保留 |
| F02 | 新任务→切 Agent→查看历史→搜索→重命名/归档/删除 | 无错会话、草稿串会话、未读丢失；新任务和切 Agent 不混淆 |
| F03 | 输出中输入两条排队消息→停止当前→编辑/排序队列→补充当前任务 | 停止不清队列；立即发送、补充、取消全部语义分别正确；重复点击不重复受理 |
| F04 | 中文拼音候选 Enter、换行、快速发送；语音转写与引用插入 | 候选确认不发送；发送读最终草稿；失败草稿保留；原快捷键有效 |
| F05 | 长 Markdown、未闭合代码/公式、表格、制品图片及加载失败 | 无丢行/闪回/整页横滚；复制取原始文本；预览退出焦点正确 |
| F06 | reasoning→工具→子代理→审批→完成，刷新/重连后再看 | 顺序/状态/展开一致；审批不隐藏；子代理详情不混入主代理全文 |
| F07 | 三种转录+专注+固定引用+重跑/删除+朗读 | 数据不被展示模式修改；所有现有角色动作可达 |
| F08 | 断网再连、服务失败、权限拒绝、无统计、压缩 | 原文/草稿保留；无假完成/假 0/自动危险重试；错误有明确恢复入口 |
| F09 | Checkpoint 还原/分叉/删除/清空；Goal 与开发 tabs | 当前受支持操作全部有效；还原提示/会话时间线与懒加载保留 |
| S01 | 向上阅读同时流式，加载更早历史，展开工具，图片晚加载 | 可见锚点位移目标 ≤4 CSS px（显式跳转除外）；不抢滚动；回到最新可用 |
| A01 | 仅键盘遍历、Escape、屏幕阅读器、触屏 | 无焦点陷阱/隐藏焦点；菜单/技能级联可操作；不逐 token 播报 |
| A02 | 减少动画、高对比、标签页隐藏 | 装饰循环停止；仍识别状态与焦点；业务完成不依赖动画 |
| P01 | §10 同机同数据 trace+现有 bundle 检查 | 达到性能目标；输入叶子/懒加载/memo 不退化；bundle 门禁通过 |
| SEC01 | 含危险 HTML、URL 的消息与制品测试 | 无脚本执行/越权桥调用；审批控件不被内容覆盖或伪造 |
| D01 | 新构建 WebView2：焦点、快捷键、IME、主题和弹层 | 与浏览器交互一致；Shell 标题栏不重复；使用已加载新构建证据 |

检查命令从 `Source/PuddingPlatformAdmin` 执行，统一 pnpm：`pnpm run test -- --runInBand`、`pnpm run build`（包含 chat bundle budget）、`pnpm exec playwright test e2e/chat-streaming.spec.ts`。先定向运行修改组件的既有 tests，再运行适用完整门禁。新增 E2E 用固定 mock 事件覆盖大多数状态，最后用隔离测试 DataRoot 的真实 Core smoke 验证接线；不能把 mock 成功当产品端到端完成。

构建输出按仓库要求放 `temp/build`，测试产物放 `temp/test-out`；实施时先确认 Umi/Playwright 当前配置与输出覆写机制，不让默认 dist/报告散落。Desktop build/test/publish 串行，使用恢复构建规定的 artifacts 路径。不要对运行中的 `D:\data` 试跑或复制密钥。

通过条件：F01 矩阵覆盖率 100%；无功能入口遗漏、错误权限提升、草稿/消息丢失或滚动抢占；V/A/P/SEC/D 全通过。真实模型/摄像头等未具备测试条件的项记录为“未验收”，不能记通过。人工记录“美观”还需与基线同尺寸对照：正文层级清晰、无多重光晕/持续扫描、按钮样式统一、错误与等待明确；不能仅以截图相似度判断交互合格。

建议验收表字段：`ID | commit | 环境/fixture | 操作 | 期望 | 实际 | 证据路径 | 通过/失败/未验收 | 缺陷编号`。验收资料清理前保留必要结论到 `Docs/Reports`，持久截图如需入库先确认大小与敏感内容，临时 trace 不入库。
