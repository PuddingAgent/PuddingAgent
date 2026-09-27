# ADR：角色优先的 WinUI 3 Coding 工作台与 DLL 内核边界

- 标识：`Desktop-WinUI3-Shell-Core-Boundary`（专题 ADR，未占用仓库数字 ADR 编号）。
- 日期：2026-09-27。
- 状态：**用户最新裁定：原地重建 Desktop，Core 为 DLL 内核；角色卡和聊天均原生 WinUI 3，直接函数调用 Core。文字聊天与角色组件已接入，完整产品迁移验收未完成。**
- 权威实施规格：[WinUI 3 骨架与迁移方案](Desktop-WinUI3-Migration-Plan-2026-09-26.md)。
- 取代范围：该方案 2026-09-26 初稿的阶段划分、Core 进程内化、共享浏览器环境、前端零改动及必然自研聊天布局等设计前提；不宣称改写当前 WPF 产品事实。

## 背景

用户要求参考三栏界面规划 Desktop 骨架、PuddingAgent 接入和整体架构，并补充：PuddingAgent 是**角色为一等公民的 Coding Agent**。迁移要利用已有 Agent/主会话/Run、浏览器 Bridge、Core 监管和 Web 前端，而不是重建一个以匿名聊天为中心的客户端。

迁移前 Desktop 为 WPF，Core 为独立 ASP.NET Core 子进程。同日较早讨论曾选择长期独立 Core，**用户后续明确改为 Core DLL 内核，并要求原地重建 PuddingDesktop**，以本版为准。程序集逻辑边界继续保留，进程隔离不再是最终架构约束。

## 决策

1. **角色优先**：主导航是项目中的角色实例；角色承担工作，会话承载沟通，Run 承载执行。使用模板引用、workspaceId、agentId 和主会话等现有身份；Role 不是 RBAC、模型选择或前端自由文本标签。
2. **原生角色与聊天组件**：标题栏、角色卡、消息卡、执行明细、输入框、设置/运行中心使用 WinUI。`PuddingChat.WinUI → PuddingChat`；组件通过独立测试后接入 Desktop。管理页面暂由用户另行打开，不作为聊天容器。
3. **Coding 工作区**：右栏采用类型化文档合同，覆盖 file/diff/terminal/browser/artifact；浏览器是其中一个适配器。每个对象有明确工作区、角色、Run 来源。接口缺失则禁用能力并登记，不将日志冒充交互终端或把占位页算作已完成。
4. **DLL 内核**：最终在 Desktop 进程内通过组合入口装配 Core Host。View/Foundation 不引用 Runtime/SQLite；`IDesktopKernel` 是窄生命周期端口。内核先独立测试，再接入；不承诺 ALC 热卸载。`InProcessKernel` 负责串行生命周期，Composition 装配真实 Host；Core 使用 `IDesktopServices` 回调桌面展示。
5. **统一业务入口与直接调用**：`InProcessChatClient` 在 Composition 内以独立 DI scope 调用 Core 角色、主会话、SubmitTurn/Cancel handler 和投影服务。聊天没有 HTTP/JWT/Controller 调用；投影中的内部 HTTP 同样改为共享会话仓储。Core 持有唯一业务事实，UI 只管理选择与显示。
6. **通道分责**：HTTP 保留给管理网页与外部接入；原生聊天使用应用接口，Core 回调桌面使用 `IDesktopServices` 与 UI Dispatcher。浏览器命令仍需受控端口、Broker 准入、OperationId、deadline 和证据语义。
7. **执行与显示分离**：选择角色、切会话、切可见标签不改变已受理命令或 Agent 浏览器目标。人类接管必须触发真正 Dispatcher 门控；活动操作的结果可追溯。
8. **浏览器隔离**：可信 Workbench 与第三方/产物环境隔离，只有前者可握手 Shell 桥；消息校验来源、版本和文档代次，控制 token 不进入网页。
9. **组件先验证**：Foundation 不包含 UI/WebView2/业务闭包；驱动与 WPF/WinUI 适配拆分。耦合文件先留壳侧，组件通过独立测试和构建期边界门禁后才接入。
10. **原地重建**：产品工程和 exe 名保留，原 WPF 归档为测试基线。原生文字聊天已接入；富交互、完整历史、事件推送、多 context、多窗口分后续切片。完整 DLL 包更新和硬崩溃恢复由进程外部署方执行。

## 所有者与验收

| 责任域 | 所有者 | 验收证据 |
|---|---|---|
| 角色模板、实例、Run、执行根、权限、任务、记忆 | Core | 真实 API/事件和投影；同模板实例不合并，角色切换不改执行目标 |
| 窗口、布局、角色选中态与文档标签 | WinUI Shell | Mock/harness、IME/DPI/辅助功能、迟到响应和草稿分区测试 |
| 业务消息与轨迹呈现 | PuddingChat.WinUI / Core 投影 | 独立窗口测试、直接调用集成、游标与选择竞态、工具来源身份 |
| 浏览器实例与本地执行门控 | Desktop Browser 组件 | Bridge 端到端、接管/取消/目标稳定、后台执行和隔离验证 |
| 内核生命周期与升级 | Desktop 组合入口 + 进程外部署方 | Host 启停/失败修复、资源回收、完整进程更新、崩溃恢复 |

阶段门禁、文件级清单、协议和初始布局参数统一维护于实施规格，不在 ADR 重复版本化。WinUI 骨架构建与窗口 smoke 已有证据；DLL 接入另有真实 WinUI 启停/重启与程序集证据，不等于整个产品迁移完成。

## 代价与被排除的路线

- 原生化增加文本呈现与控件维护成本，但复用 Core 应用服务和 canonical 投影，不复制业务执行状态机。当前只承诺实施记录中的文字聊天范围。
- 每次调用独立 scope，停机必须取消并排空调用。客户端模式不显示登录、不读取账号或校验口令。Composition 为原生调用固定提供 `single-user` 本机身份，与 Core 单用户会话归属一致，控件不能指定身份。角色启用/冻结、会话归属与统一受理检查保留；Web/远程入口保持原有认证，不添加匿名或 loopback 自动登录。
- DLL 内核失去原有进程崩溃隔离；普通启动失败可在 UI 修复，原生崩溃/OOM/未处理异常可能终止整个 Desktop。更新需退出进程，不能靠 View 层 try/catch 或停止 IHost 假装已卸载程序集。
- 不采用每消息一个 WebView2、Desktop 直接打开业务数据库、复制运行状态机、Shell 桥任意执行脚本或同进程混合 WPF/WinUI Application。
- 不为视觉迁移额外创建角色持久化体系；若需要跨项目独立 Role 实体或模板版本快照，由 Core 专题设计决定。

## 后续变更条件

若提议原生聊天或独立角色目录，必须分别提供实际收益、依赖和故障边界、迁移成本、回滚与验收方案，再形成独立 ADR。不能通过修改 WinUI ViewModel 或增加 ProjectReference 隐式改变本决策。

## 当前实施边界

`Source/PuddingDesktop` 已原地成为 WinUI 骨架；`Source/PuddingDesktop.WpfArchive` 保留旧 247 项测试基线。`Foundation` 已独立测试后接入。Core DLL、健康检查、工作台、直接 UI 回调和退出回收已有隔离 smoke；原生角色/API 同步、Agent 浏览器、托盘和完整产品升级仍待后续切片，详见[实施记录](../Reports/Desktop-WinUI3-Skeleton-2026-09-27.md)。

详见[Core DLL 实施记录](../Reports/Desktop-Core-DLL-Integration-2026-09-27.md)。
