# PuddingVectorIndex CodeMAP（U4-1b）

> 向量索引组件：把「文本 → 向量 → 内存暴力余弦检索」做成**可独立测试的纯逻辑**，而不是塞进探针脚本。
> 边界（ADR-089 索引服务与库管理 §1）：**叶子** —— `ProjectReference = 0`、`PackageReference = 0`（不引入向量数据库、不新增 NuGet）。
> **关键设计**：向量由**端口 `IEmbeddingProvider`** 从外部喂入，route（`provider/model`）只是**值**；组件**不解析** route、
> **不读**资源池配置、**不持** apiKey/baseUrl、**不直连** HTTP。解析 route → 活的服务实例由**组合根**（探针）做。
> 命名空间：`PuddingVectorIndex`

## 契约（EmbeddingContracts.cs）

| 类型 | 用途 |
|------|------|
| `EmbeddingRoute` | 用户口径的 `"服务商/模型"` 值对象；`Parse` 按**第一个**斜杠切分（modelId 本身可含 `/`），空白即 fail-closed |
| `EmbeddingModelInfo` | 描述向量服务：维度 / 最大上下文 token / 是否本地 / **配置级**可用性（不是存活探测）+ `Route` |
| `IEmbeddingProvider` | **唯一端口**：`Describe()` / `EmbedAsync(text)` / `EmbedBatchAsync(texts)`（**保序**）|

> 刻意**不复用** `PuddingCode.Abstractions.IEmbeddingService`：它把 provider 固定在实现内部且已被记忆域
> （`SessionChunkIndexer` / `EmbeddingGenerationHook`）消费，改签名会打穿记忆层。本端口是**新增**且**带 route** 的。

## 实现

| 文件 | 用途 |
|------|------|
| `VectorMath.cs` | `Dot` / `Norm` / `Cosine` / `Normalize`。**维度不一致抛异常（绝不静默截断）**；零向量抛异常（方向不存在，分数不能编造）；累加用 `double`（1024 维 float 相加会因精度重排近分）|
| `VectorIndexEntry.cs` | 向量条目：向量（构造即**拷贝**）+ 出处（文件/行范围/层级/boost）；`VectorSearchResult` 含 `Score` 与 0 基 `Rank` |
| `InMemoryVectorIndex.cs` | **内存暴力余弦**索引：`Add` 拒绝维度不符 / 重复 id / 零向量；`Search` 按分数降序、**同分按 id 序数**打破平局（否则两次运行 top-k 可能不同）|
| `VectorIndexBuilder.cs` | `VectorDocument` + `VectorIndexBuildOptions(BatchSize)` + 构建：经端口批量嵌入并落地索引；**校验批长度、每条向量长度、非空向量**，任一不符即抛；`EmbeddingCalls` / `EmbeddingMs` 单列供四轴报告 |

## 三个不变量（测试断言）

1. **维度一致**：`Add` / `Search` / 构建三条路径都对维度不一致显式失败，绝不截断。
2. **批量 = 单条**：`BatchSize=1` 与 `BatchSize=N` 产出的索引**逐向量相同**、检索排名逐位相同（测试用测试替身对比）。
3. **可复现排序**：分数降序 + id 序数平局打破 ⇒ 同输入两次检索结果逐位相同。

## 测试工程（Source/PuddingVectorIndexTests）

- `ComponentBoundaryTests`：检测器自检（**阳性对照**：程序集名 + 源码 token）+ 进程未加载 8 个禁用程序集
  + deps.json 依赖闭包 + 组件程序集引用表无 HTTP/数据库/Lucene + **源码不得出现 `HttpClient`/`http://`/`apiKey`/`baseUrl`**
  + 读盘校验组件 csproj 的 `ProjectReference`/`PackageReference` 必须为 0 + 测试 csproj 只引用本组件。
- `VectorMathTests`：手算期望值（点积 32、范数 5、余弦 0.9746318461970762）、正交/反向/尺度不变、维度与零向量 fail-closed。
- `InMemoryVectorIndexTests`：排序、平局打破、topK、空索引、维度/重复/零向量拒绝、向量拷贝语义、route 解析（含 modelId 带斜杠）。
- `VectorIndexBuilderTests`：空输入、批量=单条、批量切分调用次数、**保序（正交基向量判定批次错位）**、
  不可用 provider / 未声明维度 / 向量长度不符 / 短批 / 空向量全部 fail-closed、`Describe()` 能力字段贯通。

## 边界之外（诚实登记）

- **无持久化**：组件不写盘（纯逻辑）。向量存储的落盘/回读在探针侧（`VectorStore.cs`），
  因为「索引体积」这一轴量的是**磁盘产物**，属组合根职责。
- **无加权/无 ANN**：本刀只做暴力余弦，不引入近似索引（会把自身的召回损失混进「评估 embedding」的结论里）。
  规模边界见 `temp/U4-1b-REPORT.md` 的诚实留白。
