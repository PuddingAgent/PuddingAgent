# PuddingBrowser.Protocol CodeMAP

> WebSocket Bridge 线协议 | 命令 · 载荷 · 序列化 · 错误码

## 协议定义（8 个 .cs）

| 文件 | 用途 |
|------|------|
| `BrowserBridgeCommandNames.cs` | 桥接命令名称常量 |
| `BrowserBridgeCommandPayloads.cs` | 命令载荷定义：PageLocateArguments.ExpectedPageVersion（ref 必须）/ ScopeRef / ScrollTarget / 滚动结果描述符（见下「关键语义」） |
| `BrowserBridgeEnvelope.cs` | 消息信封 |
| `BrowserBridgeErrorCodes.cs` | 错误码 |
| `BrowserBridgeMessages.cs` | 消息类型定义 |
| `BrowserBridgeProtocol.cs` | 协议常量（当前修订 3：D8 结果合同修复隔离） |
| `BrowserBridgeBuildIdentity.cs` | D9 构建身份：程序集名称/版本/MVID/路径与 stamp 的源修订；握手携带，仅诊断不授权 |
| `BrowserBridgeSerializer.cs` | JSON 序列化器；信封版本严格相等，拒绝包含 expected/received 诊断 |
| `BrowserBridgeJsonSerializerContext.cs` | AOT 源码生成上下文 |

## 关键语义（用途原文逐字下沉）

`BrowserBridgeCommandPayloads.cs` 用途原文（逐字）：

命令载荷定义。`PageSnapshotArguments.ScopeRef`（§151 的 `scope`，null = 整页）；`PageInteractArguments.ScrollTarget` 承载滚动目标模型（§7）；`BrowserInteractionResultDescriptor.Scroll`（`BrowserScrollReportDescriptor`）承载滚动结果事实；非滚动动作或宿主未报告时为 **null**（线缆上不编造"滚了 0 像素"）

## 命令结果合同

命令名、参数与结果 DTO 成对维护：`context.list` 是列表，`context.getInfo/create` 是单 context；`page.goto` 是导航结果，back/forward/reload/stop 是单页；close 无 Value。Desktop 内部能力返回全集时必须按请求裁剪，工具摘要不等于 wire。合同及修订策略见 [ADR-097](../../Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) 与[修复规格](../../Docs/12_features/浏览器发现契约与配套部署修复方案-2026-10-18.md)。

## 测试

现有 Host.Tests Bridge Endpoint 与 Desktop adapter 测试；真实 producer→wire→consumer 的连通门禁按修复规格补齐，不能仅以两侧独立 DTO 测试替代。
