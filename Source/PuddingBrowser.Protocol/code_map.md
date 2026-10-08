# PuddingBrowser.Protocol CodeMAP

> WebSocket Bridge 线协议 | 命令 · 载荷 · 序列化 · 错误码

## 协议定义（8 个 .cs）

| 文件 | 用途 |
|------|------|
| `BrowserBridgeCommandNames.cs` | 桥接命令名称常量 |
| `BrowserBridgeCommandPayloads.cs` | 命令载荷定义。`PageSnapshotArguments.ScopeRef`（§151 的 `scope`，null = 整页）；`PageInteractArguments.ScrollTarget` 承载滚动目标模型（§7）；`BrowserInteractionResultDescriptor.Scroll`（`BrowserScrollReportDescriptor`）承载滚动结果事实；非滚动动作或宿主未报告时为 **null**（线缆上不编造"滚了 0 像素"） |
| `BrowserBridgeEnvelope.cs` | 消息信封 |
| `BrowserBridgeErrorCodes.cs` | 错误码 |
| `BrowserBridgeMessages.cs` | 消息类型定义 |
| `BrowserBridgeProtocol.cs` | 协议常量 |
| `BrowserBridgeSerializer.cs` | JSON 序列化器 |
| `BrowserBridgeJsonSerializerContext.cs` | AOT 源码生成上下文 |

## 测试

—（由 Host.Tests Bridge Endpoint 测试覆盖）
