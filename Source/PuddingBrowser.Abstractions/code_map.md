# PuddingBrowser.Abstractions CodeMAP

> Browser 抽象契约 | 接口 · 类型 · 策略

## 核心接口

| 文件 | 用途 |
|------|------|
| `BrowserInterfaces.cs` | `IBrowserRuntime`、`IBrowserPage`、`IBrowserElement` 等核心接口 |

## 模型与类型

| 文件 | 用途 |
|------|------|
| `BrowserTypes.cs` | 浏览器类型定义（标签、Cookie、视口等） |
| `BrowserIds.cs` | 浏览器 ID 常量 |
| `BrowserOperationOrigin.cs` | 操作来源标记 |
| `CapabilityPolicy.cs` | 能力策略 |
| `Locator.cs` | 元素定位器模型 |
| `Models.cs` | 通用模型 |
| `ScriptAndSnapshot.cs` | 脚本与快照模型 |

## 测试

—（由 AgentTools / Desktop.Tests 间接覆盖）
