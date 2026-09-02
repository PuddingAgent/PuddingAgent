# ADR-068：桌面引导式自举闭环（重建-重启）

- **状态**：已接受（2026-08-29 修订：Codex 可调用的 Desktop 本机制品控制面）
- **日期**：2026-08-06
- **相关**：ADR-041（开发构建与发布打包链路分离）、`YoloSignalService`（信号先例）、`How-Debuge.md §11.17`、`Source/code_map.md`
- **提交**：`0bbb223`（WP-B1 信号服务）、`4a09175`（WP-B2 HTTP 端点）、`b6e1bc4`（回填不阻塞修复）

---

## 1. 背景

Desktop 的定位是未来的开发控制台（VS 启动/双击启动，`dev-up.py` 逐步废弃）。Agent 日常需要完成「代码改动 → 重建 → 重启 Core」循环，此前该循环依赖用户手工执行：

1. Agent 无法重启承载自己的进程，改完代码只能等用户操作；
2. 手工循环慢且易错（忘记构建、锁文件、忘记重启）；
3. 无结构化结果记录，失败后诊断靠翻日志。

目标：给 Desktop 一个**可由 Agent/UI 触发的引导式自举闭环**，全程路径动态配置、零硬编码，失败永远可回退到旧二进制。

## 2. 决策

### 2.1 触发面：HTTP 主通道 + 信号文件轮询（opt-in）

- **回环 HTTP 控制端点**（默认开启，`HttpEnabled=true`）：只绑定 `127.0.0.1:8199`（刻意避开 Core 的 8080），绝不监听 LAN 接口。
  | 路由 | 语义 | 结果 |
  |---|---|---|
  | `POST /desktop/bootstrap/start` | token 校验后触发完整重建-重启 | 202 / 401 / 409 |
  | `POST /desktop/bootstrap/core/stop` | 原子停 Core | 200 / 401 / 409 |
  | `POST /desktop/bootstrap/build` | 原子构建（前置：Core 已全停） | 200 / 401 / 409 |
  | `POST /desktop/bootstrap/core/start` | 原子启 Core | 200 / 401 / 409 |
  | `POST /desktop/bootstrap/core/restart` | 不替换制品，仅冷重启 Core | 200 / 401 / 409 / 422 |
  | `POST /desktop/bootstrap/core/deploy-restart` | 加载仓库内预构建 Core 制品、清单校验并重启 | 200 / 401 / 409 / 422 |
  | `POST /desktop/bootstrap/frontend/build-deploy` | Desktop 执行 `pnpm run build` 并热部署 Admin 静态制品 | 200 / 401 / 409 / 500 |
  | `POST /desktop/bootstrap/frontend/load` | 不重新编译，加载仓库内已构建 `dist`；可校验 `index.html` SHA-256 | 200 / 400 / 401 / 409 / 500 |
  | `GET /desktop/bootstrap/diagnostics` | 鉴权后的 Desktop/Core 状态、部署忙状态、路径与最近 100 行有界日志 | 200 / 401 |
  | `GET /desktop/bootstrap/status` | busy 标志 + coreState + 上次结果 | 200 |
- **鉴权**：`X-Control-Token` 头或 body `token` 字段，与 `<DataRoot>/config/system.json → desktop.core.controlToken` 比对，恒定时间比较。token 由 `DesktopControlTokenService` 生成（32 字节 hex），UI 只显示「已生成/重新生成」，不回显全文。
- **信号文件轮询**（`Enabled` 默认 false）：保留 `<DataRoot>\config\rebuild.signal` 文件协议作为备用通道；畸形信号删除防重试循环；关机时保留信号防误导。
- **并发守卫**：Core 生命周期/程序集操作由 `busy`（Interlocked）串行化；前端构建/加载由独立 `SemaphoreSlim` 串行化，避免两个请求互相删除 `wwwroot/admin`。重复触发统一返回 409。前端静态替换不占用 Core 生命周期锁，因此 Core 运行中可热部署。
- **制品路径围栏**：Core 与前端预构建目录都必须是仓库根内的绝对路径；前端目标仍强制以 `wwwroot/admin` 结尾，避免控制面退化为任意本地文件复制器。
- **诊断边界**：`/diagnostics` 必须鉴权，返回有界日志且绝不回显 ControlToken；公开 `/status` 只保留低敏状态与上次结构化结果。

### 2.2 闭环流程

```
停 Core → 等 Core 完全退出（≤30s 轮询，Stopped/Idle 才算全停）
        → Desktop 构建或接收 Agent 预构建产物
        → 事务部署到实际 CoreExecutablePath 所在目录（不是 Desktop 根目录）
        → 校验部署前后 PuddingAgent.dll SHA-256
        → （可选）写 yolo.signal（AutoYolo 且请求带 yolo）
        → 重启 Core → 再次核对实际启动目录与程序集 SHA-256
        → 写 <SignalPath>.result.json
```

`deploymentMode` 是显式执行策略：

| 模式 | 编译责任 | Desktop 责任 | 适用场景 |
|---|---|---|---|
| `desktop-build`（默认） | Desktop | 停 Core、`dotnet build`、事务部署、校验、重启 | 方案 B，点火工具默认路径 |
| `prebuilt-artifact` | Agent/外部构建器 | 校验仓库内绝对产物目录、事务部署、校验、重启 | 方案 A，复用已完成构建 |
| `restart-only` | 无 | 仅重启 | 明确只需要配置重载时使用；不得用于源码更新验收 |

关键不变量：

1. **构建前 Core 必须完全退出**——否则运行中的二进制持有文件锁，增量构建会失败。Core 未全停时**跳过构建但仍重启 Core**（旧二进制兜底），并把原因记入 result。
2. **部署目标唯一来自实际启动路径**：使用 `DesktopApplicationCoordinator.CoreExecutablePath` 的父目录；禁止把产物默认复制到 `AppContext.BaseDirectory`，因为发布包的 Core 位于 `core/` 子目录。
3. **事务部署与可回滚**：所有变更文件先在目标同卷暂存并逐字节校验，再逐文件提交；提交失败恢复已覆盖文件。构建/部署失败仍重启旧 Core。
4. **程序集加载必须有证据**：除 `restart-only` 外，同时校验入口 `PuddingAgent.dll` 与完整托管启动产物清单。清单覆盖排序后的全部 DLL、EXE、deps.json、runtimeconfig.json，以相对路径 + 单文件 SHA-256 合成总指纹，确保 `PuddingRuntime.dll` 等依赖也进入加载验收。只有准备产物、目标目录、重启后实际启动目录三方指纹一致且 Core Ready，`assembliesReloaded=true`、`success=true` 才成立。
5. **任何失败都写 result.json**：除原有字段外，记录 `deploymentMode/buildOutputDirectory/deploymentDirectory/coreExecutablePath/deploymentCopied/deploymentSkipped/preparedAssemblySha256/loadedAssemblySha256/preparedArtifactManifestSha256/loadedArtifactManifestSha256/managedArtifactFileCount/assembliesReloaded`，WriteThrough 落盘。
6. **构建失败 → 旧二进制备份**：构建/部署失败只记录错误、清掉 `success`，不阻止 Core 用旧二进制恢复——闭环永不把系统弄死。
7. **零硬编码**：信号路径、构建目标（`BuildProjectPath` 绝对路径优先，否则 `BuildProjectRelativePath` 拼仓库根）、默认部署模式、构建参数、超时全部来自 `system.json → desktop.bootstrap`。

### 2.3 启动侧不变量（实战教训）

Core 启动路径上的任何 Hosted Service **不得阻塞 `app.StartAsync()`**：DesktopChild 只有在 `StartAsync` 返回后才输出 `PUDDING_DESKTOP_READY`。一次性长任务（如 SessionChunkVectors 存量回填）必须继承 `BackgroundService` 并在 `ExecuteAsync` 首句 `await Task.Yield()`（见 `b6e1bc4` 与 How-Debuge §11.17）。

数据库冷升级等必须在 Ready 前完成的有限初始化通过 stdout 控制协议每 5 秒发送 `PUDDING_DESKTOP_STARTING` 租约（协议版本、当前 Core PID、单调序号、阶段、耗时）。`startupTimeoutSeconds` 表示“无有效进度的最大静默时间”；Desktop 只接受当前子进程且序号递增的租约，并保留 `5 × startupTimeoutSeconds`、最高 10 分钟的绝对上限。租约不等于 Ready，不能绕过最终 `PUDDING_DESKTOP_READY`、PID 校验和 `/health/ready`。这样既允许一次性 schema/index 冷升级超过 60 秒，也不会让卡死或伪造旧消息无限续命。

### 2.4 Desktop 自身不在闭环内

闭环只重建/部署 `Source/PuddingAgent/PuddingAgent.csproj`（Core）。Desktop 自身更新仍由进程外控制器停止旧 Desktop、编译并启动新 Desktop；运行中的 Desktop 不覆盖自己的程序集。Desktop 与 Agent 运行时的依赖保持进程隔离，Desktop 以外部进程方式监督 Core。

## 3. 拒绝的方案

- **只保留信号文件轮询**：1 秒轮询浪费，且 Agent 触发需要文件写权限竞争；HTTP 更直接、可返回状态码。信号文件降级为 opt-in 备份。
- **热重载/增量补丁**：.NET 热重载对 DI/宿主改动不可靠，复杂度高；先做稳「冷重建-重启」，热重载留作后续阶段。
- **构建与重启合并为不可分事务**：构建失败率不低（锁、警告升级），必须允许「构建失败但恢复旧 Core」的降级路径。

## 4. 验收 Runbook

```powershell
# 1) 探活
Invoke-RestMethod http://127.0.0.1:8199/desktop/bootstrap/status
# → { busy=false, coreState=Ready, lastResult=... }

# 2) 点火（token 在 D:\data\config\system.json → desktop.core.controlToken）
Invoke-RestMethod -Method Post http://127.0.0.1:8199/desktop/bootstrap/start `
  -Body (@{ token='<TOKEN>'; requestedBy='ops'; yolo=$true; deploymentMode='desktop-build' } | ConvertTo-Json) `
  -ContentType 'application/json'
# → 202；触发方（Agent）会随 Core 一起下线 1~2 分钟

# 3) 重启后验收
Get-Content D:\data\config\rebuild.signal.result.json
# success=true, buildExitCode=0, assembliesReloaded=true,
# preparedArtifactManifestSha256 == loadedArtifactManifestSha256,
# assembliesReloaded=true, coreRestarted=true, errors=[]

# 4) Codex/外部控制器加载已经编译好的前端 dist（不再次运行 pnpm）
$systemConfig = Get-Content D:\data\config\system.json -Raw | ConvertFrom-Json
$headers = @{ 'X-Control-Token' = $systemConfig.desktop.core.controlToken }
$body = @{ artifactDirectory='E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist' } | ConvertTo-Json
Invoke-RestMethod -Method Post http://127.0.0.1:8199/desktop/bootstrap/frontend/load `
  -Headers $headers -Body $body -ContentType 'application/json'

# 5) 鉴权诊断（不得输出或记录 $headers）
Invoke-RestMethod http://127.0.0.1:8199/desktop/bootstrap/diagnostics -Headers $headers
```

失败诊断顺序：`result.json errors[]` → `<DataRoot>\logs\desktop-bootstrap-build.log` → 确认旧二进制已自动恢复 Core（status coreState=Ready 即系统存活）。

## 5. 证据

| 演练 | 时间 | 结果 |
|---|---|---|
| #1 | 2026-08-06 16:28 | success=true，构建 7.76s，全程 19s |
| #2（修复回填阻塞后复测） | 2026-08-06 17:03 | success=true，构建 13.38s（增量 no-op），全程 39s |
| #3（程序集部署修订实机复测） | 2026-08-28 14:35 | `desktop-build`，success/assembliesReloaded/coreRestarted=true；Core PID 3108→13308；268 个托管产物清单指纹一致，全程 37s，health=healthy |
| #4（冷升级越过 60s 事故） | 2026-08-28 19:08 | Core 已完成 Platform schema 并启动到 Connector/ChatWorker，但旧 Desktop 在 Ready 前按固定 60s 杀进程；受控二次启动约 5s Ready，确认不是崩溃。修订为 5s 启动租约 + 60s 静默超时 + 300s 绝对上限，仍保留 Ready/health 双门禁。 |

测试：2026-08-28 定向 `PuddingDesktopBootstrapConfigTests` + `PuddingBuildOutputSyncTests` 14/14，`BootstrapRebootToolTests` 13/13，Desktop 请求协议 6/6，启动租约/主管定向 23/23；`PuddingDesktop.Tests` 全量 222/222；`PuddingApplicationHostCompositionTests` 1/1。`SessionChunkBackfillServiceTests` 继续覆盖「回填运行中宿主 StartAsync 不阻塞」。

## 6. 后果与后续

- **已得**：Agent 可全自助完成「改码 → 构建/交付产物 → 事务部署 → 重启 → 程序集哈希验收」；每次尝试都有结构化 result 审计。
- **已得**：Codex 等本机自动化客户端可通过同一鉴权控制面加载前端/Core 预构建制品、执行冷重启并取得有界诊断，不需要复制 Desktop 内部状态机。
- **约束**：触发方会随 Core 重启掉线，必须在 goal/记忆中预置「重启后第一件事」清单（本会话两次演练均靠此恢复）。
- **后续阶段**：增量编译提速、热重载、Desktop 自更新（需独立 updater 进程）、UI 触发按钮接线 `TriggerBootstrapAsync`。
