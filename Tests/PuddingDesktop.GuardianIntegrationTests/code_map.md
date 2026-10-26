# Guardian WinUI 集成测试代码地图

| 文件 | 用途 |
|---|---|
| `DesktopGuardianEndpointTests.cs` | 直接装配 WinUI 产物中的控制端点，验证 UI ping、认证退出与旧部署入口互斥，不实例化窗口 |
| `PuddingDesktop.GuardianIntegrationTests.csproj` | Windows 测试工程，引用真实 WinUI Shell；与旧 WPF 测试工程分开 |
