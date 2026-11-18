# PuddingDesktop.Guardian 代码地图

独立 BCL 守护进程；禁止引用产品程序集，编译期 `EnforceGuardianBoundary` 拒绝任何 ProjectReference。Desktop 仍直接创建、监督 Core 子进程，守护进程只经本地协议控制生命周期。

| 文件 | 权责 |
|---|---|
| `Program.cs` | 单实例文件租约、配置读取、恢复未完成事务、HTTP 与健康循环；全应用退出完成后释放租约，`--resume` 仅用于用户重新启动 |
| `GuardianContracts.cs` | 配置、命令、持久状态及可替换运行时边界 |
| `GuardianController.cs` | 命令互斥、稳定观察期、有限恢复与回滚；持久退出队列等待当前事务结束，区分停止 Core 与退出全应用 |
| `GuardianStore.cs` | 路径围栏、程序集哈希、持久备份、事务替换及新增文件清理；排除 guardian 自身与 Core 根下的运行数据，旧清单读取也遵守该边界 |
| `GuardianRuntime.cs` | UI 响应、Core readiness 与进程身份；先 Core 后 Desktop 有界停止，`launch` 遵守 Core 自动启动配置 |
| `GuardianBuilder.cs` | 隔离编译、前端产物选择与候选包完整性检查 |
| `GuardianHttpServer.cs` | 仅 IPv4 loopback、令牌认证、有界请求体、202 回执与状态查询；/guardian/status 同时返回被监护的 `desktopPid`/`corePid`，否则「监护本机」与「监护已退出的桌面」在调用方看来完全相同；通过链接的 `../PuddingSecretReferences/BootstrapSecretFile.cs` 解析启动凭据引用，不依赖产品程序集或 KeyVault HTTP |
| `../PuddingDesktop.GuardianTests/GuardianTests.cs` | 独立状态机、事务、HTTP 与进程身份回归 |
