# PuddingDesktop.Guardian 代码地图

独立 BCL 守护进程；禁止引用产品程序集，编译期 `EnforceGuardianBoundary` 拒绝任何 ProjectReference。Desktop 仍直接创建、监督 Core 子进程，守护进程只经本地协议控制生命周期。

| 文件 | 权责 |
|---|---|
| `Program.cs` | 单实例文件租约、配置读取、恢复未完成事务、HTTP 与健康循环 |
| `GuardianContracts.cs` | 配置、命令、持久状态及可替换运行时边界 |
| `GuardianController.cs` | 命令互斥、稳定观察期、有限恢复、主动停止与回滚状态机 |
| `GuardianStore.cs` | 路径围栏、程序集哈希、持久备份、事务替换及新增文件清理；排除 guardian 自身 |
| `GuardianRuntime.cs` | Desktop UI 响应与 Core readiness、PID/启动时间/路径身份、子进程父关系及有界停止 |
| `GuardianBuilder.cs` | 隔离编译、前端产物选择与候选包完整性检查 |
| `GuardianHttpServer.cs` | 仅 IPv4 loopback、令牌认证、有界请求体、202 回执与状态查询 |
| `../PuddingDesktop.GuardianTests/GuardianTests.cs` | 独立状态机、事务、HTTP 与进程身份回归 |
