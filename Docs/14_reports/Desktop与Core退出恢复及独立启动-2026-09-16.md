# Desktop 与 Core 退出恢复及独立启动（2026-09-16）

## 本次证据

- 07:33 检查时 Desktop/Core 均不在运行。Core 最新系统日志止于 07:28:31.936，之前仍正常提供 HTTP200，没有 shutdown 或异常终止堆栈。
- Windows System 的 WindowsUpdateClient 43/19 记录：07:28:32 开始更新、07:28:35 安装完成 `9PLM9XGG6VKS-OpenAI.Codex`。退出与更新同秒重合，但旧进程已消失，无法追溯查询其 Job 归属；因此外部进程树回收是有证据支持的推断，不能写成已证明的异常栈。
- 最新 Pudding .NET Runtime 崩溃及 dump 仍为 9/15 22:56 的 Goal 依赖缺失，已由 de026b4 修复；Desktop 的本地异常日志最后更新为8/29。本次没有找到新的 Desktop/Core 托管或原生崩溃事件。
- Core 00:00–00:01 的 Provider TLS EOF 是已捕获的模型请求失败；此后 Core 持续正常运行到07:28，不能归为进程崩溃原因。

## 可复现的启动风险与处理

在当前命令环境启动 Desktop 时，Desktop/Core 均进入 Windows Job；命令环境的 ExtendedLimitInformation flags 为 `0x2800`，包含 `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`。`Start-Process`/新建 `Shell.Application` 直接启动并不能保证脱离调用方进程树。单次 `CREATE_BREAKAWAY_FROM_JOB` 探针仍处于另一层 flags=`0x2000` 的 Job，因此不能以“启动成功”或“父 PowerShell 已退出”作为独立生命周期证明。

新增 `TestScripts/start-pudding-desktop-independent.ps1`：通过现有文件资源管理器窗口的 `Document.Application.ShellExecute` 请求 Explorer 启动 Desktop，并验证新 Desktop 的父进程为 explorer.exe。没有 Explorer 窗口时明确报错；已有 Desktop 时拒绝启动，避免单实例转发掩盖旧进程归属。默认隐藏启动，保留用户桌面配置，Core 仍由 Desktop 监督。该脚本仅是源码开发/外部验证的启动工具，不是新增产品守护进程。

使用方法（先正常退出既有 Desktop，并打开一个文件资源管理器窗口）：

```powershell
powershell -NoProfile -File TestScripts/start-pudding-desktop-independent.ps1
```

本次先查询两张执行表确认无活跃命令/Run，通过认证 `core/stop` 正常停止本轮恢复的 Core 8480，确认结束后仅终止由本轮启动的 Desktop 27840，再使用新脚本恢复。没有修改模型配置、数据库、应用源码或熔断阈值。

## 验证

- PowerShell 5.1 实际执行成功：Desktop PID36768，父 PID9044 explorer.exe；Core PID31188，父 PID36768。进程仍可能属于 Windows Shell 管理的 Job，不把 IsProcessInJob=true 等同于 Codex Job，也不宣称脱离所有 Windows Job。
- 再次运行脚本明确拒绝已有 Desktop，退出码1，无新实例。
- 07:39:41、07:40:15、07:40:49 三次检查均为 Ready 与 /health/ready HTTP200，Desktop/Core PID保持不变，已越过60秒恢复熔断窗口。没有通过实际再次更新/退出 Codex 进行破坏性验证，因此未宣称该场景端到端验收完成。
