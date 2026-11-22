# Samples/CapabilityCatalogClient

net10.0 控制台示例（`Exe`），仅引用 `Pudding.Api.Sdk`，演示 `ICapabilityCatalogApi` 的注入式离线用法。

> 源指纹: Samples/CapabilityCatalogClient/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CapabilityCatalogClient.csproj` | 示例控制台工程，唯一工程引用为 `Pudding.Api.Sdk` | — | `Pudding.Api.Sdk` | — |
| `CatalogDemo.cs` | 固定入口 `RunAsync`：仅接受 `list <kind> [cursor]`，转成一次 `ListAsync` 并原样输出页面 JSON | `CatalogDemo.RunAsync` | `ICapabilityCatalogApi`、`CapabilityQuery`、`CapabilityCatalogPage` | 不自动翻页、不调用工具或 MCP、不把目录条目当执行授权 |
| `Program.cs` | 离线壳：向 stderr 打印固定提示并以 2 退出；不读凭据也不联网 | `Program` | `CatalogDemo` | — |
| `code_map.md` | 本索引 | — | — | — |
