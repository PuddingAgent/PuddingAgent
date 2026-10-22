# PuddingGateway CodeMAP
> 源指纹: Adapters/**=3-dd40969c00af, Models/**=1-d823d353b147, *.cs=1-fc9933c2c022, *.csproj=1-a20e63c4abbf · 条目数: 5 · 最近整理: 2026-08-07

> LLM 网关适配层 | 多 Provider 接入

## 适配器（Adapters/）

| 文件 | 用途 |
|------|------|
| `CliGatewayAdapter.cs` | CLI 终端适配器 |
| `EmailGatewayAdapter.cs` | 邮件通道适配器 |
| `WebChatGatewayAdapter.cs` | Web Chat 适配器 |

## 核心

| 文件 | 用途 |
|------|------|
| `GatewayAdapterHost.cs` | 适配器托管宿主 |
| `Models/` | 网关模型 |

## 测试

—（无独立测试项目）
