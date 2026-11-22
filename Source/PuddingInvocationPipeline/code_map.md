# PuddingInvocationPipeline
> 源指纹: *.cs=2-24b5cff51998, *.csproj=1-18c782ebc186 · 条目数: 2 · 最近整理: 2026-11-22

调用扩展的加载与排序实现（PA-07 07B）：按点装快照、按种类排顺序，纯类库不触宿主服务。

> 源指纹: Source/PuddingInvocationPipeline/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ExtensionRegistry.cs` | 按扩展点装载不可变快照并在提交副作用前复核代际 | `ExtensionRegistry`、`LoadAsync`、`EnsureGeneration` | `Source/PuddingInvocationPipeline.Contracts/ExtensionPorts.cs` | 源不可达必须是类型化失败而不是空插件集；重复或非法的描述一律拒收 |
| `ExtensionPipeline.cs` | 排出扩展执行顺序并校验 Transform 的字段改动 | `ExtensionPipeline`、`Plan`、`ValidateTransform` | `Source/PuddingInvocationPipeline.Contracts/ExtensionPorts.cs` | 未授权的扩展只进 Skipped，不得静默改写；顺序固定为 Guard 先于 Transform 先于 Advice 与 Observer |
| `PuddingInvocationPipeline.csproj` | 构建期锁死本工程只依赖端口工程 | — | `Source/PuddingInvocationPipeline.Contracts/PuddingInvocationPipeline.Contracts.csproj` | `EnforceInvocationPipelineBoundary` 目标会失败构建：引用非 Contracts 工程或任何 PackageReference 都报错 |
