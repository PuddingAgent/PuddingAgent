# ADR-068：桌面引导式自举闭环（重建-重启）

- **状态**：已接受（实现完成并经两次实战演练验证）
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
  | `GET /desktop/bootstrap/status` | busy 标志 + coreState + 上次结果 | 200 |
- **鉴权**：`X-Control-Token` 头或 body `token` 字段，与 `<DataRoot>/config/system.json → desktop.core.controlToken` 比对，恒定时间比较。token 由 `DesktopControlTokenService` 生成（32 字节 hex），UI 只显示「已生成/重新生成」，不回显全文。
- **信号文件轮询**（`Enabled` 默认 false）：保留 `<DataRoot>\config\rebuild.signal` 文件协议作为备用通道；畸形信号删除防重试循环；关机时保留信号防误导。
- **并发守卫**：`busy` 标志（Interlocked）保证同一时刻只有一个引导操作；重复触发得到 409 或「信号已忽略」。

### 2.2 闭环流程

```
停 Core → 等 Core 完全退出（≤30s 轮询，Stopped/Idle 才算全停）
        → dotnet 增量构建（超时默认 300s）
        → （可选）构建产物同步到 Desktop 运行目录（SyncBuildOutput，默认 false）
        → （可选）写 yolo.signal（AutoYolo 且请求带 yolo）
        → 重启 Core → 写 <SignalPath>.result.json
```

关键不变量：

1. **构建前 Core 必须完全退出**——否则运行中的二进制持有文件锁，增量构建会失败。Core 未全停时**跳过构建但仍重启 Core**（旧二进制兜底），并把原因记入 result。
2. **任何失败都写 result.json**：`success/action/startedAt/finishedAt/buildExitCode/buildLogTail(30行)/coreRestarted/yoloSignalWritten/errors[]`，WriteThrough 落盘。
3. **构建失败 → 旧二进制备份**：构建/同步失败只记录错误、清掉 `success`，不阻止 Core 用旧二进制恢复——闭环永不把系统弄死。
4. **零硬编码**：信号路径、构建目标（`BuildProjectPath` 绝对路径优先，否则 `BuildProjectRelativePath` 拼仓库根）、构建参数、超时全部来自 `system.json → desktop.bootstrap`。

### 2.3 启动侧不变量（实战教训）

Core 启动路径上的任何 Hosted Service **不得阻塞 `app.StartAsync()`**：DesktopChild 只有在 `StartAsync` 返回后才输出 `PUDDING_DESKTOP_READY`，Desktop Supervisor 到 `startupTimeoutSeconds`（60s）即杀进程。一次性长任务（如 SessionChunkVectors 存量回填）必须继承 `BackgroundService` 并在 `ExecuteAsync` 首句 `await Task.Yield()`（见 `b6e1bc4` 与 How-Debuge §11.17）。

### 2.4 Desktop 自身不在闭环内

闭环只重建 `Source/PuddingAgent/PuddingAgent.csproj`（Core）。Desktop 自身更新仍需 VS/手工重建——自举不能提着自己的鞋带把自己拉起来。Desktop 与 Agent 运行时的依赖已切断（`4a09175` 移除 Desktop→PuddingAgent 项目引用），Desktop 以外部进程方式监督 Core。

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
  -Body (@{ token='<TOKEN>'; requestedBy='ops'; yolo=$true; message='...' } | ConvertTo-Json) `
  -ContentType 'application/json'
# → 202；触发方（Agent）会随 Core 一起下线 1~2 分钟

# 3) 重启后验收
Get-Content D:\data\config\rebuild.signal.result.json
# success=true, buildExitCode=0, coreRestarted=true, yoloSignalWritten=true, errors=[]
```

失败诊断顺序：`result.json errors[]` → `<DataRoot>\logs\desktop-bootstrap-build.log` → 确认旧二进制已自动恢复 Core（status coreState=Ready 即系统存活）。

## 5. 证据

| 演练 | 时间 | 结果 |
|---|---|---|
| #1 | 2026-08-06 16:28 | success=true，构建 7.76s，全程 19s |
| #2（修复回填阻塞后复测） | 2026-08-06 17:03 | success=true，构建 13.38s（增量 no-op），全程 39s |

测试：`PuddingDesktopBootstrapConfigTests` + `PuddingBuildOutputSyncTests` 9/9（commit 前验证）；`SessionChunkBackfillServiceTests` 含「回填运行中宿主 StartAsync 不阻塞」回归用例。

## 6. 后果与后续

- **已得**：Agent 可全自助完成「改码 → 提交 → 重建 → 重启 → 验收」；每次尝试都有结构化 result 审计。
- **约束**：触发方会随 Core 重启掉线，必须在 goal/记忆中预置「重启后第一件事」清单（本会话两次演练均靠此恢复）。
- **后续阶段**：增量编译提速、热重载、Desktop 自更新（需独立 updater 进程）、UI 触发按钮接线 `TriggerBootstrapAsync`。
