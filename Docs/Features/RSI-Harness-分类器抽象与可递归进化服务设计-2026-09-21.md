# RSI-Harness：分类器抽象与可递归进化服务设计（讨论稿）

| 属性 | 值 |
|------|-----|
| 日期 | 2026-09-21 |
| 状态 | **讨论稿**（未评审、未实现） |
| 作者 | 通用助手 |
| 关联 | ADR-064（仅 Skill 自进化）、`Docs/Reports/PuddingAgent夜间效率与RSI评估-2026-09-21.md`、安全分类器部署手册 |
| 取证基线 | HEAD `8ed4c5b`（工作区干净） |

> 本文所有 `文件:行号` 来自静态盘点（未编译、未跑测）；若并行协作改动工作区，行号可能漂移。

---

> **文档定位（2026-09-21 修订）**：本文档同时是**判定基础设施（三原语：打分器 / 判断器 / 分类器）的母文档**，RSI-Harness 是它的第一个大型消费者。三原语设计见 **§4.0**。

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
**S0（阻塞）** | 评测门禁可信化：判据改为 **case 身份 + 输入指纹**；必测未测 ⇒ **非零退出**；TRX/Jest JSON 机器可读 | 用**假 PASS 负例**（"修一红换一新红"、"全部未测"）证明旧脚本误放、新脚本拦住 |
| **S1a** | 抽 `OperatorBase` + 三原语契约（`IScorer` / `IJudge` / `IClassifier`）+ `JudgementEnvelope` + `ThresholdPolicy`；**只落契约与基类，不接模型** | 契约单测：三投影读同一信封；阈值外置；`Abstain` 不被折叠；**反射守卫**（场景子类只允许覆盖 `ClassifyCoreAsync`）；**反向依赖守卫**（基础设施不出现 RSI/Goal/ToolApproval 标识）。**实施规格已冻结**：`Docs/Features/S1a-判定算子基础设施-实施规格-2026-09-21.md` |
| **S1b** | 场景注册表 + 工具审批改为第一个适配者 | **行为零变更**：Runtime 1659 / Platform 1363 全绿；新增"场景键路由"用例；**Jev 隔离守卫测试**上线（§4.0.6） |
**S2** | 泛化旁挂（audit / rule / health 按场景分区）；**保留"裁决先于留痕"** | 审计失败不影响裁决 + 有 Warning（已有用例须继续绿） |
**S3** | 轨迹片段源 + 结构化字段 + 水位幂等 | 单测：不投喂正文/CoT；水位不变则不重复分析 |
**S4** | `RsiClassifier`（经 `IClassifierModel` 接 Jev）+ `Signal` 存储 | 换模型实现不改 RSI 代码（用假模型证明） |
**S5** | Analyze → Plan → Implement 流水线（**默认 shadow**） | "无反例预测不进 Plan"的机械闸门有测试 |
**S6** | Evaluate + 灰度晋升 + 回退 | 负例注射自证；回退路径有验收；C1–C7 各有用例 |

---

## 9. 待决策项

| # | 问题 | 我的建议 |
|---|---|---|
D1 | 基类落点：`PuddingCore/Classification` 还是新建 `PuddingCore/Classifiers`？ | 先复核 §7 R7 的命名空间异常，再定；**倾向留在 `PuddingCore`**（避免新工程） |
D2 | 5 个 verdict 的收敛节奏：立即统一 vs 单向收敛 | **单向收敛**（新场景用新核，旧场景按需迁移） |
D3 | RSI 触发点是否绑压缩 | **不绑**：会话结束 ∪ 压缩后，幂等水位去重 |
D4 | 是否允许 RSI 自主 `Implement`（改代码） | 建议 **Phase 1 只到 Plan**，Implement 仅对 Skill/规则类候选开放；改产品代码需人审 | D5 | 场景注册表配置键命名 | `Classifiers:{sceneKey}:*`，与既有 `ToolApproval:*` 并存 |
| D6 | 判断器是否**三值**（Yes/No/Abstain）而非布尔+置信度 | **建议三值**：低置信区间就是弃权区；否则要么退化成「不敢判就拒绝」，要么退化成「沉默放行」（§4.0.5） |
| D7 | `confidence` 是否允许下游当概率用 | **未校准前禁止**（`confidenceKind=ModelSelfReported`）；RSI 晋升走样本量/序贯检验/灰度，不比 confidence 大小（§4.0.4） |
| D8 | 基础设施内部是否允许 2 层（`OperatorBase` → 投影基类） | **允许**（职责互斥、可变点唯一）；**场景层严格 1 层**，禁止场景算子互继承（§4.3.1 / §4.3.2） |
