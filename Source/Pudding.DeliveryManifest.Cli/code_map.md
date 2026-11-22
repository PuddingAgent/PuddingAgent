# Pudding.DeliveryManifest.Cli
> 源指纹: Source/Pudding.DeliveryManifest.Cli/**=2-2f88dd27dcfa · 条目数: 1 · 最近整理: 2026-11-22

交付清单采集与外部验收判定的命令行入口（net10.0，引 Pudding.DesktopService）。两种模式：采集磁盘事实产出清单，或用清单加观察事实判定能否作出验证结论。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 汇总磁盘事实产出交付清单并判定验收结论 | `Conclude`、`ParseArguments`、`FindRepositoryRoot` | `Source/Pudding.DesktopService/` | 事实不完整就不产出清单，以退出码 2 结束并逐条打印原因 |
