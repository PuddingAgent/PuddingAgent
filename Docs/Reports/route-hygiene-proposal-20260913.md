# [路由卫生] `deepseek-v4-flash` 未注册引用清理 · 提案（2026-09-13）

> 任务卡：`0278f10610d74a0cbb4d70a0ebf2a3d5`
> **本轮为提案阶段**：**未修改任何源码/配置**，特别**未触碰** `D:\data\config\llm.providers.json`（详见文末「零写入声明」）。
> 检索方式：`search_grep`（全文）+ `file_read`；检索时间 2026-09-13。

---

## 0. 基线：当前注册表 vs 实际被引用的路由

**已注册（父级给定 `list_llm_providers`，本次未复查；另经实测 `D:\data\config\llm.providers.json` 348 行交叉确认）**

| route | 证据 |
|---|---|
| `bigmodel/glm-5.3-flash` | 配置 line 274-320（provider=`bigmodel`，`isDefault:true`，vision/reasoning-high/code/slow/cheap/long-context） |
| `deepseek/deepseek-flash` | 配置 line 169-244（provider=`deepseek`，modelId=`deepseek-flash`，`protocol:"responses"`，1M ctx / 384K out，vision，`isDefault:true`） |
| `volcengine-ark/doubao-seedream-5-0-260128` | 配置 line 1-60 |
| `volcengine-ark/doubao-seedream-5-0-pro-260628` | 配置 line 61-105 |

配置文件内 `deepseek` provider **只有 `deepseek-flash` 一个 model 条目**（无 v4-flash / v4-pro / vision-exp）——即「旧路由名在配置文件里已经清理干净，但**仓库与 Agent 配置层仍在引用旧名**」。

**未注册但被引用的 route（本次清点结果）**

| route | 引用点数 | 性质 |
|---|---|---|
| `deepseek/deepseek-v4-flash` | 14 处（A 类） | 真缺陷（本卡主题） |
| `deepseek/deepseek-v4-pro` | 18 处（A 类） | **同类缺陷（高价值副产品）** |
| `bigmodel/glm-5.3` | 2 处（A 类） | 同类缺陷（正确名为 `bigmodel/glm-5.3-flash`） |
| `qwen/qwen3.8-max` | 1 处（A 类） | 同类缺陷 |
| `deepseek-v4-flash-vision-exp` | 仅 Docs 叙述（C 类） | 历史设计文档，不该改 |
| `opencode/*`、`moonshot/*` | 仅测试内合成样本（B 类） | 非缺陷 |

**后果链（与卡面描述一致）**：`smart_*` / 子代理委派 → 读 Agent manifest 的 `{role}Model`（未注册）→ 路由解析失败告警 → 退到 `SmartXxxTool.FallbackModelIds`（**同样未注册**，见 §4.1）→ 再退到安全网 `Source/PuddingRuntime/Tools/BuiltIns/Agents/SubAgentTool.cs:62 private const string DefaultFallbackModelRoute = "bigmodel/glm-5.3-flash"`（**这个是对的**）。整条链每调用一次浪费一次解析 + 产生一条告警。

---

## 1. 分类定义

| 类 | 含义 | 处置 |
|---|---|---|
| **a** | 运行期会被解析成路由的旧名（配置/常量/Skill/预设/协议文档） | **需改** |
| **b** | 裸 modelId（无 providerId），或测试内合成样本 | **不是缺陷**；仅登记为可加固点 |
| **c** | 历史/叙述性文档（ADR、报告、Features 设计稿、会话归档、日志） | **不该改**（保留历史） |
| **d** | 备份 / 生成物 / 构建产物 / 已被 gitignore 的临时物 | **不改**（不在提案范围） |

---

## 2. ① 全量引用清单表

### 2.1 A 类（需改）— Agent 配置层（**运行期路由，最高风险**）

| # | 文件:行 | 原文（截取） | 类 | 需改 | 建议改法 |
|---|---|---|---|---|---|
| A1 | `D:\data\agents\default.global_general-assistant.6a8\manifest.json:159` | `"explorerModel": "deepseek/deepseek-v4-flash",` | a | ✅ | `"deepseek/deepseek-flash"` |
| A2 | 同上 `:160` | `"researcherModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 选项 A/B |
| A3 | 同上 `:161` | `"plannerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 同上 |
| A4 | 同上 `:162` | `"reviewerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 同上 |
| A5 | 同上 `:163` | `"developerModel": "bigmodel/glm-5.3",` | a | ✅ | `"bigmodel/glm-5.3-flash"` |
| A6 | 同上 `:164` | `"deployerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A7 | 同上 `:165` | `"testerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A8 | 同上 `:166` | `"visionHelperModel": "deepseek/deepseek-flash",` | — | ❌ | **已是正确路由（对照组）** |
| A9 | 同上 `:22`（`systemPrompt` 单行 JSON） | `…委派deepseek-v4-flash、bigmodel/glm-5.3子代理…` / `…调用deepseek-v4-flash子代理…` | a | ✅ | 改 `deepseek/deepseek-flash` + `bigmodel/glm-5.3-flash`（**这是注入模型的提示词，会持续诱导模型写出错误路由**） |
| A10 | `D:\data\agents\default.global_general-assistant.258\manifest.json:159` | `"explorerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A11 | 同上 `:160` | `"researcherModel": "deepseek/deepseek-v4-flash",` | a | ✅ | `"deepseek/deepseek-flash"` |
| A12 | 同上 `:161` | `"plannerModel": "qwen/qwen3.8-max",` | a | ✅ | 见 §4.9 |
| A13 | 同上 `:162` | `"reviewerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A14 | 同上 `:163` | `"developerModel": "deepseek/deepseek-v4-flash",` | a | ✅ | `"deepseek/deepseek-flash"` |
| A15 | 同上 `:164` | `"deployerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A16 | 同上 `:165` | `"testerModel": "deepseek/deepseek-v4-pro",` | a | ✅ | 见 §4.9 |
| A17 | `D:\data\agents\default.general-assistant-001\manifest.json:7..13` | 7 个角色全为 `"deepseek/deepseek-v4-pro"` | a | ✅ | 见 §4.9 |
| A18 | `D:\data\agent-templates\general-assistant\manifest.json:97` | `"preferredModelId": "deepseek-v4-pro",` | a | ✅ | 裸 modelId 字段，改 `"deepseek-flash"`（或待裁定的 Pro 替代） |
| A19 | 同上 `:99` | `"memoryLlmModelId": "deepseek-v4-flash",` | a | ✅ | `"deepseek-flash"`（**新 Agent 的默认记忆模型**） |

> ⚠️ **A 类共 18 行需改**（6a8: 8 行含 systemPrompt；258: 7 行；001: 7 行；模板: 2 行 → 去重后按行计为 24 行，见 §4.9 说明）。
> ⚠️ 这三个 `manifest.json` 是否被数据库权威覆盖 **未验证**（我未读 `D:\data\databases\platform.db`，属本卡范围外）→ 见 RISKS。

### 2.2 A 类（需改）— 仓库源码常量（需 build + 重启）

| # | 文件:行 | 原文 | 类 | 需改 | 建议改法 |
|---|---|---|---|---|---|
| A20 | `Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartExploreTool.cs:37` | `new[] { "deepseek/deepseek-v4-flash" };` | a | ✅ | `new[] { "deepseek/deepseek-flash" };` |
| A21 | `.../SmartDevelopTool.cs:31` | `new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };` | a | ✅ | 见 §4.2 选项 A/B（默认 A：`new[] { "deepseek/deepseek-flash" };`） |
| A22 | `.../SmartDeployTool.cs:31` | 同上 | a | ✅ | 同上 |
| A23 | `.../SmartPlanTool.cs:34` | 同上 | a | ✅ | 同上 |
| A24 | `.../SmartResearchTool.cs:33` | 同上 | a | ✅ | 同上 |
| A25 | `.../SmartReviewTool.cs:32` | 同上 | a | ✅ | 同上 |
| A26 | `.../SmartTestTool.cs:32` | 同上 | a | ✅ | 同上 |
| A27 | `Source/PuddingRuntime/Services/CroppedLayersProvider.cs:114` | `ModelId = "deepseek-v4-flash",` | a | ✅ | `ModelId = "deepseek-flash",`（同段注释已声明「只是 named profile 缺失时的 legacy fallback」） |
| A28 | `Source/PuddingRuntime/Tools/BuiltIns/Management/LlmResourcePoolTool.cs:143` | `[ToolParam("筛选特定模型（可选），如 'deepseek-v4-flash'。支持模糊匹配。")]` | a | ✅ | 示例改 `'deepseek/deepseek-flash'`（工具描述会进 prompt，属"提示词污染"） |

### 2.3 A 类（需改）— Skill / 文档（热生效，无需 build）

| # | 文件:行 | 原文 | 类 | 需改 | 建议改法 |
|---|---|---|---|---|---|
| A29 | `skills/aggregate-search/manifest.json:11` | `"model": "deepseek/deepseek-v4-flash",` | a | ✅ | `"model": "deepseek/deepseek-flash",`（**Skill 索引路由字段**） |
| A30 | `skills/aggregate-search/SKILL.md:5` | 「…使用 `deepseek-v4-flash` 子代理并行查询…」 | a | ✅ | 叙述改 `deepseek/deepseek-flash` |
| A31 | 同上 `:64` | 「…`spawn_sub_agent` 一个 `deepseek-v4-flash` 子代理…」 | a | ✅ | 同上 |
| A32-37 | 同上 `:68 / :209 / :222 / :235 / :248 / :261` | `"model": "deepseek-v4-flash",`（JSON 委派模板 ×6） | a | ✅ | **逐字改为 `"deepseek/deepseek-flash",`** |
| A38 | 同上 `:286` | 「子代理模型：只用 `deepseek-v4-flash`」 | a | ✅ | 同上 |
| A39 | 同上 `:298` | 「…使用 deepseek-v4-flash」 | a | ✅ | 同上 |
| A40 | 同上 `:316` | 「deepseek-v4-flash 定价：¥1/百万入 + ¥2/百万出」 | a | ✅ | 模型名改 `deepseek-flash`（价不变） |
| A41 | `skills/v4-flash-architect-dev/SKILL.md:53` | `"model": "deepseek-v4-flash",` | a | ✅ | `"model": "deepseek/deepseek-flash",` |
| A42 | 同上 `:118` | 「子代理只委派给 `deepseek-v4-flash`」 | a | ✅ | 同上 |
| A43 | `Docs/agent-collaboration-agreement.md:48` | 「…快速任务=deepseek/deepseek-v4-flash」 | a | ✅ | `deepseek/deepseek-flash`（**这是双方现行约定文档，非历史稿**） |
| A44 | `Tools/Diagnostics/README.md:147` | `--provider-id deepseek --model-id deepseek-v4-flash` | a | ✅ | `--model-id deepseek-flash` |

### 2.4 A 类（需改）— 前端 provider 模板（UI 预设，需前端 build）

| # | 文件:行 | 原文 | 类 | 需改 | 建议改法 |
|---|---|---|---|---|---|
| A45 | `Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts:220` | `modelId: 'deepseek-v4-flash',` | a | ✅ | `modelId: 'deepseek-flash',`（`:221` name 同步；`protocol:'openai'` 宜同时核对为 `'responses'`） |
| A46 | 同上 `:235` | `modelId: 'deepseek-v4-pro',` | a | ✅ | **待裁定**：无已注册 Pro 同层替代 → 选项：删除该条目 / 改名并保持未注册（不建议） |
| A47 | 同上 `:22` | `const DEEPSEEK_V4_CAPABILITIES = [` | — | ⭕可选 | 纯命名，可改为 `DEEPSEEK_FLASH_CAPABILITIES`（非必需） |

### 2.5 B 类（**不是缺陷**，不提案修改；仅登记可加固点）

| 位置 | 内容 | 说明 |
|---|---|---|
| `Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/index.test.tsx:110/111/122` | `modelId: 'deepseek-v4-flash'` / `'DeepSeek-V4-Flash'` / `'deepseek-v4-pro'` | 模板单测 fixture，随 A45/A46 一起改即可 |
| `.../chat/utils/chatDiagnostics.test.ts:39`、`.../chat/reducer/subAgentReducer.test.ts:198/210`、`.../chat/projections/executionFlowProjector.test.ts:500/508`、`.../chat/hooks/useChatState.recovery.test.ts:879/893`、`.../chat/hooks/__tests__/fixtures-conversation.json` | `modelId / model: 'deepseek-v4-flash'`（渲染与诊断断言 fixture） | 前端单测 fixture；非路由缺陷，可随手改可留 |
| `Source/PuddingPlatformAdmin/src/pages/chat/utils/chatDiagnostics.test.ts:39` | `modelId: 'deepseek-v4-flash'` | 诊断渲染单测 fixture |
| `.../chat/reducer/subAgentReducer.test.ts:198/210` | 同上 | 同上 |
| `.../chat/projections/executionFlowProjector.test.ts:500/508` | `model: 'deepseek-v4-flash'` 断言 | 同上 |
| `.../chat/hooks/useChatState.recovery.test.ts:879/893` | `modelId` + `Model: \`deepseek-v4-flash\`` 断言 | 同上 |
| `.../chat/components/AgentMessageBubble.projection.test.tsx:271/276`、`SubAgentActivityDock.test.tsx:379`、`execution-flow/DelegationRow.test.tsx:24/25/54/112` | 同上 | 同上 |
| `.../chat/hooks/__tests__/fixtures-conversation.json:1` | 历史会话投影 fixture（含 `deepseek-v4-flash` 文本） | **真实会话快照**，建议保留原样 |
| `Source/PuddingWebApiTests/SessionEventsControllerTests.cs:149/354/359` | `Assert.AreEqual("deepseek-v4-flash", …)` / `PreferredModelId=` / `ModelId=` | 合成断言，非路由解析 |
| `Source/PuddingRuntimeTests/Services/DesignCouncilRuntimeServiceTests.cs:45/302/352/355`、`MemoryLlmInvocationClientUsageTests.cs:34/66/98` | 合成样本 | 同上 |
| `Source/PuddingRuntimeTests/Tools/ListLlmProvidersToolTests.cs:33/35/45/47/93/98/114/233/239` | **断言「`deepseek-v4-flash` 同时注册于 `deepseek` 与 `opencode` ⇒ 歧义」** | ⚠️ **语义漂移**：`opencode` provider 已从生产注册表移除，该用例描述的场景已不存在 → 建议随 §4.1 改动一并**更新或删除**该用例（不属"未注册引用"缺陷） |
| `Source/PuddingPlatformTests/Services/AgentRuntimeProfileResolverTests.cs:28/49/50/213`、`BenchmarkEvaluationServiceTests.cs:54/101`、`SessionStateManagerSequenceTests.cs:392/452`、`TokenUsageRebuildServiceTests.cs:134/169`、`WorkspaceAgentFileServiceTests.cs:148`、`Scheduling/ProviderModelExecutionWindowResolverTests.cs:54` 等 | 合成 `ModelId` 字符串 | 非缺陷；如做全仓"零旧名"洁癖，可批量替换，**不建议**（会制造无价值 diff） |
| `Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts` 内模型对象只有 `modelId` | 无 providerId | **设计使然**（model 嵌在 provider 对象内） |
| `D:\data\agent-templates\general-assistant\manifest.json:96/98` | `preferredProviderId`/`memoryLlmProviderId` = `"deepseek"`（与裸 modelId 成对） | **DB 契约（providerId+modelId 双字段）**，非缺陷 |

### 2.6 C 类（历史/叙述，**不改**）

| 位置 | 提及 |
|---|---|
| `Docs/README.md:182` | `deepseek-v4-flash-vision-exp` 未通过验收（ADR-077 说明） |
| `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md:103` | 指标表头 |
| `Docs/Features/原生视觉理解与多模态意图同步规划-2026-09-12.md:386` | 引用 DeepSeek 官方公告「旧模型名 `deepseek-v4-flash` … 已下线」（**反证本卡成立**） |
| `Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md:38/39/1452` | 定价表 + 示例 `goalQuestionerModelId` |
| `Docs/07架构/65ADR-064…md:171/178`、`66ADR-065…md:87`、`92ADR-077…md:13/67/112/315/533`、`design/auto-compression-strategy-v1.md:89/173`、`design/sliding-window-fuse-v1.md:110` | ADR / 设计稿 |
| `Docs/Reports/*`（`pudding-agent-efficiency-2026-09-05.metrics.json:20/36/52/76/92/108`、`PuddingAgent首轮修复与验证-2026-09-05.md:41`、`PuddingAgent-GLM-Optimization-2026-09-11/audit-evidence/**/board-receipts.json:263`、`PuddingAgent-Autonomy-Audit-2026-09-12/02-….md:27`、`…/evidence/board-receipts.json:47`） | 历史度量/看板快照 |
| `How-Debuge.md:2921` | 排障笔记 |
| `memory/**`（`agent-collaboration-agreement.md:48` 之外的档案：`goal-archive-2026081*.md`、`goal-archive-20260820*.md`、`goal-archive-20260821*.md`、`goal-archive-2026-09-12*.md` 等 40+ 处） | 会话归档 |
| `D:\data\jsonl\**.jsonl`（如 `446afcb3….jsonl:319-321`、`861ce7e8…-sub-e8fcdaf2.jsonl:1-3`）+ `D:\data\agents\*\memory\session-summaries\**.md` | **运行日志/摘要（不可变历史）** |
| `D:\data\agents\*\goal.md:18/25/36/58` | 我自己的目标叙述（可由各自 Agent 择机更新，非配置） |
| `Reports/PuddingAgent-GLM-Optimization-*` | 同上 |

### 2.7 D 类（备份 / 生成物 / 已被 gitignore，**不改**）

| 位置 | 说明 |
|---|---|
| `D:\data\config\llm.providers.json.bak`、`.bak2`、`.bak-20260723-vision-fix`、`.bak-20260805-lmstudio-embedding`、`.bak-20260826-152233`、`.bak-20260826-152852`、`before-model-protocol-20260809-121522`、`before-opencode-models-20260809-125538`、`before-opencode-go-models-20260810-161443`、`before-rename-back-20260810-162623` | **10 份历史配置备份**（含 `deepseek-v4-flash`/`-pro`/`-vision-exp`/opencode-go 条目）→ 属"配置考古"，**保留** |
| `Source/PuddingPlatformAdmin/src/.umi-test/appData.json:4151`、`src/.umi-production/appData.json:4154` | UMI 生成缓存 |
| `tmp/codex-build/llm-input-budget/wwwroot/admin/da21f5f2-async.*.js` | 前端构建产物（内含旧模板字符串） |
| `Source/PuddingWebApiTests/temp/shadow-perm-out/default-data/agent-template-presets/general-assistant.json:8` | 测试影子目录；路径含 `temp/`，已被根 `.gitignore` 的 `temp/` 规则忽略 → **无需清理** |
| `Artifacts/TestResults/publish/obj/bin/dist/wwwroot/**` | 构建/测试产物 |

---

## 3. ③ 执行窗口建议（先说结论，再给 diff）

| 批次 | 内容 | 生效方式 | 风险 |
|---|---|---|---|
| **W1（可立即热改，零重启）** | A29–A44：`skills/aggregate-search/{manifest.json,SKILL.md}`、`skills/v4-flash-architect-dev/SKILL.md`、`Docs/agent-collaboration-agreement.md`、`Tools/Diagnostics/README.md` | Skill 在**每次加载**时读盘 → 下一次 `spawn_sub_agent(model=...)` 即生效；文档纯文本 | 🟢 低。唯一注意：Skill 内的 `"model"` 字段若有索引缓存，改后需确认 Skill 列表刷新 |
| **W2（需前端构建，可与下次常规前端构建合并）** | A45–A47：`providerTemplates.ts` + `index.test.tsx` | 前端 build + 刷新页面 | 🟢 低。**只影响"新建 provider 的模板"，不改变存量 provider 配置**；`protocol` 建议同步由 `openai` 复核为 `responses` |
| **W3（需 `dotnet build` + 重启 Core，唯一串行点）** | A20–A28：7 个 `SmartWorkflow*.cs` + `CroppedLayersProvider.cs` + `LlmResourcePoolTool.cs` | 编译 → 部署 → 重启 | 🟡 **中**：① 当前有 7 个 agent 在跑，重启窗口须与父级/蜜糖对齐；② **严禁并发 build（MSB3027 文件锁）**；③ `CroppedLayersProvider` 是 subconscious 记忆裁剪路径，若仍解析失败会**静默降级**（改后应观察 `[MemoryCrop] Flash invocation failed` 告警是否消失）；④ `SmartWorkflow` 改的是 `FallbackModelIds`，只影响"manifest 角色模型缺失/解析失败"时的兜底，**不改变 manifest 有值时的行为** |
| **W4（最高风险，需逐 Agent 停机窗口 + DB 核对）** | A1–A19：`D:\data\agents\*\manifest.json`、`D:\data\agent-templates\...\manifest.json` | 改盘上 JSON 后，需确认是否被 `platform.db` 覆盖（**未验证**）；若 DB 权威 → 必须走管理 API/UI 改，改文件无效 | 🔴 **高**：直接影响 7 个在用 Agent 的委派路由；建议①先备份②单个 Agent 试点（建议 6a8）③改后跑 1 次 `smart_explore` smoke 确认无路由告警 |

**批次依赖**：W3 与 W4 不宜同窗口（同时改代码与配置会使"告警消失"归因困难）。建议顺序 **W1 → W3 → W4**，W2 择机。

---

## 4. ② 逐文件精确 diff 提案（unified diff，可直接 `apply_patch`）

### 4.1 SmartWorkflow 兜底模型（7 文件）

`Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartExploreTool.cs`
```diff
--- a/Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartExploreTool.cs
+++ b/Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartExploreTool.cs
@@ -36,2 +36,2 @@
     protected override IReadOnlyList<string>? FallbackModelIds =>
-        new[] { "deepseek/deepseek-v4-flash" };
+        new[] { "deepseek/deepseek-flash" };
```

`SmartDevelopTool.cs:31` / `SmartDeployTool.cs:31` / `SmartPlanTool.cs:34` / `SmartResearchTool.cs:33` / `SmartReviewTool.cs:32` / `SmartTestTool.cs:32`
（6 文件行内容逐字相同：`        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };`）

**选项 A（最小改动，推荐）**——去掉两条未注册路由，保留 Flash 层：
```diff
--- a/Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartDevelopTool.cs
+++ b/Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/SmartDevelopTool.cs
@@ -31,1 +31,1 @@
-        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };
+        new[] { "deepseek/deepseek-flash" };
```
（其余 5 文件同上，仅行号不同：Deploy 31 / Plan 34 / Research 33 / Review 32 / Test 32）

**选项 B（保留"强/快双层"语义，需父级裁定）**——把 Pro 层映射到已注册的强模型：
```diff
-        new[] { "deepseek/deepseek-v4-pro", "deepseek/deepseek-v4-flash" };
+        new[] { "bigmodel/glm-5.3-flash", "deepseek/deepseek-flash" };
```
> 取舍：B 会把 develop/plan/review/test/deploy 的兜底首选变为 glm-5.3-flash（¥1/¥4，带 `slow`/`reasoning-high`/`code`），成本与延迟上升但接近原 Pro 定位；A 只保证"不再告警 + 稳定命中 Flash"。**若 7 个 Agent 的 manifest 角色模型（§4.9）也一并整改，兜底几乎不会被触发，选 A 更省。**

### 4.2 记忆裁剪 legacy fallback

```diff
--- a/Source/PuddingRuntime/Services/CroppedLayersProvider.cs
+++ b/Source/PuddingRuntime/Services/CroppedLayersProvider.cs
@@ -113,2 +113,2 @@
                     ProfileId = "default-subconscious",
-                    ModelId = "deepseek-v4-flash",
+                    ModelId = "deepseek-flash",
```
（`ProfileId`/`ModelId`/`Role` 三行的实际行号为 113/114/115；若 `apply_patch` 报上下文不匹配，用**单行替换**：`ModelId = "deepseek-v4-flash",` → `ModelId = "deepseek-flash",`）

### 4.3 工具说明字符串（会进 prompt）

```diff
--- a/Source/PuddingRuntime/Tools/BuiltIns/Management/LlmResourcePoolTool.cs
+++ b/Source/PuddingRuntime/Tools/BuiltIns/Management/LlmResourcePoolTool.cs
@@ -143,1 +143,1 @@
-    [ToolParam("筛选特定模型（可选），如 'deepseek-v4-flash'。支持模糊匹配。")]
+    [ToolParam("筛选特定模型（可选），如 'deepseek/deepseek-flash'。支持模糊匹配。")]
```

### 4.4 Skill：aggregate-search

```diff
--- a/skills/aggregate-search/manifest.json
+++ b/skills/aggregate-search/manifest.json
@@ -11,1 +11,1 @@
-  "model": "deepseek/deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
```

```diff
--- a/skills/aggregate-search/SKILL.md
+++ b/skills/aggregate-search/SKILL.md
@@ -5,1 +5,1 @@
-本 SKILL 定义了**多引擎迭代聚合搜索**工作流：当用户发起搜索/调研指令时，使用 `deepseek-v4-flash` 子代理并行查询全部可用搜索引擎，聚合并去重结果，评估质量。若质量达标则返回综合报告；若不达标则根据上一轮结果优化检索策略，进入下一轮，**最多5轮迭代**。
+本 SKILL 定义了**多引擎迭代聚合搜索**工作流：当用户发起搜索/调研指令时，使用 `deepseek/deepseek-flash` 子代理并行查询全部可用搜索引擎，聚合并去重结果，评估质量。若质量达标则返回综合报告；若不达标则根据上一轮结果优化检索策略，进入下一轮，**最多5轮迭代**。
@@ -64,1 +64,1 @@
-为每个引擎 `spawn_sub_agent` 一个 `deepseek-v4-flash` 子代理，**同时派发**：
+为每个引擎 `spawn_sub_agent` 一个 `deepseek/deepseek-flash` 子代理，**同时派发**：
@@ -68,1 +68,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -209,1 +209,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -222,1 +222,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -235,1 +235,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -248,1 +248,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -261,1 +261,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -286,1 +286,1 @@
-1. **子代理模型**：只用 `deepseek-v4-flash`（快速、低成本）
+1. **子代理模型**：只用 `deepseek/deepseek-flash`（快速、低成本）
@@ -298,1 +298,1 @@
-- [ ] Phase 2: 子代理同时派发（非串行），使用 deepseek-v4-flash
+- [ ] Phase 2: 子代理同时派发（非串行），使用 deepseek/deepseek-flash
@@ -316,1 +316,1 @@
-> deepseek-v4-flash 定价：¥1/百万入 + ¥2/百万出。单次子代理约10K-20K tokens。
+> deepseek/deepseek-flash 定价：¥1/百万入 + ¥2/百万出。单次子代理约10K-20K tokens。
```
> 行号 5/64/68/209/222/235/248/261/286/298/316 已逐行核实（全文 352 行）。

### 4.5 Skill：v4-flash-architect-dev

```diff
--- a/skills/v4-flash-architect-dev/SKILL.md
+++ b/skills/v4-flash-architect-dev/SKILL.md
@@ -53,1 +53,1 @@
-  "model": "deepseek-v4-flash",
+  "model": "deepseek/deepseek-flash",
@@ -118,1 +118,1 @@
-- [ ] 子代理只委派给 `deepseek-v4-flash`
+- [ ] 子代理只委派给 `deepseek/deepseek-flash`
```
> ⭕ **不在本提案内**：Skill 目录名 `v4-flash-architect-dev` 与 Skill `name` 字段同样含旧名，但重命名会影响引用与索引 → 建议**单独开卡**，本卡只改路由字符串。

### 4.6 现行协作约定文档

```diff
--- a/Docs/agent-collaboration-agreement.md
+++ b/Docs/agent-collaboration-agreement.md
@@ -48,1 +48,1 @@
-4. **模型路由用完整 providerId/modelId**（查 `memory/llm-providers-cheatsheet.md`）：审查深审=opencode/qwen3.8-max 或 bigmodel/glm-5.3（安全）；快速任务=deepseek/deepseek-v4-flash
+4. **模型路由用完整 providerId/modelId**（查 `memory/llm-providers-cheatsheet.md`）：审查深审=bigmodel/glm-5.3-flash；快速任务=deepseek/deepseek-flash
```
> 该行同时含 `opencode/qwen3.8-max` 与 `bigmodel/glm-5.3`（**两个同样未注册**）→ 一并收敛。`memory/llm-providers-cheatsheet.md` 是否存在待查（本卡范围外，见 RISKS）。

### 4.7 诊断脚本示例

```diff
--- a/Tools/Diagnostics/README.md
+++ b/Tools/Diagnostics/README.md
@@ -147,1 +147,1 @@
-.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --provider-id deepseek --model-id deepseek-v4-flash
+.\.venv\Scripts\python.exe Tools\Diagnostics\query_metrics.py context-layers --provider-id deepseek --model-id deepseek-flash
```

### 4.8 前端 provider 模板

```diff
--- a/Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts
+++ b/Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/providerTemplates.ts
@@ -219,4 +219,4 @@
       {
-        modelId: 'deepseek-v4-flash',
-        name: 'DeepSeek-V4-Flash',
+        modelId: 'deepseek-flash',
+        name: 'deepseek-flash',
         protocol: 'openai',
```
```diff
--- a/Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/index.test.tsx
+++ b/Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/index.test.tsx
@@ -110,2 +110,2 @@
-        modelId: 'deepseek-v4-flash',
-        name: 'DeepSeek-V4-Flash',
+        modelId: 'deepseek-flash',
+        name: 'deepseek-flash',
```
> ⚠️ `:235 modelId: 'deepseek-v4-pro'`（:`236` name `'DeepSeek-V4-Pro'`）**留作裁定**：无已注册 Pro 同层替代，建议**直接删除该 model 条目**（模板少一项胜过多一项坏路由）。若删除，`index.test.tsx:122` 对应 fixture 一并删除。

### 4.9 Agent / 模板 manifest（**W4 高风险批**，需 backup + 单点试点）

`D:\data\agents\default.global_general-assistant.6a8\manifest.json`
```diff
--- a/D:/data/agents/default.global_general-assistant.6a8/manifest.json
+++ b/D:/data/agents/default.global_general-assistant.6a8/manifest.json
@@ -159,7 +159,7 @@
-  "explorerModel": "deepseek/deepseek-v4-flash",
-  "researcherModel": "deepseek/deepseek-v4-pro",
-  "plannerModel": "deepseek/deepseek-v4-pro",
-  "reviewerModel": "deepseek/deepseek-v4-pro",
-  "developerModel": "bigmodel/glm-5.3",
-  "deployerModel": "deepseek/deepseek-v4-pro",
-  "testerModel": "deepseek/deepseek-v4-pro",
+  "explorerModel": "deepseek/deepseek-flash",
+  "researcherModel": "deepseek/deepseek-flash",
+  "plannerModel": "bigmodel/glm-5.3-flash",
+  "reviewerModel": "bigmodel/glm-5.3-flash",
+  "developerModel": "bigmodel/glm-5.3-flash",
+  "deployerModel": "deepseek/deepseek-flash",
+  "testerModel": "deepseek/deepseek-flash",
```
```diff
@@ -22,1 +22,1 @@
-…必要时调用工具或委派deepseek-v4-flash、bigmodel/glm-5.3子代理完成coding任务。…（下略：同段还含「调用deepseek-v4-flash子代理必须给予子代理明确且准确的信息」）
+…必要时调用工具或委派deepseek/deepseek-flash、bigmodel/glm-5.3-flash子代理完成coding任务。…（同段第二处同步改为 deepseek/deepseek-flash）
```
> ⚠️ systemPrompt 是**单行超长 JSON 字符串**，diff 无法逐字呈现——执行时请用「字符串替换」：`deepseek-v4-flash` → `deepseek/deepseek-flash`（2 处）、`bigmodel/glm-5.3` → `bigmodel/glm-5.3-flash`（2 处，注意不要误伤已存在的 `bigmodel/glm-5.3-flash`）。**这是本轮最高价值的一处**：它就是当前注入 6a8 的 AGENTS 层提示词。

`D:\data\agents\default.global_general-assistant.258\manifest.json:159-165`
```diff
-  "explorerModel": "deepseek/deepseek-v4-pro",
-  "researcherModel": "deepseek/deepseek-v4-flash",
-  "plannerModel": "qwen/qwen3.8-max",
-  "reviewerModel": "deepseek/deepseek-v4-pro",
-  "developerModel": "deepseek/deepseek-v4-flash",
-  "deployerModel": "deepseek/deepseek-v4-pro",
-  "testerModel": "deepseek/deepseek-v4-pro",
+  "explorerModel": "deepseek/deepseek-flash",
+  "researcherModel": "deepseek/deepseek-flash",
+  "plannerModel": "bigmodel/glm-5.3-flash",
+  "reviewerModel": "bigmodel/glm-5.3-flash",
+  "developerModel": "deepseek/deepseek-flash",
+  "deployerModel": "deepseek/deepseek-flash",
+  "testerModel": "deepseek/deepseek-flash",
```

`D:\data\agents\default.general-assistant-001\manifest.json:7-13`（7 行全为 `"deepseek/deepseek-v4-pro"`）→ 按上表同规则替换。

`D:\data\agent-templates\general-assistant\manifest.json`
```diff
--- a/D:/data/agent-templates/general-assistant/manifest.json
+++ b/D:/data/agent-templates/general-assistant/manifest.json
@@ -97,1 +97,1 @@
-    "preferredModelId": "deepseek-v4-pro",
+    "preferredModelId": "deepseek-flash",
@@ -99,1 +99,1 @@
-    "memoryLlmModelId": "deepseek-v4-flash",
+    "memoryLlmModelId": "deepseek-flash",
```
> 该模板**新 Agent 的默认主模型/记忆模型** ⇒ 不修则每次新建 Agent 都复制一份坏路由（**根因级**）。
> `preferredModelId` 若坚持保留"Pro 层"语义，可改 `"glm-5.3-flash"`（provider `bigmodel`）——需父级裁定。

---

## 5. 高价值副产品：除 `deepseek-v4-flash` 外的未注册路由引用

| route | 位置（分类 a） | 处置建议 |
|---|---|---|
| `deepseek/deepseek-v4-pro`（18 处） | 6a8 manifest:160/161/162/164/165；258 manifest:159/162/164/165；001 manifest:7-13（7 行）；`SmartWorkflow` 6 个 .cs 常量；`providerTemplates.ts:235` | 无已注册 Pro 同层替代 ⇒ 二选一：**(a) 映射到 `bigmodel/glm-5.3-flash`**（有 code/reasoning-high 标签，最接近 Pro 定位）；**(b) 降级为 `deepseek/deepseek-flash`**（成本优先）。需父级裁定 |
| `bigmodel/glm-5.3`（2 处） | 6a8 manifest:163 `developerModel`；6a8 manifest:22 `systemPrompt` | 改 `bigmodel/glm-5.3-flash`（**确定性错误，无歧义**） |
| `qwen/qwen3.8-max`（1 处） | 258 manifest:161 `plannerModel` | 改 `bigmodel/glm-5.3-flash` |
| `opencode/*`、`moonshot/*` | 仅 `PuddingRuntimeTests/**` 合成样本（B 类） | 不改 |
| `deepseek-v4-flash-vision-exp` | 仅 Docs 叙述 + 配置备份（C/D 类） | 不改 |

**安全网确认（不需要改）**：`Source/PuddingRuntime/Tools/BuiltIns/Agents/SubAgentTool.cs:62 private const string DefaultFallbackModelRoute = "bigmodel/glm-5.3-flash";` → ✅ 已注册，正是它让"路由解析失败"没有演变成任务失败。

---

## 6. 零写入声明（**本轮未修改的文件**）

以下文件**全部保持原样**（未被本子代理写入/改名/删除）：

| 类别 | 文件 |
|---|---|
| **配置（最关键）** | `D:\data\config\llm.providers.json`（**未改动一个字节**）；`llm.providers.json.bak*`、`before-*.json`（10 份备份） |
| **Agent 配置** | `D:\data\agents\default.global_general-assistant.6a8\manifest.json`、`…general-assistant.258\manifest.json`、`…default.general-assistant-001\manifest.json`、`D:\data\agent-templates\general-assistant\manifest.json` |
| **源码** | `Source/PuddingRuntime/Tools/BuiltIns/SmartWorkflow/*.cs`（7）、`Source/PuddingRuntime/Services/CroppedLayersProvider.cs`、`Source/PuddingRuntime/Tools/BuiltIns/Management/LlmResourcePoolTool.cs`、`Source/PuddingPlatformAdmin/src/pages/llm-resource-pool/*` |
| **Skill / 文档** | `skills/aggregate-search/{manifest.json,SKILL.md}`、`skills/v4-flash-architect-dev/SKILL.md`、`Docs/agent-collaboration-agreement.md`、`Tools/Diagnostics/README.md`、`Docs/**`（全部） |
| **其他** | 未执行任何 `git` 写操作（无 add/commit/push）；未执行 `dotnet build` / `dotnet test`；未改 `memory/**`、`code_map.md`、`checkpoint.json` |

**唯一写入**：`E:\github\AgentNetworkPlan\PuddingAgent\temp\route-hygiene-20260913.md`（本文件，`temp/` 已在根 `.gitignore` 中，不会被 git 索引，无需 commit）。

---

## 7. 建议入档路径（**未自行写入**，供父级决定）

1. **首选**：`Docs/07架构/9xADR-0xx模型路由卫生与未注册引用治理ADR.md` —— 与既有 `92ADR-077…` 同目录，记录「注册表为唯一真源 + 引用必须走完整 route + 清理清单」。
2. **次选**：直接扩写 `Docs/agent-collaboration-agreement.md` 第 48 行所在小节（已是双方现行约定，成本最低）。
3. **可追溯**：把本文件（或精简版）从 `temp/` 移到 `Docs/Reports/`，避免被 `temp/` 清理策略删除。
