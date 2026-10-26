---
title: Desktop Guardian 运行手册
author: hyfree
date: 2026-10-26
last_reviewed: 2026-10-26
status: active
description: 独立守护进程通过本地认证 HTTP 编译和重启 Desktop/Core，并在健康检查失败时回滚程序集。
categories: [docs, runbooks]
tags: [desktop, guardian, bootstrap, rollback]
related_docs: [Docs/08_how_debuge/11-案例-桌面与Core启动.md]
related_files: [Agents.md, Source/PuddingDesktop.Guardian/Program.cs, Source/PuddingRuntime/Tools/BuiltIns/Management/BootstrapRebootTool.cs, TestScripts/test-pudding-guardian.ps1]
slug: runbooks-desktop-guardian
draft: false
---

# Desktop Guardian 运行手册

Desktop 随包携带 `guardian/PuddingDesktop.Guardian.exe`，启动时自动拉起独立守护进程。Guardian 接收编译/重启命令、检查 Shell 与 Core、管理程序集回滚；Desktop 仍直接启动 `core/PuddingAgent.exe --desktop-child`。进程间只使用本地协议，不共享程序集引用或数据库访问权。

## 配置与启动

在 `<DataRoot>/config/system.json` 的 `desktop` 下配置：

```json
{
  "guardian": {
    "enabled": true,
    "httpPort": 8200,
    "repositoryRoot": "D:/CodeProject/PuddingAgent/PuddingAgent",
    "configuration": "Debug",
    "startupTimeoutSeconds": 120,
    "stableSeconds": 15,
    "pollSeconds": 5,
    "failureThreshold": 3,
    "buildTimeoutSeconds": 600
  },
  "bootstrap": { "httpEnabled": true, "httpPort": 8199 }
}
```

示例仓库路径应替换为实际路径，也可以省略让 Desktop 向上查找。没有源码的安装使用 `prebuilt-artifact`；源码构建需要 .NET SDK，前端构建还需要已安装的 pnpm 依赖。Desktop Debug 模式必须关闭，Core 路径必须位于当前 Desktop 程序包内。Guardian 配置改动在下一次 Guardian 启动时生效。

Desktop 从自身实际路径、DataRoot 和该配置生成 `<DesktopHome>/guardian.json`。可由进程外控制器直接执行 `guardian/PuddingDesktop.Guardian.exe --config <DesktopHome>/guardian.json`；`--adopt-pid <pid>` 只能接管路径匹配的 Desktop。`guardian.lock` 是独占文件句柄租约，不删除锁文件绕过互斥。

初次安装本功能或更新 Guardian 自身，需要外部部署：先退出 Desktop/Core，停止对应 Guardian，部署完整发布包，再启动 Desktop。后续 Desktop/Core 更新即可由 Guardian 完成。Guardian 目录从更新/回滚清单中排除，不支持运行中的自我替换。

## Agent 与 HTTP 命令

Agent 调用示例：

```json
{"target":"desktop","action":"build-restart","deploymentMode":"desktop-build","frontendMode":"skip"}
```

`bootstrap_reboot` 默认 `target=core`、`action=build-restart`。`keepYolo` 沿用现有行为，还受 `desktop.bootstrap.autoYolo` 控制。开启 Guardian 后，工具直接向它提交；关闭时只保留旧 Core 点火行为，不支持 Desktop 操作。

HTTP 仅绑定 `127.0.0.1`。令牌从 `desktop.core.controlToken` 读取并放入 `X-Control-Token` 请求头；请求体、命令行和环境变量不放令牌。浏览器 Origin 请求拒绝，不提供跨域访问。

```http
POST /guardian/commands
Content-Type: application/json
X-Control-Token: <从配置读取，勿输出>

{"requestId":"2f7835ef-88f1-442b-95cd-089291d08fd5","action":"build-restart","target":"core","deploymentMode":"desktop-build","frontendMode":"skip"}
```

| 字段/动作 | 行为 |
|---|---|
| `target=core` | 编译或替换 Core；健康更新时保留 Desktop PID |
| `target=desktop` | 编译或替换 Desktop 和随包 Core |
| `action=build` | 只准备候选包，状态 `built` 的 `preparedDirectory` 可用于后续部署 |
| `action=build-restart` | 默认构建并部署；`deploymentMode=prebuilt-artifact` 使用 `artifactDirectory`，`restart-only` 只重启 |
| `action=restart/start/stop` | 不替换程序集的生命周期操作；stop 停止自动恢复 |
| `action=rollback` | 恢复最近部署的备份；有未完成事务时优先恢复该事务，重启整个 Desktop/Core 对以保证一致性 |
| `artifactAssemblySha256` | 可选，校验 Core 的 `PuddingAgent.dll` 或 Desktop 的 `PuddingDesktop.dll` |
| `frontendMode=skip/build/load` | skip 使用当前 Core 的静态前端；build 用 pnpm 构建到隔离目录；load 用 `frontendArtifactDirectory`，可加 `frontendArtifactIndexSha256`；build/load 仅用于源码 build/build-restart |

`GET /guardian/status` 使用同一认证头，返回当前/最近请求的 `requestId`、`phase`、`busy`、`error`、候选目录和备份是否存在。202 是接受，200 可为相同最近请求的幂等重试；不同请求忙碌或同 ID 不同内容返回 409。每次新操作生成 UUID；只重试最近的完全相同请求时复用 ID。服务不保留无限历史账本。

## 健康检查与回滚

候选构建成功前保持旧进程运行。部署前对当前健康包做独立快照，保存每个文件 SHA-256，再持久化 `Pending` 事务，停止对应进程，替换文件并删除候选中已移除的旧文件。路径不允许越界或 reparse point。部分复制失败也可恢复；回滚删除候选新增文件。

Desktop 内部 `GET /desktop/guardian/health` 必须完成 UI dispatcher 往返；返回的进程 PID/启动时间/路径、DataRoot 及 Core 父关系由 Guardian 检验。Core 必须处于 `Ready`，且它报告的 loopback 地址 `/health/ready` 返回成功；Shell 工作台须完成初始化，`WorkbenchFailed` 不算健康。短暂成功不能通过验收：默认持续 15 秒健康才接受。启动默认限 120 秒；平时默认每 5 秒检查、连续 3 次失败恢复。

新版本验收失败或事务中途被打断时恢复旧快照并重新检查。已运行版本之后失去健康也使用保留备份恢复；初次运行尚无健康备份时只能尝试重启当前二进制，不能声称恢复过旧版本。初次坏安装须先经外部修复达到健康，才能建立可信回滚基线。恢复最多每五分钟 3 次；恢复自身失败直接 `faulted`，停止自动尝试。可以修复外部条件后提交 `rollback` 重试保留事务。

保留当前回滚快照及最近两份不再引用的快照；未完成事务不清理。备份仅含程序集/资源，**不含 DataRoot，不恢复数据库或用户配置**。若升级改变数据库且旧程序集不能读取，需要另行处理数据；守护进程不擅自重置或覆盖用户数据。

主动停止、托盘退出、窗口关闭退出策略、Windows 注销不自动复活。之后手动启动到健康可重新启用监督。初次 Guardian 可能由 Desktop 启动，因此强制停止 Shell 时只终止已验证的 Shell 本身，再单独停止已验证的 Core；不会杀死包含 Guardian 的整棵 Desktop 进程树。

## 诊断与验收

- `<DesktopHome>/guardian/state.json`：当前状态、请求 ID、进程身份、备份与未完成事务；不要手改事务来绕过检查。
- `<DesktopHome>/guardian/releases/`：GUID 快照及旁边的 `.manifest.json`。
- `<DesktopHome>/logs/desktop-runs/`：Desktop 现场、Core 输出末尾、主动退出原因。`GuardianStart` 表示守护接线失败；Desktop 仍能进入运行中心修复。
- `<RepositoryRoot>/temp/build/guardian/<id>/build.log` 和 `frontend.log`：隔离构建日志，控制令牌被替换；失败不会停止当前产品。
- `phase=healthy` 是新版本验收完成；`rolled-back` 是部署被拒绝但旧版已恢复；`faulted` 需外部处理。只看 202、build exit 0 或 UI 当前仍可用，不能证明新程序集被接受。

发布到独立 `temp/build/<package>` 后执行：

```powershell
./TestScripts/test-pudding-guardian.ps1 -DesktopExe temp/build/<package>/PuddingDesktop.exe
```

脚本自行分配 DesktopHome、DataRoot、三组端口和令牌。默认验证真实 Core/Desktop 编译、Core 更新、损坏 Core/损坏 Desktop 回滚、Core 意外退出恢复、主动停止与数据租约释放；`-SkipBuilds` 只验证启动/监督/停止。脚本拒绝当前已运行的包。所有故障注入只作用于测试包与候选，不使用生产数据。测试结果写该隔离目录的 `acceptance.json`。
