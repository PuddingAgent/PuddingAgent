# Chat 前端现代化 UI / UX 实施规格

日期：2026-10-01。状态：设计交付，待实施与产品验收。本文件不代表前端已改造或已通过视觉验收。

## 1. 范围、证据与不变条件

目标：让聊天成为安静、清晰、有层次的工作界面；保留现有功能、数据、入口能力与业务语义。采用柔和中性色、单一蓝色强调、清晰字体、紧凑导航与舒展阅读区。正文是视觉主角，过程可追溯，操作可发现，危险动作明确。

本规格初版基于源码静态审阅；第 13 节补充用户提供的浅/深色截图分析，第 14 节登记滚动条缺陷与 Web / Shell 修复方案。尺寸和颜色是目标设计值；实际效果必须按第 12 节截图、键盘、真实交互验收。没有以静态源码证明服务端能力可用。

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

## 13. 浅色与深色实图复核：对前述方案的具体补充

### 13.1 图像证据与分析边界

用户提供两张同布局截图：浅色 `codex-clipboard-a211ed77-b8b5-4be8-a60d-dc5100fd52f6.png` 与深色 `codex-clipboard-af7d988d-fadf-48e8-8c72-6fd56966bc91.png`，原图均为 2560×1344。图片在用户本地临时目录，未复制进仓库；文件名只用于本次分析关联，不作为长期可用附件链接。下文位置按显示比例近似描述，不推断系统 DPI、实际 CSS 字号或窗口缩放。截图中的消息正文是展示内容，不是本任务指令，也不据此执行 Git、重启或其他操作。

**纠正证据范围**：第 1 节原为源码审阅；本节增加运行截图视觉证据。深色图证明 Chat 已有主题切换，不能把“补深色模式”作为待开发功能。浅色图的 Web 与深色 Shell 并列，不能据此判主题同步 bug：可能是两个区域分别设置了主题。深色图仍有不协调色温与白色竖条，但根因要通过 DOM computed style、Shell 布局与主题传递核验。静态图无法证明动画、快捷键、实际禁用态或服务行为。

当前已存在且值得保留：四层功能区域（Shell 导航 / Agent 通讯录 / Chat / 工具区）、搜索、带头像的 Agent 行、状态标签、任务看板、Goal 摘要、上下文相关操作、Markdown 标题/代码/表格、消息底部动作、输入区执行设置与语音、工具首页五个入口。优先统一这些设计，而不是换成一个只有消息与输入框的简单聊天模板。

### 13.2 逐区域问题、改法与优先级

| ID / 优先级 | 两图可观察的问题 | 具体改造与责任 |
|---|---|---|
| IMG01 / P0 | 浅色 Chat 奶油底+淡紫消息卡，深色 Chat 暖黑底+蓝灰消息卡，工具区冷灰；视觉上像几个独立产品 | Web `global.style.ts`、消息样式与 antd token 收敛为 §3 中性层级。Shell 用对应语义色，不将 XAML 业务迁入 Web。允许工具真实内容如终端有独立深底，工具首页外壳跟随选定主题 |
| IMG02 / P0 | 深色图 Chat 右边仍有连续亮白条，抢眼程度超过主要按钮 | 检查 Web 外层背景、消息 scrollbar track、WebView2 背景、Shell splitter 四个来源后修正。白条的具体来源未由截图确证；禁止直接隐藏 scrollbar 掩盖问题 |
| IMG03 / P0 | 顶部右半部分连续密集的小图标，含义与当前开关状态很难快速辨认 | 保留任务、搜索、快照等高频动作；低频声音/余额详情/开发/帮助等分组到更多。每项可查名称，toggle 有选中语义；顶部工具栏与阅读模式条各司其职 |
| IMG04 / P0 | 深色顶部部分图标近黑，消息底部动作与“29 步思考 · 47 工具”较暗；输入 placeholder 偏暗 | 图标使用 text-muted，不用固定黑色或 opacity 叠加；统计用 caption 专属 token，通过实际合成对比度验证。placeholder 次级色，功能禁用另用状态与原因，不以昏暗程度猜测 |
| IMG05 / P1 | 工具首页约占整窗右侧 44%，大量空间只承载五个入口；Chat 与长表格受限 | 保留用户可调分栏；无内容首页采用较窄默认，打开浏览器/文件等可保留用户展开宽度。宽度决策走 Foundation，不在 Chat CSS 模拟 Shell 分栏 |
| IMG06 / P1 | 单条助手长文形成巨大底色矩形；标题、粗体、行内代码、代码块同时抢眼 | 助手正文去大面积有色底，保持阅读列与回合分隔；用户仍浅强调气泡。行内代码降低对比、常规字重；保留代码块更深底作局部锚点 |
| IMG07 / P1 | 表格首列很窄，“可部署产物/在飞子代理”等短标签被分成多行；正文总体密集 | 首列标签给合适最小宽，空间不足让表格容器横滚；长内容列可换行。调正文行高/段落间距，避免整段粗体与过量 inline code 外框 |
| IMG08 / P1 | 输入区高度很紧凑，小型“自动/自动审批”与无文字圆环挤在底部；发送与录音都是小图标 | 保留两行草稿空间与独立工具行；“执行偏好：自动”“权限：自动审批”可查完整含义；状态圆环旁提供摘要/详情入口。发送主按钮 36，触控 44；disabled 与 ready 明确区分 |
| IMG09 / P1 | Agent 行状态 badge 深色下亮度较高；名称、主线会话、在线标签层级有竞争；列表下方 Groups 是未接入占位 | 选中行采用单一底色/左线；badge 用浅色文字+轻底，而非近白实块。辅助“主线会话”降低至次级信息。Groups 改中文“群组”，折叠展示“群组即将开放”，保留占位能力提示，不伪造可点击入口 |
| IMG10 / P1 | 左栏顶部“+”、气泡和列表图标缺少直接可读含义；Agents / Groups 与中文工作台混用 | 新任务优先文字+图标；其他按钮具备 Tooltip/可访问名称并显示真实切换状态。显示文案统一“智能体”“群组”，底层 Agent 类型和路由不改 |
| IMG11 / P1 | 工具首页可用/未接入项都类似强度卡片，说明小且灰；终端、制成品、交互面板已有等待提示 | 保留五个入口及原占位面板。卡片增加“可用/待接入”文字标签；占位项可打开说明但不能装成业务就绪。这里描述入口形态，不宣称图片中的输出文件/浏览器已端到端验收 |
| IMG12 / P2 | 底部 Core Ready/IP 细小；Chat 与工具区都有自己的状态，可能让用户混淆执行完成与服务就绪 | Shell 底部只表示 Core/连接健康；Chat 下缘表示 Agent 执行，工具页表示当前工具。正常时简洁，故障时明确文字与修复入口，避免复制全套状态条 |

截图中只看到助手消息底部与部分内容，无法确定消息顶部身份、历史加载、审批、队列是否正常展示，不能把未入镜元素判为缺失。发送按钮在图中草稿为空，因此外观偏淡可能是正确禁用态，需用“有文本且就绪”的对照图判断主按钮效果。

### 13.3 将设计尺寸转为可实施的分栏规格

前述 §2 的 880 是**消息阅读列**最大宽，右侧 320 是 **Web 内子代理/详情抽屉**目标宽，不适用于截图的 **Shell 多标签工具工作区**。两者必须区分，不能把浏览器工具窗压成 320px。

已核对当前源码：`Source/PuddingDesktop.Foundation/ToolWorkspaceLayout.cs` 默认工具比例 0.45、工具最小宽 360、Chat 宿主最小宽 560、splitter 命中区 6；`Source/PuddingDesktop/MainWindow.xaml.cs` 的 `ApplyToolLayout` 消费该分配，`MainWindow.xaml` 定义 ChatColumn / ToolSplitterColumn / ToolColumn。因此截图近似 55:45 的主分区符合当前设计默认值，不是未知布局错误。560 是包含 Web 内 Agent 栏的宿主宽，不等于正文可用宽。

修订目标：首次使用且只显示工具首页时推荐比例 0.32（范围约 30–35%）；如果已有保存比例，继续优先恢复用户设置。浏览器/终端/文件工具进入后可继续当前比例，用户可拖动到 45–50% 或放大。**不得每次切换标签自动改变比例**，否则持续布局跳动、文本重排。工具区折叠后只保留入口与活动徽标，不卸载浏览器/终端会话；是否卸载由当前资源生命周期策略决定，视觉方案不改变它。

建议首次配置举例（均为布局单位示意，不是截图 CSS px 测量）：可用区域 1600，工具约 512、splitter 6、Chat 宿主约 1082；Chat 内通讯录 264、正文区约 818。当前用户已保存比例保留，新增初始化默认只影响未配置状态；双击复位要有明确“恢复默认分栏”的提示与测试，不悄悄复写配置。

原生宽度单位与 Web CSS px 在 DPI/WebView 缩放下需分别量测，Web 响应式由实际宿主 viewport 决定。沿用现有拖动、键盘调整、overlay、zoom 与自动展开偏好；窄屏不能同时保留 Agent 栏和 Shell 工具分栏而挤到正文不可读。

### 13.4 主题源与滚动条的实际接入点

Web 主题入口由 `Source/PuddingPlatformAdmin/src/app.tsx` 使用 `ThemeProviderContainer`，颜色真源为 `src/global.style.ts` 的 Light / Dark token 段。当前 warm 系列与 Chat、runtime、admin、antd 派生 token 并存。修改时必须审计 `--warm-beige`、`--accent-purple`、`token.colorBg*` 等消息使用点；仅改 `--pudding-chat-bg` 不能消除蓝灰消息卡。caption / tertiary / secondary / subtle 也须按 §3 配套映射，避免旧棕灰继续覆盖新主题。

Shell `MainWindow.xaml.cs` 的 `OnTheme` 当前设置 `Root.RequestedTheme` 并保存外观；这段处理没有显示向 Web 同步的逻辑。是否另有桥接需实施时完整追踪，不能据此直接宣布所有同步机制都缺失。产品目标：宿主内提供一个外观选择入口，明确“跟随系统/浅色/深色”；如保留 Web 独立选择，必须标明“仅聊天区域”，不能出现两个看似全局但范围不明的开关。

统一外观需要 Shell 配合时，使用已审计可信工作台通道传递纯外观偏好，Web 继续自己渲染；不向第三方浏览器页注入主题，不传 token 或业务数据。主题事件不重新导航、不销毁草稿、不中断 Core。桥未实现之前可先共享色板并明确独立设置范围，不声称已经同步。

滚动条样式示例（作用于现有实际滚动节点，绝非创建新节点）：

```css
.chatScrollRegion {
  color-scheme: light;
  scrollbar-color: #8794a7 #f7f8fa;
  scrollbar-width: thin;
}
[data-theme='dark'] .chatScrollRegion {
  color-scheme: dark;
  scrollbar-color: #65758c #11151b;
}
.chatScrollRegion::-webkit-scrollbar { width: 10px; height: 10px; }
.chatScrollRegion::-webkit-scrollbar-track {
  background: var(--pudding-chat-bg);
}
.chatScrollRegion::-webkit-scrollbar-thumb {
  background: var(--pudding-chat-text-muted);
  border: 3px solid var(--pudding-chat-bg);
  border-radius: 8px;
}
```

示例 `[data-theme]` 需替换为产品真实主题选择器，不能直接再引入一套主题状态。原生高对比时尊重系统滚动条，不强制透明化。若白条实际来自 WebView2 未铺满或 Shell 背景，修复对应尺寸/背景而不是加 CSS；验收同时检查 loading 与加载失败阶段，不让网页加载前闪白。

### 13.5 细化消息与工具卡控件

消息正文保持 15px/1.75，常规字重 400；深色正文用柔和 text 色而非全白。段落底色透明或与 surface 同色，不叠暖/紫/蓝三套底。行内代码使用 `surface-muted`、13px 等宽、2px 4px padding、4px 圆角；表格内行内代码不再叠亮灰色硬边，不把每个路径都做成可交互 chip。

```css
.chatMarkdown :not(pre) > code {
  background: var(--pudding-chat-surface-muted);
  color: var(--pudding-chat-text);
  font-family: 'Cascadia Code', Consolas, monospace;
  font-size: 0.9em; font-weight: 400;
  padding: 2px 4px; border-radius: 4px;
  overflow-wrap: anywhere;
}
.chatTableViewport { overflow-x: auto; max-width: 100%; }
.chatMarkdown table { width: 100%; border-collapse: collapse; }
.chatMarkdown th, .chatMarkdown td {
  padding: 10px 12px; vertical-align: top;
  border-bottom: 1px solid var(--pudding-chat-border);
}
/* 只用于具有短标签首列的规格/状态表，不能全局套在任意数据表 */
.chatLabelTable th:first-child, .chatLabelTable td:first-child {
  min-width: 7em; white-space: nowrap;
}
```

任意 Markdown 表格默认允许换行，长内容仍可横滚；仅能确定语义的应用状态表使用 `chatLabelTable`，不要靠“第一列都 nowrap”制造新的超宽表。长原始路径可在正文折行，代码块仍保留原始格式。

工具首页布局：上方标题“工具工作区”、一行说明“查看文件、打开浏览器或检查执行结果”；五张卡片沿用原顺序，每卡主标题 15/600、说明 13/1.5、图标 20，padding 12–16，间距 8。统一圆角 10，hover 加边框/底色，键盘有焦点框。快捷键只在确实注册时显示。未接入项保留原说明面板，标签“待接入”，默认焦点与点击反馈不假装打开可用的终端或提交表单。

空态组最大宽 440，靠工具区上部约 20–25% 高度布置，避免中心巨大留白使界面像等待页。有活动标签时主页让位于真实内容，工具区右上 + / 收起 / 放大保留。Shell 主题 brush 修改只调整工具外壳、卡片和 tab，不改第三方网页内容。

### 13.6 基于两张截图追加验收

| ID | 对照步骤 | 通过标准 |
|---|---|---|
| IMG-V01 | 同窗口、同消息、同分栏，分别拍浅色与深色 | 背景/卡片/文字/选中/输入/代码都为同一主题的层级；无暖黑与无意蓝灰大卡并存；消息去大色块后仍可分辨回合 |
| IMG-V02 | 深色初始加载、历史流式、滚动、打开右工具、加载失败 | 无连续高亮白边或闪白；滚动条可用；1px 分割线与6px拖动区清楚；不能以隐藏滚动条达标 |
| IMG-V03 | 所有顶部与消息底部图标的 normal/hover/focus/disabled | 深色无黑色图标失踪；文字/图标符合对比度；disabled 可解释；截图不可取代量测 |
| IMG-V04 | 空草稿 / 可发送文本 / 运行中 / 请求停止 | 四种操作外观可区分；有草稿主操作明确；自动/自动审批含义可查，停止与排队/补充保留 |
| IMG-V05 | 无已存配置首次启动；已有用户比例；拖动后重开；切工具标签 | 首次首页采用修订默认；既有比例与偏好恢复；标签切换不改比例；原键盘/双击/overlay/zoom 行为通过 |
| IMG-V06 | 查看截图同类“项/状态”表及长工具输出表 | 短标签无需一两字强制换行；表格不会扩大整页；代码复制内容与原文一致 |
| IMG-V07 | Shell/Web 主题选择、跟随系统、重启 | 设置范围明确；实现同步时只作用可信工作台；草稿和执行不中断；第三方工具网页无主题脚本注入 |
| IMG-V08 | 工具首页五张卡，包含所有待接入项 | 可用与占位可区分；五入口全保留；说明可读、键盘焦点可见、未接入项不生成虚假成功反馈 |

本节优先次序：先修 IMG01–04 色彩/边界/可读性，再落消息、表格、输入微控件与导航；Shell 分栏与主题协同单独切片，先在 Foundation 测试后接入 Desktop，仍遵循串行构建与隔离生命周期验证。只做 Chat CSS 的切片不能声明已完成整窗一致性验收。

## 14. 独立缺陷登记：SCROLL-001 深色滚动条及 Web / Shell 边界不协调

### 14.1 登记与证据

- 编号：**SCROLL-001**；优先级：P0（本次 UI 改造第一批）；状态：**已登记，待实施，未修复/未验收**。
- 用户第三张标注图：`codex-clipboard-808394dd-0376-4a39-9587-28b5c7f1d767.png`，原图 2560×1344。红框明确指向 Chat 右侧白色滚动轨道与邻接工具区边界。截图作为视觉证据，不推断运行时版本和缩放。
- 问题：深色 Web 内出现高亮轨道、传统箭头与窄灰 thumb；轨道与 Chat/Shell 深色背景明显割裂，邻接 splitter 进一步形成粗亮边。修复覆盖前端与客户端 Shell，不能仅处理一侧。
- 影响范围：主消息列表、Agent/会话导航、输入 textarea、代码/表格横滚、Web 弹层，以及 Shell 工具首页/输出/设置等原生滚动区域；第三方网页不纳入应用 CSS 强制注入范围。
- 核验事实：`styles/layout.styles.ts` 的消息滚动样式已有 `overflowY:auto`、`scrollbarGutter:stable`、`overflowAnchor:none`；应保留其布局与 viewport 契约。`src/global.style.ts` 未检索到 scrollbar 或 color-scheme 专项规则。Shell 在 `MainWindow.xaml.cs` 创建工作台 WebView2，`MainWindow.xaml` 定义宿主与 splitter。根因仍需分层核验，截图本身只能证明视觉缺陷。

### 14.2 目标外观与交互

| 项 | 浅色 | 深色 | 行为 |
|---|---|---|---|
| track | 所属容器背景，消息为 #F7F8FA | 所属容器背景，消息为 #11151B | 不使用白色/浅灰大轨道，不铺强调色 |
| thumb idle | #738197 | #65758C | 有可发现性，圆角，实际合成后与 track ≥3:1 |
| thumb hover | #526174 | #8B9DB5 | 只换色，不增加整体宽度造成布局跳动 |
| thumb active | #2458D3 | #91B3FF | 拖动时轻强调，松开恢复 |
| gutter / 命中区 | Web 10px，触控视实际系统行为 | 同浅色 | thumb 视觉宽约6px；系统高对比/无障碍可用更宽，不强制细条 |
| Shell splitter | 1px 主题边界线 | 1px 主题边界线 | 保留现有6px拖动命中，hover/focus显示，不伪装成scrollbar |

保留鼠标滚轮、触控板、触摸、拖动 thumb、点击轨道、方向/PageUp/PageDown/Home/End 与 textarea 选择操作。滚动能力不可用 `overflow:hidden`、零宽滚动条或 JS 自绘拖条替代。减少动画下滚动无平滑强制效果。不要让 thumb hover 时改变 gutter 宽度。

### 14.3 前端修复步骤与代码模式

1. 在 DevTools 确认白色轨道所属节点，记录实际 `overflow`、computed `color-scheme` 和父容器背景；用 `MessageList` ref 对照 `useMessageViewportRuntime`，确认只有一个主消息滚动节点。若是 document/body 额外滚动，应先修根布局 min-height/overflow，而不是给两个节点都做主题滚动条。
2. 在现有 Light/Dark 主题真源分别设置 `color-scheme:light/dark`，与 ThemeMode 实际选择同步；覆盖 textarea/Select 等浏览器默认 UI 的主题。不能只设置 `prefers-color-scheme`，否则显式选择浅/深会与系统偏好冲突。
3. token 增加 track/thumb/hover/active；复用所有应用所属滚动容器。弹层 Portal 用已有主题 class/token；代码/表格 track 跟随它自己的 surface，不统一铺页面 bg。
4. 优先一个样式入口。第13.4节代码为通用示意；本节细化为两种实现策略，**实施时择一**，不能让非auto的标准 scrollbar 属性与 Chromium 伪元素宽度/hover样式互相覆盖。

```css
/* 策略 A：最小实现，原生外观与可访问行为优先；宽度/hover随runtime */
.appScrollRegion {
  scrollbar-width: thin;
  scrollbar-color: var(--chat-scroll-thumb) var(--chat-scroll-track);
}
/* 深浅主题在真实主题真源设置，下列变量值不是新主题状态 */
/* light: thumb #738197; dark: thumb #65758c; track取当前容器背景 */
```

```css
/* 策略 B：若产品要求 WebView2 中固定10px及明确hover反馈，
   对工作台应用滚动节点使用Chromium伪元素，不同时应用策略A。 */
.appScrollRegion {
  scrollbar-width: auto;
  scrollbar-color: auto;
}
.appScrollRegion::-webkit-scrollbar { width: 10px; height: 10px; }
.appScrollRegion::-webkit-scrollbar-track,
.appScrollRegion::-webkit-scrollbar-corner {
  background: var(--chat-scroll-track);
}
.appScrollRegion::-webkit-scrollbar-thumb {
  background: var(--chat-scroll-thumb);
  border: 2px solid var(--chat-scroll-track);
  border-radius: 999px;
}
.appScrollRegion::-webkit-scrollbar-thumb:hover {
  background: var(--chat-scroll-thumb-hover);
}
.appScrollRegion::-webkit-scrollbar-thumb:active {
  background: var(--chat-scroll-thumb-active);
}
/* 不绘制上下箭头；滚轮、轨道、键盘操作仍用浏览器原生能力 */
.appScrollRegion::-webkit-scrollbar-button { display: none; }
@media (forced-colors: active) {
  .appScrollRegion { scrollbar-width: auto; scrollbar-color: auto; }
  .appScrollRegion::-webkit-scrollbar,
  .appScrollRegion::-webkit-scrollbar-track,
  .appScrollRegion::-webkit-scrollbar-corner,
  .appScrollRegion::-webkit-scrollbar-thumb,
  .appScrollRegion::-webkit-scrollbar-thumb:hover,
  .appScrollRegion::-webkit-scrollbar-thumb:active,
  .appScrollRegion::-webkit-scrollbar-button { all: revert; }
}
```

这些是样式接入模式，需在目标 WebView2/浏览器实测兼容性；不支持时退回跟随正确 color-scheme 的原生滚动条，不要求加新依赖。`appScrollRegion` 挂到现有真实节点。高对比优先系统绘制，检查按钮与corner都恢复，不把自定义强制色样式残留。现有 stable gutter / overflowAnchor 不变，样式完成后重新测滚动锚点。

### 14.4 Shell 修复步骤与责任

1. 核对 `WorkbenchHost`、WebView2 control、外围 Grid、ToolSplitterColumn 背景及尺寸：WebView2 必须 stretch 填满宿主，父容器没有白色 gutter；加载前/失败占位也使用主题背景。具体 WebView2 默认背景设置应在已锁定 SDK 提供的可用接口上实现，不套用 WPF 控件代码。
2. 在现有 `OnTheme` / 启动外观恢复路径统一生效；必要时提供纯外观适配函数（effective theme → 宿主背景、工作台 preferred color scheme、原生刷子），避免启动、切换、系统主题变化三个分支各写一份颜色。preferred scheme 只能改善 UA 默认控件，**不能替代 Web 应用主题更新**。
3. splitter 使用 `ThemeResource` 边界色；正常态1px、命中6px，拖动/键盘焦点强调，不新增白色 Border。保留 `OnSplitter*` 事件与 Foundation 分配算法；滚动条样式修复不改分栏比例。
4. 原生 `ScrollViewer` / `ScrollBar` 优先使用 WinUI 默认主题模板，检查是否有固定白色背景、旧样式或错误 RequestedTheme 覆盖；必要时按已引用 WinUI 版本的真实资源 key 覆盖刷子。勿用未核验 key 写一份“可编译示例”，也不整套复制 ScrollBar 模板破坏系统交互。
5. 原生系统滚动条和 Chromium scrollbar 的实现不相同，验收要求色温、对比度与轻量感一致，不强制像素级同模板。工具标签页、输出文件或日志视图如由 Web 承载，按自身可信范围套主题；第三方浏览器页由其内容决定，不注入应用 CSS。
6. 独立颜色映射/布局判断先在 Foundation 或现有无UI边界测试，再接入 MainWindow。可视控件与 DPI 需真实 Desktop 外部验证，CSS 与单元测试不能证明整窗已修复。

### 14.5 任务拆解、验收与关闭条件

- SCROLL-001-WEB：前端主题真源、真实滚动节点、textarea/代码/表格/portal；责任文件 `src/global.style.ts`、ThemeMode 与现有 `styles/*.styles.ts`，不修改客户端状态机。
- SCROLL-001-SHELL：宿主默认背景/加载态、effective theme、原生滚动区及splitter；责任 `MainWindow.xaml` / `.xaml.cs` 与必要的纯外观适配边界。
- SCROLL-001-QA：同窗口录制前后截图与交互，证据放 `temp/test-out`，结果登记 `Docs/Reports`。

| 验收 | 必须通过 |
|---|---|
| 深浅切换 | Chat轨道、corner、thumb、textarea、菜单与Shell原生滚动区无主题滞留；深色无白轨道 |
| 初始/恢复 | 首次加载、刷新、导航失败、Core重连、Shell重开不闪白；不丢草稿或改变执行 |
| 操作 | 滚轮/触控板/拖条/轨道/键盘仍可用，底部与顶部都可达；不改变viewport自动跟随与阅读暂停 |
| 布局 | 高度不足、长历史、长代码/表格、消息后加载图片无双层主滚动条，无横向白corner；gutter不因hover跳动 |
| 分栏 | 拖动splitter、收起/放大工具区、键盘调整后，滚动条与拖动区各自操作正确 |
| 可访问 | thumb合成对比度≥3:1；150/200% DPI和200/400%缩放可辨识；高对比恢复系统控件，减少动画不影响操作 |
| 范围 | 第三方网页不被注入样式，Shell与Web主题作用域明确；支持的浏览器/runtime分别验证 |

关闭条件：WEB、SHELL、QA均完成并给出新构建证据；只改CSS或只改Root.RequestedTheme不能关闭缺陷。本次仅登记与方案交付，状态保持“待实施”。
