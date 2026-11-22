# Plugin Windows Supervisor
> 源指纹: *.cs=2-ed2ecf4514d7, *.csproj=1-6c229f6b1bae · 条目数: 2 · 最近整理: 2026-11-09

| File | Responsibility |
|---|---|
| `WindowsPluginWorkerFactory.cs` | Hidden workers, same-user named pipe with PID verification, bounded RPC and shutdown |
| `WorkerJob.cs` | Job Object tree ownership, kill-on-close, 512 MiB memory and child-process limits |
