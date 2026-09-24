# PuddingVectorIndex CodeMAP（U4-1b → U4-3）

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
| `VectorIndexBuilder.cs` | `VectorDocument` + `VectorIndexBuildOptions(BatchSize, TierFilter, Quantize)` + 构建：经端口批量嵌入并落地索引；**校验批长度、每条向量长度、非空向量**，任一不符即抛；`EmbeddingCalls` / `EmbeddingMs` 单列供四轴报告 |
| `VectorQuantization.cs` | `QuantizedVector`（codes + scale，构造即校验）+ `VectorQuantizer.Quantize/Dequantize`：**对称 absmax**（`scale = max‖x‖/127`、`code = round(x/scale)`），**round-trip 误差界 ≤ scale/2（成文且可断言）**；空/NaN/Inf/全零、非法 scale、越界 code 一律 fail-closed；`Float32StorageBytes` / `Int8StorageBytes` 是**纯逻辑**的格式体积 |
| `QuantizedVectorEntry.cs` | 量化行 = codes + scale + 出处；`ToVectorEntry()` 是**唯一**的「存储形态 → 检索形态」桥（反量化后走既有 float 余弦）|
| `QuantizedVectorMath.cs` | 量化空间相似度：**只提供「反量化后走 `VectorMath.Cosine`」这一条路径**，不引入融合整数点积（否则质量差无法归因于存储格式）|
| `VectorTierFilter.cs` | 分层选择谓词：按 `VectorDocument.Kind`（**字符串值**，序数比较）过滤；`OutlineOnly` = P0。**P0/P1/P2 → tier 名的映射属于组合根**，叶子不持 chunking 的优先级表 |

## 三个不变量（测试断言）

1. **维度一致**：`Add` / `Search` / 构建 / 量化余弦四条路径都对维度不一致显式失败，绝不截断。
2. **批量 = 单条**：`BatchSize=1` 与 `BatchSize=N` 产出的索引**逐向量相同**、检索排名逐位相同（测试用测试替身对比）。
3. **可复现排序**：分数降序 + id 序数平局打破 ⇒ 同输入两次检索结果逐位相同。

## U4-3 增量：int8 量化 + 分层选择（U4-3 报告：`temp/U4-3-REPORT.md`）

两条机制都**默认关闭**，不传选项时构建与检索结果与 U4-1b 逐位一致（A1）。

1. **int8 量化**：`Quantize=true` 时每条向量存为 `sbyte[D]` + 一个 float32 scale（`StorageBytes = D + 4`）。
   - **索引里放的是「反量化后的值」**：真实存储只有 codes，加载后必须先反量化再检索；若把原始 float 留在内存里，
     量化的质量代价会被测成**恰好 0**——这是本刀唯一不能产出的答案。故质量差 = 量化代价，可直接归因。
   - 误差界是**契约**而不是观测：`|x[i] − x'[i]| ≤ scale/2`，证明见 `VectorQuantization.cs` 的 XML 注释。
2. **分层选择**：`TierFilter` 在**嵌入之前**过滤（`VectorIndexBuildOptions.TierFilter`）。被排除的块**不产生任何
   embedding 往返**；这也正是它区别于「后置裁剪」的判据（A7 断言 `EmbeddingCalls`）。
3. **过滤性空 ≠ 真空**：结果为空的索引必须给出 `EmptyReason`，并区分「输入为空」与「被 tier 过滤掉」
   （ADR-089 §2.3；A8），否则一个拼错的 tier 名会被读成「仓库里什么也没有」。

| 断言 | 测试 | 取红方式 |
|---|---|---|
A1 默认不变 | `VectorLayeredBuildTests.A1_Default_Options_Keep_The_Unfiltered_Unquantised_Build` | — |
A2 round-trip 误差界 | `VectorQuantizationTests.A2_Round_Trip_Error_Never_Exceeds_Half_A_Scale_Step` / `A2_Round_Trip_Scales_With_The_Data_Not_With_A_Constant` | M1（固定 scale=1）|
A3 维度/退化 fail-closed | `VectorQuantizationTests.A3_Dimension_Mismatch_And_Degenerate_Inputs_Fail_Closed` | M2（越界 code 静默 clamp）、M3（维度静默截断）|
A4 scale 合法 | `VectorQuantizationTests.A4_Scale_Must_Be_Positive_And_Finite` | — |
A5 量化后排序稳定 | `VectorLayeredBuildTests.A5_Quantised_Search_Is_Repeatable_And_Breaks_Ties_By_Id_Ordinal` | M6（去掉 id 序数打破）|
A6 分层过滤生效 | `VectorLayeredBuildTests.A6_The_Tier_Filter_Keeps_Exactly_The_Selected_Blocks` | M4（后置裁剪）|
A7 分层不改变剩余块 | `VectorLayeredBuildTests.A7_Filtering_Only_Removes_Blocks_It_Never_Changes_The_Survivors` | M4 |
A8 过滤性空 | `VectorLayeredBuildTests.A8_Filtered_Empty_Is_Reported_As_Filtered_Empty_Not_As_Nothing_To_Index` | M5（空索引不报原因）|
A9 体积单调性 | `VectorLayeredBuildTests.A9_Int8_Storage_Is_Smaller_Than_Float32_And_P0_Is_Smaller_Than_Every_Tier` | M4 |
A10 边界不破 | **沿用既有** `ComponentBoundaryTests`（进程未加载禁用程序集 + csproj 两个引用计数 = 0 + 源码 token 扫描）| — |

## 测试工程（Source/PuddingVectorIndexTests）

- `ComponentBoundaryTests`：检测器自检（**阳性对照**：程序集名 + 源码 token）+ 进程未加载 8 个禁用程序集
  + deps.json 依赖闭包 + 组件程序集引用表无 HTTP/数据库/Lucene + **源码不得出现 `HttpClient`/`http://`/`apiKey`/`baseUrl`**
  + 读盘校验组件 csproj 的 `ProjectReference`/`PackageReference` 必须为 0 + 测试 csproj 只引用本组件。
- `VectorMathTests`：手算期望值（点积 32、范数 5、余弦 0.9746318461970762）、正交/反向/尺度不变、维度与零向量 fail-closed。
- `InMemoryVectorIndexTests`：排序、平局打破、topK、空索引、维度/重复/零向量拒绝、向量拷贝语义、route 解析（含 modelId 带斜杠）。
- `VectorIndexBuilderTests`：空输入、批量=单条、批量切分调用次数、**保序（正交基向量判定批次错位）**、
  不可用 provider / 未声明维度 / 向量长度不符 / 短批 / 空向量全部 fail-closed、`Describe()` 能力字段贯通。
- `VectorQuantizationTests`（U4-3）：A2/A3/A4 + 拷贝语义 + 量化空间余弦。
- `VectorLayeredBuildTests`（U4-3）：A1 + A5~A9 + 空 tier 名与拼错 tier 的 fail-closed 行为。

## 边界之外（诚实登记）

- **无持久化**：组件不写盘（纯逻辑）。向量存储的落盘/回读在探针侧（`VectorStore.cs`，
  `vectors.f32` / `vectors.i8` + `manifest.json`），因为「索引体积」这一轴量的是**磁盘产物**，属组合根职责。
  组件只定义**每行多少字节**（`QuantizedVector.StorageBytes`），不定义文件布局。
- **无加权/无 ANN**：本刀只做暴力余弦，不引入近似索引（会把自身的召回损失混进「评估 embedding」的结论里）。
  规模边界见 `temp/U4-1b-REPORT.md` 与 `temp/U4-3-REPORT.md` 的诚实留白。
- **量化口径单点**：只做对称 absmax（per-vector scale），不做 per-channel / 分组量化 / 残差编码；
  `i8` 行的 scale 是**每行**一个（不是全店一个），因为 absmax 的 scale 由向量本身决定。
