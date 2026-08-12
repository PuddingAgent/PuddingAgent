# 多引擎迭代聚合搜索 (aggregate-search)

## 概述

本 SKILL 定义了**多引擎迭代聚合搜索**工作流：当用户发起搜索/调研指令时，使用 `deepseek-v4-flash` 子代理并行查询全部可用搜索引擎，聚合并去重结果，评估质量。若质量达标则返回综合报告；若不达标则根据上一轮结果优化检索策略，进入下一轮，**最多5轮迭代**。

**设计目标**：在成本可控的前提下，最大化搜索覆盖度和结果质量。

---

## 搜索引擎清单

| 引擎 | 工具名 | 特点 | 适用场景 |
|------|--------|------|----------|
| 豆包搜索 | `doubao_search` | 公开网页搜索，覆盖面广 | 通用知识、新闻、技术博客 |
| AnySearch | `anysearch_search` | 统一搜索，支持 domain/tag/content_types 过滤 | 精确领域搜索、按内容类型筛选 |
| 知乎站内 | `zhihu_search` | 知乎站内搜索 | 中文专业讨论、经验分享、观点 |
| 知乎全局 | `zhihu_global_search` | 知乎全局搜索，支持高级过滤 | 更宽的知乎覆盖、按时间/热度过滤 |
| GitHub | `github_search` | 代码仓库/Issue/文档搜索 | 开源项目、代码示例、技术实现 |

每轮搜索应**同时调用全部5个引擎**，并行查询，不串行等待。

---

## 工作流（5 Phase × 最多5轮）

```
┌─────────────────────────────────────────────────┐
│  Phase 1: 解析查询  ──→  Phase 2: 并行搜索     │
│                                        │        │
│  Phase 5: 循环判断 ←─ Phase 4: 质量判断 ←─ Phase 3: 聚合评估 │
└─────────────────────────────────────────────────┘
```

### Phase 1: 解析用户查询

由主代理（非子代理）执行：

1. **理解意图**：分析用户搜索/调研指令，明确：
   - 核心问题是什么（what）
   - 需要什么类型的信息（事实/观点/代码/教程/对比）
   - 语言偏好（中文/英文/不限）
   - 时间偏好（最新/不限/特定时间段）

2. **生成搜索变体**：为每个引擎生成针对性查询：
   - `doubao_search`：用自然语言完整提问
   - `anysearch_search`：提取关键词 + domain/tag/content_types 筛选条件
   - `zhihu_search`：知乎社区常见表述方式
   - `zhihu_global_search`：带时间/排序过滤的查询
   - `github_search`：技术关键词 + 仓库名/Issue 关键词

3. **输出查询计划**：
   ```
   QUERY PLAN (Round N):
   - doubao: "查询文本"
   - anysearch: { query: "关键词", domain: "...", content_types: [...] }
   - zhihu: "知乎风格查询"
   - zhihu_global: { query: "查询", sort: "...", time: "..." }
   - github: "技术关键词 repo: 或 #"
   ```

### Phase 2: 并行搜索

为每个引擎 `spawn_sub_agent` 一个 `deepseek-v4-flash` 子代理，**同时派发**：

```json
{
  "model": "deepseek-v4-flash",
  "question": "使用 {工具名} 搜索以下查询，返回结果：{查询文本}。提取每条结果的标题、URL、摘要、发布时间。",
  "effort": "quick",
  "stop_condition": "搜索完成，返回结构化结果",
  "output": "SUMMARY: 搜索结果概要\nFINDINGS: [{title, url, snippet, date}]",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

**关键约束**：
- 5个子代理同时派发，不等串行
- 每个子代理只调用一个搜索工具
- 子代理只读：不写文件、不执行命令、不修改任何状态
- 若某个引擎不可用（工具报错），跳过该引擎并记录

### Phase 3: 聚合评估

主代理收集所有子代理返回结果后执行：

#### 3.1 去重
- 按 URL 去重（规范化 URL 后比较）
- 按 标题相似度 去重（相似度 > 85% 视为重复）
- 保留信息量最大的版本

#### 3.2 排序
按以下权重综合排序：
- 相关性（与查询意图的匹配度）：权重 40%
- 权威性（来源域名/作者可信度）：权重 25%
- 新鲜度（信息时效性，视查询需求）：权重 20%
- 信息深度（内容详实程度）：权重 15%

#### 3.3 质量评估（4维度打分，每维度 0-10）

| 维度 | 评分标准 |
|------|----------|
| **相关度** | 结果与用户查询意图的直接匹配程度。10=完全匹配，0=毫不相关 |
| **覆盖度** | 是否回答了用户问题的各方面。10=全覆盖，0=只触及冰山一角 |
| **多样性** | 信息来源多样性：不同引擎命中数、不同域名数。10=来源丰富，0=单一来源 |
| **权威性** | 来源可信度：官方文档>权威媒体>社区讨论>个人博客。10=高度权威，0=低可信 |

**综合质量分** = (相关度 + 覆盖度 + 多样性 + 权威性) / 4，范围 0-10。

#### 3.4 置信度
基于以下因素计算置信度（高/中/低）：
- 结果总数（>20条为高，10-20为中，<10为低）
- 引擎覆盖数（5个全命中为高，3-4为中，<3为低）
- 跨引擎一致信息占比

### Phase 4: 质量判断

```
IF 综合质量分 >= 7.0 AND 覆盖度 >= 6.0:
    → 质量达标，进入报告生成
ELSE IF 已达最大轮次(5轮):
    → 返回当前最佳结果 + 标注 GAPS
ELSE:
    → 进入 Phase 5 优化循环
```

### Phase 5: 迭代优化（若不达标）

分析当前轮的不足，生成下一轮优化策略：

1. **识别知识空白**：哪些方面的信息仍然缺失？
2. **分析失败原因**：
   - 相关度低 → 重新表述查询，使用同义词/专业术语
   - 覆盖度低 → 拆分子问题，补充新维度的查询
   - 多样性低 → 调整引擎参数（换 domain/content_types/sort）
   - 权威性低 → 指定权威来源（如 `anysearch_search` 的 domain 参数指向官方域名）
3. **生成下一轮查询计划**：保留有效查询 + 新增/修改查询

```
OPTIMIZATION (Round N → N+1):
- 保留: [上一轮有效查询列表]
- 修改: [需要调整的查询 + 调整原因 + 新查询]
- 新增: [补充维度的新查询]
- 跳过: [上一轮无效果引擎，本轮跳过或换策略]
```

然后回到 Phase 2，使用新查询计划执行第 N+1 轮搜索。

---

## 输出格式

搜索完成后，主代理生成结构化报告：

```markdown
# AGGREGATE SEARCH REPORT

## SUMMARY
[2-5句综合摘要，直接回答用户问题]

## FINDINGS
### 主题1: [分类名称]
- 发现1: [关键信息] — 来源: [域名]
- 发现2: [关键信息] — 来源: [域名]

### 主题2: [分类名称]
- 发现3: [关键信息] — 来源: [域名]

## SOURCES
| # | 标题 | URL | 引擎 | 轮次 |
|---|------|-----|------|------|
| 1 | ... | https://... | doubao | R1 |
| 2 | ... | https://... | zhihu_global | R2 |
| ... | | | | |

## QUALITY
- 综合质量分: X.X / 10
  - 相关度: X / 10
  - 覆盖度: X / 10
  - 多样性: X / 10
  - 权威性: X / 10
- 置信度: 高/中/低
- 引擎命中: X/5 (doubao✓, anysearch✓, zhihu✓, zhihu_global✓, github✗)

## ITERATIONS
- 使用轮次: N / 5
- 各轮查询策略摘要:
  - R1: [策略简述] → 质量分 X.X
  - R2: [优化策略] → 质量分 X.X
  - ...

## GAPS
[如有，列出仍存在的知识空白或未回答的方面]
- [空白1]: [为什么未能覆盖]
- [空白2]: [建议用户补充查询的方向]
```

---

## 子代理委派模板

每轮为每个引擎派发一个子代理。以下是完整模板：

### doubao_search 子代理

```json
{
  "model": "deepseek-v4-flash",
  "question": "你是搜索子代理。使用 doubao_search 工具执行以下查询：\n查询: \"{query}\"\n\n要求：\n1. 调用 doubao_search 搜索\n2. 提取每条结果的：标题、URL、摘要（≤200字）、发布时间（如有）\n3. 如结果不足5条，尝试用同义词重搜\n4. 不修改任何文件，不执行任何命令\n\n返回格式：\nFINDINGS:\n- [{title}, {url}, {snippet}, {date}]\n- ...",
  "effort": "quick",
  "stop_condition": "搜索完成并返回结构化结果",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

### anysearch_search 子代理

```json
{
  "model": "deepseek-v4-flash",
  "question": "你是搜索子代理。使用 anysearch_search 工具执行以下查询：\n查询: \"{query}\"\n参数: domain={domain}, tag={tag}, content_types={content_types}\n\n要求：\n1. 调用 anysearch_search 搜索\n2. 提取每条结果的：标题、URL、摘要（≤200字）\n3. 不修改任何文件，不执行任何命令\n\n返回格式：\nFINDINGS:\n- [{title}, {url}, {snippet}]\n- ...",
  "effort": "quick",
  "stop_condition": "搜索完成并返回结构化结果",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

### zhihu_search 子代理

```json
{
  "model": "deepseek-v4-flash",
  "question": "你是搜索子代理。使用 zhihu_search 工具执行以下查询：\n查询: \"{query}\"\n\n要求：\n1. 调用 zhihu_search 搜索\n2. 提取每条结果的：标题、URL、摘要（≤200字）、作者、点赞数（如有）\n3. 不修改任何文件，不执行任何命令\n\n返回格式：\nFINDINGS:\n- [{title}, {url}, {snippet}, {author}, {upvotes}]\n- ...",
  "effort": "quick",
  "stop_condition": "搜索完成并返回结构化结果",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

### zhihu_global_search 子代理

```json
{
  "model": "deepseek-v4-flash",
  "question": "你是搜索子代理。使用 zhihu_global_search 工具执行以下查询：\n查询: \"{query}\"\n参数: sort={sort}, time_filter={time_filter}\n\n要求：\n1. 调用 zhihu_global_search 搜索\n2. 提取每条结果的：标题、URL、摘要（≤200字）、发布时间\n3. 不修改任何文件，不执行任何命令\n\n返回格式：\nFINDINGS:\n- [{title}, {url}, {snippet}, {date}]\n- ...",
  "effort": "quick",
  "stop_condition": "搜索完成并返回结构化结果",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

### github_search 子代理

```json
{
  "model": "deepseek-v4-flash",
  "question": "你是搜索子代理。使用 github_search 工具执行以下查询：\n查询: \"{query}\"\n\n要求：\n1. 调用 github_search 搜索\n2. 提取每条结果的：仓库名/标题、URL、描述（≤200字）、star数（如有）\n3. 不修改任何文件，不执行任何命令\n\n返回格式：\nFINDINGS:\n- [{repo_or_title}, {url}, {description}, {stars}]\n- ...",
  "effort": "quick",
  "stop_condition": "搜索完成并返回结构化结果",
  "timeout_seconds": 60,
  "max_rounds": 5
}
```

---

## 迭代优化策略矩阵

| 不足维度 | 优化策略 | 示例 |
|----------|----------|------|
| 相关度低 | 用专业术语/同义词替换；缩小查询范围 | "性能优化" → "GC 调优 .NET memory pressure" |
| 覆盖度低 | 拆分子问题；补充新维度查询 | 只搜到定义 → 补搜 "优缺点""对比""实际案例" |
| 多样性低 | 调整引擎参数；换 domain/content_types | anysearch domain 改为不同垂直站点 |
| 权威性低 | anysearch 指定权威域名；zhihu 关注高赞回答 | domain 指向 official docs / RFC / arxiv |
| 引擎命中少 | 检查不可用引擎；换查询语言（中英文切换） | 中文无果 → 尝试英文查询 |

---

## 关键约束

1. **子代理模型**：只用 `deepseek-v4-flash`（快速、低成本）
2. **并行搜索**：每轮5个子代理同时派发，不串行等待
3. **最大轮次**：5轮迭代。达到5轮后无论质量如何都返回当前最佳结果
4. **子代理只读**：子代理不写文件、不执行命令、不修改任何状态，只调用搜索工具
5. **每轮关注增量**：后续轮次只搜索新/优化查询，不重复已有结果
6. **成本控制**：每轮5个子代理 × 最多5轮 = 最多25次子代理调用

---

## 质量门禁

- [ ] Phase 1: 查询计划覆盖全部5个引擎，每个引擎有针对性查询
- [ ] Phase 2: 子代理同时派发（非串行），使用 deepseek-v4-flash
- [ ] Phase 3: 去重+排序+4维度评分均执行
- [ ] Phase 4: 质量判断逻辑正确（≥7.0且覆盖度≥6.0达标）
- [ ] Phase 5: 优化策略有针对性，不是简单重复
- [ ] 输出格式完整：SUMMARY / FINDINGS / SOURCES / QUALITY / ITERATIONS / GAPS
- [ ] 子代理只读约束未被违反
- [ ] 最大轮次不超过5

---

## 成本估算

| 场景 | 子代理调用数 | 估算 Token | 估算费用（¥） |
|------|:---:|:---:|:---:|
| 1轮达标 | 5 | ~50K-100K | 0.10-0.20 |
| 3轮达标 | 15 | ~150K-300K | 0.30-0.60 |
| 5轮未达标 | 25 | ~250K-500K | 0.50-1.00 |

> deepseek-v4-flash 定价：¥1/百万入 + ¥2/百万出。单次子代理约10K-20K tokens。

---

## 示例：用户查询"C# 异步编程最佳实践"

### R1 查询计划
```
- doubao: "C# 异步编程最佳实践 async await"
- anysearch: { query: "C# async await best practices", content_types: ["article", "documentation"] }
- zhihu: "C# 异步编程 最佳实践"
- zhihu_global: { query: "C# async await 最佳实践", sort: "relevance" }
- github: "C# async best practices"
```

### R1 评估 → 质量分 6.0（覆盖度不足）
- 缺少：错误处理模式、性能陷阱、正式规范文档

### R2 优化查询
```
- doubao: "C# async await 错误处理 性能陷阱 CancellationToken"
- anysearch: { query: "C# async pitfalls exception handling", domain: "learn.microsoft.com", content_types: ["documentation"] }
- zhihu: "C# 异步编程 坑 CancellationToken"
- zhihu_global: { query: "C# async await 踩坑", sort: "upvotes" }
- github: "C# async CancellationToken BestPractices"
```

### R2 评估 → 质量分 8.2（达标 ✅）

### 输出报告
（按上述输出格式生成完整报告）

---

## 历史参考

- v1.0.0（2026-08-12 首次创建，基于多引擎聚合搜索需求设计）