# Plugin Windows Supervisor

| File | Responsibility |
|---|---|
| `WindowsPluginWorkerFactory.cs` | Hidden workers, same-user named pipe with PID verification, bounded RPC and shutdown |
| `WorkerJob.cs` | Job Object tree ownership, kill-on-close, 512 MiB memory and child-process limits |
