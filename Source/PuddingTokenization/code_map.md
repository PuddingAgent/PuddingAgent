# PuddingTokenization — code_map

按模型自己的词表做 token 计数。**叶子组件**：零 `ProjectReference`，不认识 `ChatMessage` /
`LlmToolDefinition` / `ContextUsageSnapshot` 等任何 Core 类型；只接收文本，返回数量。

## 为什么独立成组件

词表选择原先写在 `PuddingCore/Platform/LlmOptions.cs` 里，因此：

1. 不启动宿主就无法验证（这条缺陷正是这样活了很久：把 DeepSeek 当成 OpenAI 词表算，
   实测高估 **1.83 倍**）；
2. `Microsoft.ML.Tokenizers` 被绑在 LLM 客户端里，测试必须拖上整个 Core。

拆出来后可以在独立工程里编译、用官方向量比对、变异取红，全程不碰宿主。

## 边界（由测试强制，不靠文档）

`PuddingTokenizationTests/ComponentBoundaryTests.cs`：

- `Component_HasNoProjectReferences` — 运行期枚举引用程序集，出现任何 `Pudding*` 即失败；
- `Component_DoesNotReferenceCoreOrRuntimeTypes` — 双重保险，禁止宿主类型名出现。

依赖方向只能是 `PuddingCore → PuddingTokenization`。反向引用会立刻被上述测试拒绝。

## 文件职责

| 文件 | 职责 |
|---|---|
| `TokenizerFallbackReason.cs` | 回退原因枚举。存在的意义是让「回退」可观测，而不是静默给一个数字 |
| `ModelTokenizerCatalog.cs` | `modelId → 词表身份` 的**精确别名**映射。刻意不用 `Contains("deepseek")` |
| `DeepSeekTokenizerJsonLoader.cs` | 解析 HuggingFace `tokenizer.json` → `BpeTokenizer` |
| `TokenizerProvider.cs` | `TokenizerHandle`（单次解析结果）+ 按 modelId 缓存与回退 |
| `PuddingTokenization.csproj` | 唯一允许引 `Microsoft.ML.Tokenizers` 的地方 |

## 三个必须保留的实现细节（每一个都是踩出来的）

1. **`BpeOptions.ByteLevel = true`**
   关掉它，`"人工智能"` 会编码成 **0 个 token**（实测）。DeepSeek 词表以 GPT-2 风格的字节
   表示存储（空格写作 `Ġ`），不做字节映射则 UTF-8 中文匹配不到任何词条。
   注意：ByteLevel 必须走 `BpeOptions` 开关，**不要**自写 `Normalizer` 来做字节映射 ——
   官方语义是 `Sequence[Split…, ByteLevel]`（先切分后映射），而 `Create(preTokenizer,
   normalizer)` 是「先 normalizer」，顺序反转会让正则匹配不到原文而失效。

2. **三条 Split 正则从 JSON 程序化读取**
   第三条含引号、反斜杠与 `\p{L}` 等 Unicode 类别转义，手抄极难与上游一致；差一个字符
   产出的计数就是错的，而且看不出来。

3. **`specialTokens` 必须逐级传给 `RegexPreTokenizer` / `CompositePreTokenizer`**
   只传给 `BpeOptions` 无效 —— 实测 `<｜begin▁of▁sentence｜>` 会被拆成 12 个普通 token，
   正确结果是单 token `[0]`。

## 词表资产

不在仓库里（6.4 MB），也不嵌入程序集。由 `Tools/Tokenization/Prepare-TokenizerAssets.ps1`
安装到数据根 `D:\Data\models\tokenizers\`，并校验长度与 SHA-256：

```
文件名  deepseek-v4.1-flash.tokenizer.json
字节数  6,367,257
SHA256  C90DFA01249DB1BE4245780A052EDE752E1361C612AC6D08E2BDADA7D599476B
```

⚠️ **必须是 V4.1-Flash 那一份**。V4-Pro / V4-Flash 的 `tokenizer.json` 是另一个文件
（5,530,562 B，ETag `628e3364…`），拿它去算 V4.1 会产出错误计数而不是报错 —— 哈希就是
为拦住这个而钉的。

运行时目录来源（`LlmOptions.ResolveTokenizerAssetDirectory`）：
`PUDDING_TOKENIZER_ASSETS` 环境变量 → 应用目录旁的 `tokenizers/` → 都没有则回退通用编码。

## 正确性判据

**唯一**判据是 `GoldenVectorTests.EncodeMatchesOfficialTokenizerPerTokenId`：期望值由官方
`@huggingface/tokenizers`（Node 0.2.0）对同一份 `tokenizer.json` 生成，与本实现**逐 token ID**
比对，10/10 相同。

round-trip 可逆只说明映射自洽，不排除「两边一起偏移但各自自洽」，**不足以**作为完成判据。
