# DeepSeek Tokenizer 在 .NET 的可用性调研（2026-10-24）

- **目的**：判断「用真实 DeepSeek 词表替代 `o200k_base` 估算」是否可行，为上下文用量诊断报告 §13.4 的方案 A 提供决策依据。
- **方法**：多引擎检索（deepseek_search / anysearch_search）+ **本地程序集反射**（比文档可靠）+ 直接探测 HuggingFace 端点。
- **结论先行**：**方案 A 可行且不需要换库**，但需要引入 5.5 MB 词表资源；建议「先上 B 止血、A 作为根治跟进」。

## 1. 本地依赖现状（已核实）

`Source/PuddingCore/PuddingCore.csproj` 引用：

```
Microsoft.ML.Tokenizers                        2.0.0
Microsoft.ML.Tokenizers.Data.Cl100kBase        2.0.0
Microsoft.ML.Tokenizers.Data.O200kBase         2.0.0
```

两个 Data 包**都不含 DeepSeek 词表**，所以现有依赖不能直接算出 DeepSeek token。

## 2. 关键证据：`BpeTokenizer` 支持任意 BPE（本地反射，非文档）

对运行目录里的 `Microsoft.ML.Tokenizers.dll` 反射 `BpeTokenizer` 的全部 public static `Create` 重载：

```
Create(System.String, System.String)
Create(System.String, System.String, PreTokenizer, Normalizer, IReadOnlyDictionary<string,int>, String, String, String, Boolean)
Create(Microsoft.ML.Tokenizers.BpeOptions)
Create(System.IO.Stream, System.IO.Stream)
Create(System.IO.Stream, System.IO.Stream, PreTokenizer, Normalizer, IReadOnlyDictionary<string,int>, String, String, String, Boolean)

BpeOptions ctors:
  BpeOptions(IEnumerable<KeyValuePair<string,int>>)      // 直接喂内存里的 vocabulary
  BpeOptions(string, string)                              // vocabFile, mergesFile
```

**意义**：

- `Create(string vocabFile, string mergesFile)` 与 `Create(Stream, Stream)` 都能加载自定义词表；
- `BpeOptions(IEnumerable<KeyValuePair<string,int>>)` + `Merges` 属性允许**先把 `tokenizer.json` 解析成内存结构再构造**，不必落地 vocab/merges 两个中间文件。

官方文档的选型建议也一致（deepseek_search 命中 learn.microsoft.com 与 dotnet/machinelearning #7512）：
> Use `TiktokenTokenizer` for OpenAI models. Use `BpeTokenizer` for everything else that uses BPE.

## 3. 关键证据：DeepSeek V4 词表公开可得

直接探测（非二手描述）：

| URL | 结果 |
|---|---|
| `https://huggingface.co/deepseek-ai/DeepSeek-V4-Pro/raw/main/tokenizer.json` | **HTTP 200**，`Content-Disposition: filename="tokenizer.json"`，**5,530,562 字节** |
| `https://huggingface.co/deepseek-ai/DeepSeek-V4-Flash/raw/main/tokenizer.json` | **HTTP 200**，**5,530,562 字节**，**ETag `W/"628e3364caad11bdf9e67cea06eae7878122811d"` 与 Pro 完全相同** |
| `https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/raw/main/tokenizer.json` | **HTTP 200**，**5,530,707 字节**，**ETag `W/"6a15814dd25c934028034531da744c689f87ff21"`（与 V4 不同）** |

两仓库 `X-Repo-Commit` 不同（`b5968e91…` vs `60d8d707…`），但**文件 ETag 与字节数完全相同** ⇒ V4-Pro 与 V4-Flash 是同一个词表文件。

**但 V4.1-Flash 不是同一份**：同为 HTTP 200，却 **5,530,707 字节、ETag `6a15814d…`**，与 V4 的 `628e3364…` 不同。
差 **145 字节（≈0.0026%）**，量级上像是 `added_tokens` 里有少量调整（V4.1-Flash 的 pipeline 标为 `image-text-to-text`，可能新增了多模态相关特殊 token），词表主体应高度重叠但**不能字节级等同**。
⇒ 实施时必须选**对应模型**的那一份，不能拿 V4 的 JSON 套到 V4.1 上。

其他旁证：

- 仓库 `license: mit`（HF API `tags` 中 `license:mit`）—— 许可上没有障碍。
- 内容首段是标准 HuggingFace tokenizers JSON：`{"version":"1.0","added_tokens":[{"content":"<｜begin▁of▁sentence｜>",...}]}`，与 DeepSeek 一贯的 special token 一致。
- llama.cpp 侧的 GGUF 元数据显示 `tokenizer.ggml.model = bpe`、`tokenizer.ggml.pre = joyai-llm` ⇒ 确为 BPE 家族。
- 多个独立来源给出 V4 词表大小 **129,280**（NVIDIA TensorRT-LLM `vocab_size=129280`、LLM Explorer、teacher-cache 探针）；NVIDIA 注释更写明「Derived from the **DeepSeek-V3** tokenizer vocab size (129280)」⇒ V3/V4 词表规模相同，很可能同一份。

## 4. 一个必须写明的方法论修正

我第一次探测用到 `https://huggingface.co/api/models/<repo>/blob/main/tokenizer.json`，返回 **404**。若就此写「文件不存在」就错了 —— 用**同样端点**查 `config.json`（该文件确定存在，`tree` API 已列出、size 1828）**同样 404**，说明是 **API 路径格式问题**，不是文件缺失。

⇒ 这也是本项目反复强调的「阴性结论必先做阳性对照」在此处的直接应用。

## 5. 剩余未确认项（不得当作结论）

1. **`tokenizer.json` 的 `model.type` 是否恰为 `"BPE"`、`vocab`/`merges` 的字段形态**：只读到开头的 `added_tokens` 段，`model` 段在 5.5 MB 文件的后部，尚未读取。
2. ~~我们实际路由的 `deepseek-flash`（V4.1-Flash）是否与 V4-Flash 共用同一词表~~ —— **已确认：不共用**（见 §3）。⇒ 实施时必须锁定 `deepseek-ai/DeepSeek-V4.1-Flash` 自己的 `tokenizer.json`，**不能**拿 V4 的 JSON 套上去。
3. **5.5 MB 词表的加载耗时**：`BpeTokenizer` 构建 129K 词表 BPE 的冷启动成本未知；若每次进程启动都解析会明显拖慢。
4. **分发方式**：嵌入资源（撑大 `PuddingCore.dll` 或独立资源程序集）vs 首次运行下载并缓存（引入网络依赖与校验需求）。

## 6. 建议

**分两步，不二选一：**

**第一步（止血，零依赖）**：采用诊断报告 §13.4 的方案 B —— 允许 `observedRatio < 1.0` 参与校准，加「连续 3 轮一致才下调、下限 0.5、出现 `> 1.0` 立即回升」的保护。它能自适应 §15.1 观测到的漂移，且不引入任何资源与分发问题。

**第二步（根治，可并行准备）**：方案 A。现在可行性已从「三个未知」降为「有明确工程量」：

1. 解析 `tokenizer.json` 的 `model.vocab` / `model.merges` → `BpeOptions(vocabulary) { Merges = … }`；
2. 作为独立资源随应用分发（不走运行时下载，避免网络依赖）；
3. 按 modelId 选择 tokenizer，**默认回退到现有 `o200k_base`**，即词表缺失时行为不变；
4. 上线前用真实请求做一次对照：同一 prompt 分别用新旧估算，与 Provider 回报的 `prompt_tokens` 比对，误差应显著小于当前的 1.83 倍。

**词表来源已确定**（§3）：用 `deepseek-ai/DeepSeek-V4.1-Flash` 的 `tokenizer.json`（5,530,707 B，MIT），**不要**用 V4 的（两者 ETag 不同）。代码里按 modelId 映射，**映射缺失时回退 `o200k_base`**，保证行为不劣于现状。

## 7. 调研受限说明

- `github_search` 全程失败：`api.github.com` 的 TLS 被本机代理阻断（`The SSL connection could not be established`），与本仓库 2026-10-23 记录的同一环境缺陷一致。因此 GitHub 侧只能靠搜索结果中的快照，未做仓库级穷举。
- `zhihu_search` 本次未调用（该主题知乎内容预期价值低）；如需中文社区实操经验可补。
- HuggingFace `tree` API 的完整文件列表被 64 个 safetensors 条目挤占而未读完，故 tokenizer.json 的存在性改用 raw 直链 200 + Content-Disposition 确认，而非文件清单。


## 8. 实测探针：默认加载路径不可用，方案 A 需要自建预分词链路（2026-10-24 09:53）

### 8.1 做了什么

1. 下载 `deepseek-ai/DeepSeek-V4.1-Flash` 的 `tokenizer.json`（**6,367,257 字节**，SHA256 `C90DFA01249DB1BE4245780A052EDE752E1361C612AC6D08E2BDADA7D599476B`）。
2. 解析并拆出 `BpeTokenizer.Create(vocabFile, mergesFile)` 需要的两个文件：
   - `ds-vocab.json`：**128,000 条**，4,082,558 字节
   - `ds-merges.txt`：**127,741 行**，1,632,575 字节（格式为 `"Ġ t"` 空格分隔字符串，**不是**二元数组）
3. 用 .NET 10 file-based app 直接调用 `BpeTokenizer.Create(vocab, merges)` 编码样本。

### 8.2 结构化事实（`System.Text.Json` 精确读取）

```
MODEL_TYPE     = BPE
VOCAB          = 128,000 entries          （首项 <｜begin▁of▁sentence｜>=0, <｜end▁of▁sentence｜>=1, <｜▁pad▁｜>=2）
MERGES         = 127,741                  （元素类型 = String）
ADDED_TOKENS   = 1,283
PRE_TOKENIZER  = Sequence[
                   Split(\p{N}{1,3}, Isolated),
                   Split([一-龥぀-ゟ゠-ヿ]+, Isolated),
                   Split([!"#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~][A-Za-z]+|[^\r\n\p{L}\p{P}\p{S}]?[\p{L}\p{M}]+| ?[\p{P}\p{S}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+, Isolated),
                   ByteLevel(add_prefix_space=false, trim_offsets=true, use_regex=false)
                 ]
DECODER        = ByteLevel(...)
NORMALIZER     = Sequence[]               （空）
```

⚠️ 顺带一条工具坑：`ConvertFrom-Json` 解析该文件会**直接失败** —— 词表里存在仅大小写不同的键（`A` / `a`）而 PowerShell 对象属性大小写不敏感。必须用 `-AsHashtable`；而 `-AsHashtable` 对这种深层结构给出的 `.Count` 也可能是**错的**（实测报 8,420，真实为 128,000）。**以 `System.Text.Json` 的 `JsonDocument` 枚举结果为准。**

### 8.3 探针原始输出（`dotnet run`，代码如下）

```csharp
var tok = BpeTokenizer.Create(vocab, merges);
// 样本 → EncodeToIds
```

```
LOADED in 293 ms
ascii      chars=11   tokens=2    roundtrip=False ids=[33310,29616]
cjk        chars=4    tokens=0    roundtrip=False ids=[]
code       chars=22   tokens=9    roundtrip=False ids=[3465,40897,10,90,2605,3916,90,13,19]
json       chars=17   tokens=11   roundtrip=True  ids=[24313,67,3362,19,3184,68,96893,20,14,21,48663]
bos_token  chars=21   tokens=6    roundtrip=False ids=[30,8277,2154,85,51015,32]
eos_token  chars=19   tokens=6    roundtrip=False ids=[30,523,2154,85,51015,32]
pad_token  chars=9    tokens=3    roundtrip=False ids=[30,24591,32]
mixed      chars=29   tokens=8    roundtrip=False ids=[67612,6900,5598,11093,4287,16,25,7]
```

**三条结论：**

1. **加载性能不是问题**：128K 词表 + 127K merges 构建仅 **293 ms**，不必预编译缓存。
2. **中文完全编码不出来**：`"人工智能"` → **0 个 token**。原因是 DeepSeek 词表用 GPT-2 风格的 byte-level 表示（`Ġ`=空格），而 `BpeTokenizer.Create(vocab, merges)` 的默认路径**不做 bytes→unicode 映射**，UTF-8 中文在这样的词表里匹配不到任何条目。
3. **special token 未被识别**：`<｜begin▁of▁sentence｜>` 应是 `[0]`，实际被拆成 **6 个**普通 token `[30,8277,2154,85,51015,32]`。默认路径没有注册 `specialTokens`。

⇒ **这直接证实了「不能把词表成功加载当作完成判据」**。若只看“LOADED 成功”就收工，产出的计数会比现在的 `o200k_base` **更糟**（中文直接算 0）。

### 8.4 库能力边界（本地反射）

`Microsoft.ML.Tokenizers` 2.0.0 的公开 `PreTokenizer` 只有：

```
CompositePreTokenizer
PreTokenizer              （抽象基类，PreTokenize(string) → IEnumerable<(int,int)>）
RegexPreTokenizer
RobertaPreTokenizer
```

**没有 `ByteLevelPreTokenizer`。** 公开 `Normalizer` 也只有 `LowerCaseNormalizer` / `UpperCaseNormalizer` / `SentencePieceNormalizer`。

### 8.5 对实施方案的影响（修正 codex 规划的成本估计）

codex 规划里 `DeepSeekPreTokenizer.cs` 一项写着“按实际 JSON 内容决定所需逻辑”。实测表明这是**必须实现、且不止一个类**：

| 需要的能力 | 库是否现成 | 实现方式 |
|---|---|---|
| 三条正则 Split | `RegexPreTokenizer` + `CompositePreTokenizer` 可组合 | 组合即可 |
| **ByteLevel（bytes→unicode + 字节级切分）** | **无** | 必须自定义。语义上属文本变换，落在 `Normalizer` 子类更合适（`PreTokenizer` 只返回区间，改不了字符） |
| special token 单 id 识别 | 需传入 `specialTokens` 字典 | 从 `added_tokens`（1,283 条）构造 |
| 空 Normalizer | — | DeepSeek 的 `normalizer` 为空序列，自定义 ByteLevel 需排在正确位置 |

**因此方案 A 的真实成本 = 自建一条完整预分词链路 + 黄金样本逐 token 对齐**，而不是“加载一份词表”。可行性未变（`PreTokenizer`/`Normalizer` 均为可继承的抽象类），但工作量显著高于初判，且中间任何一步不一致都会让计数比现状更差。

### 8.6 仍待确认

1. `Microsoft.ML.Tokenizers` 的 `Normalizer` 基类签名是否允许改变长度（ByteLevel 会把 1 个中文字符变成 3 个字节字符），需读其公开成员确认。
2. `BpeTokenizer` 内部是否在 BPE 前还会做额外的空白/大小写处理，可能与 ByteLevel 叠加。
3. 黄金样本仍需官方 `transformers` 产出（本机 python 为 Store 存根，不可用）—— 需要另找途径（离线 wheel、或 Node 的 `@huggingface/tokenizers`、或官方 `encoding/` 目录里的参考实现）。


## 9. 探针 v2：`ByteLevel=true` 解决了中文，special token 仍需 PreTokenizer（2026-10-24 09:58）

### 9.1 `BpeOptions` 的完整能力（本地反射）

```
IEnumerable<KeyValuePair<string,int>> Vocabulary   (get)
IEnumerable<string>                   Merges        (get/set)
IReadOnlyDictionary<string,int>       SpecialTokens (get/set)
Normalizer                            Normalizer    (get/set)
PreTokenizer                          PreTokenizer  (get/set)
string                                UnknownToken  (get/set)
bool                                  FuseUnknownTokens (get/set)
string                                ContinuingSubwordPrefix (get/set)
string                                EndOfWordSuffix (get/set)
bool                                  ByteLevel      (get/set)   ← 关键
string                                BeginningOfSentenceToken (get/set)
string                                EndOfSentenceToken (get/set)
```

`Normalizer.Normalize(string) → string` 且 `Normalizer` 为 **abstract**（可继承）；`RegexPreTokenizer(Regex, IReadOnlyDictionary<string,int>)` 与 `CompositePreTokenizer(IReadOnlyList<PreTokenizer>, IReadOnlyDictionary<string,int>)` 均接受 special token 字典 —— 说明 **special token 的切分发生在 PreTokenizer 层**。

### 9.2 探针 v2 结果（`ByteLevel = true` + `SpecialTokens`，未传 PreTokenizer）

```
vocab=128000 merges=127741
CREATED in 200 ms (ByteLevel=true)

ascii      chars=11   tokens=2    roundtrip=False ids=[33310,29616]
cjk        chars=4    tokens=1    roundtrip=True  ids=[33574]
code       chars=22   tokens=9    roundtrip=False ids=[3465,40897,10,90,2605,3916,90,13,19]
json       chars=17   tokens=11   roundtrip=True  ids=[24313,67,3362,19,3184,68,96893,20,14,21,48663]
bos_token  chars=21   tokens=12   roundtrip=True  ids=[30,28217,8277,5487,226,2154,5487,226,85,51015,28217,32]
eos_token  chars=19   tokens=12   roundtrip=True  ids=[30,28217,523,5487,226,2154,5487,226,85,51015,28217,32]
pad_token  chars=9    tokens=9    roundtrip=True  ids=[30,28217,5487,226,24591,5487,226,28217,32]
mixed      chars=29   tokens=11   roundtrip=False ids=[67612,22250,6900,5598,11093,15581,2295,4287,16,25,7]
```

**对照探针 v1（默认路径）**：

| 样本 | v1 tokens | v2 tokens | 变化 |
|---|---|---|---|
| `人工智能` | **0** | **1**（roundtrip ✅） | 中文从「编码不出来」变成正确单 token |
| `{"a":1,"b":[2,3]}` | 11 | 11（roundtrip ✅） | 一致 |
| `<｜begin▁of▁sentence｜>` | 6 | **12** | 更碎，说明**仍未按 special token 处理** |

⇒ **`ByteLevel = true` 是必需的，且是解决中文的决定性开关**；加载仍只要 **200 ms**。

### 9.3 仍未解决的一项

`SpecialTokens` 单独传给 `BpeOptions` **没有让 special token 被整体识别**（`bos_token` 反而从 6 碎成 12）。结合 9.1 的构造签名，正确做法应是把它**同时**传给 `CompositePreTokenizer` / `RegexPreTokenizer`，由 PreTokenizer 层整体匹配。

**但影响面需冷静评估**：真实请求的 system prompt 与用户消息里通常不出现 `<｜begin▁of▁sentence｜>` 这类字面量（它们是模型内部标记），因此 special token 处理对「估算 prompt token 数」的权重可能很小。**不要为了这一项阻塞主线**，但也不能在实现时假装它不存在。

### 9.4 下一步（S1 尚未完成的部分）

1. 用 `CompositePreTokenizer([RegexPreTokenizer(数字正则), RegexPreTokenizer(CJK正则), RegexPreTokenizer(标点/空白正则)], specialTokens)` 复现官方的三条 Split，**顺序必须一致**。
2. 此时才把 `PreTokenizer` 与 `SpecialTokens` 一起传入 `BpeOptions`。
3. 用黄金样本验证**逐 token ID 一致** —— 这是唯一能判定「真的对齐了」的标准，`roundtrip=True` 和「能编码出非 0 个 token」都**不足以**作为完成判据。
4. ⚠️ 顺序隐患：官方 `Sequence[Split×3, ByteLevel]` 是「先切分再字节映射」，而 `Create(preTokenizer, normalizer)` 的语义是「先 normalizer 后 preTokenizer」。当前靠 `ByteLevel=true` 这个开关规避了顺序问题；若改为自写 Normalizer 做字节映射，**顺序会反转并导致正则匹配失效**，不可取。

### 9.5 黄金样本的现实障碍

本机 `python` 为 Microsoft Store 存根（`python.exe` 不可执行），无法直接用官方 `transformers` 生成期望 token ID。可选替代：

1. 复用 `deepseek-ai/DeepSeek-V4.1-Flash` 仓库自带的 `encoding/encoding_dsv4.py` 作为**参考实现**读其逻辑（需要能跑 Python，同样受阻）。
2. 用 Node 侧 `@huggingface/tokenizers` 或已发现的 `@tlibnx/tokenizer-deepseek_v4_flash` 生成期望值（Node 是否可用未核实）。
3. 用 Provider 实测值做**区间校验**：对若干条真实已发送请求，比较本地估算与 `providerPromptTokens` 的偏差，要求显著优于当前 1.83 倍 —— 这只能证明「更好」，不能证明「逐 token 一致」，但可作为回归门槛。

在拿到逐 token 一致的证据前，**不得宣称方案 A 已可用**。


## 10. 探针 v3：三条正则 + special tokens 全链路打通（2026-10-24 10:19）

### 10.1 做法

三处关键改进，均针对 v2 的遗留问题：

1. **正则从 `tokenizer.json` 程序化读取**，不手抄 —— 第三条正则含 `"`、`[`、`\\`、`\p{L}` 等，手抄极易出错。
2. **`CompositePreTokenizer` 的每一级都传入 `specialTokens`**（v2 只传给 `BpeOptions`，无效）。
3. **`specialTokens` 从 `added_tokens` 全量构造**（1,283 条），不再只给 3 个。

### 10.2 输出（`dotnet run`，exit=0）

```
model=BPE vocab=128000 merges=127741 special=1283 byteLevelFlag=True
splitPatterns=3
  [0] \p{N}{1,3}
  [1] [一-龥぀-ゟ゠-ヿ]+
  [2] [!"#$%&'()*+,\-./:;<=>?@\[\\\]^_`{|}~][A-Za-z]+|[^\r\n\p{L}\p{P}\p{S}]?[\p{L}\p{M}]+| ?[\p{P}\p{S}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+
composite built with 3 split stages
CREATED in 185 ms

ascii      chars=11   tokens=2    rt=True  ids=[33310,2058]
cjk        chars=4    tokens=1    rt=True  ids=[33574]
cjk_long   chars=11   tokens=5    rt=True  ids=[112356,589,23229,1959,25556]
digits     chars=10   tokens=4    rt=True  ids=[6895,18009,25744,18]
code       chars=22   tokens=8    rt=True  ids=[3465,52735,4042,2605,1354,1527,13,19]
json       chars=17   tokens=11   rt=True  ids=[24313,67,3362,19,3184,68,96893,20,14,21,48663]
bos_token  chars=21   tokens=1    rt=True  ids=[0]
eos_token  chars=19   tokens=1    rt=True  ids=[1]
pad_token  chars=9    tokens=1    rt=True  ids=[2]
mixed      chars=29   tokens=14   rt=True  ids=[67612,223,22250,7283,5598,11093,223,15581,2295,223,4287,16,25,7]
```

### 10.3 三个版本对比

| 样本 | v1 默认 | v2 仅 ByteLevel | **v3 + Composite** |
|---|---|---|---|
| `人工智能` | **0** | 1 | **1** ✅ |
| `1234567890` | — | — | **4** ✅（`\p{N}{1,3}` 生效的直接证据：10 位切成 3+3+3+1） |
| `<｜begin▁of▁sentence｜>` | 6 | 12 | **1 → `[0]`** ✅ |
| `hello world` | 2（rt=False） | 2（rt=False） | **2（rt=True）** ✅ |
| `Agent 调用 http_fetch 失败率 51.7%` | 8（rt=False） | 11（rt=False） | **14（rt=True）** ✅ |
| **全部样本 roundtrip** | 1/8 | 4/8 | **10/10** ✅ |

**四个可独立验证的正确性信号**（不只是「能跑」）：

1. `bos/eos/pad` → **精确** `[0]` / `[1]` / `[2]`，与 vocab 声明一致；
2. `1234567890` → 4 token，**恰是 `\p{N}{1,3}` 的切分行为**，证明正则真的参与运算而非被忽略；
3. `人工智能` → 单 token `[33574]`，中文不再丢失；
4. **10/10 样本 `Decode(Encode(x)) == x`**，说明字节映射与词表一致（v2 有 4 个样本失败）。

加载 **185 ms**（128K vocab + 127K merges + 3 级预分词 + 1,283 special tokens）。

### 10.4 仍未证明的事（不得越界）

**没有黄金样本，因此「与官方逐 token 一致」仍未证实。** roundtrip 可逆只说明映射自洽，不排除「两边都偏移但自洽」的可能。当前证据强度是「多个独立信号一致」，不是「逐 ID 比对通过」。

结论的准确表述：**方案 A 的技术路径已打通并具备四个正确性信号，尚缺官方向量比对。**

### 10.5 核心实现（可直接作为 S1 组件的骨架）

```csharp
// 1) 从 tokenizer.json 一次读取四样东西：vocab / merges / added_tokens / 三条 Split 正则
foreach (var p in model.GetProperty("vocab").EnumerateObject())
    vocab.Add(new(p.Name, p.Value.GetInt32()));

foreach (var m in model.GetProperty("merges").EnumerateArray())
    merges.Add(m.GetString()!);                      // 形如 "Ġ t"

foreach (var t in root.GetProperty("added_tokens").EnumerateArray())
    specialTokens[t.GetProperty("content").GetString()!] = t.GetProperty("id").GetInt32();

// 递归遍历 pre_tokenizer，收集 Split.Regex，并记录是否存在 ByteLevel 节点
Walk(root.GetProperty("pre_tokenizer"));

// 2) 每一级 RegexPreTokenizer 都带 specialTokens —— 这是 v2 失败的原因
var stages = splitPatterns.Select(p => (PreTokenizer)new RegexPreTokenizer(new Regex(p), specialTokens)).ToList();
var composite = new CompositePreTokenizer(stages, specialTokens);

// 3) ByteLevel 用 BpeOptions 开关，而不是自写 Normalizer（顺序会反，见 §9.4）
var tok = BpeTokenizer.Create(new BpeOptions(vocab)
{
    Merges = merges,
    ByteLevel = true,
    SpecialTokens = specialTokens,
    PreTokenizer = composite,
});
```

### 10.6 下一步（S1 正式化）

1. 新建 `Source/PuddingTokenization`（叶子组件，`ProjectReference=0`），把上述逻辑拆成 `ModelTokenizerCatalog` / `DeepSeekTokenizerJsonLoader` / `TokenizerHandle`。
2. 词表资源：`ds-vocab.json`（4.08 MB）+ `ds-merges.txt`（1.63 MB）随应用分发；或只发 `tokenizer.json`（6.37 MB）在启动时拆分 —— 后者省空间但多一次解析，需实测取舍。
3. 测试工程固化 §10.3 的样本为**回归基线**（含 v1/v2 会失败的那几条），并加变异：把 `ByteLevel` 置 false 必须让中文用例取红，删掉任一级 `RegexPreTokenizer` 必须让数字/中文用例取红。
4. 黄金样本仍待解决（§9.5）。


## 11. S5 接入（2026-10-24 10:49，`26dee1b`）

### 11.1 改动面：3 处插入、0 处删除

| 文件 | 改动 |
|---|---|
| `Source/PuddingTokenization/TokenizerProvider.cs` | `TokenizerHandle` 增加 `UnderlyingTokenizer` |
| `Source/PuddingCore/Platform/LlmOptions.cs` | `GetTokenizer` 开头增加精确分支 + 目录定位 |
| `Source/PuddingCore/PuddingCore.csproj` | 增加对叶子组件的 `ProjectReference` |

**为什么能这么小**：`BpeTokenizer` 本身就是 `Tokenizer` 的派生类，所以精确 tokenizer 可以直接嵌进现有 `GetTokenizer` 返回值，**整个计数链路（消息/工具调用/call id/+4/+8 开销）一行都不用动**，只换了「用哪套词表」。

```csharp
private static Tokenizer GetTokenizer(string? modelId)
{
    if (ModelTokenizers.Resolve(modelId).UnderlyingTokenizer is { } exactTokenizer)
        return exactTokenizer;

    var key = ResolveTokenizerKey(modelId);   // 原逻辑，未改动
    return Tokenizers.GetOrAdd(key, ...);
}
```

### 11.2 词表目录来源（fail-safe）

```csharp
static string? ResolveTokenizerAssetDirectory()
{
    var fromEnvironment = Environment.GetEnvironmentVariable("PUDDING_TOKENIZER_ASSETS");
    if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

    var besideApplication = Path.Combine(AppContext.BaseDirectory, "tokenizers");
    return Directory.Exists(besideApplication) ? besideApplication : null;
}
```

三级：环境变量 → 应用目录旁 `tokenizers/` → **都没有则回退**。

**不配置就不改变行为**是刻意设计：`TokenizerProvider` 在目录缺失时返回 `NotConfigured`，`GetTokenizer` 落到原路径，产出与改动前逐字节相同的结果。这样部署顺序可以先发代码、再接资源，中间不存在行为不确定窗口。

⚠️ 这也意味着：**如果部署时不接词表目录，S5 等于没生效**。资源准备见 §11.4。

### 11.3 两件故意没做的事

1. **保留 `ResolveTokenizerKey` 里的 deepseek 分支**（映射到 `o200k_base`）。
   它现在是「未登记 DeepSeek 模型」的正确回退。删掉会让这些模型继续往下匹配，最终落进 `gpt-4o` 分支——同一个结果但语义全错，未来改 gpt-4o 分支时会连带打歪。

2. **不动 `Math.Max(1.0, …)`**。
   codex 评审明确指出：它防的是 Provider 侧输入超限那类失败，而本次修的是**文本分词**，并没有证明请求封装、工具模板等口径也准确。保留上调保护、不新增向下学习。

### 11.4 资源准备与部署

```powershell
# 首次部署（可联网）
pwsh -NoProfile -File Tools\Tokenization\Prepare-TokenizerAssets.ps1

# 本机 / 离线（PowerShell 的 TLS 到 HuggingFace 会失败，但 http_fetch 通道可用）
pwsh -NoProfile -File Tools\Tokenization\Prepare-TokenizerAssets.ps1 -SourcePath <本地 tokenizer.json>
```

装到 `D:\Data\models\tokenizers\`（默认），校验长度 6,367,257 + SHA256 `C90DFA01…9476B`。幂等：已存在且校验通过时不动，实测二次运行输出 `[skip] asset already present and verified`。

运行时还需要让 `PuddingCore` 找到它：把文件放到运行目录旁的 `tokenizers\`，或设环境变量 `PUDDING_TOKENIZER_ASSETS=D:\Data\models\tokenizers`。

### 11.5 验证状态（严格区分）

**已证实**：

- `PuddingCore` 编译通过，仅剩既存警告（CS0618 / CS8603）；
- `PuddingTokenizationTests` **18/18**，含官方向量逐 token 比对；
- S3 无宿主验证（§10.2 + `PathToProvider` 探针）：`deepseek-flash` → `IsExact=True`，token 数与黄金向量一致；`deepseek-v4-pro` / `gpt-6.1-sol` → `ModelNotRegistered` 回退；同身份复用同一 handle 实例。

**未证实（不得写成结论）**：

- **运行时效果**。本次改的是 `PuddingCore.dll`，宿主进程仍加载旧程序集。在重启并对比 `lastMeasuredRawEstimatedTokens` 与 `providerPromptTokens` 之前，不能说估算偏差已从 1.83 倍收敛。
- 请求级口径：即使文本分词完全正确，`usedTokens` 仍包含消息封装、工具模板等本地估算部分，真实误差只能在重启后由实测给出。


## 12. 检索路径的状态误报：`code_index_status` 说 Pending，索引其实可用（2026-10-24 12:15 诊断）

### 12.1 现象与实证

同一次 `Source` 范围检索，两个后端给出**不同完整度**：

| 工具 | 命中 | 完整性 | 是否含真实实现 |
|---|---|---|---|
| `search_grep`（scan 默认） | 6 | **`complete=false`**，`scanned 983/2000` | ❌ 只有 5 个测试 mock + 1 个调用点 |
| `code_symbol_search`（index） | **7** | `complete=true`，`stale_skipped=0` | ✅ 含 `ICodeQueryService` 接口与 `CodeQueryService` 实现 |

而被漏掉的那个是 `PuddingCodeIntelligence/Services/CodeQueryService.cs` —— **正是解析状态的地方**。按 scan 的结果会得出「只有 mock 实现了它」的错误结论。

### 12.2 为什么 scan 会漏

`SearchGrepTool` 硬上限：`MaxEnumeratedFiles = 2000`、`MaxScannedFiles = 2000`（`SearchGrepTool.cs:68-69`）。
实测本仓 `Source` 下：**13,480** 个文件、其中 `.cs` **3,282** 个 ⇒ 只搜 `.cs` 也超上限 64%。

上限本身是**合理设计**（scan 面向窄范围，速度有 10s 兜底），截断时也确实给了提示并建议索引工具。所以问题不在 scan。

### 12.3 真正的问题：状态误报把人从索引工具推回 scan

```
code_index_status  →  status: "Pending", started_at_utc: null,
                       completed_at_utc: 2026-10-06T15:08:16  （18 天前）
code_symbol_search →  index_freshness.state: "idle",
                       watcher_attached: true,
                       last_calibration_at_utc: 2026-10-24T03:50:09  （今天）
```

两个投影**互相矛盾**，且 `Pending` 稳定复现（连查两次一致）。

根因链（已读源码确认）：

```csharp
// CodeQueryService.ToIndexStatus
CodeProjectStatus.Registering => CodeIndexStatus.Pending,
CodeProjectStatus.Active      => CodeIndexStatus.Completed,
```

而 `CodeProjectStatus` 枚举里**没有 `Pending` 值**（`Unknown/Registering/Active/Removing/Removed/Failed`）⇒ 输出的 `Pending` 只可能来自 `project.Status == Registering`。

即：**项目注册后停在 `Registering`，从未推进到 `Active`**，但索引数据实际已建好并持续校准。

雪上加霜的是工具描述写着：

> status 为 Pending/Indexing 时**查询类工具结果不完整，等 Completed 再查**

对当前这个项目而言**这条描述是错的** —— `Pending` 状态下 `code_symbol_search` 返回 `complete=true`、`stale_skipped=0`。

### 12.4 后果（不是理论）

按描述行事 ⇒ 认为索引不可用 ⇒ 退回 `search_grep` ⇒ 撞 2000 上限 ⇒ `partial` ⇒ **漏掉唯一的真实实现**（§12.1 已实证）。
这正是「检索优化」里最贵的一类浪费：**不是搜得慢，是搜错地方还自信**。

### 12.5 修复方案（待实施）

1. **`code_index_status` 增加 `index_freshness`**，与 4 个查询工具保持一致 —— 让调用方同时看到「注册状态（可能脏）」与「运行时真实状态」，不再被单一字段误导。
2. **抽共享 helper**：`index_freshness` 的计算逻辑目前在 `CodeQueryTools.cs` 里**重复 3 份**（L370 / L798 / L1052，每份约 56 行）。加第 4 处之前必须先抽，否则就是把重复从 3 份变成 4 份。
3. **修描述**：把「Pending 时结果不完整」改成「以 `index_freshness.state` 为准；注册状态与运行时状态可能不一致」。
4. 可选的根因修复：注册/索引完成后把 `Status` 推进到 `Active`，并对已脏数据做一次校准。涉及状态机 + 需要重启，单独评估。

### 12.6 顺带观察

`index_freshness.unresolved_source_path_count = 6580`（今天校准过）。这个数量级说明有大量源路径未解析，可能与索引覆盖不全有关，但本轮未展开，**不作为结论**。


## 13. `unresolved_source_path_count = 6580` 的定性（2026-10-24 13:15）

### 13.1 这个数字是什么

`ICodeIndexMaintenance.SourceMaintenanceUnresolvedPathCount` ← `CodeSourceMaintenanceCoordinator` 的 `RetryableFileCount`，其注释写明：

> 未解决的路径数：本轮失败/待重试的路径 **加上** 仍在退避窗口内被跳过的路径。两者都让扫描水位停住，所以必须如实报告（不是「本轮尝试了几次」）。

本仓 `Source` 下共 13,480 个文件 ⇒ 6580 约占一半，**单看这个比例足以让人怀疑索引只覆盖了一半**。上一轮我把它记为「未展开，不作结论」，这是对的。

### 13.2 用「今天新写的代码」做探针（决定性）

与其抽样统计，不如问一个索引必须能回答的问题：**今天下午刚写的符号，索引里有吗？**

- 13:00 前后在 `CodeQueryTools.cs` 加入 `CodeQueryToolHelper.ReadIndexFreshness`
- 13:15 调 `code_symbol_search("ReadIndexFreshness")`
- 结果：**命中 1 条**，`complete=true`，`stale_skipped=0`，`file_path` 与 `start_line=141` 均正确

⇒ **索引能纳入当天新增代码，扫描水位在前进，索引不是陈旧的。**

### 13.3 结论与边界

**已证实**：索引对新代码的响应是实时的；`code_symbol_search` 的覆盖在当前状态下可用。

**未证实 / 不下结论**：6580 里「失败」与「退避窗口内被跳过」各占多少，以及它们具体是哪些路径。要回答需要源维护轮次日志，本轮未取。

**为什么不再往下挖**：该计数对「索引能否被信任」的决定性问题已被 13.2 的实验回答（能）。继续追构成属于性能/噪声治理，不是当前阻塞项。

### 13.4 顺带记录的环境事实

- 索引库：`D:\Data\databases\code-index\code_index.db`，**1,787,154,432 字节（≈1.7 GB）**，`LastWriteTime = 2026-10-24 12:12:04`（活跃写入）
- 本机 **没有 `sqlite3` CLI**（`Get-Command sqlite3` 为空）⇒ 想直接查表必须另找读法（EF Core / 项目内探针），不要假设命令行可用


## 14. `Registering` 不是脏数据，是**有意保留**的语义（2026-10-24 13:20）

§12 里我推断「项目停在 `Registering` 18 天、从未推进 `Active`」，并把「注册后推进 `Active`」列为可选的根因修复。**这个推断需要修正**：读代码后确认 `Registering` 是有意为之。

`CodeIndexScheduler` 的状态机：

| 位置 | 行为 |
|---|---|
| `CodeIndexScheduler.cs:204` | 作业开始 ⇒ 写 `Registering` |
| `CodeIndexScheduler.cs:226` | 作业结束 ⇒ `Active`（成功）/ `Failed`（失败） |
| `CodeIndexScheduler.cs:292-315` | `MarkInterruptedAsync`：**作业被取消时不把行移出 `Registering`** |

关键注释原文：

> U3-G1: records a cancelled run **without moving the row off** `CodeProjectStatus.Registering`（the scope still owes a full run - the status semantics are unchanged）

所以 `Registering` 的语义是 **「欠一次完整索引运行」**，不是「正在索引」也不是「脏数据」。10-06 那次运行被中断后留下的就是这个合法标记。

### 14.1 由此得出的两条结论

1. **不要**把 `Registering` 自动推进到 `Active`。那会把「这个 scope 还欠一次完整运行」这个事实抹掉——而这正是该标记要传达的信息。
2. **但「欠一次完整运行」≠「索引结果不可用」**：watcher 与校准仍在工作，今天的新代码照样进索引（§13.2 实证）。所以工具描述里那句「Pending/Indexing ⇒ 查询结果不完整，等 Completed 再查」**在语义上就是错的**，不只是对本项目不成立。

⇒ §12 的修复方向（补 `index_freshness` + 改描述，**不动状态机**）事后看仍然正确，而且现在有了更硬的依据：`Pending` 是一个**本来就该如实在报告里存在的**状态，需要修正的是「它意味着什么」的说明，不是它本身。

### 14.2 另一个观察（未修）

`CodeQueryService.GetProjectIndexStatusAsync` 构造 `CodeIndexResult` 时**没有传 `Message`**，于是 `code_index_status` 输出的 `message` 恒为 `null`——而 `MarkInterruptedAsync` 明明把 `InterruptedStatusMessage`（"Indexing run interrupted: cancelled before a result could be recorded..."）写进了记录。

不过 `CodeProjectRecord` 本身**没有 Message 字段**（`WorkspaceId/ProjectId/ProjectPath/Status/DisplayName/AddedAtUtc/UpdatedAtUtc/Source/ScopeState`），所以这条消息的落点需要先查实存储层，不能凭 `UpdateProjectStatusAsync` 的签名断定它被丢弃。**本轮不动，只登记。**


## 15. `StatusMessage` 是只写不读的字段：诊断信息写入即蒸发（2026-10-24 13:16，已修 `723e340`）

### 15.1 完整链路

`CodeProjects` 表有 `StatusMessage TEXT NULL` 列（`SqliteCodeIndexStore.cs:60`），`UpdateProjectStatusAsync` 会写它（L313-331）。`CodeIndexScheduler.MarkInterruptedAsync` 正是靠它区分两种 scope：

> `CodeIndexScheduler.cs:291` — distinguishable from a scope that never started (whose StatusMessage stays NULL)

这个区分**值得有**。问题是它**读不回来**：

| 环节 | 事实 |
|---|---|
| `CodeProjectRecord` | **没有 StatusMessage 成员** |
| 两处 SELECT（`GetProjectAsync` / `ListProjectsAsync`） | 第 8 列写的是**字面量 `NULL`**，不是 `StatusMessage` |
| `ReadProject` | 读列 0-6 后**跳过列 7**，直接读 8、9 |
| `CodeQueryService.GetProjectIndexStatusAsync` | 构造 `CodeIndexResult` 时**不传 Message** |

⇒ `code_index_status` 的 `message` **恒为 null**。自该列被加入以来写入的中断诊断，事实上是**只写**。

那个 `NULL` 占位符是这个意图的化石：**列在 SELECT 里被预留了，然后没有人去读它**。

### 15.2 修法与刻意的取舍

- `CodeProjectRecord` 增加 `StatusMessage`，**追加在末尾**而不是紧邻 `Status`：位置参数一旦插到中间，5 个构造点全要改，而这里只需要一个读者。
- 两处 SELECT 的 `NULL` → `StatusMessage`；`ReadProject` 读列 7。
- `GetProjectIndexStatusAsync` 把 `project.StatusMessage` 传成结果的 `Message`。

**没改状态机**：`Registering` 不是垃圾（§14），所以这里不推进它，只把原因**并列**呈现，而不是用它重新解释状态。

### 15.3 为什么这个洞能活这么久

原有的往返测试**已经写了 `"indexed"` 这个 message，却从没断言读回**——它只断言 `Status`，不断言 `Message`。

也就是说：**测试覆盖了写入路径，恰好漏掉了读取路径**。补断言时同时覆盖 `GetProjectAsync` 与 `ListProjectsAsync` 两个读取路径（同一列，不能一个读一个漏）。

### 15.4 变异验证

| 阶段 | 结果 |
|---|---|
| 原始 | `PuddingCodeIndexTests` **373/373** 绿 |
| 变异（两处 SELECT 的 `StatusMessage` 改回 `NULL`） | **1 红 / 9 绿**，且红的正是新断言：`应为: <indexed>，实际为: <>`，表达式 `stored.StatusMessage` |
| 复原 | SHA-256 `1C6719C1…C7012` **逐位相同**，`-t:Rebuild` 后 **373/373** 绿 |
| `code_index_status` 冻结契约门禁 | **6/6**（用 mock query service，注册项目的 wire 契约未受影响） |

变异这一步是必须的：没有它，一片绿色无法区分「message 真被读了」和「测试压根没看它」。
