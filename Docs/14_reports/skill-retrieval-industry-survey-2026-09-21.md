# 业界调研：智能体技能/工具的「渐进披露 + 语义检索 + 有效性统计」

- 调研日期：2026-09-21
- 范围：公开资料（官方文档 / 论文 / 开源仓库），聚焦 2024–2026 的 agent skills、tool retrieval、progressive disclosure、skill library。
- 方法：anysearch_search（多轮）+ http_fetch 打开一手原文 + github_search 定位仓库，再以 raw.githubusercontent.com 直接核验 LICENSE 文件。
- 声明：本文只做公开资料调研，不涉及 PuddingAgent 自身代码；结论中括号内的数字均来自文末来源清单。

---

## 0. 一句话结论（可直接用的判断）

业界对「技能/工具数量持续增长」的公认解法**不是更聪明的关键词匹配，而是三层结构 + 按需检索 + 评测淘汰**：

1. **渐进披露（progressive disclosure）**：把常驻上下文压到 `O(技能数) × ~100 tokens` 的**元数据层**（name + description），正文与资源改为**由模型在判定相关后主动读取**（agentic 读文件），而不是宿主用 if-else/关键词把正文灌进去。
2. **语义检索**：候选收敛到 **3–10 条**再喂模型。纯向量有效但**不够**——公开数据里工具检索的 dense SOTA nDCG@10 仅 ~33.8（ToolRet），所以生产实现几乎都是**混合检索（dense + BM25/sparse + RRF）+ 二阶段 rerank**，并保留「检索不到→兜底搜索」通道。
3. **有效性统计与淘汰**：**没有厂商内建"技能调用次数/成功率"遥测**；业界标准做法是**用 OpenTelemetry GenAI semconv 把每次工具调用记成 span**（`gen_ai.tool.name` 等），再按技能维度聚合；技能"有效性"的评测法已由 SkillsBench 定式化（同任务 with/without skill 的 pass rate 差）。

**对 PuddingAgent 的直接含义**：现有的 `SkillEnforcerService.CollectKeywords`（Keywords ∪ Tags ∪ SkillId ∪ Name ∪ 分词 → OrdinalIgnoreCase 去重 → 先到先得抢映射位，139 技能 / 2490 槽位 / 89.1% 噪声）在业界属于**已证伪的反模式**（关键词无限膨胀 + 元数据质量被稀释）；正解是把 description 做成唯一"路由契约"，用检索（语义+BM25 混合）替代抢槽位。

---

## 1. ① 渐进披露（progressive disclosure）怎么分层落地

### 1.1 官方三层模型（Anthropic Agent Skills 规格）

Agent Skills 是 Anthropic 2025-10 推出的机制，2025-12-18 作为**开放标准**发布在 agentskills.io（Anthropic engineering 博客 + agentskills.io/specification）。一个技能 = 一个目录，至少含 `SKILL.md`：

```
skill-name/
├── SKILL.md          # 必需：YAML frontmatter + 正文
├── scripts/          # 可选：可执行代码
├── references/       # 可选：文档
├── assets/           # 可选：模板/图片/数据
```

| 层 | 放什么 | 谁决定加载 | Token 预算（官方建议） |
| --- | --- | --- | --- |
| **元数据层**（Level 1） | `name` + `description`（可选 `license` / `compatibility` / `metadata` / `allowed-tools`） | **平台/客户端无条件预载**，进入 system prompt | **~100 tokens / 技能** |
| **摘要/正文层**（Level 2） | `SKILL.md` Markdown 正文（步骤、输入输出示例、边界情况） | **模型**判定相关后主动读文件 | **< 5000 tokens**，单文件 **< 500 行** |
| **资源层**（Level 3+） | `references/`、`scripts/`、`assets/` 内文件 | **模型**按需读（相对路径引用，**只允许一层深**，禁止深层引用链） | 按需，越小越好 |

**关键设计约束（官方原文口径）：**

- `description` **必须同时写"做什么"和"何时用"**，并**包含有助于识别的具体关键词**（spec 明确："Should include specific keywords that help agents identify relevant tasks"）。好例：`Extracts text and tables from PDF files, fills PDF forms... Use when working with PDF documents or when the user mentions PDFs, forms, or document extraction.`；坏例：`Helps with PDFs.`。
- `name`：≤64 字符，小写字母/数字/连字符，须与父目录同名；`description` ≤1024 字符。
- **触发是模型行为**：Agent 用 Bash/Read 工具去读 `SKILL.md`，再（如有必要）读 `references/FORMS.md`。Anthropic 用 PDF 技能举例：正文瘦身，把"填表"逻辑挪到 `forms.md`，**信任模型只在填表时才读它**。
- 因此**可打包进技能的上下文实质无上界**（"the amount of context that can be bundled into a skill is effectively unbounded"），代价被推迟到真正需要时。

### 1.2 工具侧的同一原则：`defer_loading` + Tool Search Tool

Anthropic 2025-11-24 "Introducing advanced tool use" 把同一套分层搬到了**工具**维度：

- 工具定义默认全量塞进上下文。真实规模成本：5 个 MCP server = **58 个工具 ≈ 55K tokens**；Anthropic 内部见过**优化前 134K tokens**。
- 方案：给工具打 `defer_loading: true`，**初始只加载 Tool Search Tool 本身（~500 tokens）**；模型按需搜索，命中后**才把命中工具的完整定义展开进上下文（3–5 个 ≈ 3K tokens）**。
- 效果（官方）：**token 用量 -85%**，上下文从 ~77K 降到 **~8.7K**（保留 95% 上下文窗口）；Anthropic MCP 评测准确率 **Opus 4: 49% → 74%**、**Opus 4.5: 79.5% → 88.1%**。
- 并列出配套两件套：**Programmatic Tool Calling**（工具在代码执行环境里调用，中间结果不回灌上下文）与 **Tool Use Examples**（用示例表达使用模式，schema 表达不了）。
- 代码执行路线的极端形态见 "Code execution with MCP"：把每个工具暴露成一个 `.ts` 文件（`servers/google-drive/getDocument.ts`），模型**写代码调工具**；甚至可把写好的函数存成 `skills/save-sheet-as-csv.ts` + 加 `SKILL.md` 变成技能。**注意其自陈代价**：需要安全沙箱、资源限制与监控。

### 1.3 MCP 协议侧的分层/治理原语

MCP spec（2026-07-28 版）在协议层提供了可复用的约束（modelcontextprotocol.io）：

- `tools/list` **支持分页与缓存**（返回 `nextCursor` / `ttlMs` / `cacheScope`）。
- server 声明 `tools.listChanged` 后可发**工具列表变更通知**。
- **SHOULD 返回确定顺序**，因为"deterministic ordering enables clients to reliably cache the tool list and improves LLM prompt cache hit rates"——**工具列表顺序稳定 = prompt cache 命中率**，这是渐进披露落地时容易被忽略的工程约束。
- **跨 server 名称冲突**：协议明确 client/proxy 聚合多 server 时**可能撞名**（例如两个 `search`），**SHOULD 用 server 前缀消歧**。

### 1.4 关键判断

> **谁决定加载：模型决定（agentic），宿主只决定"何时把名字给模型"。**
> 这与"宿主预先把技能正文/关键词灌进 prompt"是两种范式。业界 2024–2026 的收敛点是前者。宿主侧的唯一职责是**在启动时提供高质量、可检索的元数据**（第 3 节的反模式全部围绕"元数据质量"）。

---

## 2. ② 技能/工具的语义向量检索：已验证的实现与公开指标

### 2.1 公开评测数字（一手）

| 工作 | 做法 | 公开指标 |
| --- | --- | --- |
| **RAG-MCP**（arXiv:2505.03275, 2025-05） | 先把 MCP 工具放进外部索引，**语义检索**命中后再把命中的工具描述喂 LLM | 工具选择准确率 **43.13% vs 13.62%（基线）**；prompt token **-50% 以上**（部分复现报 -73%） |
| **ToolRet**（arXiv:2503.01763, ACL 2025） | 首个工具检索基准：**7.6k 检索任务 / 43k 工具**，评 6 类 IR 模型 | **最强 embedding（NV-embedd-v1）nDCG@10 仅 33.83**；通用 IR 强模型在工具检索上"表现很差"；检索质量低会**拉低工具使用 agent 的端到端 pass rate** |
| **Tool-to-Agent Retrieval**（arXiv:2511.01854, 2025-11） | 把工具与其父 agent **嵌入同一向量空间**，用 metadata 关系在 tool 级/agent 级检索 | 相对此前 SOTA agent retriever，**Recall@5 +19.4%、nDCG@5 +17.7%**（LiveMCPBench） |
| **ToolScope**（ACL 2026 long, 2026.acl-long.1573） | ① 合并重叠工具（ToolScopeMerger + Auto-Correction）② 上下文感知过滤只留最相关工具 | 3 个 LLM × 3 个基准上工具选择准确率 **+8.38% ~ +38.6%** |
| **Dynamic ReAct**（arXiv:2509.20386, 2025-09） | 面向大规模 MCP 的 search-and-load 架构（比较 5 种架构） | **工具加载量最多 -50%**，任务完成准确率保持 |
| **How Many Tools Should an LLM Agent See?**（arXiv:2605.24660, 2026-05/06） | 用 **Bits-over-Random（BoR）** 机会校正指标评估"短列表长度"，并用 RL 学**每 query 自适应深度** | BFCL（370 工具）：自适应覆盖 **90.3% vs 固定 50 条的 90.8%**，但**平均只展示 7 条**；ToolBench（3251 工具）：固定 top-5 覆盖 64.7% vs 61.9%，但**硬查询（正确工具排 6–20）命中 0%**，BoR 探深后 **16.7%**；下游 Claude Sonnet 4.6 选择正确率 **93.1% vs 87.1%**，中等难度 **76.8% vs 60.9%** |

### 2.2 语义检索 vs 关键词检索：取舍（本节最该带走的结论）

- **纯语义会漏**：`ajing` 的生产总结指出纯 semantic retrieval 在**词汇不匹配**上失败（用户说 "cancel"，工具叫 "refund"）；工程界正解是 **hybrid = BM25(sparse) + dense + Reciprocal Rank Fusion**，或 **dense 召回 + LLM rerank 两阶段级联**。
- **通用 embedding 强 ≠ 工具检索强**：Gemini Embedding 在 MTEB 上领先，但在**工具检索专用榜（Agentset）上落后**；工具检索要挑 **Voyage-3-large / text-embedding-3-large / BGE-M3（自托管）** 并按工具场景评测。
- **微调有效但需要 hard negative**：ToolRet 用 200k+ 训练实例微调 IR 模型，显著提升工具检索；关键是 hard negative 要"语义相似但功能不同"的工具。
- **规模—准确率经验表**（ajing 汇总的生产经验，作方向性参考）：5–10 工具 90–95% / 20–50 工具 80–90% / 100–200 工具 60–80% / **500+ 工具 40–60%（必须多阶段选择）**；单工具定义 200–500 tokens，500 工具 = 200K+ tokens。
- **检索之外还有两条被验证的路线**：
  - **描述优化**：Bloomberg（ACL 2025）**联合优化 agent 指令 + 工具描述**，无效工具调用 **-70%（StableToolBench）/ -47%（RestBench）**，pass rate 不降；模板要素 = `when_to_use` / `when_not_to_use` / `example_queries`。
  - **受约束解码**：Manus 的做法是**工具全留在上下文，用 logit masking 按状态机屏蔽不允许的工具**——因为"动态增删工具会 invalidate KV-cache"，而"retrieval 过滤可能把需要的工具滤掉"。

### 2.3 关键判断

> **语义检索是"收敛候选"的标准手段，但公开 recall/precision 只有 60–90% 量级，且 dense-only 明确不够。**
> 可复用的目标形态：**混合检索（dense+BM25+RRF）→ 小候选（3–10）→ 可选 LLM rerank → 结果不足时允许模型再次搜索（search-and-load 回路）**。你方已有 `IEmbeddingService`（1024 维）+ SQLite 向量 + FTS5，正好能直接组合 dense + FTS5 的 hybrid，不需要新基建。

---

## 3. ③ 技能有效性统计：怎么做、用什么数据模型、怎么决定退役

### 3.1 现状：没有厂商内建"技能调用统计"

调研结论（负向事实，但很重要）：**Anthropic Agent Skills / MCP 均未定义"技能调用次数/成功率"的遥测字段**。SKILL.md 只有静态元数据。有效性的业界标准做法有两条：

**(A) 用 OTel GenAI semantic conventions 做统一数据模型（推荐）**

OpenTelemetry 的 GenAI semconv 把 agent 运行建模为 trace，**每次工具调用是一个 span**（`gen_ai.tool.name` / `gen_ai.tool.call.id` / `gen_ai.tool.type` 等属性；模型调用是另一个 span；token 用量是 metric）。因此：

> **技能/工具级统计 = 按 `gen_ai.tool.name`（或自定义 `skill.id`）聚合 span，得到 调用次数 / 错误率 / 延迟 / token 成本，再 join 技能的静态元数据（skillId、version、contentHash、source）即可。**

建议的退役决策数据模型（字段级）：

```
skill_id, version, content_hash, source_session, enabled,
call_count, success_count, error_count, success_rate,
avg_latency_ms, avg_token_cost, last_used_at, injected_count,
overlap_group_id, defect_flags, last_eval_pass_rate
```

其中 `defect_flags` 来自静态 lint（见 3.3），`last_eval_pass_rate` 来自评测（见 3.2）。

**(B) 评测法（把"有效性"定义成可测量的因果量）**

**SkillsBench**（arXiv:2602.12670）给出的定式：在**同一任务集**上对比 **with-skill vs without-skill 的 pass rate**：

- 精选（curated）技能：平均 pass rate **33.9% → 50.5%，+16.2pp（归一化增益 25.5%）**，但**域间差异大（SWE 仅 +4.5pp）**。
- **自生成（self-generated）技能几乎零收益甚至负收益**；**84 个任务里有 16 个"加了技能反而更差"**。
- 另有报告口径：精选技能提升 **+16.2pp**，但**公开技能平均质量分仅 6.2/12**（SkillsBench 分析 47,150 个技能）。

**(C) 静态"可检索性"代理指标（无需真实调用即可筛）**

**138K SKILL.md 研究**（arXiv:2608.08453，Agent Skills Workshop 2026）用 7 类 / 31 项 defect taxonomy + **确定性 routing stress test**：

- **89.3% 违反官方 spec、91.8% 至少含 1 个缺陷**（宽松/严格阈值下 88.8–94.6% 稳定）。
- 主导缺陷是"普通打包问题"：**路由元数据弱、正文臃肿/不可执行、资源组织差**。
- stress test（20,000 技能）证明：**路由元数据合规的技能，从 startup description 被检索到的可靠性显著更高**——即"元数据质量 → 可检索性"是可静态预判的。

### 3.2 质量/安全统计（用于"是否值得保留"的输入）

| 指标 | 数值 | 来源 |
| --- | --- | --- |
| 社区技能含漏洞比例 | **26.1%** | arXiv:2602.12430（Agent Skills survey） |
| 22,511 技能安全审计 | **140,963 issues（≈6.3 / 技能）** | Agentman 生态报告 2026（引 Agensi 审计） |
| prompt injection 比例 | **36%**（Snyk ToxicSkills） | Agentman 生态报告 2026 |
| 公开技能平均质量分 | **6.2 / 12**（47,150 技能） | SkillsBench / Agentman |
| 非可执行正文占比 | **> 60%**；26.4% 完全缺 routing description | SkillReducer（arXiv:2603.29919） |

### 3.3 淘汰/退役的可操作启发式

综合上述来源，业界可迁移的淘汰规则（**合并优先于删除**）：

1. **低调用 + 高重叠** → 合并（ToolScope 证明"合并冗余工具"本身就是 **+8.38%~+38.6%** 的收益来源）。
2. **缺/弱 routing description** → 先修复（SkillReducer 自动补描述 + 压缩），修不好再淘汰。
3. **静态 defect 未过 lint** → 阻断（138K 研究支持"轻量 lint + 自动修复 + 安全门禁"的 workflow）。
4. **评测 pass rate 差 ≤ 0（with-skill 不优于 without-skill）** → 退役（SkillsBench 口径；自生成技能即典型）。
5. **长期 `last_used_at` 陈旧 / 版本绑定** → 退役或重写（138K：provenance 显著影响缺陷率）。
6. **通用软件口径可参照**：某功能 6 个月内未达 ~10% 采用率即进入弃用候选（来自 userpilot 的 SaaS 实践，非 agent 原生，仅作方向性参考）。

---

## 4. ④ 反模式清单（业界已证明会失败的做法）

| # | 反模式 | 后果（含证据） |
| --- | --- | --- |
| 1 | **把大量技能/工具全文注入上下文** | **Context Rot**：Chroma 对 18 个 LLM 的实验显示**仅增加输入长度就会让性能下降**，且"干扰项（topically related but wrong）"比无关内容伤害更大；SkillReducer 的 **less-is-more**：描述压 **48%**、正文压 **39%**，**功能质量反而 +2.8%**。Anthropic 亦记录过**优化前 134K tokens** 的工具定义开销。 |
| 2 | **巨型单体技能（"全塞一个文档"）** | SkillsBench：monolithic 技能使性能 **-2.9pp**；而 2–3 个聚焦技能 **+18.6pp**。 |
| 3 | **自生成/自动生成技能不做评审就启用** | SkillsBench：self-generated **无收益甚至负收益**，16/84 任务变差；138K 研究：AI-marked 技能**安全与可移植性问题更多**。 |
| 4 | **技能/工具功能重叠 + 命名相近** | Anthropic 明确："最常见的失败是**选错工具和参数错误**，尤其在 `notification-send-user` vs `notification-send-channel` 这类相似命名下"；ToolScope 通过**合并冗余**换来 +8.38%~+38.6%。 |
| 5 | **路由元数据弱（缺失/过短/无关键词）** | 138K：89.3% 违反 spec、91.8% 有缺陷，且**有路由缺陷的技能检索可靠性显著更低**；SkillReducer：26.4% 技能**完全没有 routing description**。 |
| 6 | **资源/引用文件单次注入数万 token** | SkillReducer 实测：reference 文件"can inject tens of thousands of tokens per invocation"。官方因此要求 **<5000 tokens / <500 行 / 引用只许一层深**。 |
| 7 | **动态增删工具（为了省 token 而移除工具定义）** | 会 **invalidate KV-cache**；Manus 因此改用 **logit masking** 而非移除工具（ajing 生产总结）。 |
| 8 | **硬编码凭据 / 本地路径 / 平台·版本绑定 / setup 说明与 changelog 混入正文** | 138K 研究把这些列为高频缺陷类别（可移植性、安全）；survey：**26.1%** 社区技能含漏洞。 |
| 9 | **关键词无限膨胀、靠"先到先得抢槽位"做路由** | 与 spec 正解（**description 承担路由契约**）相悖；你方实测 139 技能 → **2490 关键字槽位、89.1% 噪声、1735 次注入被静默挤掉**，属该类反模式的典型样本。 |
| 10 | **检索失败时 fail-open 到全量注入** | 等价于反模式 #1；Anthropic Tool Search Tool 的收益正好来自"永不回到全量"。你方 `SearchToolsTool` 的 fail-open 到全量是同类风险点。 |
| 11 | **通用 embedding 直接用于工具检索** | ToolRet：通用 IR 强模型在工具检索上表现差（最佳 nDCG@10 33.83）；ajing：Gemini Embedding 在工具检索专用榜落后。 |
| 12 | **固定 top-k 短列表** | "How many tools"论文：固定 5 条在硬查询上命中 **0%**，自适应深度可达 **16.7%**；固定长度无法用统一指标判断是否合适。 |

---

## 5. 对比表：机制 × 落点 × 可复用性 × 风险

| 机制 | 落点（Layer） | 可复用性 | 风险 / 约束 |
| --- | --- | --- | --- |
| **元数据三字段（name/description/tags）预载** | 启动 system prompt（L0） | ★★★★★ 直接可抄（agentskills spec 是开放标准） | 描述质量=路由质量；1024 字符上限；需 lint 防"弱元数据" |
| **模型按需读 SKILL.md 正文** | 模型 tool-call 读文件（L1） | ★★★★★（技能已是文件，改"注入方式"即可） | 需 filesystem+读工具；须约束"引用只一层深" |
| **引用资源按需读（references/scripts/assets）** | 模型按需（L2） | ★★★★☆ | 资源膨胀会单次注入数万 token（SkillReducer） |
| **工具 defer_loading + Tool Search Tool** | 工具定义展开（L0→L1） | ★★★★☆（平台 API 侧已产品化；可自研等价物） | 需要"搜索→展开"二次往返；顺序稳定才不破 prompt cache（MCP spec） |
| **语义检索（dense）** | 候选收敛 | ★★★★☆ | dense-only 漏词面不匹配；工具检索 SOTA nDCG@10 仅 ~33.8 |
| **混合检索（dense+BM25+RRF）** | 候选收敛 | ★★★★★（已有 1024 维向量 + FTS5） | 需调参（RRF k、候选深度、rerank 开关） |
| **两阶段：检索 + LLM rerank** | 候选收敛 | ★★★★☆ | rerank 增延迟/成本 |
| **工具/技能合并去冗余** | 库治理 | ★★★★☆ | 合并错误会误伤功能；需 Auto-Correction（ToolScope） |
| **自适应短列表深度（BoR/RL）** | 候选长度 | ★★★☆☆（论文级，实现较重） | 需机会校正指标才能评估 |
| **受约束解码 / logit masking** | 推理期 | ★★★☆☆（依赖推理引擎能力） | 不解决 token 成本，只解决"选错" |
| **OTel GenAI 遥测 → 技能级聚合** | 观测 | ★★★★★ | 需在 span 上打 skill_id；厂商未内建，需自建 join |
| **with/without-skill 评测（SkillsBench 口径）** | 淘汰决策 | ★★★★★ | 需可复现任务集；域间收益差异大（SWE +4.5pp） |
| **静态 defect lint + routing stress test** | 淘汰决策 | ★★★★★ | 只测"可检索性"，不测真实收益 |

---

## 6. 可复用开源实现（含许可证，已核验 LICENSE 文件）

| 项目 | 用途 | 许可 | 可否生产使用 | 备注 |
| --- | --- | --- | --- | --- |
| **agentskills/agentskills**（含 `skills-ref` 校验器） | 官方 spec + `skills-ref validate ./my-skill` | **Apache-2.0**（已核验 raw LICENSE） | ✅ 可 | 直接用作技能的 lint/合规门禁 |
| **anthropics/skills** | 官方技能样例库 | **混合**：多数技能 **Apache-2.0**；`docx/pdf/pptx/xlsx` 为 **source-available（非开源）** | ⚠️ 需分目录判断 | README 明确声明；doc 系列**不要**当开源依赖使用 |
| **modelcontextprotocol/use-mcp** | MCP 客户端 SDK（动态工具发现/发现式加载） | **MIT**（Copyright Cloudflare） | ✅ 可 | 作为"defer/搜索加载"的实现参考 |
| **mcp-use/mcp-use** | MCP 全栈框架（client/server SDK、inspector） | **MIT** | ✅ 可 | 便于做工具检索/加载的实验脚手架 |
| **ComposioHQ/composio** | 1000+ 工具集成 + **tool search** + 上下文管理 | **MIT**（OSS 核心） | ✅ 可（注意托管服务条款） | 生产可参考其"tool search"抽象 |
| **IBM/mcp-context-forge** | **MCP Gateway**：多 server 聚合、工具过滤/治理 | **Apache-2.0** | ✅ 可 | 解决"跨 server 工具爆炸 + 命名冲突"治理层 |
| **mangopy/tool-retrieval-benchmark（ToolRet）** | 工具检索基准 + 训练数据 | **Apache-2.0** | ✅ 可（评测用） | 7.6k 任务 / 43k 工具；可直接做检索选型对照 |
| **minedojo/voyager** | 早期"不断增长的技能库 + embedding 检索"范式 | **MIT** | ✅ 可（研究参考） | 技能库增长/检索的经典实现 |
| **memoverflow/rag-mcp** | RAG-MCP 论文的社区实现 | ⚠️ **未找到 LICENSE 文件**（raw 404） | ❌ 不建议生产 | 只作论文复现参考 |
| **open-telemetry/semantic-conventions-genai** | `gen_ai.*` span/metric 属性规范 | Apache-2.0（spec） | ✅ 可 | 技能级统计的数据模型来源 |

（许可证核验方式：对每个仓库的 `raw.githubusercontent.com/<owner>/<repo>/main/LICENSE` 直接取文件；GitHub API/网页在本环境被 403/超时拦截，故用 raw 路径核验。`agentskills/agentskills`、`ToolRet`、`IBM/mcp-context-forge` = Apache-2.0；`use-mcp`、`mcp-use`、`composio`、`voyager` = MIT；`anthropics/skills` = 混合。）

---

## 7. 对 PuddingAgent 的可迁移要点（仅建议，不改代码）

1. **停止用关键词抢槽位做路由**（反模式 #9）。把 `description`/`keywords` 收敛成**唯一路由契约**（spec 口径：做了什么 + 何时用 + 具体关键词），其余 Tags/Name 分词只做**检索字段**，不再"先到先得抢映射位"。
2. **技能侧复制"三层"**：manifest.json 已相当于 L0 元数据；把 `SKILL.md` 改为**模型按需读取**（而非注入），资源文件按需读。
3. **检索改为混合**：`IEmbeddingService`(1024) + **FTS5 BM25** → RRF 融合 → 取 3–10 条。**保留 search-and-load 回路**，但**去掉 fail-open 到全量**（反模式 #10）。
4. **加静态 lint 门禁**：对齐 agentskills spec（name 规则、description ≤1024 且含"何时用"、正文 <500 行、引用一层深、禁硬编码凭据/本地路径）。可直接复用 `skills-ref` 思路。
5. **建技能遥测**：用 OTel GenAI span（`gen_ai.tool.name`）+ 自定义 `skill.id` 聚合出 `call_count / success_rate / latency / token_cost / last_used_at`，再叠加静态 defect flags 与 SkillsBench 式 with/without 评测，形成第 3 节的退役模型。
6. **合并优先于删除**：对 144 个技能做 overlap 检测（向量相似 + 功能重叠），先合并再退役。

---

## 8. 来源清单

**官方文档 / 一手工程博客**
1. Anthropic Engineering — *Equipping agents for the real world with Agent Skills*（2025-10-16）https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills
2. Agent Skills 开放标准规格 — https://agentskills.io/specification
3. Anthropic Engineering — *Introducing advanced tool use on the Claude Developer Platform*（2025-11-24）https://www.anthropic.com/engineering/advanced-tool-use
4. Anthropic Engineering — *Writing effective tools for agents — with agents*（2025-09-11）https://www.anthropic.com/engineering/writing-tools-for-agents
5. Anthropic Engineering — *Code execution with MCP*（2025-11-04）https://www.anthropic.com/engineering/code-execution-with-mcp
6. Claude Platform Docs — *Tool search tool* https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool （本环境因区域限制返回 "App unavailable"，内容以 #3 为准）
7. MCP Specification — *Tools*（2026-07-28）https://modelcontextprotocol.io/specification/2026-07-28/server/tools

**论文**
8. RAG-MCP — arXiv:2505.03275 https://arxiv.org/abs/2505.03275
9. ToolRet (*Retrieval Models Aren't Tool-Savvy*, ACL 2025) — arXiv:2503.01763 https://arxiv.org/html/2503.01763v1 ；仓库 https://github.com/mangopy/tool-retrieval-benchmark
10. Tool-to-Agent Retrieval — arXiv:2511.01854 https://arxiv.org/abs/2511.01854
11. ToolScope (ACL 2026 Long) — https://aclanthology.org/2026.acl-long.1573/
12. Dynamic ReAct — arXiv:2509.20386 https://arxiv.org/abs/2509.20386
13. *How Many Tools Should an LLM Agent See? A Chance-Corrected Answer* — arXiv:2605.24660 https://arxiv.org/abs/2605.24660
14. *Agent Skills for LLMs: Architecture, Acquisition, Security, and the Path Forward*（survey）— arXiv:2602.12430 https://arxiv.org/html/2602.12430v3
15. *What Keeps Agent Skills from Being Reusable? Evidence from 138K SKILL.md Files* — arXiv:2608.08453 https://arxiv.org/html/2608.08453v1
16. SkillsBench — arXiv:2602.12670 https://arxiv.org/abs/2602.12670 ；https://www.skillsbench.ai/blogs/introducing-skillsbench
17. SkillReducer — arXiv:2603.29919 https://arxiv.org/abs/2603.29919
18. Voyager — arXiv:2305.16291 https://arxiv.org/abs/2305.16291

**行业/工程实践**
19. Chroma — *Context Rot: How Increasing Input Tokens Impacts LLM Performance*（2025-07）https://www.trychroma.com/research/context-rot ；https://github.com/chroma-core/context-rot
20. ajing — *Tool Selection Optimization for LLM Agents at Scale*（2026-01-10）https://ajing.github.io/posts/2026-01-10-tool-selection-optimization-llm-agents-at-scale/
21. Agentman — *The Agent Skills Ecosystem in 2026*（2026-06-25）https://agentman.ai/blog/agent-skills-ecosystem-report-2026
22. OpenTelemetry — GenAI semantic conventions https://opentelemetry.io/docs/specs/semconv/registry/attributes/gen-ai/ ；https://github.com/open-telemetry/semantic-conventions-genai
23. Lunar.dev — *Dynamic Tool Selection* https://www.lunar.dev/post/why-dynamic-tool-discovery-solves-the-context-management-problem

**许可证核验（raw 文件）**
24. https://raw.githubusercontent.com/agentskills/agentskills/main/LICENSE （Apache-2.0）
25. https://raw.githubusercontent.com/mangopy/tool-retrieval-benchmark/main/LICENSE （Apache-2.0）
26. https://raw.githubusercontent.com/IBM/mcp-context-forge/main/LICENSE （Apache-2.0）
27. https://raw.githubusercontent.com/modelcontextprotocol/use-mcp/main/LICENSE （MIT）
28. https://raw.githubusercontent.com/mcp-use/mcp-use/main/LICENSE （MIT）
29. https://raw.githubusercontent.com/ComposioHQ/composio/master/LICENSE （MIT）
30. https://raw.githubusercontent.com/minedojo/voyager/main/LICENSE （MIT）
31. https://raw.githubusercontent.com/anthropics/skills/main/README.md （多数 Apache-2.0，doc 系列 source-available）
32. https://raw.githubusercontent.com/memoverflow/rag-mcp/main/LICENSE （404，未找到许可）
