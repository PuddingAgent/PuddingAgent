# PuddingMemoryEngineBenchmarks
> 源指纹: *.cs=2-948eec21e3fc, *.csproj=1-c3704258c220 · 条目数: 2 · 最近整理: 2026-11-22

BenchmarkDotNet 跑批工程（net10.0，引 PuddingMemoryEngine）。测记忆库读写检索与召回服务在不同事实规模下的耗时与分配，短跑配置，结果只落在本工程目录。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `MemoryLibraryBenchmarks.cs` | 记忆库读写与检索的微基准及运行入口 | `MemoryLibraryBenchmarks`、`Program` | `Source/PuddingMemoryEngine/Data/MemoryLibraryDbContext.cs` | 建库与手写 FTS5 语句要和初始化器保持一致 |
| `MemoryRecallBenchmarks.cs` | 不同事实规模下的召回性能基准 | `MemoryRecallBenchmarks` | `Source/PuddingMemoryEngine/Services/MemoryRecallService.cs` | 用共享连接的内存库隔离磁盘 IO 干扰 |
