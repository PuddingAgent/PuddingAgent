# Samples/Plugins/ReviewAudit

离线只读的插件示例：把一条已授权的 `review.completed` 事件格式化成一条 scoped 审计记录，不依赖任何宿主服务。

> 源指纹: Samples/Plugins/ReviewAudit/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ReviewAudit.csproj` | 示例插件工程，输出 net10.0 类库且只引用 SDK 工程 | — | `../../Source/Pudding.ToolPlugins.AgentSdk/Pudding.ToolPlugins.AgentSdk.csproj` | 不打包（`IsPackable=false`）；无任何 PackageReference |
| `ReviewAuditPlugin.cs` | 只把带负载的 `review.completed` 事件写成一条审计记录，其余事件静默忽略 | `ReviewAuditPlugin`、`HandleAsync`、`FormatForTest` | `IPluginEventObserver`、`PluginAuditRecord` | `OperationId` 必须等于 `EventId`（宿主据此去重）；非目标事件不得写记录或抛异常 |
| `manifest.json` | 冻结的插件清单：声明入口类型、两项 scoped 能力与一个事件观察 | — | `ReviewAuditPlugin` | 能力必须是 scoped 精确值，禁止通配符；`sdkApi` 与 `coreApi` 版本不可随意提升 |
