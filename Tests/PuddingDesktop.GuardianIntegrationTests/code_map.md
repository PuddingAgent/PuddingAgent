# Guardian WinUI 集成测试代码地图

| 文件 | 用途 |
|---|---|
| `DesktopGuardianEndpointTests.cs` | 直接装配 WinUI 控制端点，验证 UI ping、认证退出、旧入口互斥与 Guardian 专用 Core 停止头，不实例化窗口 |
| `GuardianLaunchPlanTests.cs` | 纯决策函数：子进程退出码 0 表示安装租约已被其他 Guardian 持有，必须与启动超时区分；含变异验证（忽略退出原因时 2/9 转红） |
| `PuddingDesktop.GuardianIntegrationTests.csproj` | Windows 测试工程，引用真实 WinUI Shell；与旧 WPF 测试工程分开 |
