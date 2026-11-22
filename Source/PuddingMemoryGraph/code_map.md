# PuddingMemoryGraph
> 源指纹: Source/PuddingMemoryGraph/**=6-5cc486a9dbd2 · 条目数: 5 · 最近整理: 2026-11-22

零工程依赖的记忆图组件（net10.0，不引任何其他工程）。节点契约、线索提取、图遍历、打分排序与渐进加载都是纯数据与纯函数，可脱离宿主单测。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `MemoryGraphContracts.cs` | 记忆节点与边的纯数据契约 | `MemoryNode`、`MemoryEdge`、`MemoryNodeRevision` | `MemoryRanker.cs` | 删除只置软删标记，物理移除会让「这条记忆为何消失」永远查不出来 |
| `MemoryCueExtractor.cs` | 从对话文本提取召回线索并判断该不该召回 | `MemoryCueExtractor`、`MemoryCue`、`MemoryCueKind` | `MemoryGraphContracts.cs` | 进度类只收动词加对象的短语，裸词会让近半 turn 误触发召回 |
| `MemoryGraphTraversal.cs` | 从锚点做有界展开并回报跳数 | `MemoryGraphTraversal`、`MemoryTraversalResult` | `MemoryGraphContracts.cs` | 默认不展开矛盾与取代关系，否则一条边就能把整片旧结论拉回来 |
| `MemoryRanker.cs` | 纯函数打分与稳定排序 | `MemoryRanker`、`MemoryRankingWeights`、`MemoryRankContext` | `MemoryProgressiveLoader.cs` | 新鲜度取自节点字段，排序层不自算时间，否则排序不可测 |
| `MemoryProgressiveLoader.cs` | 按档位与 token 预算切出可注入切片 | `MemoryProgressiveLoader`、`MemorySlice`、`MemorySliceResult` | `MemoryRanker.cs` | 预算先到先得，被裁条数必须回报而不是静默丢弃 |
