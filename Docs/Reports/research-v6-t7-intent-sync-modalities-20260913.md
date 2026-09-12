# [V6-T7] 调研报告：意图同步新模态——从「聊天」升级为「共享工作物」

- **日期 / 联网访问日期**：2026-09-13
- **作者**：sub-9107afae（V6-T7 只读调研代理）
- **任务来源**：V6「原生视觉理解与多模态意图同步」主线第 5 条需求
- **范围**：只读仓库 + 联网调研；未改任何源码、未建卡、未 push
- **证据规则**：所有产品/论文/URL 均来自本次真实联网检索（doubao_search / anysearch_search / http_fetch），检索日期 2026-09-13；仓库定位均为 file:line 实读。**已验证事实**与**推断**在 §6 明确分开。

---

## 0. SUMMARY（一页结论）

1. **业界 2025-2026 已收敛出明确共识**：把「对齐」从聊天流中拆出来，落到**可编辑的共享工作物**上。五大类真实模式全部可验证：A 结构化任务看板/计划类、B 共享文档/画布协作类、C 意图图/工作流+人审节点类、D 结构化表单/澄清类、E Agent 主动提案+可审阅执行类（§2）。
2. **PuddingAgent 最被低估的资产**：前端已有完整的记忆图书馆用户侧 UI（`memory-library` 页面：页面树 + 检查器 + **页面编辑器** + 搜索，`src/pages/memory-library/index.tsx` 共 730 行）。「共享工作物」的地基已经打好，**缺的不是存储和 UI，而是「用户编辑 → Agent 感知」的闭环**，以及「Agent 主动把对齐产物写成工作物而非聊天长文」的工作流习惯。
3. **优先级排序**（低成本→高成本）：
   - **P0 提案-审阅模式**：方案/调研先落 md 文件 + 看板卡（含验收标准），用户在卡与文档上批注，Agent 按卡执行。**零新代码**（file_write + manage_tasks 均已存在），只需工作流约定 + 卡详情页批注体验小改。依据：Copilot Workspace 的三层可编辑、Cursor「approach 对错先于代码对错」。
   - **P1 结构化澄清表单（`ask_user_form` 工具）**：把 N 轮文字澄清压成 1 次表单（1-6 题 × 选项 + 自由文本），答案作为 tool result 回流。业界已有 3 个可参考实现。落点：Runtime 工具层 + 前端表单渲染。
   - **P1.5 意图同步失败量化仪表**：4 个可落地指标全部有现成数据源（会话日志全文检索、看板状态历史、记忆反查日志），可先用 temp/ 离线脚本验证。
   - **P2 goal.md 工作物化 + 记忆图书馆双向闭环**：goal.md 本身就是共享文档，缺前端呈现与 mtime/hash 变更感知；memory-library 缺「用户改动 → 下轮上下文注入」。
   - **P3 白板/画布类**（tldraw Make Real 模式最有想象力，但 tldraw 为 AGPL-3.0，此前选型已否决其生产使用；自建需 Konva，成本高，暂缓）；**P3 Claude Code Artifacts 式 live 页面**（需前端基建，暂缓）。
4. **「意图同步失败」存在可量化判据**，且有 2025-2026 学术基准支撑：Goal Success Rate（RegretBench）、信息需求清单满足率（CarryOnBench）、平均澄清轮数、over-clarification / unsupported clarification rate、看板返工率。PuddingAgent 版落地指标见 §3.2。

---

## 1. 背景与问题定义

用户痛点原话：**"进行很多会话之后，管理上下文记忆和目标、以及与用户对齐（我是否真的理解了用户的描述、用户是否真的理解了我的描述）是一个很麻烦的事情。"**

PuddingAgent 已为此建了三件套：Memory（记忆图书馆）、Goal（goal.md + GoalRun）、任务看板。用户进一步判断：**"聊天"这个交互模态本身太慢**。本报告回答四个问题：

1. 业界在"人机意图同步 / 共享工作物"上有哪些真实可验证的模式？
2. 每类模态能替代/不能替代哪些聊天场景？
3. 在现有资产上，哪些模态是低成本高收益的增量？
4. "意图同步失败"是否有可量化判据？

---

## 2. 业界模态盘点（五类，全部联网验证）

### 2.A 结构化任务看板 / 计划类

**模式**：把需求与方案从聊天流中抽出，落成结构化、可逐条勾选/批注的计划工件；聊天退化为「对工件的修改指令」。

| 代表 | URL（访问日期 2026-09-13） | 关键事实 |
|---|---|---|
| Devin Cascade Plan Mode | https://docs.devin.ai/desktop/cascade/modes | Plan 模式：探索代码库 → 提澄清问题 → 给多选项交互界面 → **计划写在会话外部 Markdown 文件**（跨消息持久）→ 用户点 "Implement" 切入执行；`megaplan`/`ultraplan` 关键词强制深度规划（先至少问一个澄清问题） |
| Devin Interactive Planning | https://docs.devin.ai/work-with-devin/interactive-planning | 详细计划含**代码引用，可 deep-link 进 IDE** 共同核查；默认等待 30 秒反馈，可设 "Wait for my approval" |
| GitHub Copilot Plan Mode（官方培训模块） | https://learn.microsoft.com/sk-sk/training/modules/use-plan-mode-cloud-ops/2-what-is-github-copilot-plan-mode | 计划自动写入 `memories/session/plan.md`；后续反馈 = **把旧计划当先验 amend，不重启**；官方明确："**agent 提澄清问题的质量本身就是有用信号**——对欠规格 prompt 不问就直接出计划的，要加倍审视" |
| GitHub Copilot CLI plan（官方文档） | https://github.com/github/docs/blob/main/content/copilot/how-tos/copilot-cli/cli-best-practices.md | `/plan` 命令 → 澄清问题 → checkbox 计划 → 存 `plan.md` → **等待批准后才实施** |

**能替代**：多轮需求澄清、方案取舍、范围/验收确认、执行前对齐——这类"达成一致"的聊天。
**不能替代**：开放式头脑风暴、探索式诊断（目标本身在漂移）、纯咨询问答、紧急打断式指令。
**边界判据**：目标可写成有限条目的清单 → 用计划工件；目标仍在演化 → 留在聊天。

### 2.B 共享文档 / 画布协作类

**模式**：AI 输出不再塞进聊天气泡，而是出现在**独立的可编辑面板**；双方在同一份产物上轮流编辑。

| 代表 | URL（访问日期 2026-09-13） | 关键事实 |
|---|---|---|
| ChatGPT Canvas（OpenAI Academy 官方页） | https://academy.openai.com/en/public/clubs/work-users-ynjqu/resources/canvas | "collaborative editing space…create, edit, refine in real time"；高亮定点修改；快捷菜单；**back button 版本回退**；检测到适用场景自动打开 |
| Gemini Canvas（Google 官方博客） | https://blog.google/products/gemini/gemini-collaboration-features/ | 2025-03-18 发布："a new interactive space within Gemini"；实时编辑面板；一键导出 Google Docs 与他人协作 |
| Claude Artifacts（Anthropic 官方博客） | https://claude.com/blog/build-artifacts | 对话直接生成可交互 app；2025-10-21 更新：**Artifacts 支持 MCP 与持久存储**；可一键分享链接，他人可导入 fork（量子位报道 http://m.toutiao.com/group/7389885074050646528/ ） |
| Claude Code Artifacts（官方文档） | https://code.claude.com/docs/en/artifacts | **会话工作产出变成 claude.ai 上的 live 交互页面**，私有/组织/公开三种可见性 |
| tldraw Make Real | https://github.com/tldraw/make-real-starter/ （AGPL-3.0）、https://makereal.tldraw.com/ | 白板画 UI 草图 → vision 模型生成 HTML 贴回画布；**关键交互：在生成物上用红笔涂画/箭头/标注 = 修改指令**，再按 Make Real 迭代；prompt 明确 "red marks…are always markup/annotations" |
| DrawAUI | https://github.com/SawyerHood/draw-a-ui | 同模式开源先行者（tldraw + GPT-4V） |

**能替代**：文档/代码/UI 等有明确产物的协作改稿（"指哪改哪"取代轮次轰炸）。
**不能替代**：无产物的纯问答、即时性短交互（画布是重资产，开关成本高）。
**边界判据**：存在"一份双方都要反复改的产物" → 画布/文档；一问一答即完事 → 聊天。
**许可警示**：tldraw SDK 为 AGPL-3.0（make-real-starter 仓库标注），PuddingAgent 前期 Web 白板选型已因许可硬否决 tldraw、改用 Konva + react-konva 自建标注层（项目既有决策，见 Memory「Web 图片标注编辑器选型」）。

### 2.C 意图图 / 工作流 + 人审节点类

**模式**：意图同步被建模为**图上的状态流转**，在关键节点 `interrupt()` 暂停，把"当前信念"呈现给人审，人通过结构化动作（approve / edit state / reject+feedback）恢复执行。

| 代表 | URL（访问日期 2026-09-13） | 关键事实 |
|---|---|---|
| LangGraph Human-in-the-loop（LangChain 官方文档） | https://docs.langchain.com/langsmith/add-human-in-the-loop | `interrupt()` 在节点暂停并把 payload 呈现给人；恢复时人提供的值直接更新图状态 |
| LangGraph 官方概念文档（镜像） | https://github.com/dkacz/langgraph-app/blob/master/langgraph_docs/concepts_human_in_the_loop.md | 三种标准人审动作：**Approve or Reject / Edit Graph State / Get Input** |
| LangGraph Interrupt Workflow Template | https://github.com/KirtiJha/langgraph-interrupt-workflow-template/ | Approve/Edit/Reject 完整模板：`interrupt({"type":"approval","draft":draft,"actions":["approve","edit","reject"]})` → `Command(resume=...)` |
| Learnixo HITL 教程（2026-05-16） | https://learnixo.io/blog/lg-human-in-loop | 四种模式：approval gates / error correction / audit trails / override points；**HITL 依赖 checkpointer**（无 checkpoint 就没有可恢复状态） |

**能替代**：流程可预知的重复任务中的逐段确认（审批门、低置信纠错、审计留痕）。
**不能替代**：开放探索（图结构本身需要对齐时，先用 2.A/E 建图）。
**边界判据**：能把任务画成有限状态图 → 用 interrupt 审批；画不出来 → 先共同产出图（回到 A/E 类）。

### 2.D 结构化表单 / 澄清表单类

**模式**：Agent 需要澄清时，不逐题追问，而是**一次性弹出结构化表单**（题目 + 选项 + 多选 + 自由文本），用户 1-2 次点击完成，答案作为 tool result 回流。

| 代表 | URL（访问日期 2026-09-13） | 关键事实 |
|---|---|---|
| AskUserQuestion 提案（inference-gateway/cli #652，2026-06-26） | https://github.com/inference-gateway/cli/issues/652 | plan mode 专用只读工具：1-4 题 × 2-4 选项（label+description）+ multiSelect + 每题 "Other" 自由文本；**答案作为 tool result 返回，agent 循环继续**，与 RequestPlanApproval（停循环等决定）分工 |
| askUserChoice 提案（mulmoclaude #826，2026-04-25） | https://github.com/receptron/mulmoclaude/issues/826 | 动机直白："LLM 问'选哪个'时让用户打字回答成本高"；前端渲染 checkbox/radio/textarea/boolean 表单，提交后作为下一条 user turn 流入 |
| A2UI（Agent-to-User Interface，CSDN 2026-07-30） | https://blog.csdn.net/weixin_36001569/article/details/160475351 | 三设计目标：表单是 Agent 工具能力的一部分；LLM 按上下文自动生成表单结构；**Agent 提交后暂停、表单返回后无缝恢复**；零前端配置（前端只做渲染引擎） |
| Perspective Concierge（反向模式） | https://getperspective.ai/docs/guide/design/concierge-agent | **用对话替代静态表单**：infer-and-validate——先广问、激进推断、跳过已知字段、只确认不确定项。证明表单与聊天互为边界 |
| Google "Beyond the Chatbot"（2025-11-19） | https://discuss.google.dev/t/beyond-the-chatbot-designing-effective-agentic-ai-experiences/289245 | Form-based and action-driven UI："highly effective for well-defined, repeatable tasks where inputs are structured and outcomes must be consistent" |
| MDMA 框架（Mobile Reality 案例库） | https://themobilereality.com/software-development-case-studies/mr-chat-assistant/ | 聊天中嵌入 AI 生成的交互组件（表单/按钮），旧消息表单自动禁用 |

**能替代**：字段可枚举、选项有限的决策与信息收集（这正是聊天最慢的场景——每轮一问一答）。
**不能替代**：开放描述、探索式需求（硬填表单反而逼用户说谎）。
**边界判据**：≥2 个可枚举决策点待确认 → 表单一次收齐；全是开放问题 → 聊天；已知信息多、缺口少 → Concierge 式对话推断。

### 2.E Agent 主动产出「可审阅提案」类

**模式**：Agent 不等用户问，主动把理解写成**结构化提案工件**，用户在其上做小规模编辑即可纠偏——编辑提案的成本远低于用文字重新解释。

| 代表 | URL（访问日期 2026-09-13） | 关键事实 |
|---|---|---|
| GitHub Copilot Workspace（GitHub Next 官方页） | https://githubnext.com/projects/copilot-workspace/ | **三层工件全可编辑**：spec（现状/期望两个 bullet 列表）→ plan（逐文件步骤）→ diff；官方手册 https://github.com/githubnext/copilot-workspace-user-manual/blob/main/overview.md （含 "View references" 让用户核查模型引用了哪些文件）；**改上游任意一层，下游自动重新生成，不必从头再来** |
| Cursor Plan Mode（2026-07-28 更新） | https://www.learncursor.dev/learn/cursor-agents/agent-plan-mode | 核心论断："**Agent 递给你 diff，问题是代码对不对；Plan Mode 递给你文档，问题是 approach 对不对。第二个问题可以在还没有任何东西需要回滚之前回答**"；Shift+Tab 切换；计划可存 md 可提交 |
| Devin Knowledge / Playbooks（官方 Release Notes 2026） | https://docs.devin.ai/zh/release-notes/2026 、https://docs.devin.ai/zh/cli/changelog/stable | Knowledge notes 增删改查+归档+**审查 knowledge suggestions**；Playbook 可指定 Devin 模式；Agent Profile（plan/ask/normal）与权限模式解耦 |

**能替代**：复杂任务的"先对齐再执行"；用户难以从零描述、但擅长挑错的场景（认知负担转移：**识别错误 < 生成正确描述**）。
**不能替代**：用户完全不知道要什么的前期发散。
**边界判据**：用户"看到错的才知道对的" → 提案模式；需求本身空白 → 先聊天/表单收敛。

---

## 3. 意图同步失败的可量化判据

### 3.1 学术基准（全部为 2025-2026 真实 arXiv 论文，检索于 2026-09-13）

| 基准 | URL | 可量化指标 |
|---|---|---|
| **RegretBench**（A Regret-Based Multi-Turn Benchmark for LLMs' Clarification Policies） | https://arxiv.org/html/2607.21143v1 | **Goal Success Rate**（最终选择意图与隐藏真实意图的匹配率，式 11）；平均轮数；**over-clarification**（高成功但轮数多=过度澄清）；**unsupported clarification rate**（无据提问率）；premature answering |
| **CarryOnBench**（Utility Recovery with User Intent Clarification） | https://arxiv.org/html/2604.27093v1 | **Benign Information Needs Checklist**：把意图拆成 N 个原子信息单元，turn 级/会话级 **Utility = 清单满足率**（逐 item 独立判定，避免聚合偏差） |
| **Flexible Agent Alignment with Goal Inference** | https://arxiv.org/html/2508.15119v2 | goal update score（每个目标增删是否有对话依据）；**interpretability score（行为能否追溯到系统当时的目标信念分布——显式惩罚"无可见目标推理"的系统）**；conversation/action/task 三维 LLM-judge 评分 |
| 双赢对话（UIUC + VMware，2025-10） | 论文 arXiv 2510.08872v1（中文报道 https://view.inews.qq.com/a/20251114A037HH00 ） | 人机对话的"囚徒困境"：双向调优后推理效率 +21.5%、回答质量 +4.9%、**用户满意度 +11.3%**——意图对齐要同时优化双方收益 |
| ClarifyMT-Bench | https://arxiv.org/html/2512.21120 | 多轮澄清质量 LLM-as-a-Judge 基准 |
| Wisdom of Agent Crowds（BDI 框架） | https://arxiv.org/html/2505.06947v1 | 用户调研中"Intent Alignment and System Feedback"维度：系统反馈是否对准操作意图/是否准确预测需求/是否误解意图（附具体场景） |

### 3.2 PuddingAgent 可落地指标（4 个，数据源全部现成）

> 以下为**基于上述学术判据的落地设计（推断）**，标注了各自的数据源。

| # | 指标 | 定义 | 数据源（已有） |
|---|---|---|---|
| M1 | **澄清轮数 / 任务** | 从用户首条消息到 Agent 开始执行之间的来回 turn 数 | 会话日志（`Source/PuddingPlatform/Services/RawSessionLogService.cs` + `Source/PuddingRuntime/Tools/BuiltIns/Sessions/QuerySessionLogsTool.cs`） |
| M2 | **重复纠正率** | 同一事实/偏好在 N 个会话内被用户重复纠正（说明上轮意图同步失败） | 会话日志全文检索 + Memory 反查命中日志（`GrepMemoryTool` 调用记录） |
| M3 | **看板返工率** | 卡从 NeedsReview 被打回（reopen）的比例与平均打回次数 | `manage_tasks` 已有 reopen/mark_failed 等命令与状态历史（`Source/PuddingRuntime/Services/TaskTools/ManageTasksTool.cs`） |
| M4 | **记忆反查命中率** | Agent 主动反查用户历史偏好后，用户未再纠正的比例 | ContextMemoryIndicator（`src/pages/chat/components/ContextMemoryIndicator.tsx`）+ save/grep memory 审计 |

**主指标建议取 M3（返工率）+ M1（澄清轮数）**：前者直接对应"已交付但理解错"（最高成本失败），后者对应"开工前就没对齐"。学术对照：M1 ↔ RegretBench 平均轮数/over-clarification；M3 ↔ CarryOnBench 的 conversation-level Utility（验收标准清单满足率——PuddingAgent 的卡验收标准天然就是 checklist）。

---

## 4. PuddingAgent 现有资产盘点（只读实测，file:line）

> 以下行号基于 2026-09-13 工作区工作树（分支 master）。

### 4.1 Memory（记忆图书馆）——**最完整的共享工作物雏形**

| 位置 | 内容 |
|---|---|
| `Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs:13-15` | 类注释："主动记忆写入 Tool：Agent 可直接将事实、偏好、摘要写入 MemoryLibrary"；`:18-24` Tool 特性（AutoAllowed 免审批，2026-08-27 用户裁定）；`:26` class 声明 |
| `Source/PuddingRuntime/Tools/BuiltIns/Memory/` | 同目录还有 `ManageMemoryTool.cs`、`GrepMemoryTool.cs`、`MemoryToolArgs.cs` 等（GrepMemoryTool 即记忆反查入口） |
| `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs` | 记忆存储引擎；`MemoryLibraryConvenience.cs`、`MemoryLibraryDbContext.cs` 同目录 |
| `Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs` + `Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs` | 已有管理端 REST API |
| **`Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx:1-50`（全文件 730 行）** | **用户侧图书馆 UI 已存在**：工作区/Agent 选择、`getAgentMemoryLibraryTree`、`getAgentMemoryBookPage`、`searchAgentMemoryLibrary`、Book/Chapter 的 create/update/archive、Sources/Pointers 列表 |
| `Source/PuddingPlatformAdmin/src/pages/memory-library/components/MemoryPageEditor.tsx` | **用户可直接编辑记忆页面**（共享工作物的"用户侧编辑"半边已实现） |
| `components/MemoryInspector.tsx`、`MemoryPageTree.tsx`、`MemorySearchResults.tsx` | 检查器/树/搜索 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMenu.tsx:129-134` | 「加入记忆」菜单项（StarOutlined，`onAddToMemory`）；`:157` 回调接口声明。用户侧"把聊天内容钉进工作物"的入口已通 |

### 4.2 Goal

| 位置 | 内容 |
|---|---|
| `Source/PuddingPlatform/Services/Goals/GoalCommandService.cs:9-12` | 类注释（ADR-074 §4）：**slash 文本入口与结构化 Control Plane API 共用**；状态可重启重放；同一会话最多一个非终态 Goal；`:13` class 声明（primary constructor） |
| `Source/PuddingPlatform/Controllers/Api/GoalCommandsController.cs` | Goal 的 HTTP API 已存在（意图同步的"结构化通道"已铺好） |
| `Source/PuddingPlatform/Services/Goals/GoalContinuationWorker.cs`、`ConservativeGoalIterationVerifier.cs` | 目标续跑与保守迭代校验 |
| `Source/PuddingCore/Goals/GoalCommandTextParser.cs`、`GoalContinuationContracts.cs` | slash 文本解析与续跑契约 |

### 4.3 任务看板

| 位置 | 内容 |
|---|---|
| `Source/PuddingRuntime/Services/TaskTools/ManageTasksTool.cs:8-11` | 类注释："管理者视角任务看板交互（跨 Agent 的完整 CRUD + 命令操作）…与执行者视角的 task_list/task_get/task_claim/task_update 互补"；`:12-18` Tool 特性：action=list/create/get/update/delete/assign/run_now/cancel/**reopen**/archive/mark_failed/resume/requeue |
| 同目录 `TaskClaimTool.cs` / `TaskGetTool.cs` / `TaskListTool.cs` / `TaskUpdateTool.cs` / `TaskToolModels.cs` | 执行者视角工具族 |
| `Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMenu.tsx:136-143` | 「从这里创建分支」（onBranch）——聊天已可与分支/任务产生结构化关联 |

### 4.4 全文检索 / 会话日志 / 代码索引

| 位置 | 内容 |
|---|---|
| `Source/PuddingFullTextIndex/Contracts/IFullTextSearchEngine.cs:6-9` | 接口注释：hasIndex / search / build / rebuild，由 LuceneSearchEngine 实现；`:20-27` SearchAsync（支持扩展名白名单与子目录过滤） |
| `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs`（及 JiebaTokenizer/JiebaSegmenterPool） | 中文分词已内建 |
| `Source/PuddingMemoryEngine/Infrastructure/Text/Fts5QueryBuilder.cs` | **SQLite FTS5 查询构建器**（会话日志全文检索的实现所在） |
| `Source/PuddingPlatform/Services/RawSessionLogService.cs` + `Source/PuddingRuntime/Tools/BuiltIns/Sessions/QuerySessionLogsTool.cs` | 会话日志服务与 Agent 侧检索工具（M1/M2 指标数据源） |
| `Source/PuddingCodeIntelligence/` | 代码索引/符号检索（code_symbol_search 等），报告类提案可引用符号级证据 |

---

## 5. 落点评估与优先级（低成本高收益排序）

### P0｜提案-审阅模式（成本：极低；收益：高；**零新代码起步**）

- **做法**：凡调研/设计/方案类产出，Agent 统一"三件套"交付：① md 文件落盘（`Docs/Features/` 或评审期 `temp/`）② 看板卡挂验收标准（manage_tasks create）③ 聊天只发**摘要 + 指针**（"卡 7f41… 的第 2 条验收标准逐字就是…"已被实践证明有效）。用户纠偏 = 改卡验收标准或改 md —— **改上游，Agent 重新生成下游**（Copilot Workspace 语义）。
- **业界依据**：Copilot Workspace 三层可编辑 + 下游重生成（§2.E）；Cursor "approach 对错先于代码对错"（§2.E）；Devin 计划外置 md + Implement 按钮（§2.A）。
- **现有落点**：`ManageTasksTool.cs:12-18`（reopen/archive 等命令齐全）+ `file_write` + 验收标准字段。**唯一硬约束**：看板状态机 `Backlog→NeedsReview` 需两级跳（先 Ready 再 NeedsReview），该机制性问题已登记卡 `2a92b3edb69f45c588c1c27cf6cc8eeb`。
- **增量开发（小）**：前端卡详情页支持行内批注/直接编辑验收标准（现有 Admin 前端已有表单基建，属小改）。

### P1｜结构化澄清表单 `ask_user_form`（成本：中低；收益：高）

- **做法**：新增 Runtime 工具：Agent 传 1-6 题 `{question, options[], multiSelect, freeText}`，前端渲染表单，用户提交后答案作为 tool result 回流，Agent 循环继续。
- **业界依据**：AskUserQuestion（§2.D，与 RequestPlanApproval 的"停循环/不停循环"分工值得照抄）、askUserChoice、A2UI 的"暂停-提交-无缝恢复"。
- **现有落点**：① 工具层仿 `SaveMemoryTool.cs:18-26` 的 `[Tool]` 模式新建（目录 `Source/PuddingRuntime/Tools/BuiltIns/`）；② 平台已有 ask_question 持久等待机制（120 秒）可扩展为表单载荷；③ 前端渲染可复用 `ContextMenu.tsx` 的 Portal + 边缘翻转模式或 antd Modal（memory-library/index.tsx 已大量使用 Modal/Form）。
- **边界**：仅用于可枚举决策点；开放问题仍走聊天（Perspective Concierge 证明反向也存在市场）。

### P1.5｜意图同步失败量化仪表（成本：低；收益：中高，为 P0/P1 提供验收依据）

- **做法**：先用离线脚本（放 `temp/`，不进 git）对会话日志 + 看板历史做 M1-M4 四指标（§3.2）统计，跑 2-4 周基线，再决定是否产品化进 Admin 仪表盘。
- **依据**：RegretBench/CarryOnBench 证明"澄清轮数/清单满足率"可测且可解释（§3.1）。

### P2｜goal.md 工作物化 + 记忆图书馆双向闭环（成本：中；收益：中高）

- goal.md 本身已是共享文档（文件在磁盘上用户可改），缺：① 前端呈现/编辑器 ② **变更感知**（Agent 心跳检测 mtime/hash，用户改了 goal → 下轮自动校准，对应 `GoalCommandService.cs:9-12` "状态可重启重放"的既有能力可承接）。
- memory-library 已有用户编辑器（MemoryPageEditor），缺**反向感知**：用户改了记忆页 → 下轮上下文注入该变更（ContextMemoryIndicator 已有展示位）。这是 ChatGPT/Gemini Canvas "同一份产物双端编辑"的最小化等价物，且**无需任何画布**。
- 依据：Gemini Canvas/ChatGPT Canvas/Claude Artifacts 持久存储更新（§2.B）。

### P3｜白板/画布类 与 live 页面（成本：高；暂缓）

- tldraw Make Real 的"红笔标注=修改指令"与 V6 主线"以用户上传图片为底图"的标注编辑器（V6-T4）天然同构——**若 V6-T4 的 Konva 标注层落地，白板模式的边际成本会大降**；但独立立项白板当前不划算，且 tldraw AGPL 不可用于生产（项目既有否决决策）。
- Claude Code Artifacts 式"会话产出 → live 页面"需要前端基建（预览沙箱、发布链接、可见性管理），等 P0/P1 验证意图同步收益后再评估。

### 依赖关系

P1.5 基线数据应**先于/伴随** P0、P1 启动（否则无法证明新模态有效）。P2 的 goal 变更感知不依赖任何其他项，可随时并行。P3 依赖 V6-T4 标注层与 P0 的收益验证。

---

## 6. 已验证事实 vs 推断（明确区分）

**已验证事实（联网检索/实读源码）**
- §2 所有产品行为描述与 URL（检索日期 2026-09-13）；§3.1 所有论文与其指标定义；§4 所有 file:line（当日工作树实读）；tldraw make-real-starter 的 AGPL-3.0 许可标注。

**推断（本报告的设计判断）**
- §3.2 四个指标是"学术指标 → PuddingAgent 数据源"的映射设计，未实测；M4 的"未再纠正"判定窗口需用户裁定。
- §5 全部优先级排序与成本估计是推断；"卡详情页批注属小改"基于 Admin 前端已有表单基建的观察，未估行数。
- P0 的"聊天只发摘要+指针"已被本会话实践证明有效（V6-T4 卡引用经历），但未做跨会话量化。

---

## 7. EVIDENCE 汇总

**联网来源（访问日期均为 2026-09-13）**
- https://docs.devin.ai/desktop/cascade/modes ｜ https://docs.devin.ai/work-with-devin/interactive-planning ｜ https://docs.devin.ai/zh/release-notes/2026 ｜ https://docs.devin.ai/zh/cli/changelog/stable
- https://githubnext.com/projects/copilot-workspace/ ｜ https://github.com/githubnext/copilot-workspace-user-manual/blob/main/overview.md ｜ https://learn.microsoft.com/sk-sk/training/modules/use-plan-mode-cloud-ops/2-what-is-github-copilot-plan-mode ｜ https://github.com/github/docs/blob/main/content/copilot/how-tos/copilot-cli/cli-best-practices.md
- https://www.learncursor.dev/learn/cursor-agents/agent-plan-mode
- https://academy.openai.com/en/public/clubs/work-users-ynjqu/resources/canvas ｜ https://blog.google/products/gemini/gemini-collaboration-features/ ｜ https://claude.com/blog/build-artifacts ｜ https://support.claude.com/en/articles/9487310-what-are-artifacts-and-how-do-i-use-them ｜ https://code.claude.com/docs/en/artifacts
- https://github.com/tldraw/make-real-starter/ ｜ https://makereal.tldraw.com/ ｜ https://github.com/SawyerHood/draw-a-ui
- https://docs.langchain.com/langsmith/add-human-in-the-loop ｜ https://github.com/dkacz/langgraph-app/blob/master/langgraph_docs/concepts_human_in_the_loop.md ｜ https://github.com/KirtiJha/langgraph-interrupt-workflow-template/ ｜ https://learnixo.io/blog/lg-human-in-loop
- https://github.com/inference-gateway/cli/issues/652 ｜ https://github.com/receptron/mulmoclaude/issues/826 ｜ https://blog.csdn.net/weixin_36001569/article/details/160475351 ｜ https://getperspective.ai/docs/guide/design/concierge-agent ｜ https://discuss.google.dev/t/beyond-the-chatbot-designing-effective-agentic-ai-experiences/289245 ｜ https://themobilereality.com/software-development-case-studies/mr-chat-assistant/
- https://arxiv.org/html/2607.21143v1 ｜ https://arxiv.org/html/2604.27093v1 ｜ https://arxiv.org/html/2508.15119v2 ｜ arXiv 2510.08872v1（报道 https://view.inews.qq.com/a/20251114A037HH00 ）｜ https://arxiv.org/html/2512.21120 ｜ https://arxiv.org/html/2505.06947v1

**仓库定位（file:line，2026-09-13 工作树）**
- `Source/PuddingRuntime/Tools/BuiltIns/Memory/SaveMemoryTool.cs:13-15,18-26`；同目录 ManageMemoryTool.cs / GrepMemoryTool.cs
- `Source/PuddingMemoryEngine/Data/MemoryLibrary.cs`；`Source/PuddingMemoryEngine/Infrastructure/Text/Fts5QueryBuilder.cs`
- `Source/PuddingPlatform/Controllers/Api/MemoryLibraryAdminController.cs`；`Source/PuddingPlatform/Services/MemoryLibraryAdminService.cs`
- `Source/PuddingPlatformAdmin/src/pages/memory-library/index.tsx:1-50`（全 730 行）；`.../components/MemoryPageEditor.tsx`、`MemoryInspector.tsx`、`MemoryPageTree.tsx`、`MemorySearchResults.tsx`
- `Source/PuddingPlatformAdmin/src/pages/chat/components/ContextMenu.tsx:129-134,157`；`.../chat/components/ContextMemoryIndicator.tsx`
- `Source/PuddingPlatform/Services/Goals/GoalCommandService.cs:9-13`；`GoalCommandsController.cs`；`GoalContinuationWorker.cs`；`Source/PuddingCore/Goals/GoalCommandTextParser.cs`
- `Source/PuddingRuntime/Services/TaskTools/ManageTasksTool.cs:8-18`；同目录 TaskClaimTool/TaskGetTool/TaskListTool/TaskUpdateTool
- `Source/PuddingFullTextIndex/Contracts/IFullTextSearchEngine.cs:6-9,20-27`；`.../Infrastructure/Text/JiebaAnalyzer.cs`
- `Source/PuddingPlatform/Services/RawSessionLogService.cs`；`Source/PuddingRuntime/Tools/BuiltIns/Sessions/QuerySessionLogsTool.cs`
- `Source/PuddingCodeIntelligence/`

---

## 8. RISKS

1. **看板状态机两级跳约束**：`Backlog→NeedsReview` 被拒，P0 提案-审阅工作流若不改状态机，收口仍要手工两级跳（卡 `2a92b3ed…` 已登记修复项）。
2. **搜索结果的时效性**：部分来源为第三方教程/媒体（CSDN、腾讯新闻、learncursor.dev 非 Cursor 官方），核心结论均已用官方文档交叉验证；Learn Cursor 页仅用作 Cursor Plan Mode 的行为描述补充。
3. **表单模态的滥用风险**：若 Agent 对开放性问题硬弹表单，会逼用户在错误选项里选——必须严格执行 §2.D 边界判据，且表单必含自由文本出口（AskUserQuestion 的 "Other" 设计）。
4. **M1-M4 指标的代理性**：返工率受"任务本身变更"污染，需区分"需求变了"与"理解错了"（打回原因字段）。
5. **tldraw 不可用但"红笔标注"语义值得抄**：若未来做白板，标注→指令的 prompt 约定可直接移植到 Konva 自建层。
6. **本报告基于工作树当日状态**：行号会随后续 commit 漂移，引用时建议以符号名为主、行号为辅。

---

## 9. BLOCKERS

- none（联网工具 doubao_search / anysearch_search / github_search 全程可用；OpenAI 官方页 openai.com/index/introducing-canvas/ 返回 HTTP 403，已用 OpenAI Academy 官方页与多家媒体交叉替代，不影响结论）。
- 备注（非阻塞）：SQLite FTS5 会话日志的**建索引端**（写路径）源码位置本次未逐一定位（只确认了查询构建器 `Fts5QueryBuilder.cs` 与服务/工具层），若 P1.5 落地需要精确到建表 DDL，可补一轮只读检索。
