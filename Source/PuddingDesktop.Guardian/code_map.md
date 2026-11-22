# PuddingDesktop.Guardian 代码地图
> 源指纹: *.cs=9-27ee97cfee13, *.csproj=1-8e1e5a86c76c · 条目数: 8 · 最近整理: 2026-11-18

独立 BCL 守护进程；禁止引用产品程序集，编译期 `EnforceGuardianBoundary` 拒绝任何 ProjectReference。Desktop 仍直接创建、监督 Core 子进程，守护进程只经本地协议控制生命周期。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 单实例文件租约与未完成事务恢复，并承载 HTTP 健康循环 | `—` | `GuardianController.cs` | 全应用退出完成后才释放租约；`--resume` 只用于用户重新启动 |
| `GuardianContracts.cs` | 配置、命令、持久状态与可替换的运行时边界 | `GuardianSettings`、`GuardianCommand`、`ProcessIdentity` | `GuardianController.cs` | — |
| `GuardianController.cs` | 命令互斥与稳定观察期，含有限恢复和回滚 | `GuardianController` | `GuardianStore.cs` | 持久退出队列必须等当前事务结束；停止 Core 与退出全应用是两件事 |
| `GuardianStore.cs` | 路径围栏与程序集哈希，含持久备份和事务替换 | `GuardianStore`、`SafeFiles` | `GuardianController.cs` | 必须排除 guardian 自身与 Core 根下的运行数据，否则会搬走运行库 |
| `GuardianRuntime.cs` | UI 响应、Core readiness 与进程身份，按序有界停止 | `GuardianRuntime` | `GuardianController.cs` | 必须显式按先 Core 后 Desktop 停止；`launch` 遵守 Core 自动启动配置 |
| `GuardianBuilder.cs` | 隔离编译与候选包完整性检查 | `GuardianBuilder` | `GuardianStore.cs` | 候选包完整性检查不过不得进入替换 |
| `GuardianHttpServer.cs` | 仅 IPv4 loopback + 令牌认证的有界控制面 | `GuardianHttpServer`、`GuardianToken` | `GuardianController.cs` | `/guardian/status` 必须同时返回 `desktopPid`/`corePid`，否则监护已退出的桌面与监护本机无法区分 |
| `../PuddingDesktop.GuardianTests/GuardianTests.cs` | 状态机、事务、HTTP 与进程身份回归 | `GuardianTests` | `GuardianController.cs` | — |
