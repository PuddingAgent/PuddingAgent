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
