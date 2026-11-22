# PuddingInvocationPipeline.Contracts

调用扩展端口的纯契约（PA-07 07B）：声明扩展分类与代际围栏，BCL-only。

> 源指纹: Source/PuddingInvocationPipeline.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ExtensionPorts.cs` | 声明扩展点分类、描述符与受保护的工具字段 | `ExtensionPoints`、`ExtensionDescriptor`、`ExtensionPlan`、`ProtectedToolFields`、`ExtensionErrorCodes` | `Source/PuddingInvocationPipeline/ExtensionPipeline.cs` | 插件不得改 tool 身份、授权、真实退出码与预算归属，也不得把失败翻成成功；授权由宿主持有 |
| `PuddingInvocationPipeline.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceInvocationPipelinePortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
