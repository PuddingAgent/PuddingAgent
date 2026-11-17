# PuddingDesktop.GuardianTests 代码地图

| 文件 | 用途 |
|---|---|
| `GuardianTests.cs` | 只引用 Guardian 的状态机、HTTP、进程身份与包保护测试；涵盖持久退出、部署期间排队、请求去重、有限重试、手动停止与重新启动；假时钟与隔离目录不启动产品 |
| `PuddingDesktop.GuardianTests.csproj` | 独立 xUnit 测试工程，唯一产品引用为 Guardian |
