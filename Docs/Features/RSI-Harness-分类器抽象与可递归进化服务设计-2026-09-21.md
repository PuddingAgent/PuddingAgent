# RSI-Harness：分类器抽象与可递归进化服务设计（讨论稿）

| 属性 | 值 |
|------|-----|
| 日期 | 2026-09-21 |
| 状态 | **讨论稿**（未评审、未实现） |
| 作者 | 通用助手 |
| 关联 | ADR-064（仅 Skill 自进化）、`Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.md`、安全分类器部署手册 |
| 取证基线 | HEAD `8ed4c5b`（§1–§12）；`574851b`（§13 实测）；`84ac5fd`（§14/§15） |

> 本文所有 `文件:行号` 来自静态盘点（未编译、未跑测）；若并行协作改动工作区，行号可能漂移。

---

> **文档定位（2026-09-21 修订）**：本文档同时是**判定基础设施（三原语：打分器 / 判断器 / 分类器）的母文档**，RSI-Harness 是它的第一个大型消费者。三原语设计见 **§4.0**。
>
> **文档导航**：
> - **§15 = 改进基础设施完整规划（总纲，先读这一节）**
> - §13 / §14 = 技能组合治理与潜意识整理作业（§15 的两个子领域）
> - §4.0 = 三原语（打分器 / 判断器 / 分类器）契约
> - §8 / §15.6 = 切片计划（§15.6 是合并后的单一序列）
> - 实施规格：`Docs/Features/S1a-判定算子基础设施-实施规格-2026-09-21.md`

## 1. 目标与边界

### 1.1 目标
把**判定能力**从**工具审批专用**提升为**平台级基础设施**，并在其上实现一个**可递归自我改进（RSI）的独立服务**：

1. **三原语基础设施**：抽出**打分器 / 判断器 / 分类器**共用的判定内核 + 三种投影契约（见 §4.0）。三者是**同一内核的三种投影**，**不是三套实现**；服务对象为工具审批、RSI，以及将来的 Goal / 调度 / 记忆质量等模块；
2. 基础设施层**完全隔离 Jev**（含一切「类 Jev 模型」与其他服务商）—— 换模型只改**一个注册点**，且该隔离有一条**可执行的门禁测试**守住（见 §4.0.6）；
3. RSI 判定基于**判断器 / 打分器契约**实现，用于决定「哪些行为值得晋升、哪些该丢弃」；
4. 可递归进化服务是**独立 Service**，**只依赖三原语抽象**，不依赖任何具体模型（含 Jev）；
5. 会话结束（或压缩后）投喂**轨迹片段**（非消息全文、非思维链），判定层产出**好/差信号**并汇总存储；
6. 演进流水线：**分析（第一性原理）→ 规划 → 实施**（本文主张补上第 4 步：**评测/晋升**）。

### 1.2 明确不做（本设计边界）
- **不做模型训练、不做模型权重层面的递归**（RSI 指行为/策略/Skill/代码层面的改进）；
- **不做一次性大爆炸重构**（现有 5 个 verdict 记录的统一下文 §6 有单向收敛策略）；
- **不允许候选修改判定自己的标准**（§5 宪法条款 C1）。

---

## 2. 现状事实（代码级）

### 2.1 已经通用了（抽象可直接继承）
| 资产 | 形状 | 位置 |
|---|---|---|
`IToolCallClassifier` | `string ClassifierId { get; }`；`Task<ClassificationVerdict> ClassifyAsync(ToolCallClassificationContext, CancellationToken)` | `Source/PuddingCore/Classification/*` |
`ClassificationVerdict` / `ClassificationOutcome` | 裁决 + outcome 枚举 | 同上 |
`IClassifierHealthReporter` / `ClassifierStatus` / `ClassifierHealth` / `ClassifierHealthReporter` | 健康面上报 | `PuddingRuntime/Classification/*` |
`ClassifierArbiterRegistrationState` | 仲裁位注册状态 | 同上 |
`ToolCallClassifierPipeline` | **本身即实现 `IToolCallClassifier`**；规则环 + 仲裁位求值序，与厂商无关 | `PuddingRuntime/Classification/ToolCallClassifierPipeline.cs` |

> **重要**：抽象层**不是缺失的**。`PuddingCore/Classification` 已是一套成形契约，且管线已做到厂商无关。这改变了设计起点——我们要做的是**泛化三个耦合点**，而不是从零造抽象。

### 2.2 其实是工具审批专用，需要泛化
| 耦合点 | 现状 | 为什么要泛化 |
|---|---|---|
`ToolCallClassificationContext` | 含 **7 个审批出题字段** | RSI 的输入是「轨迹片段」，字段集完全不同 ⇒ 硬塞进同一 record 会变成万能桶 |
`ClassificationVerdict.AppliedRuleId` / 四键 `PerOutcomeConfidence` | 绑定审批的规则/四结局语义 | RSI 的结局不是 allow/deny |
`SystemRuleClassifier` / `ClassificationRuleCurator` / 管线 | **直连** `IToolApprovalAllowlistStore` / `IToolApprovalAuditStore` | 规则与审计应**按场景键分区**，而不是绑死审批 |
`ToolApprovalAuditEventType` / `ToolApprovalAuditEvent` / `IToolApprovalAuditStore` | 审批专用审计 | RSI 需要独立于审批的分类器审计流 |
`IToolApprovalReviewer` | 审查者接口 | 是审批域的编排者，不属于基类 |
配置节 `ToolApproval:Reviewer` / `:Classifier` / `:Jev` | 硬编码在审批命名空间下 | 需要**场景键 + 分类器路由**，否则每个新场景都要加一节配置 |
`ClassifierHealthReporter` | 上报维度是 `(toolId, argsHash)` | 维度应参数化为「场景键 + 输入身份」 |
原因码前缀 `approval_review_classifier_unknown` | 审批前缀写死 | 需要场景前缀 |

### 2.3 根本不存在，要新建
1. **Base 基类 / 通用分类器基类**；
2. **场景键控的分类器路由**（`RSI` / `Goal` / `Scheduling` …）；
3. `ClassificationOutcome` 的 `Deferred` / `NeedHuman` 一等语义（见 §3 外部实践的提醒）；
4. **独立于审批的分类器审计存储**；
5. **仓库级统一裁决结构** —— 当前**并存 5 个互不复用的 verdict record**（这是"以后要四处打补丁"的根因）；
6. 信号模型与存储（好/差信号、聚合、晋升事实）。

### 2.4 另外发现的两处并行实现（风险）
- **前端平行分类器**：`Source/PuddingPlatformAdmin/src/pages/chat/classifier/autoReviewClassifier.ts` 自带状态机与阈值（3/20），与后端**无共享契约**。抽 Base 时若不管它，会出现**第三份**判定实现。
- **命名异常**：契约目录为 `PuddingCore/Classification`，但盘点得到命名空间写作 `PuddingCode.Classification`。**待复核**（若真如此，说明存在跨工程共享命名空间，会影响基类落点）。

### 2.5 盘点纪律备注
- 代码索引对本项目返回 `Pending`（上次 completed 2026-07-28）⇒ 本轮全用 `git grep` + 逐文件取证；
- `search_grep` 在本仓库会撞「枚举 2000 文件上限」并给 partial 告警 ⇒ 后续同类盘点**优先 `git grep`**。

---

## 3. 外部实践要点（含证据强度）

| # | 结论 | 出处 | 强度 |
|---|---|---|---|
1 | 统一输出面收敛为四类：**score / label / verdict / reason**；`defer` 在主流框架里**很少做成一等枚举**，而是**分档阈值**（低/中/高三档，高风险动作门槛更高） | AWS Bedrock Guardrails、OpenAI Moderation、Jev 文档 | 工程实践 |
2 | 成熟范式不是深继承树，而是 **「单一窄契约 + 实现注册表 + 声明所属阶段」** | NeMo Guardrails 五类 rail、Guardrails AI 65+ validator 共用同一 Pass/Fail 契约、Bedrock 单服务多 policy | 工程实践 |
3 | judge 有**服务化**与**库函数**两种形态；托管 judge 适合服务侧、确定性硬规则适合进程内，**两者共用同一裁决契约** | Bedrock ApplyGuardrail / Cloudflare `typesafe/jev` / Langfuse vs Guardrails AI / NeMo | 工程实践 |
4 | 轨迹应先规范化为**事件/span 而非自然语言摘要**；失败用结构化 status/error 表达，不靠文本 | OTel GenAI agent spans（**标注未稳定**）、ATIF 轨迹交换格式 | 官方规范（未稳定）/ 规范 |
5 | 上下文压缩有**三条互不替代**的路：compaction / tool-result clearing / memory；且有 context rot 现象（token 越多召回越差） | Anthropic cookbook、Chroma 研究、压缩综述 | 官方 cookbook + 学术综述 |
6 | 「逐条工具结果做 keep/truncate/drop」已被产品化；Jev 另有 tool-call risk gate | Jev 产品页 / Cloudflare 模型页 | **厂商主张，需自证** |
7 | 反奖励黑客机制：判据冻结、负例注射、held-out 集、判据本身被评测 | 见 §5 宪法条款来源 | 待补权威出处（见 §7 GAP） |

### 3.1 关于 Jev 的事实与我方已有证据
- Jev 是 **typesafe.ai 的 System One** 系列，已上架 Cloudflare Workers AI；能力面含 **`score`（有序档）**、**`choice`（≤255 选项 + 全选项概率）**、**一次往返并行回答多问题**；官方文档给出三档置信策略（自动执行 / 先确认 / 转人工）与门槛建议（常见下限 0.5、高风险 0.9）。
  - 出处：`https://docs.typesafe.ai/confidence`、`https://developers.cloudflare.com/ai/models/typesafe/jev/`、`https://jevtypesafeai.com`
- **我方已有一手证据（不依赖厂商主张）**：资源池 `jev` provider 启用、端点/密钥可解析；Live 探针 **2/2 通过**，断言到 `Outcome ≠ Unknown`、`ReasonCode == ReasonCodeVerdict`（非 `unparsed`）、`answers["outcome"].type == "choice"`，并拿到 `classifier_model=jev-1.13.0`、`latency_ms≈1409`。
  ⇒ **传输与解析层已验证**；**性能与价格数字仍属厂商主张，需独立复核**。
- ⚠️ 三源不一致（端点/价格），**必须**以我方 `D:\data\config\llm.providers.json` 为事实源，不要照抄任何外部数字。

---

## 4. 架构设计

### 4.0 三原语：打分器 / 判断器 / 分类器（基础设施核心）

#### 4.0.1 第一原则：**一个判定内核，三种投影**

> **不要把打分器 / 判断器 / 分类器做成三套独立实现。**

两条理由都是硬的：
1. **会立刻复制已知病历** —— 本仓库已经并存 **5 个互不复用的 verdict record**；再来三套，就是 8 个；
2. **同输入三次模型调用 = 3× 成本 + 三个视图互相不一致**（同一个判断器说 Yes、打分器说低分，下游该信谁？）。

**做法**：一个 `JudgementKernel`（横切关注点）+ 三种**投影契约**。一次模型调用产出**一个信封**，三种投影读同一信封的不同字段。

#### 4.0.2 语义边界（必须钉清，否则三者会互相蚕食）

| 原语 | 回答的问题 | 主输出形状 | 典型消费者 |
|---|---|---|---|
**打分器** `IScorer` | 「有多好 / 多差 / 多像？」 | **分**（连续或有序）+ **分制声明** | 排序、阈值无关的比较、候选优选、样本筛选 |
**判断器** `IJudge` | 「命题是否成立？」（≈ `if`） | **三值 Yes / No / Abstain** + 置信度 + 所用阈值 | 准入、门禁、分支、止损 |
**分类器** `IClassifier` | 「属于哪一类？」 | **标签 + 每类置信/概率**（分布） | 归因、场景识别、信号打标、路由 |

三条硬规矩：
- **打分器不得输出「通过/不通过」**（那会让阈值藏进打分器，见 4.0.3）；
- **判断器不得把连续分当主输出**（分只能作为 `evidence` 附带）；
- **分类器不得只返回最大类**（必须返回分布，否则无法算置信度、也无法弃权）。

三者的关系不是并列而是**可推导**：`判断器 ≈ 打分器 + 阈值策略`。但**阈值不得住在打分器内部** —— 这正是下一条。

#### 4.0.3 阈值策略外置（`ThresholdPolicy` 一等对象）

```
ThresholdPolicy { policyId, version, lo, hi, riskClass, sceneKey }
Judge(score) = score ≥ hi → Yes
             | score ≤ lo → No
             | 其余      → Abstain
```

- 结果必须**连阈值策略一起落库**，否则事后无法回答「为什么放行」；
- 调参 = 换策略版本，**不改模型调用**；
- ⭐ 关键安全收益：**RSI 候选无法偷偷移动判据**（宪法 C1）。改阈值成为一次**显式、版本化、可被评测守卫**的动作，而不是散落在代码里的 `> 0.8`。

#### 4.0.4 置信度的诚实性（最容易埋雷的一处）

`confidence` 必须随带 `confidenceKind`：

| 取值 | 含义 | 我们是否具备 |
|---|---|---|
`ModelSelfReported` | 模型自报（Jev 的 `score`、选项概率属此类） | ✅ 已有 |
`Calibrated` | 经独立校准（held-out 数据校准过，≈真实频率） | ❌ **目前没有** |

⇒ **未校准时禁止下游把它当概率做统计推断**（例如「置信度 0.95，所以可以直接晋升」）。RSI 的晋升决策必须走**样本量 + 序贯检验 + 灰度**（§3 Q4），而不是直接比 confidence 大小。

#### 4.0.5 弃权（`Abstain`）必须是一等结果

你说「判断器输出是/否，但实际还有置信度的问题」—— 那个问题**不是**「布尔 + 置信度」，而是**三值 + 置信度**：
**低置信区间本身就是弃权区**（上/下阈值之间的地带）。

- 弃权**映射到既有 `Deferred` 语义**（不往审批域 outcome 集合里塞新枚举）；
- **禁止**把弃权折叠成 `No`（会退化成「不敢判就拒绝」）或 `Yes`（会退化成「沉默放行」）；
- 弃权必须带 `reasonCode`（模型不可用 / 超时 / 超出可判范围 / 证据不足）。

#### 4.0.6 Jev 隔离点与**可执行守卫**

- 全天只有 **`JevClassifierModel`**（唯一适配器）可引用 Jev 的端点、协议名词与 `choice` 构造；
- **守卫测试（把「隔离」变成可验证事实，而不是口头约定）**：断言基础设施命名空间内 `grep -i "jev"` **只命中该适配器与其自身测试**；新增越界引用即红；
- `instruction` / `questions` 由**场景侧**声明，模型层**不得**知晓业务语义（避免模型层变成业务逻辑垃圾场）；
- **利用 Jev 的「一次往返并行回答多问题」**：一次调用同时取 **分 + 判断 + 标签**，避免三次往返造成 3× 成本与视图不一致（这正是 4.0.1 的落地红利）。

#### 4.0.7 与既有资产的落位（不另起炉灶）

| 既有资产 | 在新架构中的位置 |
|---|---|
`IToolCallClassifier` | 保留为**工具审批场景的门面**；内部改为调用内核。**不删、不改签名**（S1 零行为变更的前提） |
`ToolCallClassifierPipeline` | 它「规则环 + 仲裁位求值序」本质就是**判断器的确定性分支** ⇒ 直接作为 `IJudge` 的一个实现（规则优先、模型兜底） |
`ClassificationVerdict` 等 5 个 record | 逐步**内嵌 `JudgementEnvelope`**（§6 单向收敛），不做一次性重写 |
`IClassifierHealthReporter` / `ClassifierArbiterRegistrationState` | 升级为**按 `sceneKey` 分区**的通用健康面与仲裁位 |

#### 4.0.8 统一信封 `JudgementEnvelope`（收敛 5 个 verdict 的关键）

```
JudgementEnvelope {
  // 身份与可复现性（缺一不可，否则信号不可回放/不可对账）
  judgementId, sceneKey, inputDigest,
  modelId, instructionVersion, thresholdPolicyVersion?, schemaVersion,
  // 结果（按投影填充；同一信封可被三种投影共享）
  score?, scoreScale?, label?, labelDistribution?, outcome?,
  confidence, confidenceKind,
  // 证据与成本
  evidence[], reasonCode, latencyMs, cost?, tokenUsage?, cached,
  // 溯源
  sourceEventIds?, sourceSha?, ts
}
```

### 4.1 分层

```
┌─ 消费方（互不感知彼此）─────────────────────────────────┐
│  工具审批（现有）    RSI 进化（新）   未来: Goal/调度/记忆质量  │
└──────────────┬──────────────┬───────────────────────────┘
               │ 只依赖抽象
┌──────────────▼──────────────▼───────────────────────────┐
│  IClassifier<TContext, TVerdict>    ▲ 场景注册表(SceneKey) │
│  ClassifierBase<TContext, TVerdict> ▲ 横切关注点           │
└──────────────┬──────────────────────────────────────────┘
               │ 依赖「模型抽象」，不认供应商
┌──────────────▼──────────────────────────────────────────┐
│  IClassifierModel.JudgeAsync(JudgementRequest)           │
│     └── JevClassifierModel（唯一接触 Jev 端点/协议的地方）  │
│     └── （将来）其他模型实现，按场景选择                     │
└──────────────┬──────────────────────────────────────────┘
               │ 旁挂（可选、按场景键分区）
┌──────────────▼──────────────────────────────────────────┐
│  IClassifierAuditSink / IRuleStore / IHealthReporter      │
└─────────────────────────────────────────────────────────┘
```

### 4.2 契约层（新建，落 `PuddingCore/Classification`）

- **输入**：泛型上下文，替代"把 RSI 塞进审批 record"：
  - 工具审批继续用它自己的 `ToolCallClassificationContext`；
  - RSI 用新的 `RsiSignalContext`（字段见 §4.6）；
  - 二者都实现一个**极窄**的公共面 `IClassificationContext { string SceneKey; string InputIdentityHash; }`（`InputIdentityHash` 用于审计/去重/缓存指纹）。
- **输出**：一个**公共核** + 场景扩展：
  - 公共核 `ClassificationDecision { Decision, Confidence, ReasonCode, ClassifierId, ClassifierVersion, LatencyMs, Evidence[] }`
  - 场景裁决 = 公共核 + 场景决定的枚举（审批的 allow/deny/… ；RSI 的 promote/discard/…）
  - **不强行把 5 个 verdict 合成一个怪 record**，而是让它们**都内嵌公共核**（见 §6 收敛策略）。
- **`defer` 的定位（采纳外部实践）**：`defer` **不新增为一等枚举**去改现有 outcome 集合，而是表达为 **`Decision=Deferred`（已有语义）+ 分档阈值策略**；但**新增一个显式的"阈值档位"声明**（如 `Tier: Auto | Confirm | Human`），使「不确定就升级」在类型上可见，而不是散落在 if 里。

### 4.3 基类与分层责任边界

#### 4.3.0 责任边界（固化为可验证规则）

**基础设施层只提供「基础算子 + 抽象」，绝不承载业务语义**：

| 层 | 拥有 | **绝不拥有** |
|---|---|---|
基础设施（`PuddingCore/Operators` + `PuddingRuntime/Operators`） | 三种原语契约；`OperatorBase` 横切；`JudgementEnvelope`；`ThresholdPolicy`；模型抽象与**唯一**供应商适配器；场景注册表 | **不认** RSI / Goal / ToolApproval 任何领域类型；**不内置**任何场景的默认阈值；**不做**跨场景编排 |
场景层（如 RSI） | 自己的算子（`RsiSignalClassifier : ClassifierBase<…>`）；本场景的问句 / 标签 / 阈值策略 / 输入投影 | 不碰模型协议；不碰审计 / 健康 / 超时实现 |
消费层（如 `RsiEvolutionService`） | 编排（分析 → 规划 → 实施 → 评测） | 不依赖具体算子实现，只依赖原语抽象 |

**两条可执行守卫（把「边界」变成测试，而不是口头约定）**：
1. `grep -i "jev"` 在基础设施命名空间内**只命中**唯一适配器与其测试；
2. 基础设施命名空间**不得出现** `Rsi` / `Goal` / `ToolApproval` 领域标识（**反向依赖守卫**）。

#### 4.3.1 基础设施内部 ≤2 层 + 场景层恰好 1 层

读法：`OperatorBase` → `ClassifierBase` → `RsiSignalClassifier` = **两跳**，但前两跳**都在基础设施内且职责互斥**，场景层只有**一跳**。

| 层 | 类型 | 职责 | 可变点 |
|---|---|---|---|
基础设施-1 | `OperatorBase<TContext, TResult>` | **横切关注点**（见下） | **非虚** |
基础设施-2 | `ScorerBase` / `JudgeBase` / `ClassifierBase` | **固定投影契约**（通用信封 → 本原语输出形状），**不引入新抽象成员** | **非虚** |
场景 | `RsiSignalClassifier` 等 | **只声明**（见 4.3.3 六项） | **唯一**可变点 `ClassifyCoreAsync` |

`OperatorBase` 的横切职责（子类**不得**覆盖其语义，只可配置）：
1. **超时与取消**（独立 CTS；绝不把超时冒泡成异常，转 `Deferred` + reason code）；
2. **健康上报**（成功 / 失败 / 弃权计数，维度 = 场景键 + 输入身份）；
3. **审计旁挂**（**可选**，见下方硬约束）；
4. **置信度分档**（模型原始分 → `Tier`；门槛**来自 `ThresholdPolicy`，不是子类常量**）；
5. **模型调用封装**（经模型抽象，含重试 / 预算 / 采样）；
6. **判定缓存指纹**（`InputDigest` + 算子版本 + 提示词版本）。

**硬约束（来自既有语义，必须保留）**：
> ⚠️ **审计不得成为同步必经环节**。现有语义是「**裁决先于留痕**」：审计写入失败**不改变已定裁决**，只记 Warning。基类若把审计提为必经步骤，会**回退这条已有契约**。
> ⇒ 基类审计挂钩必须是 **旁挂 + 失败不影响裁决 + 失败必留 Warning**（fail-open for verdict / fail-loud for trace）。

#### 4.3.2 继承 vs 组合：一条规则避免两种极端

| 关系 | 用什么 | 例子 |
|---|---|---|
**同一原语内特化**（IS-A） | **继承** | `RsiSignalClassifier : ClassifierBase<…>` |
**跨原语装配**（HAS-A） | **组合** | RSI 的「打分 + 判断驱动晋升」⇒ 编排层分别注入 `IScorer` / `IJudge` |
**跨层引用** | **依赖抽象** | `RsiEvolutionService` 只依赖原语接口 + 模型抽象 |

⇒ 你给的两个选项**都要用，但用在不同的轴上**：**原语内继承、跨原语组合、跨层依赖抽象**。
**禁止**：① 场景算子之间互相继承；② 一个类同时实现两个原语（例如同时是 `IJudge` 与 `IScorer`）——那会让输出语义再次混淆。

#### 4.3.3 「不需要再实现细节」的精确含义 + 反面约束

你说的「RSI 分类器不需要再实现细节」= **场景子类的代码是「声明」，不是「逻辑」**。它只提供 6 项声明：
1. 场景键 `sceneKey`；
2. **问句 + 版本**；
3. 标签集 / 分值域；
4. **阈值策略引用**（非硬编码）；
5. 输入投影（canonical 事件 → 结构化片段）；
6. 输出 → 信号的映射。

其余（超时 / 预算 / 缓存 / 审计 / 健康 / 模型调用 / 分档 / 弃权）**全部由 Base 供给**。

**反面约束（必须防）**：若让 Base 去**猜**这 6 项（内置默认标签集、默认阈值），Base 就会变成**上帝类**，场景差异被压成开关 ⇒ 这 6 项必须是**抽象的、编译期强制的**，缺一个就**不编译**。

#### 4.3.4 反脆弱基类（继承唯一的真风险）

继承的真风险不是「层数」，而是**基类一改、所有场景行为静默漂移**。对策：
- Base 用**模板方法**：`ClassifyCoreAsync` 是**唯一**可变点，其余**非虚**；
- **可执行守卫**：反射测试断言「场景子类只覆盖白名单成员」，出现新覆盖即红；
- Base 变更必须跑**全部场景回归**（工具审批 Runtime 1659 / Platform 1363 + 新场景），**不接受「只测了 RSI 就合」**。

**修正我上一版的说法**：上一版我写「最多 1 层抽象基类」。结合你的分层（基础设施 Base → RSI 分类器）并加上基础设施内部两层，正确规则是 **「基础设施内 ≤2 层 + 场景层恰好 1 层，且每层可变点唯一」**，而不是机械的 1 层。此处按修正后的版本执行。

### 4.4 模型抽象 `IClassifierModel`（Jev 隔离在这里）

```csharp
public interface IClassifierModel
{
    string ModelId { get; }                      // 例 "jev-1.13.0"，用于审计与版本溯源
    Task<ModelJudgement> JudgeAsync(JudgementRequest request, CancellationToken ct);
}

public sealed record JudgementRequest(
    string SceneKey,
    string Instruction,          // 场景提示（版本化！见宪法 C4）
    IReadOnlyList<JudgementQuestion> Questions,   // 对应 Jev 的"一次往返多问题"
    string InputDigest);         // 结构化片段的指纹，便于复现

public sealed record ModelJudgement(
    IReadOnlyList<QuestionAnswer> Answers,  // 含 score / choice / probabilities / confidence
    string RawDigest, int LatencyMs, string ModelId);
```

- `JevClassifierModel` 是**唯一**接触 Jev 端点、协议、`choice` 参数构造的地方；
- **RSI 分类器与进化服务都不认 Jev**：换模型 = 改一行注册；
- 不构造"万能 JudgementRequest"：`Questions` 由场景侧声明，避免模型层知晓业务语义。

### 4.5 分类器服务化 + 场景注册表

- 新增 `ClassifierRegistry`：`SceneKey → IClassifier<TContext, TVerdict>`（**键控**，取代当前的**非键控单例**）。
- 现有工具审批注册改为 `SceneKey = "tool_approval"`；RSI 注册为 `"rsi.signal"`。
- **配置泛化**：`ToolApproval:Reviewer/Classifier/Jev` 保留不动（向后兼容），新增**场景级**配置 `Classifiers:{sceneKey}:{Model|Thresholds|Enabled}`。旧键作为 `tool_approval` 场景的**默认来源**，避免破坏已部署配置。
- 这样"其他基础设施要用分类器"时，只需：注册一个场景 + 声明问句 + 选模型，**不再各自实现一遍**。这正是用户诉求「避免以后到处打补丁」的机制化落地。

### 4.6 RSI 分类器：输入与输出契约

**输入 = 轨迹片段（结构化事件流，不是文本）**

建议字段集（每条**事件**，不是每条消息）：
```
event_id, seq, ts, kind ∈ {model_call, tool_call, human_intervention,
                          verdict, compaction, promotion, rollback},
tool_id, args_hash, exit_code, duration_ms, error_code,
verdict_id?, tier?, goal_id?, task_id?, turn_id?, run_id?,
source_sha,        // 源码身份（防"便条日期当真"）
parent_event_id?   // 支持配对（tool.call.requested/completed）
```

**明确不投喂**（token 纪律）：
| 不投喂 | 替代 |
|---|---|
assistant 正文 | 不给（或只给长度/是否含代码块等元数据） |
**思维链 / reasoning** | **绝不给** |
工具输出全文 | `args_hash` + `exit_code` + 截断摘要(≤N 字符) + 字节数 |
用户消息原文 | 不给（可选给意图分类标签） |

**输出 = 信号裁决**（公共核 + RSI 枚举）：
```
polarity ∈ {positive, negative, unclear}
category ∈ {productive_iteration, no_progress_retry, false_positive_block,
            wasteful_loop, successful_recovery, redundant_work, ...}
confidence, reason_code, tier
evidence_event_ids[]     // 必须可回溯到 canonical 事件
```

### 4.7 信号模型与存储（三层）

| 层 | 名称 | 作用 |
|---|---|---|
L1 | `Signal`（原子） | 分类器一次判定的产物，append-only，带 `sourceEventIds` / `classifierVersion` / 场景键 |
L2 | `SignalCluster`（聚合） | **同目标 + 同根因族**的失败/成功 episode —— 直接对应用户报告里「23 条 Shell 变体」这类**无进展循环** |
L3 | `PromotionRecord`（晋升事实） | `{candidateId, plan, baseline, candidate, verdict, evidence, promotedAt, rollbackRef}` |

- 存储：通用化后的 `IClassifierAuditSink`（按场景键分区）或新表 `classifier_signals`；**append-only**，不物理删；
- **必须带 `classifierVersion` + `sceneKey` + `sourceEventIds`**，否则信号不可复现、不可回放、不可对账。

### 4.8 `RsiEvolutionService`：四阶段 + 第 5 阶段（评测）

```
Ingest   轨迹片段 → RsiClassifier 打标 → Signal 汇总（幂等：水位 = 已处理到的事件 seq）
   ↓
Analyze  潜意识 LLM（第一性原理；见下）→ 结构化假设
   ↓
Plan     方案 + 反事实基线 + 回退路径 + 预估影响
   ↓
Implement 隔离实施（默认禁用 / 单 Agent 灰度）
   ↓
Evaluate 独立评测器 + 负例注射自证            ← 用户方案缺此环，本文主张必补
   ↓
Promote | Rollback（带验收）
```

**Analyze 的"第一性原理"落地手段 = 强制结构化输出**（防打补丁）：
输出必须齐备，缺一不进 Plan：
1. 现象（可观测事实，绑 canonical 事件）；
2. 机制（为什么发生）；
3. 5Why 链（到"架构/契约缺失"层，而非"换条命令"层）；
4. **最小可证伪假设**；
5. **反例预测**（若假设成立，应当观测到什么；若观测不到则假设被否）；
6. 判别信号（用于 Evaluate）。
> 机械闸门：**没有第 5 项就不许进入 Plan** —— 这是把"不要停留在表面"变成流程约束，而不是靠提示词祈祷。

**依赖倒置（用户核心诉求）**：
`RsiEvolutionService` 构造函数**只**接受：
`IRsiClassifier`（= `IClassifier<RsiSignalContext, RsiSignalVerdict>`）、`ISignalStore`、`IAnalysisLlm`（角色化，非具体模型）、`IEvaluator`、`IPromotionStore`。
**不出现** Jev、不出现具体 provider、不出现 HTTP 客户端。

### 4.9 触发点与幂等

- 触发条件：**会话结束** 或 **压缩 checkpoint 之后**（用户提的两个点都收，但**不绑死压缩**——压缩时机由容量决定，不是会话边界）；
- **幂等**：键 = `sessionId + 已处理事件水位 + classifierVersion`；内容未变则复用"无需分析"判定（现有 `skill.improve` 的 `dedup-reviewed` 水位模式可复用）；
- **成本闸门**：场景级预算 + 采样率 + 可关闭开关；分类器判定失败 ⇒ 记信号缺失，**不得**静默当 positive。

---

## 5. 关键设计约束（RSI 宪法草案）

| 编号 | 条款 | 理由 |
|---|---|---|
**C1** | **候选不得修改判定自己的标准**（含改验收线、改门槛、改评测集） | 防"移动球门"；对应外部共识中的判据冻结 |
**C2** | 一切晋升必须**可回退**，且**回退路径本身有验收** | ADR-064 的 `superseded-by` 软删已做到，应继承 |
**C3** | 涉及审批/权限的候选，**不得**以"减少拒绝"为成功指标，**不得**自动学习扩权 | 安全边界 |
**C4** | 场景 **Instruction/提示词必须版本化**，并随信号一起存储 | 否则信号不可复现（"同 token 数 ≠ 同内容"） |
**C5** | 评测器自身也要被评测：改动判据必须用**负例注射**证明「注入已知坏候选仍会红」 | 递归性所在（见 §5.1） |
**C6** | 模型自述不是事实；一切溯源绑 canonical 事件 + 源码 SHA | 已有反例：canonical 07:09 vs 便条"15:10 BJT" |
**C7** | 在评测器可信化完成前，晋升通道 **shadow-only** | 防"从零产出跳到静默劣化" |

### 5.1 「可递归」到底递归在哪
> **递归对象是评测器本身**，而不是"代码改代码"。

因为：若判据不可信，任何"改进"都无法被判定为改进。因此 RSI-Harness 的递归闭环 =
**改进候选 → 独立评测 → 改进评测器（并用负例自证未放水）→ 改进候选…**

### 5.2 北极星指标（防自我欺骗）
| 不要用 | 要用 |
|---|---|
提交数 / churn / 缓存命中率 / `job completed` | **单位通过验收业务增量的成本 / 耗时 / 工具调用数（goodput 归一化）** |
信号条数 | **未测/未知比例**、**无进展调用率** |
"减少拒绝" | （审批场景禁用） |

---

## 6. 与既有资产的迁移策略（反大爆炸）

| 项 | 策略 |
|---|---|
**5 个 verdict record** | **新老并存 + 单向收敛**：新建公共核，新场景只用新核；旧场景**按需迁移**，并在 `code_map` 记录迁移水位与剩余计数。**不一次性重写。** |
工具审批（第一个消费者） | 作为**基类的第一个适配者**：抽 Base 后**必须证明行为不变**（回归 Runtime 1659 / Platform 1363 全绿）。**这是 S1 的硬验收。** |
`ToolApproval:Reviewer/Classifier/Jev` 配置 | **保留**，作为 `tool_approval` 场景的默认来源；新场景用新键。 |
`ConversationSkillEvolutionTrajectorySource` | **不放宽**其"排除含失败轨迹"门禁；RSI 另建**隔离**片段源（含失败→纠偏→验证）。 |
前端 `autoReviewClassifier.ts` | 抽象落地时**必须点名处理**（复用后端契约或明确标注为 UI 侧状态机），避免出现第三份判定逻辑。 |

---

## 7. 风险与诚实清单

| # | 风险 / 未知 | 说明 |
|---|---|---|
R1 | **Jev 性能与价格未经独立验证** | 已有一手证据只到"端点/协议/解析可用"（Live 2/2）；latency/价格是厂商主张 |
R2 | **端点与价格三源不一致** | 必须以 `llm.providers.json` 为唯一事实源 |
R3 | 抽象可能"抽早了" | 目前只有**一个**消费者（工具审批）。缓解：**只抽已被 2 个以上场景证明的横切点**，其余留在场景内 |
R4 | 场景化分类器带来**调用成本放大** | 需预算/采样/降级；分类器不可用时应"记缺失"而非"判 positive" |
R5 | 现有 5 verdict / 前端平行实现的**收敛可能长期悬空** | 需计数水位 + 明确 owner，否则变成永久并存 |
R6 | 评测门禁**当前有洞**（Admin 数量判据、必测未测 exit 0） | 这是 **C7 的前置阻塞**：门禁不可信 ⇒ 晋升不可开 |
| R7 | ~~契约命名空间疑似 `PuddingCode.Classification`（与目录 `PuddingCore` 不一致）~~ **已复核（2026-09-21）**：`PuddingCode.Classification` 是**真实命名空间**（非笔误），且全部 9 个类型集中在**单文件** `Source/PuddingCore/Classification/ToolCallClassification.cs`（279 行） | ✅ 已解除；新算子契约取**兄弟命名空间** `PuddingCode.Operators` |

**未找到**（不编造）：判据冻结 / 负例注射的**权威一手出处**（METR / Apollo / UK AISI 类方法论文档未逐段打开）；我方历史 verdict 量级（需审计表近 30 天计数才能定 `n_min`）。

---

## 8. 切片计划与验收

| 切片 | 内容 | 验收 |
|---|---|---|
**S0（阻塞）** | 评测门禁可信化：判据改为 **case 身份**（预算由 `KnownRed.Count` 派生，删除 `AllowedFailures` 旋钮）；结构化证据（TRX / Jest JSON）为**唯一**判定源；必测未测 ⇒ **非零退出**；名单保鲜 30 天；豁免带到期日 | ✅ **已完成**（2026-09-21）：`-SelfTest` 11 例负例全通过且与旧判据并排对照（N1 旧 PASS/新 FAIL、N2/N4/N5 同）；实跑 Core/Runtime/Platform/AdminJest 全 PASS，退出码 0 |
| **S1a** | 抽 `OperatorBase` + 三原语契约（`IScorer` / `IJudge` / `IClassifier`）+ `JudgementEnvelope` + `ThresholdPolicy`；**只落契约与基类，不接模型** | 契约单测：三投影读同一信封；阈值外置；`Abstain` 不被折叠；**反射守卫**（场景子类只允许覆盖 `ClassifyCoreAsync`）；**反向依赖守卫**（基础设施不出现 RSI/Goal/ToolApproval 标识）。**实施规格已冻结**：`Docs/Features/S1a-判定算子基础设施-实施规格-2026-09-21.md` |
| **S1b** | 场景注册表 + 工具审批改为第一个适配者 | **行为零变更**：Runtime 1659 / Platform 1363 全绿；新增"场景键路由"用例；**Jev 隔离守卫测试**上线（§4.0.6） |
**S2** | 泛化旁挂（audit / rule / health 按场景分区）；**保留"裁决先于留痕"** | 审计失败不影响裁决 + 有 Warning（已有用例须继续绿） |
**S3** | 轨迹片段源 + 结构化字段 + 水位幂等 | 单测：不投喂正文/CoT；水位不变则不重复分析 |
**S4** | `RsiClassifier`（经 `IClassifierModel` 接 Jev）+ `Signal` 存储（**agent 隔离责任落在调用方**，见 §15.9） | 换模型实现不改 RSI 代码（用假模型证明） |
**S5** | Analyze → Plan → Implement 流水线（**默认 shadow**） | "无反例预测不进 Plan"的机械闸门有测试 |
**S6** | Evaluate + 灰度晋升 + 回退 | 负例注射自证；回退路径有验收；C1–C7 各有用例 |
| **G1–G5** | **技能组合治理**（收敛与整合的基础设施，见 §13） | 见 §13.8 |
| **G6–G8** | **潜意识 LLM 的 SKILL 整理作业**（提炼，而非记笔记式积累，见 §14） | 见 §14.7 |

> ⚠️ **次序修订（2026-09-21，用户指令驱动）**：**G 轨道应排在 S3/S4 之前** —— 理由见 §13.9（RSI 是产能放大器；G 是更便宜的首个真实消费者；G 有即时回报）。S0/S1a/S1b/S2a/S2b 均已完成，**下一个切片应为 G1**。
> ✅ **该修订已满足（2026-09-22 复核）**：G1 已于 2026-09-21 完成（只读报告 `Docs/Reports/skill-portfolio-G1-2026-09-21.md`）⇒
> 按 §15.6 合并序列，**下一个切片 = 第 6 项 `G6 skill.curate` 报告先行**（零技能变更；成本窗口 = 非工作时段）。
> ⛔ 不得再据本行重做 G1。

---

## 9. 待决策项

| # | 问题 | 我的建议 |
|---|---|---|
D1 | 基类落点：`PuddingCore/Classification` 还是新建 `PuddingCore/Classifiers`？ | 先复核 §7 R7 的命名空间异常，再定；**倾向留在 `PuddingCore`**（避免新工程） |
D2 | 5 个 verdict 的收敛节奏：立即统一 vs 单向收敛 | **单向收敛**（新场景用新核，旧场景按需迁移） |
D3 | RSI 触发点是否绑压缩 | **不绑**：会话结束 ∪ 压缩后，幂等水位去重 |
D4 | 是否允许 RSI 自主 `Implement`（改代码） | 建议 **Phase 1 只到 Plan**，Implement 仅对 Skill/规则类候选开放；改产品代码需人审 |
D5 | 场景注册表配置键命名 | `Classifiers:{sceneKey}:*`，与既有 `ToolApproval:*` 并存 |
| D6 | 判断器是否**三值**（Yes/No/Abstain）而非布尔+置信度 | **建议三值**：低置信区间就是弃权区；否则要么退化成「不敢判就拒绝」，要么退化成「沉默放行」（§4.0.5） |
| D7 | `confidence` 是否允许下游当概率用 | **未校准前禁止**（`confidenceKind=ModelSelfReported`）；RSI 晋升走样本量/序贯检验/灰度，不比 confidence 大小（§4.0.4） |
| D8 | 基础设施内部是否允许 2 层（`OperatorBase` → 投影基类） | **允许**（职责互斥、可变点唯一）；**场景层严格 1 层**，禁止场景算子互继承（§4.3.1 / §4.3.2） |

---

## 12. 与既有技能自进化代谢通路的整合（2026-09-21 追加）

> **本节由用户指令驱动**：*「RSI 请注意与 `AgentSkillEvolutionStore` + `ConversationSkillEvolutionTrajectorySource`（技能自进化轨迹源）+ `SkillEnforcerService` 等整合。」*
> **取证方式**：只读审计（两个子代理 + 父级逐文件核对），未编译、未跑测；行号以 2026-09-21 HEAD 为准，可能漂移。
> **前提事实**：**RSI 本体在代码中尚不存在** —— `git grep -E "Rsi" -- "*.cs"` **无命中**，全部命中仅在文档/规格。故本节是**整合设计**，不是改造设计。

### 12.1 既有通路的事实（代码级）

| 组件 | 契约（已验证签名） | 位置 |
|---|---|---|
`ISkillEvolutionTrajectorySource` | `Task<IReadOnlyList<SkillEvolutionTrajectory>> GetRecentSuccessfulAsync(string workspaceId, string agentInstanceId, int limit, CancellationToken ct = default)` | `PuddingCore/Abstractions/ISkillEvolution.cs:6` |
`ConversationSkillEvolutionTrajectorySource` | 上述接口的**唯一实现**（DI 注册为单例） | `PuddingRuntime/Services/Skills/ConversationSkillEvolutionTrajectorySource.cs:12`；注册 `PuddingRuntime/DependencyInjection.cs:106` |
`ISkillEvolutionDataAccess` | 数据访问层：`GetRecentSuccessfulCommandsAsync` / `GetEventsByCommandIdsAsync` / `GetMessageContentsByIdsAsync` | `PuddingCore/Platform/ISkillEvolutionDataAccess.cs:35` |
`SkillEvolutionTrajectory` | record：`WorkspaceId` / `AgentInstanceId` / `SessionId` / `TurnId` / `Goal` / `Steps` | `PuddingCore/Platform/SubconsciousDtos.cs:387` |
`IAgentSkillEvolutionStore` | `GetAsync` / `ListAutoGeneratedAsync` / `CreateAsync` / `UpdateAsync` / `SetEnabledAsync` | `PuddingCore/Abstractions/ISkillEvolution.cs:16` |
`AgentSkillEvolutionStore` | **`AgentSkillFileService` 的薄适配器**（类注释原话：*“Adapts self-evolution writes to the same filesystem/index service consumed by SkillEnforcer”*） | `PuddingRuntime/Services/Skills/AgentSkillEvolutionStore.cs:7` |
`SkillEnforcerService` | `Task<IReadOnlyList<SkillEnforcementResult>?> EnforceAsync(string agentInstanceId, string userMessage, CancellationToken ct = default)`；**PreMessageHook**：用户消息 → 关键词匹配 → 注入 SKILL.md 内容 | `PuddingRuntime/Services/Skills/SkillEnforcerService.cs:13` |

### 12.2 ⭐ 核心结论：**晋升链路本来就已经是通的（已通 80%）**

`AgentSkillEvolutionStore` 的类注释直接给出了答案 —— 它**刻意**适配到 `SkillEnforcerService` 所消费的**同一个** `AgentSkillFileService`。因此：

```
[缺失] RSI 判定层 / 信号层                ← 本次要建（消费者）
        │
        ▼
[缺失] 失败/纠偏轨迹源                    ← 语义与现有实现相反，见 12.3
        │
        ▼
[已有] IAgentSkillEvolutionStore          ← 不新建！新建会让 SkillEnforcer 看不到
        │  CreateAsync / UpdateAsync / SetEnabledAsync
        ▼
[已有] AgentSkillFileService（共享存储）
        │
        ▼
[已有] SkillEnforcerService.EnforceAsync  ← PreMessageHook 自动注入（缓存按 index.GeneratedAt 失效）
```

> **这条结论修正了「RSI 需要一套自己的技能写入通道」的直觉**：不需要。**RSI 复用 `IAgentSkillEvolutionStore` 即可让晋升结果立即被注入消费**；另外造一个 store 会让产物成为**不可达的死数据**。

### 12.3 三条硬约束（违反任一条都会造成静默劣化）

**C1｜不得修改 `GetRecentSuccessfulAsync` 的语义。**
其过滤逻辑（`TryBuildSuccessfulSteps`）在遇到 `ToolCallFailed` 时 **`return null`** ⇒ **一处失败即整条轨迹丢弃**；且要求工具链 `steps.Count >= 2`。这是 **ADR-064 已部署的生产语义**（「只从成功经验中学」）。
RSI 需要的是**失败 → 纠偏 → 验证**的轨迹 —— **恰好相反**。若为省事直接在既有方法里放开失败分支，后果是 **ADR-064 的管道开始把失败当经验学**，且无任何报错。
⇒ **正确做法：新增独立契约（如 `IRsiTrajectorySource`）共享 `ISkillEvolutionDataAccess`**（数据访问层是可复用的，语义层不是）。

**C2｜晋升必须经 `IAgentSkillEvolutionStore`，不得旁路写盘。**
理由见 12.2：`SkillEnforcerService` 只认 `AgentSkillFileService` 的索引。旁路写入 = 晋升静默不生效。

**C3｜「丢弃」应映射为 `SetEnabledAsync(false)`，而非删除文件。**
与非破坏性原则一致、可回滚，且 `SkillEnforcerService` 的 `GetOrRefreshKeywordMapAsync` 已按 `entry.Enabled` 过滤 ⇒ 禁用即**立即不再注入**。

### 12.4 ⚠️ 新发现的风险：`SkillEnforcerService` 关键词**先到先得** ⇒ RSI 可能静默挤掉既有技能

`GetOrRefreshKeywordMapAsync` 构建映射时**不区分技能来源**，且冲突时**先到先得**：

```csharp
if (!string.IsNullOrWhiteSpace(kw) && !map.ContainsKey(kw))
    map[kw] = entry.SkillId;     // 已存在则静默忽略 ⇒ 由索引遍历顺序决定归属
```

而 `CollectKeywords` 把 **SkillId、Name 整体、Name 分词**都当关键词（`Name.Split(' ', '|', ',', '/', '：', '、')`）⇒ 通用词（如「开发」「测试」「报告」）**极易碰撞**。

**后果**：RSI 晋升的自动技能若与既有技能关键词碰撞，会**由遍历顺序静默决定**谁被注入 —— 结果可能是**既有技能不再被加载，且无人知晓**。这正是 RSI 失效的典型形态：**改动生效了，但效果被静默抵消**。

⇒ **必须成为 RSI 晋升的评测门禁项**：晋升前检查**关键词冲突**（与已启用技能的交集），冲突须显式裁决（改写关键词 / 提升优先级 / 拒绝晋升），**不得放任先到先得**。

### 12.5 与并行协作者的界外划界（SkillHub）

审计中发现**并行协作者正在途**的 SkillHub 工作流（未跟踪文件：`SkillHubController.cs` / `SkillHubService.cs` / `HubSkill*Entity.cs` / `SkillHubDtos.cs` / `SkillHubServiceTests.cs` / `SkillHubSchemaBootstrapper.cs`，以及 `M PlatformDbContext.cs`）。

其概念（**技能发布 / 版本 / 血统**）与 RSI 的**晋升 / 丢弃**存在**语义重叠** ⇒ **必须划界，否则两条链路会各自实现一套晋升**：
- **RSI 侧**：判定「哪个行为值得晋升」，产出**候选**；
- **SkillHub 侧**：承载「技能的**分发与版本血统**」；
- **交界**：RSI 晋升**经 `IAgentSkillEvolutionStore` 落到本 Agent 的私有技能目录**（本设计范围）；**跨 Agent 分发**属 SkillHub 范围，**本设计不涉足**。

### 12.6 本节同时修正了本文档自身的两处落差（诚实记录）

| 落差 | 文档原假设 | 代码事实 | 处置 |
|---|---|---|---|
① | §4.1/§4.8 假设存在泛型 `IClassifier<TContext, TVerdict>` | 实际为**非泛型** `IClassifier : IOperator` | **以代码为准**，修订 §4.1/§4.8 措辞（不在本节改写，避免与 S1a/S1b 已提交规格冲突） |
② | 暗示三原语「已在服务」 | `IClassifierModel` **零实现**；`IOperatorRegistry` **无生产解析点**；**零个生产类**继承 `OperatorBase/ClassifierBase/ScorerBase/JudgeBase` | 属已知的**惰性抽象风险**，由 S3/S4 首次真实消费（RSI 即那个消费者） |

> ⚠️ **落差②是本项目最需要警惕的状态**：基础设施已被建成但**尚未被生产消费**。S1a→S2b 的每一片都以「**尽快被真实消费者使用，否则抽象会腐化**」为收尾理由，而 **RSI 正是那个消费者** —— 这也是本节把「整合」提到最前的原因。

---

## 13. 技能组合治理：收敛与整合的基础设施（2026-09-21 追加）

> **本节由用户指令驱动**：*「你的技能已经 100 多个，很明显，需要收敛和整合，这个应该有系统基础设施提供帮助。不能每次 RSI 和技能进化都产生大量的 SKILL 然后堆积。」*
> **取证方式**：只读实测（技能清单统计 + 关键词空间展开）。**未编译、未跑测、未改动任何生产代码**。数字来自 2026-09-21 实盘 `D:\data\agents\*`。

### 13.1 实测事实（"堆积"的全部证据）

#### （a）存量与分布

| Agent | 技能总数 | 启用 | 禁用 |
|---|---|---|---|
`default.global_general-assistant.6a8`（我） | **144** | **139** | 5 |
`default.global_general-assistant.0e0`（dsh） | **139** | **139** | **0** |
`default.global_general-assistant.258`（蜜糖） | 1 | 1 | 0 |
`default.general-assistant-001`（审批审计员） | 0 | 0 | 0 |

两条结论：

1. **启用数已达 139**；且 `dsh` 的 139 个里**一个都没被禁用** ⇒ 现有收敛机制**跑得动、但收不住**；
2. 我这 139 个启用技能里，`auto-generated` 标签命中 **134** 个 ⇒ 这些**几乎全部是自进化管道自己长出来的**，不是用户手工攒的。

#### （b）关键词空间已饱和并互相遮蔽（最硬的证据）

按 `SkillEnforcerService.CollectKeywords` 的**真实收集规则**（`Keywords` + `Tags` + `SkillId` + `Name` + `Name` 分词）展开我这 139 个启用技能：

```
enabled=139   关键词槽位=2496   去重关键词=747   被≥2技能共享=166（22%）
```

共享最严重的一批（⇒ **除排序第一个外，其余技能通过该关键词全部静默失效**）：

| 关键词 | 声明它的技能数 | 性质 |
|---|---|---|
`file_read` | **63** | 工具名 |
`save_memory` | **54** | 工具名 |
`shell` | **50** | 工具名 |
`query_sub_agents` | **48** | 工具名 |
`search_grep` | **47** | 工具名 |
`git_status` | 43 | 工具名 |
`terminal_wait` | 43 | 工具名 |
`goal_read` | 42 | 工具名 |
`git_commit` | 40 | 工具名 |
`spawn_sub_agent` | 39 | 工具名 |
`git_push` | 36 | 工具名 |
`self-evolution` | **135** | ⚠️ **治理标签** |
`auto-generated` | **134** | ⚠️ **治理标签** |
`source-session:206a9b48…` | **101** | ⚠️ **来源标签** |
`self-evaluated:1.0.1` | 93 | ⚠️ **版本标记** |
`dedup-reviewed:1.0.1` | 56 | ⚠️ **版本标记** |

⇒ 由此确认**两条独立缺陷**（方向一致，都让技能越多越无用）：

**D1｜治理/溯源标签泄漏进关键词空间（缺陷，不是设计）**
`CollectKeywords` 把 `entry.Tags` **全量**当关键词（源码注释写的是"Tags（次级）"，意图显然是**语义标签**），但实际 tags 里混着**治理与溯源元数据**：`auto-generated` / `self-evolution` / `dedup-reviewed:<v>` / `self-evaluated:<v>` / `source-session:<id>` / `source-turn:<id>`。
后果：
- 这些记号**不是用户会说的话**，却占满关键词槽位并制造**虚假共享**（166 个冲突里，绝大部分来自此处与工具名）；
- ⚠️ **`source-session:<sessionId>` 尤其危险**：它就是我**当前会话 ID**。一旦匹配面出现该串，**101 个技能会同时命中** ⇒ 单轮注入失控。

**D2｜工具名当关键词 ⇒ 命中面与"何时该加载"无关**
`git_status` / `file_read` / `shell` 这类**工具调用名**表达的是「这个技能用到过哪些工具」，**不是**「这个技能何时该被加载」。
后果：① 关键词槽位被工具名吃光（63 个技能共享 `file_read`）；② 真正能区分技能意图的**短语**反而没有位置。

#### （c）三个结构缺口（根因；D1/D2 只是其症状）

| 缺口 | 代码级证据 | 后果 |
|---|---|---|
**G-A 无组合预算** | 准入 `EvaluateAdmissionAsync` 是**逐候选**判定（问"与既有是否重复"），全流程**没有任何技能总数上限** | 去重能挡住**重复**，挡不住**累积** ⇒ N 单调增长 |
**G-B 无价值反馈** | `SkillEnforcerService.EnforceAsync` 命中后只 `_logger.LogDebug`，**不持久化任何使用事实** | **无人知道哪些技能从未被命中过** ⇒ 只能按"重复"淘汰，**无法按价值淘汰** |
**G-C 注入无上限** | `EnforceAsync` 把**所有**命中的技能**全部读文件并返回**；无 top-k、无预算 | 命中多则注入多；叠加 D2 的共享关键词 ⇒ 单轮上下文成本**随 N 单调上升** |

#### （d）一处放大器
`ImproveSkillsAsync` 每轮只取 **`Take(5)`** 个未自评技能；存量 139 ⇒ **自评永远追不上**（需 27 轮以上不间断才能轮一遍）。

### 13.2 三条设计原则

**P1｜治理对象是「组合」，不是「候选」。**
既有机制问的是"这个候选是否与既有重复"；缺的是"**这个组合是否已饱和、这一份增量是否值得占用一个名额**"。

**P2｜价值来自遥测，不来自印象。**
"这个技能到底有没有用"必须是**可观测事实**（是否被命中 / 是否与成功回合共现 / 注入代价），**不能由 LLM 凭描述判断** —— 否则与"候选给自己打分"（§5 宪法 C1）是同一类错误。

**P3｜一切决策可回滚、可审计、可版本化。**
沿用既有语义：**禁用而非删除**；阈值**外置为策略对象** —— 直接复用 S2a 的 `AcceptanceThresholdPolicy` / `ThresholdPolicy`，**不新建阈值类型**。

### 13.3 三项基础设施（分别落在三原语上）

| 机制 | 原语 | 输入 | 输出 |
|---|---|---|---|
**M1 组合预算 + 边际价值准入** | 判断器 `IJudge` | 候选价值分 + 启用集合的价值分布 | `Admit` / `Displace(evictId)` / `Merge` / `Defer` |
**M2 使用遥测 + 价值打分** | 打分器 `IScorer` | 命中 / 注入 / 回合结局 / 注入代价 / 重叠度 | `SkillValueScore`（带 `scoreScale` 声明） |
**M3 家族归类 + 家族内上限** | 分类器 `IClassifier` | 技能元数据 + 文本 | 家族**分布**（非单标签）+ 超限报告 |

> 三者同时是**三原语的第二个真实消费者**（第一个是工具审批适配器）。而 **M2 完全不需要 LLM** —— 它是**最便宜的一次真实消费**，正好用来检验 S1a/S1b 的抽象是否好用，从而提前解除 §12.6 落差② 的「惰性抽象」风险。

### 13.4 M2：使用遥测（唯一需要新增的采集点）

**采集点**：`SkillEnforcerService.EnforceAsync` 返回处 —— 只有那里同时掌握「命中的 skillId 集合 + 消息 + 可关联的会话/回合」。

**最小字段集**：`skillId` / `agentInstanceId` / `sessionId` / `turnId` / `matchedAt` / `injectedTokenEstimate` / **回合结局**（成功、失败、中断）。

- ⚠️ **结局不能在注入时猜**：注入发生在 LLM 调用**之前** ⇒ 结局必须由**会话结束钩子回填**（`SessionCompressedMemoryMaintenanceHook` 一类会话级钩子已存在，可直接挂）。
- ⚠️ **热路径纪律**：`EnforceAsync` 跑在**每条用户消息**上 ⇒ 遥测写入必须**非阻塞、可批量、可失败**；沿用在 S2b 确立的 **「裁决先于留痕」**：**留痕失败绝不阻断注入**。
- ⚠️ **冷启动**：**无遥测 ≠ 低价值**（与 §7 R4 同源）⇒ 冷启动期**只降不杀**。

### 13.5 M1：组合预算（**类型级约束，LLM 无法越过**）

```
SkillPortfolioPolicy { policyId, version, hardCap, softTarget, perFamilyCap, minMarginalGain, stalenessDays }
```

判定序（**fail-closed**）：

| 情形 | 判定 |
|---|---|
`enabled < softTarget` | 走既有去重；通过 ⇒ `Admit` |
`softTarget ≤ enabled < hardCap` | 仅当 `candidateScore − minEnabledScore > minMarginalGain` ⇒ `Admit`；否则 `Merge` / `Defer` |
`enabled ≥ hardCap` | **必须置换**：`candidateScore > minEnabledScore + minMarginalGain` ⇒ `Displace(价值最低者)`；否则 `Merge` / `Defer` |

**为什么这是基础设施而不是一段 `if`**：阈值全部来自**已有策略对象**（S2a）⇒ 预算**可版本化、可审计、可随结果落库、可被评测守卫**。这是 S2a 模式的直接复用 —— **不新增阈值类型**。

### 13.6 M3：家族结构（"整合"的落点）

- 分类器给每个技能打**家族分**（取**分布**，不止最大类）：检索 / 委派 / git 提交 / 图像生成 / 健康诊断 / 记忆维护 …
- **家族内上限**：同家族启用数 > `perFamilyCap` ⇒ **强制进入合并评审** —— 这条直接对应"整合"；
- **合并判据不由 LLM 单独决定**：同家族 **且** 关键词重叠 **且** 程序性文本相似 ≥ 阈值 **且** 证据可归并（复用 `ConsolidateExistingAsync` 的确定性校验思路，仅适度放宽其 0.35 文本相似度以适配"同家族不同措辞"）；
- ⭐ **关键词唯一性守卫**：准入即检测关键词与已启用集合的交集；冲突须**显式裁决**（改写关键词 / 提升优先级 / 拒绝），**不得先到先得**（修 §12.4，并顺带覆盖 D2）。

### 13.7 D1 / D2 的处置（本轮新发现的缺陷）

| # | 缺陷 | 处置 | 风险 |
|---|---|---|---|
D1 | 治理/溯源标签泄漏进关键词空间 | `CollectKeywords` **按前缀白名单过滤**治理与溯源标记（`auto-generated` / `self-evolution` / `dedup-reviewed:*` / `self-evaluated:*` / `source-session:*` / `source-turn:*`） | **会改变注入行为**（部分技能不再被这些记号命中 —— 但那本来就不是意图）⇒ 需回归 + 灰度 |
D2 | 工具名当关键词 | 降为**低权重次要命中**或移出关键词空间；改为「**意图短语优先**」 | 同上 |

> 这两条**不需要新增基础设施**即可修，但**必须在 G2（遥测）之后落地** —— 一旦改了命中面，就必须有遥测才能回答"改完到底有没有变好"。**没有度量就不许调参**，否则又是一次"移动球门"。

### 13.8 切片计划（G 轨道）

| 切片 | 内容 | 验收 |
|---|---|---|
**G1** | 组合盘点（**只读**）：启用数 / 家族分布 / 零命中清单 / 关键词冲突清单 / 索引 token 代价 | 盘点数字与实测**可复现一致**；换个 Agent 目录重跑得到同一口径 |
**G2** | 使用遥测采集 + 价值打分（**打分器首个真实消费者，无 LLM**） | 遥测非阻塞、失败不阻断注入；`scoreScale` 显式声明；**冷启动（无数据）≠ 低分**，有专门用例 |
**G3** | `SkillPortfolioPolicy` + 预算判定（判断器） | 预算**不可被 LLM 越过**（构造"LLM 要求 create 但预算已满"的用例）；置换＝禁用（可回滚）；阈值全部来自策略对象 |
**G4** | 家族归类 + 家族内合并（分类器） | 家族内上限强制生效；关键词唯一性守卫拒绝先到先得；合并须确定性四条件同时满足 |
**G5** | D1/D2 修复（在 G2 度量之后）+ 与 RSI 对齐 | 修复前后各有遥测对比；RSI 晋升走**同一**准入与预算（**无双写通道**） |
**G6–G8** | **潜意识 LLM 整理作业 `skill.curate`** —— G 轨道从「治理基础设施」到「**实际降 N**」的落地环节（见 §14） | 见 §14.7 |

> **§14 追加**：G1–G5 建的是**判据与容量**；G6–G8 才是**真正会减少技能数**的那一步（用户诉求的落点）。

### 13.9 ⭐ 次序结论：**G 轨道应排在 RSI 的 S3/S4 之前**

1. **RSI 是产能放大器。** 没有组合预算就上 RSI ⇒ **把堆积问题放大** —— 这正是用户这条指令的实质；
2. **G 是更便宜的首个真实消费者。** M2 打分器**完全确定性**（无 LLM）；RSI 才需要 LLM 判定与轨迹建模。**先在最便宜的消费者上验证抽象，再上 RSI**，符合 §7 R3（"抽象可能抽早了"）的缓解策略；
3. **G 有即时回报**：启用技能数下降 ⇒ 每轮注入的**索引与命中内容 token 直接下降**，关键词冲突率下降。

同时 G 为 RSI **备好了落点**：RSI 的晋升结果走**同一套**准入与预算 ⇒ 不产生第二条写入通道（继续遵守 §12.2 的 C2）。

### 13.10 风险与诚实清单

| # | 风险 | 说明与缓解 |
|---|---|---|
R1 | **冷启动把"无数据"当"低价值"** | 冷启动期**只降不杀**；禁用需人工确认 |
R2 | **误杀是静默的** | 注入缺失不会报错 ⇒ 每次 Retire 记录理由 + 证据 + 观察窗 N 天 |
R3 | 遥测变成新的热路径成本 | 非阻塞 + 批量 + 可失败；留痕失败不得阻断注入 |
R4 | `Take(5)` 造成自评长期欠账 | 需明确"存量清欠"节奏，否则存量技能永远没被自评过 |
R5 | 家族归类本身是 LLM 判定 | 归类**只用于分组**，**不用于判定生死**（生死由 M2 遥测 + M1 预算决定）⇒ 把 LLM 风险限制在"分组"这一侧 |
R6 | 未验证：**索引 token 真实占比** | §13.1(b) 只测了关键词空间，**未测上下文占比** ⇒ 列为 G1 交付项 |

**未验证（不编造）**：技能索引在我方上下文中占多少 token（需实测）；139 个技能里有多少**从未被命中过**（需 G2 遥测才能回答 —— **现在无法回答，这本身就是 G-B 的证明**）。

---

## 14. 潜意识 LLM 的 SKILL 整理作业：提炼，而非记笔记式积累（2026-09-21 追加）

> **本节由用户指令驱动**：*「潜意识 LLM 需要整理 SKILL，这是我们之前的规划，也即是合并和整理 SKILL，移除陈旧的 SKILL，合并重复的，保留有价值的，废弃无价值的。提炼，而不是记笔记方式的积累 SKILL。」*
> **取证方式**：只读代码核对（未编译、未跑测）；数字与 §13 同一基线。

### 14.1 与现状的落差（一句话）

用户要求潜意识 LLM 做**整理**：合并、淘汰陈旧、合并重复、保留有价值、废弃无价值。
**现状：没有任何一个既有作业的职责是"让技能变少"。**

| 作业 | 实际职责 | 对 N 的作用 |
|---|---|---|
`auto_dream` | 记忆维护（书籍/章节） | 不影响 |
`extract_patterns` | 从成功经验**产生**候选 → 物化为技能 | **只增** |
`improve_skills` | 对**单个**技能打补丁 + 去重（`Take(5)`/轮） | 仅对**完全重复**偶发 −1 |

⇒ 系统里**只有"增"的通道**，没有"降"的通道（除"重复"这一种）。这就是堆积的机制性原因。

### 14.2 ⭐ 铁证：产物**就是**笔记（`GenerateSkillMarkdown` 逐行核对）

`SubconsciousOrchestrator.GenerateSkillMarkdown` 的真实模板：

```
## 来源
- 会话: <SessionId>
- 置信度: 98%
- 验证状态: canonical conversation events verified all tool calls succeeded
- Turn: <TurnId>

## 目标
<Goal>

## 步骤
1. `git_status`
2. `file_read`
3. `file_patch`

## 质量门禁
- <checkName>: passed — <reason>
```

**四条"记笔记"特征，逐条对上**：

| # | 特征 | 为什么这是"笔记"而不是"提炼" |
|---|---|---|
1 | 正文写死 **`SessionId` + `TurnId`** | 一条会话一条记录 ⇒ 产物是**episode log**，不是可迁移的原则 |
2 | **"步骤"是工具名序列** | 记录的是"**这次用过哪些工具**"，对"**下次该怎么做**"几乎无指导价值。**这正是 D2（工具名进关键词）的同一根源** |
3 | **没有适用条件** | 没有"何时该用 / 何时不该用" ⇒ 下游**无法判断该不该加载** |
4 | **没有跨会话归纳** | `ExtractPatternsAsync` **逐候选**物化，一候选一条；`MergeCandidateAsync` 只把新证据**追加**到既有技能 ⇒ **累加，不是提炼** |

**结论**：D1/D2 不是两个孤立缺陷，而是**"记笔记式积累"这一根源**在关键词空间里的两个投影。

### 14.3 职责归属：潜意识 LLM 是**执行者**，基础设施是**判据**

- **潜意识 LLM 做提炼**（它擅长创造性综合：把 5 条具体做法归并成 1 条一般程序）；
- **裁决不由它单独下**（否则回到"候选给自己打分"，违反 §5 宪法 C1）⇒ 由 **M1 预算 + M2 价值 + M3 家族 + 确定性门禁**裁决。

> 这与既有代码哲学**一致**，不是新发明 —— `SkillEvolutionDeduplicationService` 类注释原话：*"**LLM proposals are never sufficient on their own**: target identity, confidence, tool fingerprints and evidence/text overlap are validated deterministically."* 本节只是把这条哲学**应用到"整理"这个新作业上**。

### 14.4 新增作业：`skill.curate`（`SubconsciousJobTypes.SkillCurate`）

**接入方式（既有机制，无需新建管道）**：`TryProcessPeriodicJobAsync` 的 `JobType` 白名单 + `switch` 分派 + 周期任务表（`PatternExtractionIntervalSeconds` 同款配置项），幂等键沿用既有形状 `periodic:{jobType}:{workspaceId}:{agentInstanceId}:{bucket}`。

**触发（两条并置）**：
1. **定时**：如 24h，且**优先落在非工作时段**（成本窗口 §12）；
2. **条件触发**：`enabled > softTarget`（§13.5）⇒ **提高优先级 / 缩短间隔**。
   ⭐ 条件触发是"**堆积正在发生时**就介入"的关键；纯定时等于"事后打扫"。

**与 `improve_skills` 的分工（必须划清，否则职责重叠）**：

| 作业 | 职责 | 边界 |
|---|---|---|
`extract_patterns` | **产能**：从经验产生候选 | 只管"产生" |
`improve_skills` | **单技能质量**：给单个技能打补丁 | 收缩为"补丁"，**不再声称做收敛** |
**`skill.curate`（新）** | **组合治理**：提炼 / 合并 / 淘汰 | 唯一有"降 N"职权的作业 |

**输出契约（每轮必须给出）**：
- `K` 条**提炼**产物候选；`R` 条**淘汰**建议；
- **组合变化报告**：`N_before` → `N_after`；
- ⭐ **若一轮下来既没降 N 也没合并，必须写明原因** —— **禁止"跑了但什么都没做"且无解释**（否则作业会退化成静默空转，与 §13.1(d) 的 `operationCount=0` 是同一病）。

### 14.5 提炼产物的契约（**"提炼 vs 记笔记"的可执行定义**）

一条合格的提炼产物**必须**含：

1. **适用条件**（何时该加载 / 何时不该）—— 取代"来源：会话 id"；
2. **可迁移程序**（步骤写**做什么**，而不是"用过哪些工具"）—— 取代工具名列表；
3. **陷阱 / 反例**（该轨迹中有过失败或用户纠正的，写进来）；
4. **证据引用**（provenance）只放 **tags / 附录**，**不进正文主结构、不进关键词**；
5. **一般性**不低于被它取代的技能（由 14.6 的 C2 机械判定）。

> **反笔记不变式（硬性）**：产物**正文与关键词中不得出现** `SessionId` / `TurnId` / 工具名序列。
> **这与 D1 修复同向** —— 都是把 `source-*`、工具名从"内容/关键词"降级为**审计元数据**。

### 14.6 门禁：**LLM 提议，确定性裁决**（fail-closed）

| # | 判据 | 不满足时 |
|---|---|---|
**C1 证据不丢** | 被合并候选中出现过的**全部** source turn，必须能在新技能的 provenance 中找到 | **拒绝**，保留原状 |
**C2 一般性不降** | `coveringSessions(new) ≥ max(coveringSessions(被取代者))` | **拒绝** |
**C3 关键词唯一** | 新技能关键词与既有**启用**技能集合**无交集**；有交集须**显式裁决**（改写/拒绝） | **拒绝**或要求改写 |
**C4 价值不降** | 按 M2 价值分：合并后的期望值不得低于被取代者之和的**下限**（避免"合并掉了两个有用的"） | **拒绝** |
**C5 可回滚** | 被取代者一律 `SetEnabledAsync(false)`，**不删除** | **强制** |

⚠️ **C2 / C4 的冷启动**：无遥测时算不出 ⇒ 冷启动期这两条**降级为"记录但不阻断"**，且**禁止**把"无数据"当"低价值"（与 §13.4 同源）。

### 14.7 切片（接 G 轨道）

| 切片 | 内容 | 验收 |
|---|---|---|
**G6** | `skill.curate` 作业骨架 + 输出契约 + 组合变化报告（**先只报告，不动技能**） | 报告必须给出 `N_before → N_after` 与"未降原因"；同 bucket 重跑幂等；白名单/分派/配置项三处接入与既有作业同形 |
**G7** | 提炼产物契约 + C1–C5 门禁 | **五条各有一个"必须拒绝"的负例**；C1 用真实 provenance 校验；C3 复用关键词唯一性守卫 |
**G8** | 触发与节奏（定时 + 条件触发 + 非工作时段优先） | `enabled > softTarget` 时优先级确实提升；重活确实落在非工作时段 |

### 14.8 风险与诚实清单

| # | 风险 | 缓解 |
|---|---|---|
R1 | **提炼质量**：LLM 可能把不相关技能"提炼"到一起 | **只在家族内提炼**（M3 先分组）；跨家族合并需人工确认 |
R2 | **过度收敛**：一次砍太多（禁用可回滚，但**注入缺失是静默的**） | 每轮**淘汰上限**，如 `≤ max(2, ceil(N × 10%))` |
R3 | **两个作业抢同一批技能**（`improve_skills` vs `skill.curate`） | **同一技能不得同轮被两者处理**：作业级处理标记（复用 `dedup-reviewed:` / `self-evaluated:` 同类机制）或作业级租约 |
R4 | 整理作业自身变成新的堆积源（产出新的笔记式技能） | 由 14.5 的反笔记不变式 + C2 一般性判据机械阻挡 |
R5 | **未验证**：提炼后**实际是否更好用**（注入是否更精准） | 属 G2 遥测落地后才能回答；**在此之前不得宣称改进有效** |

**未验证（不编造）**：`improve_skills` 历史运行的 `operationCount=0` 是"确无可改进"还是"门禁过严"—— 需 G6 的组合变化报告才能区分。

---

## 15. 改进基础设施完整规划（总纲 · 2026-09-21 追加）

> **本节由用户指令驱动**：*「我们在 RSI 里面也讨论过，产生的信号、问题和规划实际上是对现有的更新和升级、进化，而不能简单地停留在 add 一个 SKILL 或者添加一个 Memory，需要反思、整理和整合。实际上这是一整套的基础设施。我建议先做完整的规划，然后继续推进。」*
> **阅读顺序**：本节是**总纲**；§13/§14 是其两个子领域，§4.0 是其算子层契约。
> **取证方式**：只读（代码核对 + 实盘盘点）；未编译、未跑测。

### 15.0 一句话命题

把「改进」从**散布的 add 通道**，升级为**一整套基础设施**：
> **任何改进，都是对既有资产的一次「受裁决的变更」**（更新 / 升级 / 合并 / 取代 / 淘汰），**而不是默认新增**。

### 15.1 用户命题的形式化（本规划的出发点）

**现状的错误默示**（两条 add 通道，各自独立）：

```
signal ──▶ plan ──▶ create(skill)            ← 技能侧：只增
signal ──▶ plan ──▶ add_chapter(memory)      ← 记忆侧：只增
```

**应有的默示**：

```
signal ──▶ ImprovementProposal{ target?, op, evidence[], expectedGain }
                │  op ∈ { Create | Update | Merge | Replace | Retire }
                ▼
        ┌─ 判据层（容量预算 / 阈值策略 / 证据校验 / 关键词唯一性）
        ▼
   ChangeVerdict{ decision, policyId+version, reasonCode, rollbackHandle }
                ▼
        apply（经既有消费通道；可回滚；默认先 shadow）
```

⭐ **最关键的一条**：`Create` 必须**携带证据说明"为什么不能 Update / Merge 既有资产"**。
这把「**add 不是默认**」从口号变成**字段级强制**（§15.5 C2）—— 也是本规划区别于"再做一层封装"的地方。

### 15.2 现状盘点：已有 vs 缺口（分层）

| 层 | 已有（含证据） | 缺口 |
|---|---|---|
**L1 算子** | 三原语契约 + `OperatorBase` + 投影基类 + 场景注册表 + 旁挂（S1a/S1b/S2a/S2b **已交付**）；工具审批适配器为首个真实消费者 | ⚠️ **打分器 / 判断器无生产消费者**；`IClassifierModel` **零实现** ⇒ 惰性抽象风险（§12.6 落差②） |
**L2 判据** | `ThresholdPolicy`（三区间）/ `AcceptanceThresholdPolicy`（单侧）/ 3 个 policy id 已接线；`classifier_status` 可观测 | 缺**组合容量预算**这一类判据（M1）；缺"变更前后对比"判据 |
**L3 落点** | **两条互不相通的 add 通道**：技能侧 `IAgentSkillEvolutionStore`（SkillEnforcer 消费它）；记忆侧 `IMemoryLibrary`（**已有 `SupersededByChapterId` / `Status` ⇒ 记忆侧本就支持"取代而非新增"**） | 缺**统一落点抽象**：目标句柄 / 版本 / 变更操作 / 证据 / 回滚句柄。两条通道 id 类型不同，无法用同一份提案描述 |
**L4 编排** | 潜意识作业队列（`auto_dream` / `extract_patterns` / `improve_skills`）+ 幂等键 `periodic:{jobType}:{ws}:{agent}:{bucket}` + 租约 | 缺「整理 / 提炼」作业（§14）；缺 Analyze→Plan→Implement 流水线（S5） |
**横切** | canonical 事件、用量账本、会话日志、审计、健康面 | **缺使用遥测**（技能命中 / 注入 / 结局）；缺信号汇总存储；缺统一定价/成本记账口径 |
**评测** | 五套件门禁脚本（S0 已可信化） | 结构化证据（TRX/JSON）+ 用例身份判据已就绪；仍缺「变更前后对照」的机械流程（待 S6 / L3-b） |

⭐⭐ **一处值得单独指出的发现**：**记忆侧已经实现了"更新/取代而非新增"**（`SupersededByChapterId` / `Status` / 降级为指针；本轮蜜糖把协议副本降级为指针即此模式）。
⇒ 所以 §15.4 的 `Update/Merge/Retire` **不是发明新语义，而是把记忆侧已有的成熟模式推广到技能侧与其它资产**。规划的合法性来自**已有先例**，不是设计者偏好。

### 15.3 目标架构（四层 + 一条横切）

```
[L4 编排层]  RSI 流水线（轨迹→信号→分析→规划→实施→评测） / 整理作业(skill.curate) / 潜意识调度
                    │  只依赖 L3 的「提案」契约
[L3 落点层]  ArtifactRef + ImprovementProposal + ApplyContract（可回滚）
                    │  必须复用既有消费通道（技能→IAgentSkillEvolutionStore；记忆→IMemoryLibrary）
[L2 判据层]  容量预算 / 阈值策略 / 证据校验 / 关键词唯一性守卫 / 变更前后对照
                    │  只依赖 L1 原语，不依赖任何具体 LLM
[L1 算子层]  打分器 / 判断器 / 分类器（同一内核三投影）+ 模型抽象（隔离 Jev）
────────────────────────────────────────────────────────────
横切（旁挂，裁决先于留痕）：使用遥测 / 信号汇总 / 审计 / 健康 / 成本
```

**层间纪律（可执行守卫，不是口头约定）**：

| 纪律 | 守卫 |
|---|---|
L1 不得出现领域标识（`Rsi` / `Goal` / `ToolApproval`） | ✅ **已有**反向依赖门禁 |
L1 内 `jev` 只出现在唯一适配器 | ✅ **已有**供应商隔离门禁 |
L2 不得依赖具体模型 / HTTP 客户端 | 待建（反射 + 引用检查） |
L3 不得绕过既有消费通道 | 待建（构造"旁路写入"负例） |
L4 不得直接调用 L1 的模型 | 待建 |

### 15.4 三条核心抽象（本规划要新建的东西）

**(A) `ArtifactRef`（资产句柄）** —— *"更新既有资产"的前提*
```
ArtifactRef { kind, id, version?, evidenceRefs[] }
kind ∈ { Skill | MemoryChapter | Rule | Doc | CodeFile }
```
现状两条通道用**不同 id 类型**，因此**无法用同一份提案描述**"更新这个技能"与"更新这条记忆"。

**(B) `ImprovementProposal`（改进提案）** —— *统一货币*
```
ImprovementProposal { target: ArtifactRef?, op, payload, evidence[], expectedGain, proposedBy, idempotencyKey }
op ∈ { Create | Update | Merge | Replace | Retire }
```
- `target` 为空**仅当** `op = Create`。
- ⭐ `Create` **必须**含 `whyNotUpdateOrMerge`（不可 Update/Merge 的证据），否则机械拒绝。

**(C) `ChangeVerdict`（变更裁决）** —— *判据的落点*
```
ChangeVerdict { decision, policyId, policyVersion, reasonCode, rollbackHandle? }
decision ∈ { Apply | ApplyShadow | Reject | Defer }
```
- **默认 `ApplyShadow`**：只记录"若实施会怎样"，不落盘（与 §8 S5 的"默认 shadow"一致）；
- `rollbackHandle` **由应用时产出**；`decision = Apply` 而缺句柄 ⇒ **拒绝**（C4）。

### 15.5 关键不变式（宪法；每条都要有测试）

| # | 不变式 | 守卫方式 | 状态 |
|---|---|---|---|
**C1** | 候选不得修改判定自己的标准 | 阈值一律来自策略对象 | ✅ 部分（S2a）；预算需扩展 |
**C2** | **Add 不是默认** | `Create` 缺 `whyNotUpdateOrMerge` ⇒ 机械拒绝 | 待建 |
**C3** | 落点必须复用既有消费通道 | 技能经 `IAgentSkillEvolutionStore`、记忆经 `IMemoryLibrary`；旁路写入负例 | 待建 |
**C4** | 一切变更可回滚 | 缺 `rollbackHandle` 即拒绝；删除改禁用 | 待建 |
**C5** | 无度量不调参 | 命中面 / 阈值类变更必须先有对照遥测 | 待建（依赖 G2） |
**C6** | 裁决先于留痕 | 审计失败不阻断裁决（只记 Warning） | ✅ 已有（须保持） |
**C7** | 默认 shadow | 新作业 / 新判据首次上线一律 shadow | 待建 |
**C8** | **系统必须能"降"** | 每类资产至少一条"减少"路径 | ⚠️ **现状违反**（技能侧只有"增"） |

> **C8 是本节最该被记住的一条**：一个**只能增**的系统，其"改进能力"在数学上等同于**累积**。§13/§14 的全部工作，本质都是在给 C8 补上通道。

### 15.6 完整切片计划（合并后的**单一**序列）

| 序 | 切片 | 轨道 | 状态 | 成本窗口 |
|---|---|---|---|---|
1 | **S0 评测门禁可信化** | 评测 | ✅ **已完成**（2026-09-21：判据=结构化证据+用例身份；`AllowedFailures` 旋钮已删除；必测未测⇒非零） | — |
2 | S1a/S1b/S2a/S2b 判定算子基础设施 | 算子 | ✅ **已交付** | — |
3 | **L3-a 提案模型最小契约**（`ArtifactRef` / `ImprovementProposal` / `ChangeVerdict`；纯类型 + 守卫测试，**零行为**） | 落点 | ✅ **已完成**（2026-09-21，测试 29/29） | — |
4 | **G1 组合盘点**（只读；含家族分布 + 索引 token 实测） | 治理 | ✅ **已完成**（2026-09-21，报告 `Docs/Reports/skill-portfolio-G1-2026-09-21.md`；结论见下方 blockquote） | — |
5 | **G2 使用遥测 + 价值打分**（打分器首个真实消费者，**无 LLM**） | 治理 | ✅ **已完成**（2026-09-22 复核：遥测 `Runtime/Services/Skills/Telemetry/*.cs` 3 文件 + 打分器 `Runtime/Services/Improvement/SkillValue/*.cs` 3 文件，`list_dir` 实测）⚠️ 遥测需一次**重启窗口**才插桩激活 | — |
6 | **G6 `skill.curate` 报告先行**（**用 L3-a 模型出报告**，零技能变更） | 整理 | 未做 | 非工作时段 |
7 | **G3 组合预算**（判断器） | 治理 | 未做 | 任意 |
8 | **G7 提炼契约 + C1–C5 门禁** | 整理 | 未做 | 非工作时段 |
9 | **G4 家族归类 + 家族内上限** | 治理 | 未做 | 非工作时段 |
10 | **G8 触发与节奏**（定时 + 条件触发 + 非工作时段优先） | 整理 | 未做 | 任意 |
11 | **G5 D1/D2 修复**（**必须在 G2 之后**：无度量不调参） | 治理 | 未做 | 非工作时段 |
12 | **L3-b 落点适配**（技能 → `IAgentSkillEvolutionStore`；记忆 → `IMemoryLibrary`） | 落点 | 未做 | 非工作时段 |
13 | **S3 RSI 轨迹源**（失败→纠偏→验证；**不改** `GetRecentSuccessfulAsync`） | RSI | ✅ **已交付 B1–B4**（2026-09-22，见 `Docs/Features/S3-轨迹源-实施规格-2026-09-21.md`）⛔ 本行原标“未做”系**本表过期**，不得据此重做 | — |
14 | **S4 `RsiClassifier` + 信号存储**（⛔ 开工前置：agent 隔离责任在调用方 ⇒ §15.9） | RSI | 未做 | 非工作时段 |
15 | **S5 Analyze → Plan → Implement**（默认 shadow） | RSI | 未做 | 非工作时段 |
16 | **S6 评测 + 灰度晋升 + 回退** | RSI | 未做 | 非工作时段 |
> ⚠️ **本表过期已修（2026-09-22 复核，父代理自跑取证；“表里写未做”是同一类缺陷）**：
> 上面第 5 行（G2）与第 13 行（S3）原标“未做”，与**磁盘事实相反**。这与本会话反复遇到的静默逃逸同族：
> **文档落后于现实 ⇒ 下一个人照表重做或漏做**。逐条证据：
>
> - **G2 = 已完成**：遥测 `Runtime/Services/Skills/Telemetry/*.cs`（3）+ 打分器 `Runtime/Services/Improvement/SkillValue/*.cs`（3）
>   均已落盘（`list_dir` 实测）。⚠️ 唯一未激活项：遥测需一次**重启窗口**才插桩生效
>   —— 未激活时 `D:\data\skill-usage` 不存在是**合法状态**，不得读成“技能从未被使用”。
> - **S3 = 已交付 B1–B4**：`Runtime/Services/Improvement/Rsi/*.cs`（6 文件：`IRsiTrajectorySource`/`RsiToolOutcome`/
>   `RsiToolOutcomeDeriver`/`RsiTrajectoryAssembler`/`RsiTrajectorySource`/`RsiTypes`）+ B2 数据访问接缝（Platform 侧，
>   由 `RsiTrajectoryDataAccessTests` 覆盖）；`~Rsi` **147/147** 且冻结项均有**变异取红**证据；S5/S6 裁决已回写规格附录 B.6。
> - **S3 的两个边界义务（属 T1 采集层，不在 S3 本片）**：①`excludeFromLearning` 排除义务（S5 裁决 (a2)）
>   ⇒ 已建卡 `405677ab36ca4595b79f0e91977294b2`；②水位幂等（键 = `sessionId + 已处理事件水位 + classifierVersion`）
>   ⇒ S3 规格 `:552` 已登记为 **S3/T1 边界未决项**（本轮复核确认该登记仍在，**不是漏项**）。
> - ⇒ **合并序列的下一个切片 = 第 6 项 `G6 skill.curate` 报告先行**（零技能变更；成本窗口 = 非工作时段）。

> **L3-a 实测（2026-09-21）**：实现 `Source/PuddingCore/Improvement/{ImprovementEnums,ArtifactRef,ImprovementProposal,ChangeVerdict}.cs`
> （命名空间 `PuddingCode.Improvement`，与 `PuddingCode.Operators` / `PuddingCode.Classification` 同惯例）；
> 契约测试 `Source/PuddingCoreTests/Improvement/ImprovementContractTests.cs` **29/29 通过**
> （`dotnet test --filter FullyQualifiedName~PuddingCoreTests.Improvement`）。
>
> **构造期强制清单（不靠调用方自觉）**：枚举 `Unknown=0` 与数值冻结；`ArtifactRef` 禁 `Unknown` 种类与空 id、
> 空白版本归一为 null、证据按**内容**比较（否则 record 自动相等对集合用引用相等会静默误判）；
> `ImprovementProposal` 的 `Create` 必带 `whyNotUpdateOrMerge`、非 Create 必带 target 且**禁**带该字段、
> `Update/Merge/Replace` 必带 payload、`Retire` 禁带 payload、证据非空、幂等键非空、`ExpectedGain` 禁 NaN/无穷；
> `ChangeVerdict` 的 `Apply` 必带 `rollbackHandle`、非 Apply **禁**带、`Unknown` 决策拒绝。
>
> **零行为**：不写技能、不写记忆、不调任何服务——所以本切片不触碰任何既有消费通道。
>
> **G1 实测（2026-09-21，只读报告 `Docs/Reports/skill-portfolio-G1-2026-09-21.md`）**：
> 技能 144（启用 139）；注入器**实际**关键词空间（`CollectKeywords` 口径，`SkillEnforcerService.cs:126`）为
> **2490 槽位 / 755 去重**，其中 **89.1% 是噪声**（D1 治理与溯源标签 824 / D2 工具名 957 / D3 名称分词 502，语义仅 280）；
> 被 ≥2 技能共享的关键词 165 个 ⇒ **1735 次注入机会被「先到先得」静默挤掉**（确定值，与顺序无关）。
>
> ⭐ **一项推翻假设的结果**：按**语义**关键词聚簇得到 **139 个家族、全部是单技能（零重叠）**；
> 而含噪声聚簇得到 **2 个家族、最大 138**。即：「技能重复」是噪声制造的幻象，
> **语义层面几乎没有可合并的标的**。
>
> ⇒ **优先级调整**：**G5（D1/D2/D3 去噪）与 G2（使用遥测）是主线**；
> **G4（家族重整）的预期收益必须下调** —— 没有重叠就没有可合并的族，先修噪声再谈合并。
>
> **G5-a 去噪影响评估（2026-09-21，只读预演：`TestScripts/report-skill-portfolio.ps1 -Mode denoise-impact`；报告 `Docs/Reports/skill-keyword-denoise-impact-2026-09-21.md`）**：
>
> | 口径 | 槽位 | 共享关键词 | 被挤掉机会 | 恢复 | 零关键词技能 |
> |------|------|------------|------------|------|--------------|
> | S0 现状 | 2490 | 165 | 1735 | 0 | 0 |
> | S1 去 D1（Tags 来源） | 1690 | 125 | 1089 | 646 | 0 |
> | S2 去 D1+D2 | 744 | 75 | 204 | 1531 | 0 |
> | S3 去 D1+D2+D3 | 269 | 0 | 0 | 1735 | 0 |
> | S4 仅显式 keywords 且去噪 | 173 | 0 | 0 | 1735 | **4** |
> | S5 仅显式 keywords 原样 | 1119 | 50 | 885 | 850 | 1 |
>
> ⚠️ **但 S2/S3 的「零关键词技能 = 0」是假安全（本条是对我自己上一轮结论的纠正）**：该指标只检查「是否还剩至少一个关键词」，
> 不检查「剩下的关键词会不会出现在真实输入里」。S2/S3 剩余的主要是 `Name` 全句与 `SkillId` 这类 slug，几乎永不命中 ⇒ 等于静默停用。
>
> ⭐ **重述**：1735 次被挤掉的机会本质是**共享关键词的归属未裁决**（`map[kw]` 先到先得），而不是「关键词太多」。
> ⇒ 正确动作更可能是**「给关键词定主」**（同一关键词只归一个技能 + 让被挤掉的技能改用自身语义关键词），而非删词。
>
> ⇒ **规划再次调整：G5 的实施前置条件是 G2 使用遥测** —— 必须先有「哪些关键词在真实命中」的事实，
> 才能判断删除会不会**切断现有注入路径**（若注入主要靠 D2 工具名命中，去 D2 就是比噪声更严重的能力回退）。
> **G2 提前为下一步，G5 改为 G2 的下游。**
>
> **方向修订（2026-09-21，用户指令；详文 `Docs/Features/技能加载与检索-渐进披露-遥测与负信号设计-2026-09-21.md`）**：
> ① 「技能会越来越多」⇒ 需要**渐进性披露（L1 索引 / L2 摘要 / L3 全文三层）+ 语义向量检索 + 动态加载**；
> ② RSI **必须输出负信号**（不用某技能反而更好）；③ 建立**索引 + 调用次数 + 成功率**遥测基础设施；
> ④ 定位：**SKILL 是烹饪书 / 作业指导手册**，随模型进化反而成为执行障碍 ⇒ 应测「判据 / 边界 / 反例」而非「步骤」。
>
> **清点结论（动手前先看有没有现成轮子）**：三件已有、一件需新建 ——
> ① 工具侧已有完整先例：`ToolExposurePlanner`（`DeferredToolCount`）+ `SearchToolsTool` 按需加载 + **fail-open**（`ToolDiscoveryTests`）；
> ② 技能侧已有远端检索/版本/血统：`SkillHubTool`（Search/Browse/Get/Install/PublishVersion/Lineage；`Stats` 仅 Hub 计数）；
> ③ 已有向量基础设施：`IEmbeddingService` + SQLite 向量列 + FTS5（`SessionChunkIndexer`）；
> ④ **唯一需新建：本地技能遥测**（`SkillEnforcerService` 当前零命中记录）。
>
> **内容层实测（新增，`-Mode content-audit`）**：139 个启用技能中 **133 个 auto-generated（95.7%）**、
> 135 个含「来源」段（会话回放）、**仅 11 个（7.9%）提到边界知识**、**125 个（89.9%）是「纯回放」**。
> ⇒ 能回答的一半有了：这批技能教的是「调哪几个工具」（模型本来就会），几乎不讲「什么时候不该用」（模型真正需要的）。
> **不能回答的一半**：是否真有价值 —— 无遥测无法区分「有用 / 从未命中 / 有负价值」。
>
> ⇒ **序列修订**：遥测升为独立切片 **T1**（原 G2 拆为 T1 记录 / T2 统计回填 / T3 负信号），
> 语义索引 **T4** → 渐进披露 **T5/T6** → 关键词「定主」**T7**（前置条件仍是 T1）。

**四条次序理由（为什么不能随便调）**：

1. **S0 必须最先**：门禁不可信 ⇒ **无法判断任何改进是否真的更好** ⇒ 后面每一步都在"移动球门"上跑（§7 R6）；
2. **G2 必须先于一切"调参"与"淘汰"**：无遥测 ⇒ 只能靠印象判断价值（违反 P2）；
3. **L3-a 必须早于 G6/S5**：G6 的输出契约（`N_before → N_after` + 未降原因）**本质就是一份提案报告**；S5 的实施阶段更需要提案货币。**先有货币，再有花钱的地方**；
4. **L3-b 晚于 G6**：先用"只报告"的零风险落点**验证提案模型好不好用**，再让它真的写盘。

### 15.7 与既有两条 add 通道的关系：**收敛，不推翻**

| 资产 | 既有通道（保持） | 本规划的动作 |
|---|---|---|
**技能** | `IAgentSkillEvolutionStore` → `AgentSkillFileService` → `SkillEnforcerService` | 作为 L3-b 的**第一个适配目标**；**不新建 store**（新建会让产物不可达，§12.2 C2） |
**记忆** | `IMemoryLibrary`（**已有 `SupersededByChapterId` / `Status`**） | **范本**：其"取代而非新增"模式推广到技能侧 |
**规则 / 代码 / 文档** | 暂无统一通道 | **本规划不涉足**（列为将来；`kind` 已预留） |

### 15.8 风险与未验证清单

| # | 项 | 说明 |
|---|---|---|
R1 | **L3 抽象可能抽早了** | 缓解：L3-a **只落类型与守卫测试、零行为**；且 G6 立刻成为真实消费者 |
R2 | **提案模型与实际资产形状不匹配** | 缓解：L3-a 之后先做 G6（只报告）再 L3-b（真写盘） |
R3 | **遥测成为热路径成本** | 非阻塞 + 批量 + 可失败；留痕失败不阻断注入 |
R4 | **整理作业自身变成新堆积源** | 由 §14.5 反笔记不变式 + C2 一般性判据机械阻挡 |
R5 | **两作业抢同一技能**（`improve_skills` vs `skill.curate`） | 作业级处理标记或租约（§14.8 R3） |
**未验证** | ① 技能侧是否已有"取代"语义（**本次未核对**，只在记忆侧核到 `SupersededByChapterId`）；② 索引 token 真实占比（未测）；③ 139 个启用技能里多少**从未被命中**（需 G2）；④ 门禁洞的**真实影响面**（未量化） |

**明确不做**（本规划边界）：模型训练；跨 Agent 技能分发（属 SkillHub 范围）；`Rule` / `CodeFile` 类落点的具体实现（仅预留 `kind`）。

---

### 15.9 S4 开工前置：**agent 隔离责任落在调用方**（2026-09-22 补记）

**为什么必须现在就写**：S6 裁决（附录 B.6.2）把 §6.6 改写为**三条可达证据**（①`S15` 逐字盖章 ②只按 `ConversationId` 取数
③`(ConversationId, CreatedAt)` 索引命中），并**诚实标注"改写后不再证明 agent 隔离本身"**。
⇒ 若不显式指派责任，agent 隔离就会变成**无人认领**的约束 —— 而这正是本会话已两次实测的失败形态：
**规格写了却无执行载体时，不会有任何断言失败**（B4 的 `O6`、附录的 `S5` 均如此）。

**与 S3 的关系（为什么不留在 S3）**：S3 只做**会话级**轨迹源，**不含 agent 维度**
（§2.6.2 / §2.9 两次独立裁决均禁止按 agent 扫描 turn）。⇒ 隔离只能在**上层**完成，即 **T1 采集层 / S4 调用点**。

#### S4 冻结验收（四条，**原第 1 条之外的三条为本补充新增**）

| # | 验收项 | 判据 |
|---|---|---|
**①** | **换模型实现不改 RSI 代码**（原有） | 用**假模型**（`IClassifierModel` 的测试替身）证明：替换模型实现后 RSI 侧**零改动** |
**②** | **agent 隔离责任落在调用方**（新增） | `RsiClassifier` **自身不做** agent 隔离；**调用方必须传入与会话一致的 `scope`**（`workspaceId` + `agentInstanceId`）。用例必须断言：**跨 agent 的输入不会混入同一 scope 的信号** |
**③** | **信号可复现**（§4.7 存储契约，新增为验收项） | 每条信号**必须**带 `classifierVersion` + `sceneKey` + `sourceEventIds`；缺任一 ⇒ 不可复现/不可回放/不可对账 ⇒ 用例必红 |
**④** | **触发幂等**（§4.9，新增为验收项） | 幂等键 = `sessionId + 已处理事件水位 + classifierVersion`；内容未变 ⇒ 复用"无需分析"判定。⚠️ 该键与 **T1 的边界义务同源**（S3 规格 `:552` 已登记）⇒ 落地时**不要两处各写一份** |

#### 三条纪律

1. **⛔ 不得把 agent 隔离实现成"分类器内部默认按当前会话过滤"** —— 那是把调用方的责任偷偷搬进被调用方，
   会让隔离在**多 agent 并发**时静默失效（分类器没有会话上下文，它**只能**相信传入的 `scope`）。
2. **失败方向必须是 fail-closed**：`scope` 缺失/不一致时，**记信号缺失并拒绝产出**，
   ⛔ 不得静默当作正常 signal（与 §4.9「分类器判定失败 ⇒ 记信号缺失，**不得**静默当 positive」同源）。
3. **本补记不改变 S4 的位置**：S4 仍在合并序列**第 14 位**，⛔ 不得据此提前开工（G6/G3/G7/G4/G8/G5/L3-b 均在其前）。

> **溯源**：本补充由父代理在 G6 派发后的等待窗口写入（2026-09-22），依据为 §4.7 存储契约、§4.9 触发与幂等、
> §6.6（S6 裁决改写版）、S3 规格 `:552`（水位幂等归属 T1）、以及 S3 的 §2.6.2 / §2.9 两次 agent 维度裁决。
