# PuddingWorkspace

工作区实现：先把路径收敛到规范形态，再在受控面内做读写，可疑输入一律拒绝而不是修补。

> 源指纹: Source/PuddingWorkspace/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `WorkspaceFilePolicy.cs` | 定义 canonical 路径规则并拒绝可疑路径 | `WorkspaceFilePolicy` | `WorkspaceCatalog` | 绝不把可疑路径修补成可接受路径；Windows 保留名与结尾点空格一律拒绝 |
| `WorkspaceCatalog.cs` | 提供受控的文件面：限额列举、CAS 写入与链接拒绝 | `WorkspaceCatalog` | `WorkspaceFilePolicy` | 读写前必须先过路径策略并拒绝 link/reparse；非原子适配器一律报 `unsupported`，不得伪造成功 |
| `PuddingWorkspace.csproj` | 用构建期目标把可引用面锁到工作区端口契约 | — | `../PuddingWorkspace.Contracts` | `EnforceWorkspaceBoundary` 只允许引用 Contracts，且禁止任何 PackageReference |
