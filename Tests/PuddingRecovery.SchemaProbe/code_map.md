# PuddingRecovery.SchemaProbe · code_map
> 源指纹: *.cs=1-2abd2c8fe836, *.csproj=1-c9ae05f2d6b4 · 条目数: 1 · 最近整理: 2026-11-22

数据库快照的只读兼容校验器（控制台 net10.0）。故意不构建 Host：不启 worker、调度、connector 与模型调用。

**入口与配置**：`PuddingRecovery.SchemaProbe <snapshot 路径>`，路径必须是仓库 `temp/test-out/` 下已存在的文件；结果以 JSON 打到 stdout。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 校验快照表结构与当前 EF 模型是否兼容 | `ExecutionRunSchemaBootstrapper`、`PlatformDbContext` | `Source/PuddingPlatform/code_map.md` | 路径限定 temp/test-out；放开后 EnsureCreatedAsync 会改写真实库 |

**项目约束**：额外 NOT NULL 且无默认值的列一律记为失败（旧模型插入需要迁移），不是告警。
