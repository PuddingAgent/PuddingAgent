# Pudding Agent 项目指令

## 项目概述
Pudding 是 Windows First 的 .NET 10 桌面智能助手与 IDE，支持六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

最终产品入口是 `PuddingDesktop.exe`。WPF Desktop 提供 Windows 11 风格 Shell、WebView2 Workbench、配置、运行中心、存储管理和桌面系统集成；ASP.NET Core 作为独立 Core API/Service Plane，由 Desktop 以 `core/PuddingAgent.exe --desktop-child` 子进程方式启动和监督。业务逻辑、Agent、Connector、数据库和 Runtime 继续属于 Core，不能迁入 WPF。

`dev-up.py` 只用于源码开发和调试，负责开发态后端、前端开发服务器、代理和相关工具进程。它不是最终产品守护进程，不进入交付包，也不能替代 Desktop 的产品进程主管职责。

## PuddingDesktop 产品边界

- `PuddingDesktop` 保持 `Microsoft.NET.Sdk` + WPF，不引用 `PuddingHost` 或 ASP.NET Core；Desktop 与 Core 只通过子进程协议、动态 Loopback HTTP 和认证 WebSocket Bridge 通信。
- Desktop 必须在 Core 启动失败、配置缺失或 DataRoot 未设置时仍可启动，并允许用户进入设置和运行中心执行修复、启动、停止或重启。
- Desktop 是单实例产品进程。关闭按钮默认隐藏到系统托盘并保持 Core 运行；只有“退出 Pudding”、Windows 会话结束或配置为 `ExitAndStopCore` 时才停止 Core 并释放 WebView2。
- `desktop.json` 保存 DesktopHome 范围的 DataRoot、Core 路径、窗口和关闭行为；`<DataRoot>/config/system.json` 保存 Core 端口、ControlToken、启动超时和自动恢复策略。Token 不放环境变量，不在 UI 或诊断包回显。
- `dev-up.py` 和 Desktop 不共享 PID、端口所有权。使用同一个 `D:\data` 验证 Desktop 前必须先停止 dev-up 管理的 Core，反之亦然，避免两个 Core 同时访问数据库。
- Phase 1A、Phase 1B-R Runtime Center、Phase 1B-S Storage、Phase 2A-1、Phase 2A-2 与 Phase 2A-3 确定性实现已于 2026-08-02 完成自动验收。DesktopChild 在启用 Browser Automation 时提供 `browser_context`、`browser_tabs`、`browser_navigate`、`browser_snapshot`、`browser_locate`、`browser_interact`、`browser_wait_for` 七项工具；Snapshot ref 必须携带 PageVersion，交互提交后不得重查旧 Locator，后续状态用 Wait 或新 Snapshot 获取。进入 Douyin Adapter 前仍需用用户明确选择的测试 Agent/DataRoot 完成真实 DeepSeek 可见 smoke；不得读取或复制 `D:\data` 中的 LLM Secret 来绕过该准入。底层始终保持通用，抖音能力只位于上层适配器。
- 运行在 Pudding 内部的 Agent 可以测试当前进程已经加载的成品代码和工具，包括真实模型调用、Browser Tools、TestSite 页面操作、Bridge Activity 和无需重启的功能行为；但它不能证明刚修改的代码已被当前进程加载，也不能独立验收承载自身的 Desktop/Core 生命周期。单实例会把第二次启动转发给旧进程，退出后 Agent 也无法继续观察子进程回收。因此采用两段式验收：内部开发 Agent 先交付 `ready-for-external-deploy`，进程外控制器重启到明确的新构建；随后 Pudding 内的新测试会话执行功能 smoke 并交付 `in-product-functional-complete`，最终启动/重启/崩溃恢复/退出回收结论仍由外部控制器判定。

## 兼容性和补丁约定

不要为了兼容性而牺牲性能和可维护性，除非有明确的业务需求。比如旧的数据格式或者旧的 API 版本。

除非必要，建议直接对D:\data的数据库和配置文件进行原地升级和修补，而不是通过兼容性层。

建议对于配置，配置文件优先，而非数据库优先。比如LLM服务商和模型配置、Agent配置、系统配置（系统预制的，放到程序所在目录）、用户自定义配置（放到用户指定的data目录，见PathHelper）

因为我们还在开发阶段（所以没有历史的需要兼容的数据），所以不建议使用兼容性层，除非有明确的业务需求。不建议为了SQL迁移，增加兼容性层。因为兼容性层会增加维护成本，降低性能。重置数据库，比迁移代码更简单。对于配置类的数据，建议使用配置文件，而不是数据库。

可以清理D:\data下的数据存储和缓存还原一个干净的开发环境，但是建议备份llm.providers.json，因为包含了LLM服务商的信息。

重置开发环境之后，需要访问Bootstrap页面，完成初始化。当然，也需要重新配置一下配置文件，因为Bootstrap是根据配置文件(Bootstrap.Initialized=true)判断是否可以初始化的。

# How-Debuge.md

可以读取How-Debuge.md，了解如何调试Agent和去哪里诊断和过滤错误日志。
将调试和日志的经验，写入到How-Debuge.md。包括关键的日志埋点等，在哪里找Error日志。

## 版本号约定
- 版本号格式：`主版本号.次版本号.修订号`    

## dev-up脚本python：

dev-up 是源码开发环境的调试和代理 Python 工具，方便快速启动前后端开发栈。修改开发态 Core/Workbench 代码后可用它重启或重新编译；最终用户不使用这些命令。

```bash
# 只启动前端端，然后使用命令行启动后端，用于调试后端服务：
python E:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --frontend-only
# 关闭（如果你想手动启动，那么先down，否则会占用端口）
python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --down
# 重启
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --restart
 # 重新编译，用于排除编译缓存问题：
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --rebuild
 python e:\github\AgentNetworkPlan\PuddingAgent\dev-up.py --status
```

## 开发环境约定

用户名：admin
密码：Admin@123

测试脚本：
- TestScripts目录
必读文件：
- Agents.md


## 项目路径
- 代码目录： `E:\github\AgentNetworkPlan\PuddingAgent`
- 数据存储: `D:\data` 开发环境数据存储的目录（见PathHelper，dev-up指定的环境变量或启动参数确定）
- 工作空间: `D:\data\workspaces\default`
- 编译入口: `dotnet build PuddingRuntime`
- 代码地图: `code_map.md`  这是项目根目录下的代码快速索引，要求必须在开始前阅读，并在任务结束后维护。
- 文档: `Docs`  这是项目的架构文档目录，要求必须在开始前阅读，并在任务结束后维护。


## 运行时配置

> 这里指的是pudding的运行时配置，主要是指运行时的环境变量和工作目录。而不是你的或者项目开发代码的。

- Shell: `pwsh` (PowerShell Core)
- OS: Windows 10
- 工作目录: `D:\data`  （见PathHelper，dev-up指定的环境变量或启动参数确定）

## 代码修改约定
- dry_run 默认 false 直接写盘；仅当需要先看 diff 时显式传 dry_run=true
- 编译命令: `dotnet build PuddingRuntime --no-restore`
- Desktop 定向构建: `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo`
- Desktop 定向测试: `dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo`
- Desktop Release 预览发布: `dotnet publish Source\PuddingDesktop\PuddingDesktop.csproj -c Release --no-restore -o .tmp-build\desktop-preview --nologo`
- Desktop build/test/publish 必须串行执行；并行构建同一 WPF 项目会共享 `obj`，可能产生重复 `mainwindow.baml` 的 `RG1000`。
- 构建、测试和发布输出只允许放在仓库 `.tmp-build`、`.tmp-test-out` 或系统 Temp，不得放到 `D:\data`。


## 长效学习管道（已建成）
1. Pre-Compaction Flush — 压缩前抢救事实
2. Background Extractor — 会话后搬运事实
3. Auto-Dream — 定期整理（每6h）
4. 管道2：经验→SKILL — 黄金路径→技能（每12h）
5. Skill Self-Improvement — 技能自进化（每4h）

## 已知问题
- PuddingAgent.dll 编译后被运行中进程锁定，需重启 Pudding 部署
- SubconsciousWorkerService 新版代码需重启后生效
- 记忆库有 11 个 archived Books 待 Auto-Dream 清理
