# PuddingDesktop.ExitObserver 代码地图
> 源指纹: *.cs=1-d52d8982c6b1, *.csproj=1-592ba5dbe242 · 条目数: 3 · 最近整理: 2026-10-24

独立无窗口退出观察进程，只依赖诊断组件；不负责启动或停止 Desktop/Core。

| 文件 | 用途 |
|------|------|
| `Program.cs` | 校验 PID 和启动时间，持有进程句柄，等待退出并记录退出码与最后现场 |
| `PuddingDesktop.ExitObserver.csproj` | 禁止引用业务宿主或 UI；由 Desktop 构建与发布到 observer 子目录 |
| `../../TestScripts/test-desktop-exit-observer.ps1` | 使用隔离子进程验证强杀、非零退出、正常关闭、无协调退出和致命异常记录 |

观察器与 Shell 同时终止时，由下一次主实例启动执行现场补录，不猜测谁终止了进程。
