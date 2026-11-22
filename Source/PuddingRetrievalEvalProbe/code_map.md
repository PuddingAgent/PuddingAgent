# PuddingRetrievalEvalProbe
> 源指纹: *.cs=17-580a46e3911d, *.csproj=1-ee0bb97c6b7b · 条目数: 17 · 最近整理: 2026-11-22

检索评测探针 CLI（net10.0 控制台 Exe）。作为 PuddingRetrievalEval 的适配侧跑 count/index/measure/scan/production 五种模式，基线报告落在该组件的 eval/reports，评测组件本身保持零工程引用。

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ChunkCorpusBuilder.cs` | 概要优先策略的分块语料构建与分词记账 | `ChunkCorpusBuild`、`ChunkCorpusBuilder` | `RoslynCSharpOutlineSource.cs`、`VectorRetrievalModes.cs` | — |
| `CorpusInventory.cs` | 只读语料盘点：统计全文面在给定作用域可索引的文件量与目录分布 | `CorpusInventory` | `Program.cs`、`LuceneFullTextProbe.cs` | 统计谓词必须与引擎同一套，换谓词会让人数与实际索引量对不上 |
| `EmbeddingProviderAdapter.cs` | OpenAI 兼容嵌入客户端与分阶段调用统计 | `OpenAiCompatibleEmbeddingProvider`、`EmbeddingCallStats` | `EmbeddingRouteResolver.cs` | 统计按阶段快照相减，索引侧与查询侧混算会虚报查询延迟 |
| `EmbeddingRouteResolver.cs` | 把 provider/model 路由解析成可调用的嵌入端点 | `EmbeddingRouteResolver`、`ResolvedEmbedding` | `EmbeddingProviderAdapter.cs`、`VectorRetrievalModes.cs` | 未知或禁用路由一律 fail-closed，不得回退默认模型 |
| `IndexRunReport.cs` | 一次全文索引构建的耗时与体积记录 | `IndexRunReport`、`IndexProbeFiles` | `Program.cs` | 只在显式给 --index-json 时写出，回放基线不得多落文件 |
| `LuceneFullTextProbe.cs` | Lucene 全文引擎到 ISearchProbe 的计时适配 | `LuceneFullTextProbe` | `VectorSearchProbe.cs`、`Program.cs` | 适配器不得加过滤或回退，否则测到的是探针行为而非引擎行为 |
| `ProductionProbeCapabilities.cs` | 从实跑证据推导每个检索面的能力三态 | `ProbeCapability`、`ProbeCapabilities`、`ProductionCapabilityPolicy` | `ProductionRun.cs` | 三态不得压成布尔，未执行必须带原因，否则报告会印出没测过的向量数字 |
| `ProductionRetrievalProbe.cs` | 生产检索端口 IRetrievalPort 的逐字适配 | `ProductionRetrievalProbe` | `ProductionRun.cs` | 空结果 claim 必须逐字透传，合并后无法区分没搜过与搜过无果 |
| `ProductionRun.cs` | production 模式跑冻结查询集并写出三份产物 | `ProductionRun`、`ProductionDatabasePath`、`ProductionProbeReport` | `ProductionRetrievalProbe.cs`、`ProductionProbeCapabilities.cs` | 只经在线备份读生产库：InitializeAsync 会发 DDL，指向 code_index.db 必须 fail-closed |
| `Program.cs` | 探针 CLI 入口：按 --mode 分派 count/index/measure/scan/production 并打印生效参数 | `Program`、`ProbeOptions` | `ProductionRun.cs`、`VectorRetrievalModes.cs`、`CorpusInventory.cs` | 作用域运行时必须传 --expected-under，否则越界用例被记成假零分 |
| `RoslynCSharpOutlineSource.cs` | 用 Roslyn 抽 C# 声明概要并喂给分块端口 | `RoslynCSharpOutlineSource` | `ChunkCorpusBuilder.cs` | 与生产 OutlineSyntaxVisitor 手抄同构：只访问声明不下探方法体，改动后概要口径即偏离生产 |
| `RrfHybridProbe.cs` | 全文与向量排名在文件级的 RRF 融合 | `RrfHybridProbe` | `LuceneFullTextProbe.cs`、`VectorSearchProbe.cs` | fusionDepth 必须大于指标窗口 10，否则某侧第 11 名永远无法参与融合 |
| `VectorIndexRunReport.cs` | 一次向量或混合索引构建的机器可读记录 | `VectorIndexRunReport`、`ProbeRunSidecar` | `IndexRunReport.cs`、`VectorRetrievalModes.cs` | 报不出的值留 null 而不是 0，填 0 会让四轴对比出现伪零 |
| `VectorRetrievalModes.cs` | 向量与混合索引模式的组合根 | `VectorRetrievalModes`、`VectorIndexRequest` | `EmbeddingRouteResolver.cs`、`VectorStore.cs`、`VectorIndexRunReport.cs` | 服务不可达必须非零退出，绝不回退全文冒充向量结果 |
| `VectorScanBench.cs` | int8 扫描的规模延迟与等价性对比 | `VectorScanBench`、`ScanBenchReport`、`ScanPointReport` | `VectorStore.cs`、`Program.cs` | 两条路径必须同进程同时测，跨进程比较的延迟与分配不可比 |
| `VectorSearchProbe.cs` | 内存余弦索引到 ISearchProbe 的计时适配（含查询嵌入） | `VectorSearchProbe` | `LuceneFullTextProbe.cs`、`VectorStore.cs` | 查询向量不得缓存，缓存会隐藏嵌入耗时这一主要成本 |
| `VectorStore.cs` | 向量索引的磁盘持久化与重载 | `VectorStore`、`VectorStoreManifest`、`VectorStoreEntry` | `VectorRetrievalModes.cs` | 清单必须记录 float32/int8 与 tierFilter，缺失会让读者按文件名猜格式 |
