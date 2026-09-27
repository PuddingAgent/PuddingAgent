# ADR：角色优先的 WinUI 3 Coding 工作台与独立 Core 边界

- 标识：`Desktop-WinUI3-Shell-Core-Boundary`（专题 ADR，未占用仓库数字 ADR 编号）。
- 日期：2026-09-27。
- 状态：**用户已确认架构方向；详细合同待独立验证，产品实施与验收未完成。**
- 权威实施规格：[WinUI 3 骨架与迁移方案](Desktop-WinUI3-Migration-Plan-2026-09-26.md)。
- 取代范围：该方案 2026-09-26 初稿的阶段划分、Core 进程内化、共享浏览器环境、前端零改动及必然自研聊天布局等设计前提；不宣称改写当前 WPF 产品事实。

## 背景

用户要求参考三栏界面规划 Desktop 骨架、PuddingAgent 接入和整体架构，并补充：PuddingAgent 是**角色为一等公民的 Coding Agent**。迁移要利用已有 Agent/主会话/Run、浏览器 Bridge、Core 监管和 Web 前端，而不是重建一个以匿名聊天为中心的客户端。

当前 Desktop 为 WPF；Core 以独立 ASP.NET Core 子进程运行。初稿把换 Shell、原生聊天和 Core 进程内化串成一个项目，增加了与既有产品边界的冲突。角色身份、Coding 文档和执行证据归属也需要成为 Shell 的明确合同。

## 决策

1. **角色优先**：主导航是项目中的角色实例；角色承担工作，会话承载沟通，Run 承载执行。使用模板引用、workspaceId、agentId 和主会话等现有身份；Role 不是 RBAC、模型选择或前端自由文本标签。
2. **WinUI Shell，混合呈现**：标题栏、角色导航、工作区标签、设置/运行中心原生化；中间复用现有 Web 工作会话。嵌入模式隐藏重复外框，保留消息、工具轨迹、输入与授权行为。
3. **Coding 工作区**：右栏采用类型化文档合同，覆盖 file/diff/terminal/browser/artifact；浏览器是其中一个适配器。每个对象有明确工作区、角色、Run 来源。接口缺失则禁用能力并登记，不将日志冒充交互终端或把占位页算作已完成。
4. **独立 Core**：Desktop 不引用 PuddingHost、Agent Runtime 或业务存储。继续监督 `core/PuddingAgent.exe --desktop-child`。不实施 `coreMode=inproc`，不以进程内化作为迁移完成条件。
5. **统一业务入口**：业务命令、权限、持久事件、Agent 生命周期由 Core 决定；首版复用现有 Web 登录、Agent 主会话和 HTTP/SSE 投影。原生导航消费受限摘要，不复制完整聊天状态机。
6. **三条通道**：业务 HTTP/SSE、认证 Browser Bridge、ShellWebBridge 各自独立。Shell 桥只承载有限导航/展示意图与摘要；不提供任意 HTTP/脚本代理，不通过 WebMessage 授予 Agent 执行权。
7. **执行与显示分离**：选择角色、切会话、切可见标签不改变已受理命令或 Agent 浏览器目标。人类接管必须触发真正 Dispatcher 门控；活动操作的结果可追溯。
8. **浏览器隔离**：可信 Workbench 与第三方/产物环境隔离，只有前者可握手 Shell 桥；消息校验来源、版本和文档代次，控制 token 不进入网页。
9. **组件先验证**：Foundation 不包含 UI/WebView2/业务闭包；驱动与 WPF/WinUI 适配拆分。耦合文件先留壳侧，组件通过独立测试和构建期边界门禁后才接入。
10. **可结束的迁移**：M0–M5 完成后可退役 WPF，原生聊天、多 context、多窗口为独立后续项目。完整版本包回滚；Desktop 自更新由进程外部署方执行。

## 所有者与验收

| 责任域 | 所有者 | 验收证据 |
|---|---|---|
| 角色模板、实例、Run、执行根、权限、任务、记忆 | Core | 真实 API/事件和投影；同模板实例不合并，角色切换不改执行目标 |
| 窗口、布局、角色选中态与文档标签 | WinUI Shell | Mock/harness、IME/DPI/辅助功能、迟到响应和草稿分区测试 |
| 业务消息与轨迹呈现 | Web Workbench（首版） | 现有 transport 回归、游标恢复、工具证据定位与来源回溯 |
| 浏览器实例与本地执行门控 | Desktop Browser 组件 | Bridge 端到端、接管/取消/目标稳定、后台执行和隔离验证 |
| 进程生命周期与升级 | Desktop 监督 + 进程外部署方 | 新构建哈希/PID/Ready、崩溃/退出回收和旧版本回滚 |

阶段门禁、文件级清单、协议和初始布局参数统一维护于实施规格，不在 ADR 重复版本化。同日实现仍为未开始；不得据此声明 WinUI 可运行或迁移已完成。

## 代价与被排除的路线

- 混合呈现需要维护一个小型、版本化 Shell 桥和必要前端嵌入改造；换来对现有聊天/事件逻辑的复用。
- 角色原生导航首版依赖 Workbench 提供受权摘要；Workbench 故障时保留原生修复入口，业务导航显示不可用。后续可独立建设原生查询客户端。
- 保留 Core 子进程意味着继续维护监督与通信，但提供 UI 修复可用性和独立重启边界。
- 不采用每消息一个 WebView2、Desktop 直接打开业务数据库、复制运行状态机、Shell 桥任意执行脚本或同进程混合 WPF/WinUI Application。
- 不为视觉迁移额外创建角色持久化体系；若需要跨项目独立 Role 实体或模板版本快照，由 Core 专题设计决定。

## 后续变更条件

若提议原生聊天、Core 进程内化或独立角色目录，必须分别提供实际收益、依赖和故障边界、迁移成本、回滚与验收方案，再形成独立 ADR。不能通过修改 WinUI ViewModel 或增加 ProjectReference 隐式改变本决策。
