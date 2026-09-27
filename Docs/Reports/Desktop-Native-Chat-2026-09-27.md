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
