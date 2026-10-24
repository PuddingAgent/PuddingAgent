# PuddingDesktop.Diagnostics 代码地图
> 源指纹: *.cs=1-8e69ee07708c, *.csproj=1-b7c62ac5a0e8 · 条目数: 3 · 最近整理: 2026-10-24

独立 BCL 现场记录组件，零项目引用、零包引用，不读取配置、密钥或会话数据库。

| 文件 | 用途 |
|------|------|
| `DesktopRunJournal.cs` | 每次启动一个目录；原子状态快照、轮转事件、异常脱敏、退出分类、上次异常退出补录与保留上限 |
| `PuddingDesktop.Diagnostics.csproj` | 编译期禁止项目与第三方包引用 |
| `../../Tests/PuddingDesktop.Diagnostics.Tests/DesktopRunJournalTests.cs` | 退出分类、保留现场、脱敏、有界写入与恢复测试 |

`shutdown_completed` 与“已请求退出”严格分离；只有 PID 和启动时间共同确认的进程死亡才可补录，未知终止来源保持 unknown。
