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
