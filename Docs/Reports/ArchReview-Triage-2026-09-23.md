# 架构评审 5 条的处置裁决（2026-09-23）

**用途**：本文件是 `temp/arch-review-gpt-20260923.md`（GPT-6 Astra 只读评审，33 请求 / 325 万 token）
在**落地阶段**的边界裁决记录。存在的唯一目的是防止三类返工：

1. **重做**已完成的条目（见 §2 第 1 条）。
2. **误改**已被其它工作流冻结的代码（见 §2 第 2 条）。
3. **盲改**需要产品/架构决策的启动语义（见 §2 第 3 条）。

> ⚠️ **并发风险前提**：本工作区存在多个 Agent 使用**同一 git 身份**（`hyfree`）提交。
> 本文件依据的是**提交与磁盘事实**，不是任何 Agent 的自述。

---

## 1. 状态总表

| # | 评审条目 | 优先级 | 当前状态 | 可执行性 |
|---|---|---|---|---|
| 1 | 子代理运行归档三链缺一致性提交协议 | P0 | **未动** | ⛔ 需先出 ADR（协议 + 迁移 + 故障注入验收） |
| 2 | 工具结果溢出落盘失败 fail-open | P0 | ✅ **已修并已验证**（`c65d91e8`） | — |
| 3 | 技能关键词先到先得、冲突静默丢技能 | P1 | **冻结中**（另有工作流在办） | ⛔ **禁止**就地改冲突策略 |
| 4 | Host 启动两套配置构建路径 | P1 | **未动** | ⛔ 需先裁决「配置来源优先级」 |
| 5 | Buffered / Streaming 双 Agent Loop | P2 | **未动** | ⛔ 需先建双模式黄金合同 |

---

## 2. 逐条依据

### 第 2 条：已修复，且护栏经变异验证（可信）

修复提交：`c65d91e8`（2026-09-23 15:30:53），改动 6 文件（+553/-73）：

- `ToolResultContextPolicy.MaterializeAsync` 的异常路径不再 `return content`；落盘抽为 `SpillAsync`，
  失败重试 1 次（共 2 次），仍失败则返回**仍有界**的 `BuildUnmaterializedBound`；
- 新增常量 `MaterializationFailureCode = "context_materialization_failed"`，正文内携带
  `error=` / `reason=` / `tool=` / `call=` / `session=` / 原始字符数 / UTF-8 字节数 / 行数 /
  `content_sha256` / `full_output_file=UNAVAILABLE`；
- 顺带修掉越界隐患：`BuildBoundedPreview` 旧实现按「通知原文长度」算预览预算，超长通知会撑破 8 KiB；
- 同步更正三处「把缺陷写成期望」的文档/测试：设计文档 §3.0.1、`Source/PuddingRuntime/code_map.md:24`、
  以及旧测试 `MaterializeAsync_Fails_Open_When_Working_Directory_Does_Not_Exist`
  （其断言 `AreEqual(content, result)` 曾把 fail-open 锁为期望）。

**本轮独立验证（非采信自述）**：

| 检查 | 实测结果 |
|---|---|
| 定向测试（我本人复跑） | `已通过! - 失败: 0，通过: 6，总计: 6，持续时间: 356 ms` |
| 旧缺陷断言是否残留 | `search_grep "Fails_Open\|fail open"` ⇒ 测试目录 **0 命中**（仅 `ToolExposurePlanner.cs:113` 一处无关注释） |
| **变异取红**（临时把异常路径改回 `return content`） | **红**：`Assert.IsLessThanOrEqualTo 失败。实际值 <13192> 不小于或等于预期值 <8192>` |
| 复原后工作树 | `git_diff` 空 ⇒ 与 HEAD 完全一致 |

⇒ **护栏有牙**：「测试存在」≠「测试能抓到该缺陷」，此处经反向注入证明能抓到。

### 第 3 条：被冻结，禁止就地改（关键裁决）

源码内有**硬约束**（`SkillEnforcerService.cs`，`CollectKeywords` 文档注释原文）：

> `⛔ 本方法的行为不得改动：它决定实际注入结果（改它 = 改生产行为，需灰度与遥测）。`
> `⛔ 不得各写一份`（指 G1/G4 与 `SkillKeywordNormalization` 必须同口径）

且另有研究文档（`temp/agent-skills-progressive-disclosure-retrieval-research.md`）已把该形态列为
**已证伪的反模式 #9**：139 技能 → **2490 关键词槽位 / 89.1% 噪声 / 1735 次注入被静默挤掉**，
并给出**方向性不同**的正解（`description` 承担唯一路由契约 + 语义/BM25 混合检索）。

⇒ **结论：不得把「先到先得」改成「按优先级排序」这类半修**。理由有二：

1. 会**改生产注入结果**，违反源码内的冻结约束，且需灰度；
2. 会让 G1 的基线（165 共享关键词 / 1735 被挤掉）与 G4 的归属判据**失真**，
   属于对**在办工作流**的越权改写。

**正确动作**：交由该检索/RSI 工作流统一做（检索式路由取代抢槽位），本线不碰。

### 第 4 条：真问题，但改法取决于决策（禁止盲改）

`Source/PuddingHost/Hosting/PuddingApplicationHost.cs`（URL 绑定段）原文：

```csharp
if (options.Urls.Count > 0)
{
    builder.WebHost.UseUrls(options.Urls.ToArray());
}
else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}
```

问题实质：`builder.Configuration` 已装载 `appsettings*.json` + `system.json` + 环境变量 + 命令行，
但此处**只认环境变量** `ASPNETCORE_URLS`；而 `UseUrls` 一旦调用会**显式指定**监听地址。

⇒ **盲改会静默改变绑定地址**：若 `appsettings.json` / `system.json` 里定义了 urls 而环境变量未设，
现状会被强制为 `http://0.0.0.0:8080`；若改成"从 `builder.Configuration` 读"，
行为会随之改变 —— **这是优先级决策，不是笔误修复**。

**必须先裁决**（三选一）：

| 选项 | 语义 | 影响 |
|---|---|---|
| A | 保持现状（env 唯一权威，配置里的 urls 不生效） | 零风险，但需把「配置里写 urls 无效」写进文档 |
| B | 改为 `builder.Configuration["urls"]` 单一来源（env 因在配置链内自然覆盖 json） | 语义统一，但配置了 urls 的环境会**改变监听地址** |
| C | 显式分级：`options.Urls` > 配置链 > 默认 8080，并**启动期打印生效来源** | 最符合评审 AC，改动最大 |

同一提交还需覆盖 CORS（`builder.Configuration["Cors:AllowedOrigins"]` + 内联默认值）与
`ASPNETCORE_ENVIRONMENT`（仅 bootstrap 配置读、builder 未显式读）的一致性口径。

### 第 1 条：P0，但必须 ADR 先行

评审原文即写明「需要 ADR」。它不是一笔补丁：要定义**权威运行事件日志**、提交顺序、
幂等键、重放水位与一致性检查，并注入三个写入阶段（文件 / DB 索引 / 会话投影）的故障。
在 ADR 冻结前改代码 = 把「部分成功后继续」换成另一套未验证语义。

---

## 3. 建议执行顺序（本线）

1. **第 1 条 ADR**（P0，无代码风险，解除后续实现阻塞）——待决策：权威链选定为哪一个。
2. **第 4 条裁决 A/B/C**（P1，一行级改动 + 启动期来源审计）。
3. **第 5 条**双模式黄金合同（P2，先建对照测试再谈抽取）。
4. **第 3 条**交由检索/RSI 工作流，本线只在其定稿后做集成验收。

> 本线另有用户直报的**存储管理页**问题（数据不直观 / 与实际不匹配）待办，
> 与上表无重叠、无他人占用，优先级高于第 5 条。

---

## 4. 本文件不做的事

- 不改任何源码、测试、配置（本轮仅写入本文件并提交）。
- 不把第 1/4/5 条写成"已修复"或"根因已定位"。
- 不引用任何 Agent 的自述结论作为已证事实。

---

*生成：默认助手（`default.global_general-assistant.6a8`），2026-09-23。*
*依据：`Docs/Reports/ArchReview-GPT6Astra-2026-09-23.md` + 本人对源码/提交/测试的独立复核。*
