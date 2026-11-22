# PuddingRetrieval.Contracts
> 源指纹: Source/PuddingRetrieval.Contracts/**=29-a04eb16cdcaf · 条目数: 28 · 最近整理: 2026-11-22
> 定位：跨 provider 的检索契约（ADR-089 R2-00）。只定义请求、结果、覆盖声明与能力矩阵的形状，不含任何实现。
> 依赖方向：本工程 → PuddingDomain.Contracts → BCL；不认识 CodeIndex、CodeIntelligence、Runtime 与宿主。

## 入口与配置

唯一入口是 `IRetrievalPort.cs`：一次请求完成多路融合与一次交付。契约的不变量在**构造期**校验（`RetrievalRequest`、
`SymbolIdentity`、`RetrievalResult`），违反即抛 `RetrievalContractViolationException`，不做读时补救。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ContainerSymbolGuidance.cs` | 容器类符号在关系面零命中时的统一说法 | `ContainerSymbolGuidance`、`IsContainer`、`BuildEmptyRelationText` | `RetrievalClaim.cs` | — |
| `IRetrievalPort.cs` | 结构化检索的唯一入口 | `IRetrievalPort`、`RetrieveAsync` | `RetrievalRequest.cs`、`RetrievalResult.cs` | 端口只暴露单次检索；多路融合不许外泄成多入口 |
| `LanguageCapability.cs` | 语言能力面矩阵与支持等级 | `ILanguageCapabilityMatrix`、`LanguageCapability`、`RetrievalCapabilityLevel` | `RetrievalClaim.cs` | 不支持的等级必须带替代面；缺替代就等于声明这条路不存在 |
| `RetrievalBudget.cs` | 条数与字节及 token 的双预算常数与估算 | `RetrievalBudget`、`EstimatePayloadBytes`、`EstimateTokens`、`MaxPageSize` | `RetrievalRequest.cs` | 换算是上界估算（UTF-16 按 2 字节、每 token 按 4 字节），不得当计量口径 |
| `RetrievalClaim.cs` | 空结果的否定声明：查过与没查过的面 | `RetrievalClaim`、`ClassifyForEmpty`、`RetrievalClaimKind` | `RetrievalEmptyReason.cs` | 未查过的面必须显式列出；否则空结果会被读成不存在 |
| `RetrievalContractViolationException.cs` | 检索合同跨字段不变量的违反异常 | `RetrievalContractViolationException` | `RetrievalResult.cs` | 不变量在构造期 fail-closed；挪到读取期校验等于让坏对象先流通 |
| `RetrievalDiagnostics.cs` | 查询特异度与过载判定的事实 | `QueryDiagnostics`、`RetrievalOverloadFacts`、`RetrievalOverloadDiagnostics` | `SymbolIdentity.cs` | 过载判定必须在跨 scope 去重之后；否则索引膨胀会被误读成查询过泛 |
| `RetrievalDistribution.cs` | 命中按维度分组的分布摘要 | `RetrievalDistribution`、`FromHits` | `RetrievalPathFacts.cs` | — |
| `RetrievalEmptyReason.cs` | 零命中的原因分类与放宽建议 | `RetrievalEmptyReason`、`RetrievalEmptyReasonPolicy`、`RetrievalIndexAvailability` | `RetrievalClaim.cs` | 索引不可用与查询无命中必须分开；混同会掩盖索引故障 |
| `RetrievalEvidence.cs` | 命中的证据位置与片段 | `RetrievalEvidence`、`NormalizedFilePath`、`Line` | `SymbolIdentity.cs` | 路径规范化必须复用去重键的同一函数，各写一套会让同一文件计成两条 |
| `RetrievalFilter.cs` | 正交过滤面的描述与工厂 | `RetrievalFilter`、`RetrievalFacet`、`ForFileExtensions`、`ForDirectory` | `RetrievalRequest.cs` | 带过滤面的空结果不等于真空；裁决要看有没有过滤约束 |
| `RetrievalHit.cs` | 单条命中的身份与证据及分数 | `RetrievalHit`、`RetrievalHitKind`、`RetrievalConfidence` | `RetrievalEvidence.cs` | — |
| `RetrievalHitComparer.cs` | 命中的全序比较与有序性断言 | `RetrievalHitComparer`、`RetrievalHitOrdering`、`EnsureOrdered`、`EnsureUniqueIdentities` | `RetrievalIntent.cs` | 排序必须全序并以身份键做末级 tie-break；否则同一结果两次顺序不同 |
| `RetrievalHitDeduplicator.cs` | 跨 scope 的符号身份去重 | `RetrievalHitDeduplicator`、`Deduplicate`、`RetrievalDeduplicationResult` | `SymbolIdentity.cs` | 去重必须先于过载判定；重复符号会白吃掉条数预算 |
| `RetrievalHitViews.cs` | 命中折叠成符号视图与文件视图 | `RetrievalHitViews`、`RetrievalSymbolView`、`RetrievalFileView` | `RetrievalResult.cs` | — |
| `RetrievalIntent.cs` | 单次检索的意图枚举 | `RetrievalIntent` | `RetrievalIntentPolicy.cs` | — |
| `RetrievalIntentPolicy.cs` | 意图到过滤面与层序的成文映射 | `RetrievalIntentPolicy`、`LayerOrder`、`RelationFilter`、`SymbolKindFilter` | `RetrievalIntent.cs` | 关系面意图集合与请求校验同源；新增意图要同时进两处 |
| `RetrievalNextStep.cs` | 检索建议的下一步种类与构造 | `RetrievalNextStep`、`Narrow`、`Relax`、`RetrievalNextStepKind` | `RetrievalEmptyReason.cs` | — |
| `RetrievalOverflow.cs` | 预算触顶口径与临时落盘策略 | `RetrievalOverflow`、`RetrievalSpillPolicy`、`RetrievalBudgetKind` | `RetrievalBudget.cs` | 落盘目录限定在允许根内；任意路径会把用户目录当临时区 |
| `RetrievalPathFacts.cs` | 从路径派生语言与顶层目录的纯函数 | `RetrievalPathFacts`、`LanguageOf`、`TopLevelDirectoryOf` | `RetrievalDistribution.cs` | 必须是确定性纯函数；两处各算一次会让同一结果两种统计 |
| `RetrievalRequest.cs` | 检索请求的只读构造与入参校验 | `RetrievalRequest`、`HasAnchor`、`HasFilterConstraints` | `RetrievalBudget.cs`、`SymbolAnchor.cs` | 带锚点时查询词可空但意图必须是关系面；其余意图会静默忽略锚点 |
| `RetrievalResult.cs` | 检索结果的有界交付与溢出回显 | `RetrievalResult`、`Symbols`、`Files`、`IsTruncated` | `RetrievalOverflow.cs` | 超预算要拒绝构造并给出溢出记录；静默截断等于谎报总量 |
| `RetrievalTaxonomy.cs` | 命中层与置信度及关系种类的分类枚举 | `RetrievalHitKind`、`RetrievalConfidence`、`RetrievalRelationKind`、`RetrievalMatchTarget` | `RetrievalHit.cs` | — |
| `ScopeIdSet.cs` | scope 标识的去重规范集合 | `ScopeIdSet`、`Of`、`Union`、`IsCorroborated` | `SymbolIdentity.cs` | 集合按确定顺序编码；用无序集合当键会让相等判定失效 |
| `ScopeOverlap.cs` | 已注册范围的描述与嵌套重叠告警 | `ScopeOverlapDetector`、`ScopeOverlapWarning`、`RetrievalScopeDescriptor` | `SymbolIdentity.cs` | 根路径规范化后才比较；分隔符与大小写差异会漏报嵌套 |
| `SymbolAnchor.cs` | 已解析符号锚点的身份载体 | `SymbolAnchor` | `RetrievalRequest.cs` | 只带身份不带名字；留名字会诱使实现退回名字匹配 |
| `SymbolIdentity.cs` | 跨 scope 去重键的规范化身份 | `SymbolIdentity`、`Create`、`ForTextAt`、`NormalizePath` | `RetrievalHitDeduplicator.cs` | 归一化在构造期完成；绕过构造就得到两种"同一符号" |
| `SymbolRefV1.cs` | 可重定位符号引用的编解码与稳定性判定 | `SymbolRefV1`、`SymbolIdStability`、`MaxTokenLength` | `SymbolAnchor.cs` | token 必须不透明且有长度上限；可读串会被 docId 与路径分隔符击穿 |

## 约束与坑

- 契约的失败面是**构造期**：`RetrievalRequest` 的所有字段只读且无 `with`，校验不可绕过。
- 空结果的三种含义要分清：真空、过滤性空、面未覆盖。三者在 `RetrievalEmptyReason` 与 `RetrievalClaim` 里各有对应表达。
- 去重先于过载判定；否则多 scope 注册造成的结构性膨胀会被判成查询过泛。
