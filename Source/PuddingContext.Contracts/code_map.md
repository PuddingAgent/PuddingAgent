# Context ports

上下文端口的 BCL-only 契约：规范消息、外部贡献、带当前用户栅栏的装配请求与结果。

> 源指纹: Source/PuddingContext.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ContextPorts.cs` | 定义上下文装配所需的类型：消息与贡献、请求与结果；含分词器端口 | `ContextMessage`、`ContextContribution`、`ContextAssemblyRequest`、`IContextTokenizer` | `ContextAssemblyResult`、`ContextErrorCodes` | 记忆存储与模型/提供方适配器留在组件外；栅栏哈希随请求传递 |
| `PuddingContext.Contracts.csproj` | 纯契约工程，无工程引用与包依赖 | — | `PuddingContext.Contracts` | 保持 BCL-only |
