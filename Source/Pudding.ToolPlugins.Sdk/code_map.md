# Plugin SDK
> 源指纹: *.cs=1-d1c162be0cbf, *.csproj=1-7d512668d1e5 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ToolPlugin.cs` | 插件作者基类，只依赖插件契约。 | `ToolPlugin` | `Source/Pudding.ToolPlugins.Contracts` | 钩子与工作区事件订阅为虚方法，宿主不强制实现。 |
| `Samples/ToolPlugins/TextTools/TextTools.csproj` | 作者示例：只对已发布引用 DLL 编译。 | `TextTools` | `Source/Pudding.ToolPlugins.Sdk/ToolPlugin.cs` | 不得引用宿主工程。 |
