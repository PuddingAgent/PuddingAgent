# 原生角色卡与聊天组件接入

2026-09-27，按用户确认“都改为原生 WinUI 3”及“Core 同进程直接函数调用”实施。

## 当前架构

```text
PuddingDesktop
  ├─ PuddingChat.WinUI → PuddingChat（BCL 接口与选择状态）
  └─ PuddingDesktop.Composition → PuddingChat + PuddingHost
       └─ InProcessChatClient → Core 应用服务（每次独立 DI scope）
```

聊天控件没有 Host、Runtime、EF 或 Controller 引用。项目构建门禁拒绝反向项目依赖。Core 仍独占角色配置、主会话、准入、运行、数据库与 canonical 事件；Desktop 仅持有选择、草稿和显示状态。实现中的 HTTP 聊天适配已按最新裁定删除，不保留双通道。

| 原生组件 | 已接入能力 |
|---|---|
| `RoleAvatarCard` | 工作空间中的 Agent 实例、头像/姓名回退、职责、冻结/启用状态、运行摘要与未读 |
| `MessageCard` | 可选择正文、标题/代码块、复制、错误结果、按需加载执行明细；未知事件用通用折叠项 |
| `ChatComposer` | 多行草稿、Ctrl+Enter、发送/重试、停止；按 workspaceId+agentId 隔离草稿 |
| `ChatWorkspace` | 自动加载本机工作空间/角色导航、主会话查询、滚动保持、主题资源、页面隐藏时暂停刷新 |
| `InProcessChatClient` | 固定本机用户上下文、角色/会话查询、统一 SubmitTurn/Cancel handler、投影 DTO 映射、关闭取消和排空 |

运行中的正文与工具信息来自 Core 投影，受理回执不表示执行完成；停止按钮只针对 Core 活跃 Turn，显示“已请求停止”后仍等待事实状态。工具项按 canonical Sequence 排序，保留 ToolCallId/TurnId/DelegationRunId，不按工具名称虚构配对。

主会话建立由 Core 的 `AgentMainSessionService` 完成；角色绑定、重定向、会话仓储和创建并发门控留在业务层。`ISessionRepository` 映射至已有 `InMemorySessionRepository` 同一单例。两个既有 Agent 投影服务直接读该仓储，移除内部自调用 HTTP。

Core 通过 `IDesktopServices` 回调 UI 的原有窄端口继续保留。原生聊天调用走线程池，使用独立 scope；停止先取消并等待调用退出，再释放 Host。客户端不要求账号，不读取或验证密码，不保存 token/cookie；重新启动后直接使用 `single-user` 本机上下文。Web 管理与远程认证保持原样；本机资源头像直接读受限 wwwroot 文件。

## 验证入口

- 独立状态测试：`Source/PuddingChatTests`，不加载 Host/UI。
- 独立原生窗口：`Source/PuddingChat.WinUITests`，只引用 WinUI 组件，使用确定性 IChatClient fixture。11 项断言覆盖自动加载去重、草稿、选择竞态、回执、取消、原生文本结构与释放。
- 真实组合测试：`Tests/PuddingNativeChat.IntegrationTests`，全新系统临时 DataRoot，初始化账号/角色后直接函数调用真实服务，验证无账号客户端读取、Web 匿名拒绝/错误密码拒绝/正确凭证访问、主会话、幂等受理、canonical 投影、内核重启与失效客户端；HTTP DiagnosticListener 断言聊天业务期间对 Core 的 HTTP 请求为零。
- 脚本：`TestScripts/test-pudding-native-chat.ps1`；整窗生命周期仍用 `test-pudding-desktop-kernel.ps1`。

测试初始化使用 Bootstrap 接口创建隔离数据；这是测试 fixture 准备，不是原生聊天业务通道。未读取 `D:\data` 凭据，未调用真实付费模型，未替换用户正在运行的产品。

首次接入实测（免登录修改前）：BCL 状态 **9/9**，原生窗口 **10 项断言**，真实 Core 直接调用组合 **1/1**，既有投影/会话日志定向回归 **8/8**。整库 Release 构建 **0 错误**（1694 条既有依赖/分析器等警告）。两个组件的反向引用负向构建均按预期失败。Desktop 构建目录和发布目录均完成真实进程启停/重启/回调/退出 smoke。原生窗口浅色材质及执行过程展开已通过实际截图检查。

原始记录：`temp/native-chat-final-checks.log`、`temp/native-chat-direct-core.log`（含 HTTP 探针自身有效性断言）、`temp/native-chat-projection-tests.log`、`temp/native-chat-solution-build.log`、`temp/native-chat-boundary.log`、`temp/native-chat-view-boundary.log`、`temp/native-chat-published-smoke.log`。临时构建/数据产物不属于交付源码；通过脚本可重现。

## 迁移边界

这次完成原生文字聊天与角色组件的真实接入，不宣称网页全部功能等价迁移：当前读取 Core 最近 20 条消息和有限的活动事件窗口，通过游标查询刷新（活跃 1 秒、空闲 4 秒）；完整历史分页、事件推送、附件预览/上传、语音、Live2D、模型/权限选择和审批卡仍待后续实现。正文支持原生标题、代码块、可选文本；其他 Markdown 保留原文。角色与模型管理仍通过独立管理页面完成。编码工作区适配器与 Agent Browser 不属于本轮聊天迁移。

草稿及未确认发送命令目前保存在当前 Desktop 会话内；跨进程恢复不在本轮验收范围。正在运行的真实模型取消需要产品会话另行验收；测试取消契约由原生 fixture 覆盖。

## 客户端免登录修订（2026-09-27）

按用户最新裁定移除 `IChatClient.LoginAsync`、原生账号/密码输入及登录状态。工作台 Loaded 自动初始化；重复加载共用初始化任务，失败可重试，刷新会重新读取工作空间（支持完成初始化后返回）。Composition 使用固定 `single-user` 本机主体，不访问账号仓储、不签发凭据；角色冻结、会话归属和统一受理检查保留。此身份与当前数据目录绑定，不是 Windows 账号映射，也不会传给 Web 认证中间件。

定向验证：BCL 状态 9/9、原生窗口 11 项断言、真实 Core 组合 1/1。组合覆盖未创建账号即可读取、消息幂等/投影、Core 重启后直接可用、旧客户端失效和聊天业务零 HTTP；同时验证 Web 匿名 401、错误密码拒绝、有效凭证可访问。发布包生命周期结果另见 `temp/desktop-no-login-smoke.log`。本次记录：`temp/desktop-no-login-tests.log`、`temp/desktop-no-login-ui.log`、`temp/desktop-no-login-publish.log`。

独立 Web 管理页仍要求登录；原有“初始化与配置”入口已拆为原生首次使用与“高级管理（Web）”。原生配置页迁移是后续工作，不能通过关闭 Web 认证来实现客户端免登录。

## 原生首次使用接入（2026-09-27）

独立组件先通过 15 项 BCL 测试与 12 项原生窗口断言，再接入 Core。新增原生工作空间/首角色表单，可选择已有模型，无需账号或口令；成功返回角色导航。Core 负责创建/复用与模型验证，界面只保存输入和选择。工作空间的 DB 提交与角色文件写入不是跨存储原子事务；部分失败保留工作空间，允许重试，不删除既有数据。

真实组合测试在创建任何 Web 账号前验证本机初始化、并发幂等、无效路径与无效模型拒绝、零 HTTP；随后确认账号数仍为零，再运行原有 Web 认证与消息投影回归。测试不访问用户 DataRoot、不调用付费模型。高级模型/角色编辑仍为后续迁移项，本轮只完成首次创建和已有模型选择。

最终复核：BCL 15/15；原生窗口 13 项断言（含实际 ContentDialog 加载）；真实 Core 组合 1/1；Release 发布成功，发布包启停、重启、UI 回调与退出租约释放 smoke 通过。证据：`temp/native-setup-final.log` 的状态/UI 结果、`temp/native-setup-core-final.log` 的最终组合结果、`temp/native-setup-publish.log`、`temp/native-setup-smoke.log`。初次补充断言曾误以为 fresh Core 没有默认工作空间，已改为验证预建空间名称保留，并独立覆盖 `native-new` 新建路径。

测试脚本关闭默认覆盖率输出并指定 temp/test-out，避免覆盖率报告落入源码目录。验证仅使用隔离 DataRoot，没有重启用户当前产品进程。

## 原生模型与角色常用配置（2026-09-27）

模型与密钥、当前角色设置已接入原生表单和 `IConfigurationClient`。独立控件验证后才接入 Composition。服务商读取不含密钥；新密钥仅用于保存，成功或控件卸载时清空输入，异常不回显服务商内容，编辑请求 ToString 脱敏。保留、替换、清除包含 ApiKeyRef 的一致处理，沿用 Core 配置文件存储。

Core 新增局部保存路径，在既有写锁内合并字段；价格、配额、其他模型与高级能力保持原值。角色局部更新复用既有主更新路径，但从锁内 manifest 保留 Smart 子代理路由；原 Web 清空/修改这些字段的行为通过回归继续保留。测试实际重新读取 SOUL 文件内容，避免把 Update 返回 DTO 未填充 Markdown 误当作丢失数据。

当前原生角色字段为名称、职责、启用、主模型（含恢复默认）、角色类型和系统提示词。完整 Markdown、Skill、权限审批、头像和价格/配额编辑仍为迁移边界。配置验证不包含真实付费 LLM 或真实服务商可达性验收。

验证结果：BCL 21/21、原生窗口 16 项断言、Core 配置保留回归 2/2、真实 Host 组合 1/1。覆盖表单校验、保存后清空密钥输入、读取脱敏、密钥三态、角色主模型选择/恢复默认、隐藏字段保留、配置重启持久化及业务零 HTTP。Release 发布和发布包启停/重启/回调/退出租约验证通过。记录：`temp/native-config-ui-final.log`、`temp/native-config-preservation-tests.log`、`temp/native-config-core-final.log`、`temp/native-config-publish.log`、`temp/native-config-smoke.log`。

## 聊天布局与流式活动接入（2026-09-27）

按 Web TurnContentStream 行为落地原生交错消息流：连续思考与正文分段、工具按调用 ID 归并输入/结果、失败状态与退出码可见；块更新保留展开状态，已有消息控件不会整列卸载。新增角色搜索、输入归属提示、角色草稿与阅读位置恢复、回到最新消息。活动窗口内已见事件在当前展示生命周期累积，尚未读取的早期轨迹由完整明细补齐；没有虚构思考、工具结果或耗时。

原生会话改用 `IConversationChanges` 直接等待 Core commit 信号，40 ms 合并刷新；停止每秒会话轮询。修复 Core 通知广播和先提交后订阅竞态。读取投影前捕获游标，保留并发提交的后续追赶机会。UI 无聊天 HTTP/JSON 序列化往返；仍复用 Core 查询投影，尚未实现全量 Web 增量 reducer。角色列表状态每 15 秒刷新一次。

验证：组件 26/26；实际 WinUI 窗口 20 项检查（通知更新、角色隔离、搜索、折叠实例保留等）；真实 Core/通知组合 3/3，包含广播、先提交后订阅、取消隔离、非法角色拒绝、停止宿主时取消订阅和零聊天 HTTP。日志 `temp/native-stream-final.log`。组件先独立验证，再接入 Composition；验证使用隔离数据目录，不读取用户模型密钥、不调用付费模型。尚未完成真实模型长时间流式会话与视觉人工验收。

最终控件复核仍为 26/26 + 20 项（`temp/native-stream-ui-final.log`）。整包 Release 发布尝试受工作区并行设置页 WIP 阻塞：`MainWindow.xaml.cs` 的 `Grid.IsEnabled` 产生 CS1061（`temp/native-stream-publish.log`）；未覆盖该 WIP，因此本轮不能宣称整包发布/发布包生命周期通过。真实 Core 组合内的启停与订阅释放验证已通过。

## 原生 Markdown 消息内容（2026-09-27）

新增 `PuddingChat.WinUI/MarkdownView.cs`，替代仅标题/代码围栏的手工拆行。依赖 Markdig 1.4.0，使用其 [AST 接口](https://github.com/xoofx/markdig/blob/56e9c238584a44a169f174c881855c049768634c/site/docs/advanced/ast.md)，不在 BCL 契约中引入依赖。Web 对照源为 `MarkdownBlock.tsx` 与 `IncrementalMarkdown.tsx`。支持原生富文本、代码复制、列表/任务、引用、表格及 http/https 链接；HTML 和不可打开的链接保留可读文字。图片仍为替代文本，未声称完成语法高亮/公式/附件。

流式正文更新复用 MarkdownView 和未变化的块；链接引用定义变化重新解析，思考区滚动容器保留阅读偏移，已在底部时跟随新文本。独立验证为 BCL 26/26，实际 WinUI 窗口 27 项检查，包含富文本结构、表格、未闭合代码围栏、稳定块保留、链接协议、引用更新与任务清单。日志 `temp/native-markdown-final.log`，控件构建零警告零错误。

上一轮并行设置页导致的整包编译阻塞已在当前工作区解除，本轮 Release 发布成功（`temp/native-markdown-publish.log`），未改动或提交他方设置页文件。发布仍有既存 Host/PRI 警告。
`temp/native-markdown-smoke.log` 与隔离发布包 report 证实启停、重启、UI 回调、数据目录保存和退出检查通过。未重启用户当前 Desktop，未访问用户模型密钥或运行付费模型。

## 原生工具树与子代理执行卡（2026-09-27）

对照 Web `ToolCallTree.tsx`、`DelegationRow.tsx`，扩展 BCL `TurnFlow`。工具先按调用 ID 配对，再按同 Turn 的显式父调用 ID 建树；结果先到不被后续 running 覆盖，非零退出码判为失败，无父/跨 Turn/循环关系保留可见根节点，遍历不递归。WinUI 按深度缩进（视觉缩进最多八层），保留节点展开状态。

委派按真实执行 `run_id` 分组，独立于可复用的 `sub_agent_id`；创建与终态复用卡片，多次执行不合并。主消息展示任务、精确状态和最多 300 字的结果摘要，不复制子代理内部完整轨迹；完整运行检查器仍待接入。Core `ProcessSummaryItem` 新增父调用 ID、委派执行 ID 和精确委派状态，保留 Web 原有汇总状态语义。无父 ID 的真实调用保持平铺；测试能力不等于所有生产者都已发出嵌套关系。

组件先独立验证再接入 Core。最新 BCL 31 项、原生窗口 29 项，Core 投影定向 2 项（`temp/native-activity-projection.log`）。检查包含同名并行、乱序结果、跨 Turn/循环关系、复用子代理多次执行，以及原生终态更新保留卡片。Core 组合/发布验证结果另记下方。
`temp/native-activity-final.log` 最终验证：BCL 31/31、原生窗口 29 项、真实 Core 组合 3/3。Release 发布及隔离发布包启停/重启/回调/目录保存/退出验证通过，记录 `temp/native-activity-publish.log`、`temp/native-activity-smoke.log`。没有真实付费模型或子代理任务运行验收。

## 原生图片附件与多模态提交（2026-09-27）

新增 BCL `ImageAttachments` 端口、角色附件草稿和冻结发送引用；WinUI 支持多选图片、移除、角色切换恢复与消息内按需预览。原生选择器按 [Windows App SDK 官方用法](https://learn.microsoft.com/windows/apps/develop/files/using-file-folder-pickers) 绑定 AppWindowId。选择后调用 Core `VisionArtifactStorageService.SaveAsync`，真实字节校验/尺寸检查/Artifact 路径仍由 Core 负责。数量上限来自 Core 的 MaxImagesPerTurn；部分批次失败保留已经添加的图片，不把用户源文件移动或删除。

发送直接构建 text/image parts；图片使用 original detail，纯图片消息没有合成提示词。重试沿用稳定消息 ID 与原附件快照，回执只清除本次发送的图片，新添加图片保留。选择角色变化时不会把导入结果写入另一个角色。预览仅解析当前工作空间的 Artifact，不接受 arbitrary URI；按需加载后在收起/卸载时取消解码并释放图片。未发送后移除的 Artifact 不擅自删除，沿用 Core 存储治理。

独立测试先于 Composition 接入：BCL 34 项、真实 WinUI 窗口 34 项（含图片草稿隔离/恢复、发送引用与 PNG 实际解码），日志 `temp/native-images-components.log`、`temp/native-images-ui-final.log`。真实 Core 组合 3/3（`temp/native-images-core.log`）：导入后 canonical Artifact、跨工作空间拒绝、错误图片 MediaInvalid、不存在角色拒绝、图文/纯图 typed parts 持久化、重启预览与零聊天 HTTP。初次组合构建遇到并行本机身份抽取的临时编译状态，待其完整后复测通过，未覆盖该工作。

当前尚未验证系统选择器实际点击和真实模型视觉理解；本轮不调用付费模型。相机、剪贴板、拖放、Markdown 内生成图与一般文件上下文入口仍是后续工作；不把 PNG 解码测试外推为全部系统图片编解码器验收。
`temp/native-images-publish.log` 的 Release 发布通过，`temp/native-images-smoke.log` 的隔离发布包启停、重启、UI 回调、目录保存与退出验证通过。未重启用户现有 Desktop，也未改动用户数据目录。

## 异常终态执行记录恢复（2026-09-27）

审计发现：失败/取消可能没有助手 ChatMessages 行，旧明细查询只接受 agent 行及 turn.completed；原生输入卡片也不提供明细入口。因此离开会话后，失败前的思考/工具活动无法恢复。另一个边界是 run.lease_lost 被默认映射为 succeeded。

Core 明细查询现支持从已接受的用户输入定位异常终态，按会话、Turn 和根 Run 读取全部活动，并包含同 Turn 的子代理生命周期；子代理正文与其他 Turn 的事件不混入。成功回复仍走助手卡片。Native 为异常输入卡提供独立执行展开区，保留原始请求文本；取消不依赖 errorMessage 才可见，租约丢失显示失败。不新增 HTTP 调用或另一套执行状态机。

验证：BCL 34/34、实际 WinUI 窗口 38 项（`temp/native-terminal-ui.log`）；真实 Core 组合 3/3（`temp/native-terminal-core-final.log`），其中覆盖成功/失败/取消/租约丢失四种终态、80 条思考记录及根/子执行过滤，并保持聊天零 HTTP；终态投影定向 4/4（`temp/native-terminal-projection.log`）。构建存在既有 Core 分析器警告。未部署替换当前 Desktop、未运行真实模型、未改动 D:\data。活动快照 64 条窗口的运行中增量追赶仍待后续实现，不能将本次终态完整恢复视为该缺口已经关闭。

## 活动 Turn 完整恢复与增量呈现（2026-09-27）

新增独立 BCL `ConversationActivity` 与 `IConversationActivity`，先完成叶组件验证，再接入 WinUI 与 Core。Core `ReadActivityAsync` 按提交游标读取最多 256 条 canonical 事件，返回强类型呈现项；Composition 直接函数调用。窗口首次读取活动 Turn 时固定快照游标并分批恢复全部活动，随后按已消费游标追赶。恢复期间的新提交留给下一轮增量，不因持续输出而扩大重放上限。纯文本/思考/工具活动不再执行整份 GetConversation 投影，生命周期和未知事件仍从 Core 权威投影恢复。

呈现 reducer 校验会话/Run/Turn/游标并按事件 ID 去重；Core 校验会话归属和根 Run，委派生命周期可纳入，子代理正文不并入父输出。快照与增量读取串行化；首次窗口验证发现旧角色忽略取消时会占住读取锁，已通过可取消等待与选择代次丢弃修复。快照/事件读取竞态会重新读取并重放，不静默退回截断活动；新会话订阅使用选择生命周期，避免被旧订阅取消。

验证：BCL 37/37，原生窗口 41 项（`temp/native-delta-ui-final.log`），其中断言纯流式增量不增加 GetConversation 调用、终态回到权威快照、重放竞态重试及晚到旧角色拒绝。真实 Core 组合 3/3（`temp/native-delta-final.log`）：600 条思考跨三页恢复、固定上限期间新提交在增量出现、父/子输出隔离、错误角色拒绝、生命周期重新读取，继续验证聊天零 HTTP。Core 构建有既存分析器警告。未运行真实模型长会话或重启用户当前 Desktop；没有实测耗时/内存收益，不声称零查询、零分配或已完成历史分页/虚拟化。

## 原生聊天历史翻页（2026-09-27）

新增 BCL `HistoryCursor/HistoryPage/IConversationHistory` 与 `ChatSelection.PrependHistory`，先完成独立状态验证，再接入窗口与 Core。顶部原生按钮按需读取更早消息；Core 复用既有消息投影和过滤规则，按 `CreatedAt + Id` 的严格小于游标取候选记录，展示页最多 20 条，不使用 offset。历史查询不返回活动 Run，也不推进实时事件游标。Composition 继续直接函数调用。

已加载历史在新快照到来时保留，当前版本覆盖重复消息；信封的 canonical message identity 单独提供，用于去除跨页的不同数据库行。新消息突发导致最新页与已加载区间不重叠时重设回填游标，保留补齐缺口的入口。加载前后使用消息阅读锚点，显式加载旧页不会因为原来接近底部就自动跟随最新；角色/主会话变化及过期页被拒绝，失败后按钮可重试，到末尾隐藏入口。

验证：BCL 42/42、原生窗口 43 项（`temp/native-history-final.log`），包含追加旧页、实时刷新保留历史和阅读锚点计算；真实 Core 组合 3/3（`temp/native-history-core-final.log`），包含 65 条同毫秒消息顺序、分页期间插入新消息、完整遍历/末尾、错误会话与角色拒绝，以及真实信封跨页去重，聊天调用仍为零 HTTP。初次组合构建遇到他方配额服务的临时编译错误，待该文件修正后复验通过，未改动其代码。

本轮没有发布替换用户当前 Desktop，也未访问 D:\data。手工滚动视觉验收、角色切换后自动恢复深层历史阅读位置、长会话控件虚拟化仍待完成；分页允许继续读取历史，不代表已验证大规模会话内存表现。

## 角色切换后的深层阅读位置恢复（2026-09-27）

新增独立 BCL `ReadingBookmark`，按会话绑定消息锚点、创建时间和消息内偏移；每个角色只保存轻量书签，不缓存旧 UI 或正文。窗口切回角色后从最新会话按需读取历史直到目标消息，并在最终布局后恢复偏移。主会话更换回到最新；目标时间范围已越过或无更早页时停止读取。活动输出的临时锚点没有历史消息时间，不会触发无关历史搜索。

恢复期间切换角色会取消等待并拒绝晚到页；恢复未完成时不把空视口写回原书签。继续复用已验证的进程内历史读取端口，没有修改 Core 接口或新增网络调用。

验证：BCL 44/44、实际 WinUI 窗口 47 项（`temp/native-bookmark-final.log`）。独立测试覆盖同毫秒边界、目标缺失、会话轮换、最新/活动锚点；窗口以长消息验证角色切换后重新加载旧页和实际 24 DIP 滚动偏移恢复（容差 2 DIP），并验证中途切走、晚到结果拒绝及再次恢复原书签。未重启用户 Desktop，未修改 D:\data。书签尚不跨应用重启持久化；控件虚拟化和大规模长会话性能仍待完成。

## 消息卡控件虚拟化（2026-09-27）

独立 `VirtualTranscript` 采用 WinUI ItemsRepeater、StackLayout 与 IElementFactory，数据行保持身份，视口附近才创建 MessageCard/TurnContentView。离屏更新只修改数据，回收时取消卡片的明细等待并释放控件；非视觉 MessageViewState 保存工具/思考展开状态、完整明细与图片展开状态。已解码图片仍随卸载释放。没有增加 HTTP 或修改 Core 执行状态机。

先通过独立 1000 消息控件验证，再接入 ChatWorkspace。接入回归发现同时调用 StartBringIntoView 和 ChangeView 会覆盖历史消息内偏移，修复为目标行实现/布局后只提交一次 ChangeView。当前真实原生窗口验证包含控件创建数量小于 80、滚动后离屏控件释放、离屏数据更新不创建控件、实际末尾贴底、离屏消息 24 DIP 定位以及工具展开状态恢复；原有历史翻页和角色深层书签回归继续通过。

验证：BCL 44/44、真实 WinUI 窗口 53 项（`temp/native-virtual-final.log`），原生组件构建零警告零错误。Desktop Release 发布成功（`temp/native-virtual-publish.log`，Core 有既存分析器警告）。依据 [Microsoft ItemsRepeater 文档](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/items-repeater) 使用虚拟化布局和自定义元素工厂。此处是重新创建控件而非控件池复用；已加载消息/事件数据仍驻留内存，单 Turn 内行为块尚未虚拟化，未测量真实模型长会话性能。

隔离发布包验证（`temp/native-virtual-smoke.log`）通过：Core DLL 同进程加载、UI 回调、内核重启、数据目录保存及退出后租约释放。没有替换用户正在运行的 Desktop，也未修改 D:\data。

## 原生图片粘贴与拖放（2026-09-27）

对照 Web IntentConsole 的图片优先粘贴与文件拖放行为，新增 NativeImageTransfer 独立组件。先完成 Windows DataPackageView 的 StorageItems/Bitmap 读取测试，再接入 ChatComposer 的 TextBox.Paste、显式粘贴图片按钮、DragOver/Drop。普通文字粘贴仍由 TextBox 处理；拖放使用 Copy 与异步 deferral，不移动原文件。参考 [Microsoft TextBox.Paste](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.paste) 与 [拖放文档](https://learn.microsoft.com/en-us/windows/apps/develop/data/drag-and-drop)。

剪贴板编码流先检查 64 MiB 上限，识别格式后按原字节暂存，调用既有 Core 导入端口，并在 finally 清理唯一暂存文件。StorageItems 只接受支持的图片文件，混合不支持文件会在导入前报告错误。原生层不替代 Core 的字节/尺寸校验，不重编码原图。ChatWorkspace 在异步数据读取前捕获角色，整个批次占用附件入口，延迟数据到达后仍归属原角色；已成功的附件和正文不会因后续错误被清空。

验证：独立阶段 BCL 44 项、原生窗口 59 项（`temp/native-transfer-leaf.log`）；接入后 BCL 44/44、原生窗口 63 项（`temp/native-transfer-final.log`），构建零警告零错误。新增检查覆盖原字节一致、源文件保留、成功/失败暂存清理、不支持文件拒绝、Windows 延迟数据提供者期间角色切换、文字草稿保留和禁用入口。自动化没有读写系统剪贴板；实际 Ctrl+V、系统右键菜单和 Explorer 鼠标拖放仍待手工验收。

Desktop Release 发布通过（`temp/native-transfer-publish.log`，Core 有既存警告）。本轮没有替换运行中 Desktop、调用真实模型或改动 D:\data。

## 折叠执行活动的按需渲染（2026-09-27）

检查发现 TurnContentView 会为所有折叠工具提前生成输入和输出 Markdown；消息级虚拟化无法消除当前可见长 Turn 内的隐藏内容开销。现改为展开时根据最新 FlowBlock 构建正文，收起后清空 Content；折叠期间只更新数据、状态标题和层级缩进。事件处理器只捕获稳定 key，不捕获首帧旧内容。思考仍默认展开，已有流式显示和正文顺序不变；不调整 Core、事件读取端口或业务状态机。

原生窗口检查以 500 个工具调用、每个 10000 字符输入验证折叠内容不存在，展开构建、收起释放、隐藏状态更新与重新展开读取最新结果；另验证思考默认可见。BCL 44 项与原生窗口 69 项通过（`temp/native-lazy-activity-final.log`）。此项只减少折叠内容控件，标题数量和 FlowBlock 数据仍完整保留；不是块级虚拟化，也没有真实模型耗时/内存测量。本轮未替换用户 Desktop 或修改 D:\data。

## 长 Turn 渐进展示与阅读位置（2026-09-27）

新增 BCL FlowWindow，独立测试通过后接入 WinUI。参照 Web TurnContentStream 的最近内容块窗口，原生默认显示最新 40 块，每次显式展开更早 24 块，显示尚未展开数量；内容块包括思考、正文和工具，顺序不变。展开后绑定稳定 key，后续新增事件不会把已经展开的记录挤出范围。TurnContentView 复用仍可见控件，展开前后按可见块相对 ScrollViewer 的坐标补偿偏移。消息卡主内容、异常执行明细和活动 Run 分别保留非视觉范围，支持回收后恢复。

验证：BCL 47/47，原生窗口 75 项（`temp/native-flow-window-final.log`），构建零警告零错误。新增检查覆盖初始 40 块、逐页到开头、追加时固定范围、缺失锚点/空集合、控件身份复用、实际位置保持（容差 2 DIP）、回收状态恢复和全部历史可达。原有 500 工具懒渲染测试改为主动展开全部后执行，仍覆盖完整量级。本轮不改 Core、不新增 HTTP、不替换运行中 Desktop。全量 FlowBlock 数据仍参与归并；渐进展开不是视口虚拟化，主动展开全部或单个巨型文本块的开销仍待性能验收。

## 原生审批接入缺口核查与方案（2026-09-27）

本轮为源码核查与设计，未新增产品代码、未运行测试或变更运行数据。全仓 C# 审批事件引用核查及 SessionApprovalDecideTests 表明：Web 会话决定端点缺请求生产者，结果写入也没有接到 Runtime 的执行续行；Runtime NeedHuman 指向 /authorize 的人工工具授权路径，DeferredDependency 是不同状态。票据 Save/Get/List 端口不提供人工决定的原子转换保证。

新增 `Docs/Features/Desktop-Native-Approval-Integration-Design-2026-09-27.md`，状态 Proposed。列出真实证据、固定待处理区域、精确身份/操作绑定、Core 唯一决定真源、A1–A5 组件门禁及七项闭环验收。原生审批继续记为未接入；不以控件、事件追加或 404 接口回归冒充工具获准后恰好执行一次。下一步实施必须从 Core 真实暂停/恢复与原子决定边界开始，不能直接包装 Controller。

## A1 审批转换叶组件（2026-09-27）

新增独立 `Source/PuddingApproval` 与 `Source/PuddingApprovalTests`，未登记解决方案、未改 Host/Runtime/Composition。组件通过 BCL 合同表达执行身份与操作/策略指纹，使用 IApprovalStore.CompareExchangeAsync 转换版本；重复 decisionId 只返回原回执，消费仅在硬边界通过、绑定一致、未过期且已批准时生效，重复消费不返回 Applied。DispatchUnknown 明确阻止不确定外部执行的自动重跑。

核对 ADR-091 后修订原生审批方案：原子许可不能保证任意外部副作用 exactly-once，消费后崩溃需持久派发状态与对账。组件没有创建 AwaitingHuman 请求的生产入口，不能把硬拒绝或 DeferredDependency 变成人工放行；Core 接入必须负责这些前置约束。

独立测试 8/8（`temp/native-approval-state-final.log`）通过，包含 32 路竞争决定和消费、回执重放、身份/操作/策略变化、硬边界、到期、拒绝、未知执行以及仅依赖 System 程序集。测试仅使用内存 CAS store；生产事务存储、outbox、故障恢复和真实执行尚未验证，A1 生产门禁与 S5 接入保持未完成。没有读写 D:\data 或重启 Desktop。

## A1 SQLite 状态与 outbox（2026-09-27）

新增独立 PuddingApproval.Sqlite 及其测试工程，未修改 Host/Runtime。SQLite 事务同时保存审批记录与对应版本的 outbox；执行身份唯一约束防止不同审批 ID 重复创建相同 invocation 的许可。CompareExchange 检查版本及不可变身份，失败不追加事件。事件读取是有界、至少一次语义，消费者需先完成持久去重再确认，尚无运行消费者。

独立测试 6/6 通过（`temp/native-approval-sqlite-final.log`）：重新打开数据库后状态/未确认事件存在、两个实例 16 路决定/消费各只有一个成功、触发器注入 outbox 写入故障时状态更新和请求创建均回滚、重复执行身份拒绝、消费后重新打开不可重复消费，以及程序集依赖断言。默认 SQLite 原生依赖触发 NU1903，已显式固定 bundle 2.1.13 后复验，无关闭审计。测试使用系统 Temp 并清理，不触碰 D:\data。强杀/断电、Core 消费者与真实执行恢复仍待验证；本轮不是审批 UI 闭环验收。

## A4 原生审批卡独立交互（2026-09-27）

新增 PuddingChat 审批展示/提交合同与 WinUI ApprovalCard。卡片只提供 Core 声明的单次允许/拒绝选项，展示参数、描述、有效期和真实风险说明；没有风险信息时显示未提供。提交使用固定 decisionId，失败只重试原决定，不改变决定理由；不提前标记批准。禁用过期、依赖等待及处理中交互，拒绝跨请求回执和同版本状态修改，忽略旧版本，释放控件时取消异步等待。

验证：BCL 47/47、原生窗口 86 项（`temp/native-approval-card-final.log`），构建零警告零错误。新增覆盖真实窗口加载、双击、失败/重试同一提交、权威状态、过期、Core 能力限制、依赖等待、旧版本、跨角色、同版本冲突及 Dispose 后晚到结果。测试使用可控 IChatApprovals 客户端，没有接入生产 Core，没有在产品 ChatWorkspace 显示尚不具备真实执行续行的按钮。固定待处理区域、Core 请求投影和执行恢复仍待完成。

## 工具准入状态保真（2026-09-27）

NeedHuman、DeferredDependency 和终局拒绝现在在执行器出口保持区别；进程内调用合同与 SkillResult 传递原始状态/退出码，流式工具事件增加 status。原生 TurnFlow 不再将人工决定或依赖等待一律覆盖为失败。未改变权限、熔断、Run 调度或工具执行次数，没有新增 HTTP 调用。

验证：Runtime 定向回归 174/174（工具基础设施、调用适配、执行上下文，含新增 3 个准入分类案例及真实 facade 透传断言），BCL 聊天测试 50/50。日志为 temp/native-admission-runtime-final.log 与 temp/native-admission-status.log。构建有既存分析器警告，无编译错误；本轮未运行原生窗口或真实模型端到端审批验收。请求生产者、持久暂停/恢复和产品审批卡接入仍未完成，不能视为审批闭环交付。未改动 D:\data，未重启 Desktop。

## 消息执行明细生命周期（2026-09-27）

修复 MessageCard 的异步明细竞态：旧实现将 loaded/loading 保存在构造器闭包中，Run 更新只清理非视觉状态，既不能重新加载，也可能让旧 Run 的回执写入新视图。现由卡片持有每次加载的取消源；Run 变化取消等待、清空缓存与旧控件并重新允许加载，Dispose 取消等待且不缓存晚到结果。同消息 ID 是控件更新前提，明细响应必须匹配消息 ID；已存在的当前 canonical 事件优先于较早明细快照，历史缺失事件仍可补入。

验证：BCL 50/50、原生窗口 93 项，构建零警告零错误（temp/native-message-details-final.log）。新增 7 项覆盖并发去重、旧 Run 等待取消/新 Run 立即加载、晚到旧回执隔离、当前快照保留、虚拟化回收缓存恢复、Dispose 后回执不入缓存、跨消息响应拒绝后可重试。最初测试按思考卡数量断言未考虑连续思考合并，已改为核对显示正文，并复验通过。本轮为独立 WinUI 组件验证；未替换运行中 Desktop，未修改 Core 或 D:\data。

## 输入区窄布局（2026-09-27）

ChatComposer 原来的单行四按钮在窄聊天区会超出边界；附件名固定最大宽度也不能保证移除按钮可见。现将工具栏拆为附件与消息操作两组，组件宽度低于 480 DIP 时上下排列，宽窗口恢复同一行。附件名占剩余空间并省略，移除按钮保留自身宽度，提示显示完整文件名。缩放只调整布局，不重建输入控件或丢弃草稿/附件。

验证：BCL 50/50、原生窗口 97 项，零构建警告/错误（temp/native-composer-layout-final.log）。新增真实布局检查覆盖 320 DIP 下含“重试原消息”的全部按钮边界、200 字符附件名、900 DIP 回到单行、360 DIP 缩回时保留草稿/附件/按钮可用性。首次草稿断言误把 WinUI 换行规范化当成变化，改为比较缩放前后实际文本后通过。未修改 Core/运行数据，未部署到当前 Desktop；极端字体缩放和整窗最小尺寸验收仍待完成。

## 原生代码块组件与流式阅读（2026-09-27）

对照 Web MarkdownBlock.tsx 的代码块与复制入口，新增 CodeBlockView：语言标签、可选择的等宽文本、复制最新代码与自动换行开关。MarkdownView 在同位置同类型代码块更新时复用组件，避免每个流式片段都重建代码 ScrollViewer；无换行模式继续横向滚动，开启换行后由原生 TextBlock 布局。代码内容始终作为文本，不执行 HTML/XAML。

验证：BCL 50/50、原生窗口 102 项，零构建警告/错误（temp/native-code-block-final.log）。新增 5 项覆盖未闭合围栏的完整原文、真实横向滚动 100 DIP 后流式更新保持位置、切换换行、追加后保留控件/换行偏好和最新完整文本、恢复无换行。测试未读写用户剪贴板，复制处理器从当前 Code 属性取值；没有宣称系统剪贴板手工交互已验收。语法高亮、公式和完整视觉验收仍未完成，未修改 Core 或部署当前 Desktop。

## 展开活动的增量内容与目标核对（2026-09-27）

新增 ActivityContentView，替换 RenderDisclosure 每帧重建 StackPanel/MarkdownView 的路径。正文、输入、输出按稳定槽位更新，未变化文本不重新解析；输出移除时清理旧槽位；收起仍释放完整内容，保留既有懒加载规则。嵌套 CodeBlockView 的流式复用和换行选择因此在工具活动中也生效。

BCL 50/50、原生窗口 108 项通过，构建零警告零错误（temp/native-activity-content-final.log）。新增 6 项验证实际窗口的容器/内容身份、代码偏好、终态、失效输出清理及思考正文更新。源码核对、证据范围与剩余功能汇总到 Desktop-Native-Chat-Completion-Audit-2026-09-27.md；未进行 Core 集成复验或产品部署，目标未完成。

## 子代理检查器独立组件（2026-09-27）

核对 Web SubAgentActivityDock、subAgentReducer 与 Core SubAgentRunController 后，新增精确角色/父会话/Run 绑定的只读检查合同和 SubAgentInspector。任务、执行活动和完整结果分区显示；真实状态/时间/统计及归档降级提示保留，刷新失败标示旧快照，关闭取消等待并忽略晚到结果。主消息摘要保持独立，不把子代理内部过程混入父会话。

BCL 50/50、原生窗口 115 项通过，零构建警告/错误（temp/native-subagent-inspector-final.log）。新增 7 项涵盖并发去重、1000 字结果不截断、降级提示、失败回退、跨 Run/角色拒绝、关闭晚到结果。当前只是独立组件完成，未登记生产 Core 归档适配或聊天入口；后续精确接线门禁见 Desktop-Native-SubAgent-Inspector-2026-09-27.md。本轮不修改 Core/Host 或运行数据。

## 子代理详情直接调用接入（2026-09-27）

Composition 的 InProcessChatClient.SubAgents 直接读取 ISubAgentRunStore，前置校验角色/父会话，后置核对 Manifest 的 workspace/parentSession/run；没有 HTTP 或 Controller 转发。原生显示归档真实思考/正文预览、工具输入/结果/错误与未知事件，截断标记显式保留；完整结果取归档 Output。FlowBlock 保留 DelegationExecutionId，委派卡以精确 RunId 打开原生对话框，角色切换关闭并取消旧检查器；缺失精确 ID 或客户端能力时不提供猜测入口。

验证：BCL 50/50、原生窗口 118 项，控件构建零警告零错误；真实 Core 组合 3/3，既有零 HTTP 探针覆盖新增真实归档往返，包含错误结果保留、未知事件、截断提示与跨角色/父会话/运行隔离（temp/native-subagent-entry-final.log、temp/native-subagent-core-final.log）。Core 构建有既存警告。当前实现通过 Core 现有整份归档读取，不宣称磁盘分页或超大归档性能达标；未调用真实模型或替换运行中 Desktop。

## 原生代码语法高亮（2026-09-27）

新增展示层 ColorCode.WinUI 2.0.15（MIT）依赖，使用 RichTextBlockFormatter.FormatInlines 在既有 TextBlock 上着色，不引入 WebView、Roslyn 或 Core 引用。语言别名支持 cs/csharp、js、ts、py、pwsh/ps1 等；实际语法集由库提供。原始代码独立保存供复制，格式化后逐 Inline 对照全文，内容不一致或格式化异常回退完整纯文本。未知语言、高对比度及超过 16,384 字符同样保留全文并跳过着色。该阈值是保守实现上限，不是长期性能验收结论。

按 ActualTheme 选择浅/深色样式；系统支持时订阅高对比度变化。实际非打包测试宿主订阅该 WinRT 事件返回 0x80070490，现捕获此特定 COM 失败以防窗口崩溃，仍在加载、主题或代码变化时重新检查高对比度。不能宣称这个宿主已完成高对比度即时切换验收。

验证：BCL 50/50、原生窗口 125 项，零构建警告/错误（temp/native-code-highlight-final.log）。新增 7 项覆盖 C# 彩色 Inline、实际浅/深主题、追加时换行与原文保留、JSON/JavaScript/Python/PowerShell、未知语言、20K 完整纯文本回退与 CRLF 无损。既有代码横向阅读和活动嵌套测试继续通过。未修改 Core 或部署当前 Desktop。

来源：[官方仓库](https://github.com/CommunityToolkit/ColorCode-Universal)、[WinUI 包与许可](https://www.nuget.org/packages/ColorCode.WinUI/2.0.15)、[格式化器源文件](https://github.com/CommunityToolkit/ColorCode-Universal/blob/e6c2701c365a7a91d74d7ef38a6b60075008ce94/ColorCode.UWP/RichTextBlockFormatter.cs)。采用包元数据所声明的版本和 MIT 许可，未复制上游源码到项目。

## 原生文本文件上下文（2026-09-27）

输入框新增文件选择、快照预览和移除，按角色保存附件草稿；发送冻结内容，回执仅清理已受理附件。Composition 直接把 SubmittedText 交给现有 ISubmitTurnHandler，不新增 HTTP。支持 UTF-8/带 BOM 的 UTF-16，单文件 256 KiB、8 文件、512 KiB 合计，并遵守 Core 100,000 字符限制；拒绝超限/乱码，不截断。旧 Web 一般附件按钮本来未实现，本项是新增文本上下文，PDF/Office 提取仍待实现。

验证：57 项逻辑、132 项原生窗口、3/3 Core 组合测试通过；最终原生构建零警告/错误，Core 有既存警告。真实 Core 测试删除源文件后提交并重试，确认历史正文与快照一致、图片共存、零 HTTP。日志 temp/native-text-files-final.log、temp/native-text-files-core.log。没有部署运行中 Desktop。设计与剩余门禁见 Docs/Features/Desktop-Native-Text-Context-2026-09-27.md。

## 审批操作快照（2026-09-27）

PuddingApproval 新增有界、确定性的操作指纹和不可变工具/参数/定义/目录快照，ApprovalRecord 强制携带快照，决定/消费前核对。SQLite Create/CAS 强制一致与不可变，原始快照和状态/outbox 同事务保存。18 项逻辑与 7 项 SQLite 测试通过。没有 Host/Runtime 接线或产品发布；真实暂停/恢复尚未完成。新查明 worker 并发槽、watchdog、LeaseLost 重排队及内存 ResumeAnchor 的限制，具体后续接入门禁已写回原生审批设计，避免通过 UI 重发整轮伪造恢复。

## 完整 Desktop 装配检查（2026-09-27）

审批快照代码已提交 097bcf6。随后运行 `dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel --no-restore --nologo`，当前共享工作树构建失败（134 warnings、2 errors；temp/native-chat-desktop-build.log）。根因是并行 DS-07 尚未提交的 DesktopSkillPackageSettings.cs 第 42 行 `SkillPackageUpload` 同时命中 Foundation 和 Platform 类型（CS0104），继而导致 ISkillPackageSettings.UploadAsync 未实现（CS0535）。未修改该并行任务文件；此记录仅反映该次共享工作树快照，不能替代后续修复后的产品构建验收。原生聊天与审批各自定向测试通过的结论不等于完整 Desktop 已通过或已发布。

## 原生聊天整页自适应（2026-09-27）

ChatWorkspace 在可用宽度不足「宿主偏好侧栏宽度 + 520 DIP」或宿主隐藏侧栏时，让消息区占满剩余宽度，在标题处显示“角色”按钮，通过 WinUI Flyout 访问原有工作空间/角色/设置导航。窄于 520 DIP 时页边距缩至 12 DIP。恢复宽屏后使用宿主原偏好宽度；复用同一角色控件、草稿和会话状态。选择角色关闭面板并回到输入框，离开工作台或释放控件也关闭弹出层。

实测发现关闭中的 Flyout 被移除 Content 后，再复用同一 Flyout 可能不触发下一次 Opened；现为每次紧凑布局创建新的弹出容器，内部导航控件继续复用。另将选角后的输入焦点恢复放到异步读会话之前，避免迟到读取抢走后续用户操作焦点。

验证：57 逻辑测试、139 原生窗口检查通过，最终构建零警告/错误（temp/native-workspace-layout-final.log）。新增 7 项覆盖 320 DIP 整页、消息视口高度、实际弹出层、侧栏返回与草稿、宿主隐藏后的入口、选角关闭、离开工作台关闭和宽度偏好。测试以真实 Opened/Closed 事件同步；IsLoaded 不能作为 Flyout 关闭证明。未更改 Core、部署产品或完成系统级缩放/IME/主题视觉矩阵。

## 原生混合文件拖放与粘贴（2026-09-27）

ChatComposer 的拖放及 Ctrl+V 现接收 StorageItems 中的图片与文本/代码文件；普通文字与位图仍沿用原生文字粘贴及已有图片导入。ChatWorkspace.FileTransfer 在延迟数据源解析前捕获角色，校验数量/文本后导入图片，批次全部成功才更新草稿，失败保留原附件；不递归读文件夹、不把晚到附件写入新角色。草稿原子性不等于 Core Artifact 存储事务，已成功写入但最终未引用的图片生命周期仍归 Core。

验证：57 项逻辑、145 项 WinUI 窗口检查通过，零构建警告/错误（temp/native-file-transfer-final.log）。新增 6 项用真实 WinRT DataPackage/StorageFile 测混合导入、二进制拒绝且不调用图片导入、延迟 provider 的角色隔离与原角色快照归属、文件夹拒绝、禁用状态。没有操作用户系统剪贴板或真实资源管理器鼠标拖动；没有更改 Core 或发布 Desktop。

## 完整产品原生聊天装配通过（2026-09-27）

上次 SkillPackageUpload 重名已由并行设置任务修复。本轮扩充 MainWindow.RunKernelSmokeAsync：在显式隔离 DataRoot 内通过进程内客户端创建工作空间与角色，重新挂载真实 ChatWorkspace，校验 IsLoaded、Visible、角色列表/选择、正文草稿和文本附件。Core 重启后必须换成新聊天客户端并重新读取保存角色；外部脚本要求报告的 nativeChatMounted / nativeRoleAndFileDraft / nativeChatRecreatedAfterRestart 全为 true，且验证 PID 与退出后的数据目录租约释放。

执行 `pwsh -NoProfile -File TestScripts/test-pudding-desktop-kernel.ps1` 成功。最终构建 0 errors / 146 warnings（依赖/既存编译和 PRI 资源警告；不称零警告）。产品进程 PID 30028，加载实际输出目录的 PuddingHost.dll，UI 回调、重启、目录保存、聊天挂载和重建标记全部通过，外部脚本退出码 0。证据：temp/native-chat-product-smoke-final.log；temp/test-out/kernel-winui-8751da31ad504dfc9d445cf211a7625d/report.json。

此次使用共享工作树当时的完整产品构建，包含并行任务的现状，不宣称是单一聊天 commit 的隔离构建。未读写 D:\data、没有模型调用、没有发布替换用户 Desktop；健康探针沿用 /health/ready，角色和草稿路径是直接函数调用。发送/流式真实模型、审批恢复、完整视觉矩阵仍未通过产品验收。

## 原生数学公式（2026-09-27）

原 Web MarkdownBlock 使用 remark-math/rehype-katex。本轮以 Markdig Mathematics 解析，使用独立 MathFormulaView 原生显示，不加载 WebView 或启动 TeX。行内公式使用 RichTextBlock 的 InlineUIContainer，块公式使用可横向滚动的 Image。强调中的公式可显示，链接标签内保留 LaTeX 文本；代码围栏保留字面量。追加正文复用未变化公式控件，未闭合行内公式显示原文，闭合后转换。

渲染在后台串行执行，避免共享字体/解析状态并发；源长度最多 4096、命令分隔符最多 128、组深度最多 64，逻辑位图尺寸最多 2048×1024，以 2 倍像素绘制。加载/主题变化触发重绘，取消与代次阻止旧结果更新卸载控件。语法错误、超限和渲染异常显示可选原文；上下文菜单复制原始 LaTeX，不复制解析器规范化结果。高对比度加载/重绘回退原文，尚未验收实时高对比度通知和完整辅助技术。

依赖：[CSharpMath](https://github.com/verybadcat/CSharpMath) 的 [1.0.0-pre.1 包](https://www.nuget.org/packages/CSharpMath.SkiaSharp/1.0.0-pre.1) 支持当前 Core 使用的 SkiaSharp 3.119.2；pre.2 要求更高 Skia 版本，未为单个控件扩大升级范围。这是预发行库，保留语法回退，不声称与 KaTeX 全量对等。库内字体有各自许可证，发布时需保留依赖许可。

验证：先独立控件后接入 Markdown，最终 `test-pudding-native-chat.ps1 -SkipCoreIntegration` 通过 57 逻辑/160 原生窗口检查，构建零警告/错误，日志 `temp/native-math-final.log`。新增 15 项覆盖分数/根号/求和渲染、位图尺寸、浅深主题实际字形像素、控件复用/回收、无效语法/深度/长度回退、行内/块/强调公式、流式未闭合转闭合及代码围栏。测试等待真实渲染/卸载结果，不能以 IsLoaded 瞬时值代替事件处理完成。本次未重新执行 Core 集成或产品部署，既有产品 smoke 不证明新公式包已在用户产品加载；真实模型、生成图片资源解析、审批闭环、语音与完整 DPI/IME/视觉验收仍未完成。

## 原生生成图片与 Markdown 资源（2026-09-27）

新增 BCL `MarkdownImageReference`，与原 Web `MarkdownBlock.tsx` 的 vision ID / png、jpeg、webp 文件名形式对齐，并支持当前工作空间的相对 `/api/workspaces/.../vision-artifacts/...` 表示；该字符串仅用来提取 ID，不发起 HTTP。远程 URL、任意普通文件、跨工作空间 API 路由、多行或超长引用不解析。消息里给出的路径只提供资源名，实际读取沿用 Core 的工作空间资源解析端口。

`MarkdownImageContext` 提供原生图片工厂，接入 Markdown image 围栏与标准图片语法，通过 MessageCard、活动 TurnContentView 与 ActivityContentView 传递工作空间/取消生命周期。ImageAttachmentView 自动展开生成图片、延迟到挂载才加载，收起与卸载释放位图，重挂载恢复展开预览。Markdown 按源文本与已解析链接目标共同缓存控件，解决追加正文导致普通 Markdown 图片重建的问题，同时保留引用定义变化时的失效行为。未解析的外部图片仍显示 alt/URL 文本，不声称任意远程 Markdown 图片已迁移。

先运行独立 BCL 引用解析测试，再接原生展示：最终 `test-pudding-native-chat.ps1 -SkipCoreIntegration` 通过 60 逻辑/171 原生窗口检查，构建零警告/错误；日志 `temp/native-markdown-images-final.log`。新增 3 项 BCL 测试与 11 项窗口检查，覆盖引用规范化/工作空间路由/拒绝项、未挂载无读取、当前工作空间端口、流式保持、折叠重载、回收重载、标准图片 alt、引用目标变化、外部链接不读取、MessageCard 与执行活动装配。Core 服务和接口未修改，本轮没有重跑真实 Core 集成或部署用户产品；此前资源端口的真实 Core 测试已覆盖跨工作空间资源拒绝，但不作为本轮部署证明。

## 完整产品复验与原生截图（2026-09-27）

完整 Desktop + Core DLL 隔离 smoke 再次通过：PID 40964，原生聊天挂载、角色/文本附件草稿、Core 重启后聊天重建、UI 回调、目录保存和退出租约释放均通过。报告 `temp/test-out/kernel-winui-0442f5c9369e481cb53c60bd7b468a94/report.json`，日志 `temp/native-chat-product-refresh.log`；构建零错误、144 个既存警告。该验证包含此前公式/生成图片依赖的产品构建和启动，不证明这些内容已在真实模型会话显示，也不包括之后的输入区小改动。

新增 `PuddingChat.WinUITests/VisualPreview.cs`：真实 ChatWorkspace + fixture，渲染 1000 DIP 浅色、360 DIP 浅色和 1000 DIP 深色截图，报告附图片路径。RenderTargetBitmap 使用实色主题背景，不能证明 Mica/Acrylic 系统材质。截图发现空附件区与关闭 InfoBar 的间距浪费，ChatComposer 现在将它们 Collapsed；新增布局断言验证实际高度减少。标题副文案显示角色职责，消息状态复用中文标签，不再把内部 workspace/agent/session ID 放在主界面。60 逻辑/172 窗口检查通过，组件构建零警告/错误。

**视觉验收仍未通过：** 完整聊天截图中行内公式仍呈现原文（例如 `f(x)=x^2`），虽然控件诊断为 Rendered=true、RenderError=null；独立公式像素检查无法证明组合后的外观。已验证等待异步渲染/下一帧、重新测量 RichTextBlock 均未消除此现象；未保留无效的产品重新测量改动。下一步应检查 InlineUIContainer 内控件内容切换/原生捕获行为，不能据绿色组件测试宣称已修复。可复现截图：`temp/test-out/native-chat-dec817b4c5f64bcb89c814079fce51fd/`；后续诊断截图 `native-chat-53f298465f9b4d1c8ed9db4357cb2fed` 结果一致。完整 DPI、IME、屏幕阅读器和真实模型交互仍待验。

## 行内公式生命周期修复（2026-09-27）

关闭上节记录的“Rendered=true 但完整聊天显示原文”问题。诊断显示同一公式在短时间内渲染代次达到 46，实际内容不断回到 TextBlock，未报解析错误。InlineUIContainer 的布局期间存在短暂卸载/重载；原实现每次 Loaded 重绘、Unloaded 立即清空位图，形成循环。仅固定内容树或调用 InvalidateMeasure 不足以解决。

MathFormulaView 现在使用固定 Grid 承载文本与图片，以 Visibility 切换；同主题已经完成或进行中的渲染不重复启动；卸载清理通过 DispatcherQueue 延后检查 IsLoaded，仅真正离开视觉树才取消/释放。真实卸载后重挂载仍重新渲染。修复后的诊断代次为 1，图片具有实际尺寸且原文折叠；行内字号 14、边距 2，与正文更协调，独立公式块字号保持 22。

回归：60 逻辑/179 原生窗口检查通过，构建零警告/错误，日志 `temp/native-inline-math-final.log`。新增 7 项检查覆盖真正卸载重载，以及完整 ChatWorkspace 在宽屏/窄屏/深色三种场景的实际图片可见、原文折叠与后续帧稳定；保留浅深字形像素检查。实看最终截图 `temp/test-out/native-chat-5301d8a8a2c34a31a19eb67e08bba98f/ui.compact-light.png` 和 `ui.wide-dark.png`，公式正确排版，窄屏样例能与正文同排。此次没有重启用户产品、重新执行 Core 测试或验证全量 TeX、DPI/屏幕阅读器；只关闭已复现的组合显示问题。

## 语音播放基础组件（2026-09-27）

新增 PuddingChat/SpeechPlayback.cs：角色与消息绑定的 SpeechRequest、受限 SpeechAudio、合成端口/设备端口，以及拥有单条播放通道的 SpeechPlaybackSession。合成与播放分阶段，停止/新请求/销毁取消当前操作，代次拒绝迟到结果；播放错误可见但不回显供应商原始异常。当前只在独立 BCL 边界完成，不修改 Host 或 Composition，不创建原生语音入口。详细方案见 Desktop-Native-Voice-2026-09-27.md。

67 项逻辑测试通过（新增 7 项），日志 temp/native-speech-state.log。未打开麦克风、扬声器，未调用付费服务或修改 D:\data。下一步为真实原生播放器/消息按钮组件，然后接 Core；录音转写及持续语音会话尚未实现。

## 原生朗读播放器与按钮（2026-09-27）

新增 NativeSpeechAudioPlayer（WinRT MediaPlayer，WAV/MP3 内存流，结束/错误/取消释放）和 SpeechPlaybackButton（共享工作台播放通道上的消息动作，取消合成/停止/失败重试，控件卸载仅取消自身请求）。真实设备测试采用 IsMuted=true 的合成静音 WAV，未录音、未输出可听语音、未调用供应商。生产默认播放器不静音，只有显式 PlayAsync 才播放。

窗口测试发现取消回调可同步恢复 UI 等待者；SpeechPlaybackSession.Stop 已调整为先发布 Idle，再 Cancel，防止回收等待结束时仍观察到合成中。逻辑测试同时检查取消回调的状态。最终 67 逻辑/188 原生窗口检查通过，零构建警告/错误，日志 temp/native-speech-controls.log；新增 4 个真实播放器检查、5 个按钮检查。产品 MessageCard/ChatWorkspace、Composition 语音端口、录音/ASR 和持续语音会话仍未接线，下一步在组件门禁通过后进行装配。

## 消息朗读进程内接入（2026-09-27）

已完成的角色回复（agent/assistant）显示原生朗读按钮。ChatWorkspace 拥有共享播放通道，切换角色、离开聊天和销毁工作台停止播放；虚拟列表回收按钮也取消所属播放。Composition 按消息 ID 读取已保存正文，验证工作空间、会话角色及本机用户归属，提取 canonical envelope 的人类正文而不朗读封装 JSON，再直接调用 IVoiceSynthesisService。供应商/模型由既有 Core 默认配置解析；没有新增 Desktop HTTP 路由。

原生验证 67 项逻辑测试、192 项窗口检查通过，零构建警告/错误，日志 temp/native-speech-wiring-ui.log。测试查找朗读入口时排除 ItemsRepeater 尚留在视觉树中的已回收禁用控件；真实 Core 的 agent 投影已纳入 fixture，避免仅使用 assistant 测试漏掉入口。Core 集成 3 项测试通过（构建仍有既有依赖/代码警告），证据见 temp/native-speech-wiring-core.log；没有使用 D:\data、麦克风或付费供应商。录音/ASR、持续语音和真实供应商的产品验收仍待完成。

## 录音/转写状态组件（2026-09-27）

已独立实现 `PuddingChat/VoiceInput.cs`：IVoiceCapture/IVoiceRecording 设备端口、IChatTranscriptionClient 进程内转写端口、VoiceInputSession 及 VoiceDraftAnchor/VoiceDraftResult。状态依次为 Opening → Recording → Finalizing → Transcribing → Completed，取消进入 Cancelling 并等设备释放，失败保留可重试提示。停止录音是完成信号，等待最终 WAV 后释放设备，再开始 ASR；不移植 Web IntentConsole 的 200 ms 延时。设备端口要求内存 WAV、最多 8 MiB/2 分钟，状态层到时请求收尾，原生采集实现还必须在设备边界限制内存与时长。

录音开始绑定角色、选择代次和原始草稿。返回的转写文本不直接修改草稿、不发送消息；TryAppendTo 仅在角色/代次/草稿都未变化时追加。拒绝追加时文本仍可供 UI 显式插入。取消转写可立即放弃等待，迟到供应商结果不再发布；设备打开或收尾即使忽略取消，也必须等获得/释放句柄后才能开始下一次录音，重复 DisposeAsync 同样等待释放。

独立逻辑测试总计 **77 项通过**，新增 10 项覆盖完整收尾、设备先释放后转写、打开期间取消及迟到句柄、录音销毁、迟到 ASR、草稿编辑/角色切换、权限失败与重试、无效音频/空结果、收尾取消与重复释放等待、预取消不打开麦克风。日志 `temp/native-voice-input-state.log`，构建无警告/错误。测试全用替身设备与转写服务；**尚未实现 WinRT 麦克风采集、原生录音按钮或 Core ASR 装配，不能认定产品语音输入已可用。** 下一步按独立组件顺序完成设备与输入控件，再接 Composition/Core。

## 远程 Markdown 图片原生预览（2026-09-27）

RemoteImageReference 只接受不含用户信息的 HTTP(S) 地址；IRemoteImageSource 与 RemoteImageData 是独立合同。WinUI RemoteImageSource 采用不携带 Cookie/默认凭据的独立 HttpClient，最多 5 次重定向且逐跳校验地址，15 秒总超时、8 MiB 下载上限；没有 Content-Length 时也逐块计数。只接受 PNG/JPEG/WebP/GIF/BMP，WIC 实际解码并限定 6400 万源像素、1280 最大预览边长。默认仅展示来源主机与展开入口，明确展开才发请求，不把远程 URL 当作 Core Artifact 或本地路径。

RemoteImageView 在收起/真实卸载时取消并释放图片，迟到数据不再展示；InlineUIContainer 的短暂布局卸载在队列中确认后再处理。MarkdownImageContext 接通消息正文及思考/工具等既有 Markdown 内容，流式追加保留相同图片控件与已解码内容。失败可收起后重试；网络请求是外部图片获取，不是 Desktop/Core HTTP。

79 逻辑/218 窗口检查通过，零构建警告/错误，日志 temp/native-remote-images.log。新增地址规则、真实 WinRT 图片解码、无自动请求、显式展开、声明长度/无长度超限、凭据重定向拒绝、取消及流式控件复用检查。HTTP 使用替身 Handler，不依赖外网；完整 Desktop 部署与真实网站网络表现不在本轮证据范围内。
