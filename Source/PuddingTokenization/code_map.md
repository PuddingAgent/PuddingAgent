# PuddingTokenization
> 源指纹: *.cs=7-27c58954b980, *.csproj=1-0942e698fab2 · 条目数: 7 · 最近整理: 2026-11-22

按模型自己的词表做 token 计数的叶子组件：零工程引用，只接收文本并返回数量，不认识任何 Core 类型。

> 源指纹: Source/PuddingTokenization/**=pending · 条目数: 8 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `TokenizerFallbackReason.cs` | 让计数回退可被观测，而不是静默给出一个数字 | `TokenizerFallbackReason` | `TokenizerProvider` | 新增回退原因必须落到唯一枚举值，禁止用通用值兜底 |
| `ModelTokenizerCatalog.cs` | 用精确别名把 modelId 映射到词表身份 | `ModelTokenizerCatalog` | `TokenizerProvider` | 刻意不用 `Contains("deepseek")` 这类模糊匹配；未知模型必须走回退而不是猜 |
| `DeepSeekTokenizerJsonLoader.cs` | 解析 HuggingFace `tokenizer.json` 并产出 BPE 分词器 | `DeepSeekTokenizerJsonLoader`、`TokenizerLoadResult` | `BpeTokenizer` | `ByteLevel` 必须走 `BpeOptions` 开关，自写 `Normalizer` 会把「先切分后映射」的顺序反转，正则匹配不到原文 |
| `IsolatedSplitPreTokenizer.cs` | 切分前隔离特殊标记并保留未匹配片段 | `IsolatedSplitPreTokenizer` | `BpeTokenizer`、`ScalarRegex` | 返回区间必须连续且不重叠、不越界，延迟枚举不得持有已归还的缓冲区 |
| `ScalarRegex.cs` | 让 Split 正则按完整码点覆盖补充平面字符 | `ScalarRegex` | `IsolatedSplitPreTokenizer` | 匹配结果仍须是原文 UTF-16 坐标，不能返回码点下标 |
| `TokenizerProvider.cs` | 解析并缓存分词器，失败时返回带原因的回退句柄 | `TokenizerProvider`、`TokenizerHandle` | `ModelTokenizerCatalog`、`TokenizerFallbackReason` | 缓存键是词表身份而不是 modelId，同一身份只解析一次（`ConcurrentDictionary` 加 `Lazy`） |
| `TokenizerAssetDirectory.cs` | 决定去哪儿找词表资产并标记来源 | `TokenizerAssetDirectory`、`TokenizerAssetResolution` | `PUDDING_TOKENIZER_ASSETS` | 判据是目录里至少有一份 `*.tokenizer.json` 而不是目录存在；资产必须是 V4.1-Flash 那份（6,367,257 B），换 V4-Pro 会算出错数而不是报错（SHA256 C90DFA01…D599476B）；`PUDDING_TOKENIZER_ASSETS` 非空时优先且不得被覆盖 |
| `PuddingTokenization.csproj` | 叶子工程：零工程引用，独占分词引擎包 | — | `Microsoft.ML.Tokenizers` | `Microsoft.ML.Tokenizers` 只允许在本工程引用；加任何 `ProjectReference` 都会让边界测试变红 |
