# ADR：WinUI Shell、既有 Web UI 与独立 Core 进程

- 日期：2026-09-29。
- 状态：**Accepted（用户已裁定）；恢复主线已实现；验证覆盖与未覆盖项见[恢复报告](../14_reports/Desktop-Shell-Recovery-2026-09-29.md)。**
- 用户裁定：“WINUI只承当Shell，界面和Core还是保持不变……Core还是保持一个独立进程。”
- 取代范围：2026-09-27 的全量原生界面迁移、Desktop 进程内 Core DLL 和原生业务直调方案，以及 2026-09-29 此前“最终移除 Web Chat / Admin 界面”的方向。
- 历史依据保存在分支 B：`Docs/12_features/ADR-Desktop-WinUI3-Shell-Core-Boundary-2026-09-27.md` 与 `Docs/12_features/Desktop-WinUI3-Migration-Plan-2026-09-26.md`。这些历史文档未恢复到 master，与本裁定冲突的内容不再作为后续开发要求。

## 1. 决策与动机

产品继续采用 **WinUI 3 Shell + WebView2 承载既有 Web UI + 独立 ASP.NET Core 进程**。WinUI 的职责是桌面外壳；聊天、业务设置和工作台交互继续使用 Web 技术；Core 保留已有业务架构与服务职责，在 Desktop 进程之外运行。

桌面产品化不要求将业务界面全部重写为 XAML。全面原生化会重复投入富文本、代码、公式、多模态消息、执行轨迹及复杂工作台交互的实现与验证。本决策依据是产品边界、复用价值和维护成本，不声称 WinUI 技术上无法实现这些功能，也不预先承诺混合方案的性能提升。

“界面和 Core 保持不变”指保留既有 Web UI 与 Core 业务实现、交互语义和服务契约，不借 Shell 迁移重写业务。因当前源码已发生原生化与进程内化，恢复目标形态仍需要后续接线、打包和宿主调整；本裁定不等于这些调整已经完成，也不禁止独立的缺陷修复。

## 2. 责任边界

| 层 | 负责 | 边界 |
|---|---|---|
| WinUI Shell | 窗口、标题栏、桌面系统集成、WebView2 容器、Core 进程启动与监督、启动失败时的基础修复入口 | 不承接聊天、业务设置或 Agent 业务状态机；不装配进程内业务 Host，不打开业务数据库 |
| Web UI | 角色与会话导航、聊天与输入、执行轨迹、业务管理设置、已有工作台交互 | 复用现有前端，继续通过既有 Core API / 事件通道访问服务；不复制 Core 执行状态机 |
| 独立 Core 进程 | ASP.NET Core 服务、Agent / Run / Turn、工具、任务、记忆、连接器、配置、数据库和权威事件 | 保留业务与组件边界；不依赖 WinUI 控件，不以 Desktop 进程内 DLL 作为产品运行形态 |

目标关系：

```text
PuddingDesktop.exe（WinUI Shell 进程）
  ├─ 窗口 / 系统集成 / 基础运行中心
  ├─ WebView2 → 既有 Web UI ──既有 API / 事件通道──┐
  └─ Core 进程启动、就绪检测与监督 ──────────────┤
                                                   ▼
                              独立 Core 进程（ASP.NET Core）
                              现有业务组件 / 配置 / 数据库
```

1. 业务 UI 以完整工作台区域承载，不按每条消息创建 WebView2，也不以原生聊天和 Web 聊天长期双套实现为目标。
2. Web 与 Shell 的桥只提供明确的桌面能力。Core 调用桌面能力通过跨进程协议/适配器完成，不再以进程内 UI 回调作为部署前提；已有可复用的抽象和组件无需因此推倒重写。
3. 可信工作台与第三方网页、生成产物保持隔离。复用现有认证与准入语义，不因本机部署放宽业务授权。
4. Shell 在 Core 未就绪或退出时仍能显示状态与修复入口。进程监督、就绪、重启、退出回收的具体实现复用既有资产后单独核验，不因旧版有实现而记作新版完成。
5. 启动设置建议 DataRoot 为 `D:\data`，已保存目录优先；沿用同目录前停止旧 Core，保留 `.pudding-host.lock` 租约互斥，不通过删除锁文件绕过占用。恢复实现沿用 `desktop.json` 与 Core 配置；关闭行为由启动器的托盘/退出设置决定，切换 DataRoot 后须重开 Desktop。
6. 编译期依赖边界、组件先独立测试再接入、Core 唯一业务事实源继续有效。Shell 不引用或装配业务 Host；项目引用白名单已限制到视觉 Foundation、既有配置合同和浏览器适配器。
7. `dev-up.py` 继续只用于源码开发，不能代替产品的 Core 进程监督。

## 3. 首次登记时的源码与目标差距（历史快照）

以下是登记时的源码事实，不是目标架构已完成的证据：

- `Source/PuddingDesktop/PuddingDesktop.csproj` 仍引用 Composition、原生聊天和 ASP.NET Core 框架。
- `Source/PuddingDesktop.Composition/DesktopKernelFactory.cs` 仍创建进程内 Core；原生聊天通过 `InProcessChatClient` 调用业务服务。
- `Source/PuddingHost/Hosting/PuddingHostOptionsFactory.cs` 的 `ForDesktop` 仍设置 `ServeAdminSpa = false`；此前移除的 Web 入口、SPA 路由、构建产物与发布接线需要逐项盘点，不能假定只打开一个开关即可恢复。
- `Source/PuddingPlatformAdmin` 保留现有 Web 前端；`Source/PuddingDesktop.WpfArchive` 可供旧进程监督、WebView2 与桥接行为参考，不代表改回 WPF 产品。

首次登记仅修改文档；此后用户授权将全部成果保存到 B 并从 `765b964` 恢复 master。已有组件化、共享业务操作、查询及生命周期修复按实际价值筛选，具体取舍见恢复报告。

## 4. 对旧开发任务的影响

- 停止将“把 Web Chat / Admin 全面迁为 XAML”及“最终删除 Web UI”作为后续交付目标。
- B 中 `Docs/15_tasks/Desktop-Admin-Settings-DeepSeek-2026-09-27.md` 尚未完成的原生迁移不再继续作为默认待办；既有交付记录保留在 B。
- 本文只改变开发文档基线，不代表已修改任务看板、已通知其他运行中的 Agent 或已取消它们的执行。
- Core 独立进程已恢复；监督协议、`--desktop-child` 参数、动态端口发现、发布目录和认证接线复用原启动器，并完成隔离生命周期回归。

## 5. 后续实施与验收边界

恢复执行已按组件化规程先验证视觉 Foundation 与 WinUI 浏览器适配器，再接入产品工程。以下是完整产品验收边界；不能将恢复报告中已通过的定向测试扩大为全部场景验收。

完成标准至少包括：

- 外部观察能确认 Desktop 与 Core 为不同 PID，Core 故障不会因同进程业务 Host 而直接终止 Shell，Shell 可显示故障并执行恢复。
- Web 聊天、历史、流式输出、工具轨迹、附件和管理设置通过真实服务回归，保留既有业务行为与认证，不以页面可打开代替完整交互验收。
- Core 启动、重启、退出回收、端口释放和 DataRoot 租约均由进程外验证；不得让两个 Core 同时使用同一数据目录。
- 可信工作台与第三方网页/产物的桥权限隔离、焦点与快捷键、DPI、输入法和恢复行为通过针对性验证。
- 产品包不依赖 `dev-up.py`；构建/测试使用隔离输出和测试 DataRoot。

**首次登记只完成架构裁定；后续恢复实施与验证统一见[恢复报告](../14_reports/Desktop-Shell-Recovery-2026-09-29.md)，不将历史快照当作当前实现。**
