# HarnessAgent.Cli
> 源指纹: Tests/HarnessAgent.Cli/**=2-9ac869886a06 · 条目数: 1 · 最近整理: 2026-11-22

HarnessAgent.Core 的冒烟自检命令行（net10.0，引 HarnessAgent.Core）。按 L0 到 L4、C1 到 C6、F1 到 F3 逐层各调一次并打印计数，另有 feishu-echo 长连接入口。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 逐层冒烟自检与飞书回声入口 | `TestProvider`、`TestMcpClient`、`RunFeishuEchoAsync` | `Source/HarnessAgent.Core/` | 界面与浏览器类能力不可用时打印 skip，不据此判失败 |
