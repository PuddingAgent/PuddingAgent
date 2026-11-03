# PuddingDesktop.GuardianTests 代码地图

| 文件 | 用途 |
|---|---|
| `GuardianTests.cs` | 只引用 Guardian 的边界、构建/部署/回滚、崩溃恢复、HTTP 认证、路径和 PID 身份测试；覆盖活动运行数据和旧清单的保护、产品资源与程序集校验；假时钟与隔离目录不启动产品 |
| `PuddingDesktop.GuardianTests.csproj` | 独立 xUnit 测试工程，唯一产品引用为 Guardian |
