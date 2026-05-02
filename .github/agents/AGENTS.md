# Pudding 多 Agent 协作系统

## 核心理念

**少即是多 + 28 原则**。每个 Agent 必须有不可替代的价值；开发资源按复杂度分梯队，默认走低成本档位，仅在必要时升级。

## 可用模型池（2026-04-23 更新）

> ⚠️ **Claude Opus 4.6 当前不可用**，原占用该模型的 agent 已重新分配。
> 三款新增模型已通过 [基准测试](../../temp/benchmark/REPORT.md) 评估并按其专长落位。

| 模型 | 费率 | 定位 | 备注 |
|------|------|------|------|
| Claude Opus 4.7 (copilot) | x7.5 | 顶级推理，架构/核心算法/不可逆变更 | super-dev 专用 |
| Claude Sonnet 4.6 (copilot) | x1 | 通用审阅 | qa-sonnet 专用（审查 Codex 代码） |
| Claude Haiku 4.5 (copilot) | x0.33 | 极速探索（量大管饱） | explore 专用（读大量代码固定费率最省） |
| GPT-5.5 (copilot) | x7.5 | 长程推理/大上下文 | architect + user-agent 专用 |
| GPT-5.3-Codex (copilot) | - | 代码生成+审阅 | dev + qa 专用（审查非 Codex 代码） |
| Gemini-3.1-Pro-Preview (copilot) | 按量 | 代码生成/UI设计 | ui-designer 专用（MiMo Coding Bench 77.1 最高） |
| **DeepSeek-V4-Pro (gcmp.deepseek)** | TBD | 强推理/中文原生；编排与规划入口 | lead 专用 |
| **GLM-5.1 (按量计费) (gcmp.zhipu)** | TBD | 注释/可解释性最强；安全审查 + QA 二审 | 基准测试总分 88 |
| **Kimi-K2.6 (gcmp.moonshot)** | TBD | 代码质量最严谨；文档与规范审查 | 基准测试总分 90，最高 |
| **MiniMax-M2.7 (gcmp.minimax)** | TBD | 速度碾压（10×）；简单开发 | lightweight-developer 专用（占比 50%，速度优先） |



目前，我们有2种类型的计费模型的模型，一种是按照请求次数计费，例如使用7.5x标注，也就是每次请求消耗7.5请求次数，copilot por+套餐提供了2000次的请求额度，超过之后按照请求量计费，一种是按照tokens计费。

比如，使用gpt-5.5完成一次简单的高频的请求，是比较吃亏的，因为它的单次请求消耗就比较高，而使用按量计费的就比较便宜，因为与请求次数无关。

如果需要长程任务或者需要大的上下文的，那么使用gpt-5.5可能会比较划算，因为它的上下文能力更强，可以一次性处理更多的信息，减少请求次数。

其次，lead应该知晓什么时候使用高费率模型，什么时候使用按量计费的模型。




## Agent 清单

| # | Agent | 模型 | 费率 | 一句话职责 |
|---|-------|------|------|-----------|
| 1 | @lead | **DeepSeek-V4-Pro (按量计费)** | TBD | 用户唯一入口，意图分析、规划、编排、交付 |
| 2 | @pm | **Kimi-K2.6 (按量计费)** | TBD | 任务拆解、DoR 门禁、进度追踪 |
| 3 | @explore | **Claude Haiku 4.5** | x0.33 | 只读代码/日志探索，**量大管饱固定费率** |
| 4 | @architect | GPT-5.5 | x7.5 | 架构决策、影响面评估、Map.yaml 维护（**只出方案**） |
| 5 | @super-dev | **Claude Opus 4.7** | **x7.5** | **架构级实现、跨 3+ 模块、密码学核心、并发关键、不可逆变更** |
| 6 | @dev | **GPT-5.3-Codex** | x1 | 1-2 模块复杂逻辑、中等风险、TDD 全流程 |
| 7 | @lightweight-developer | **MiniMax-M2.7 (按量计费)** | TBD | 简单功能、低风险修复、样板代码（**速度优先**） |
| 8 | @qa | **GPT-5.3-Codex** | 免费 | 独立审阅（审查 MiniMax/Claude 开发代码）；GLM-5.1 二审 |
| 9 | @qa-sonnet | **Claude Sonnet 4.6** | x1 | 独立审阅（**专门审查 GPT-5.3-Codex 开发代码**）；GLM-5.1 二审 |
| 10 | @security-reviewer | **GLM-5.1 (按量计费)** | TBD | 安全漏洞识别、密码学合规检查（注释/解释力强） |
| 11 | @integration-debugger | **DeepSeek-V4-Pro (按量计费)** | TBD | 跨模块故障复现与根因定位 |
| 12 | @doc | **Kimi-K2.6 (按量计费)** | TBD | 文档同步、日志维护、经验沉淀（结构最严谨） |
| 13 | @ui-designer | Gemini-3.1-Pro-Preview | x1 | UI/UX 设计评审、界面一致性 |
| 14 | @crypto-evaluation-expert | **Kimi-K2.6 (按量计费)** | TBD | 密评规范审查（长文本规范理解） |
| 15 | @user-agent | GPT-5.5 | x7.5 | 模拟挑剔的测评工程师体验软件 |

### v2.8 变更（2026-04-29，双 QA 机制）
- **新增 @qa-sonnet Agent（Claude Sonnet 4.6）**：专门审查 GPT-5.3-Codex 开发的代码
- **@qa 切换回 GPT-5.3-Codex**：审查 MiniMax-M2.7 / Claude Opus 4.7 开发的代码
- **双 QA 分流规则**：Codex 开发 → Sonnet 审查；MiniMax/Claude 开发 → Codex 审查。杜绝同模型自审
- **Agent 总数 14 → 15**

### v2.7 变更（2026-04-29，UI/UA 调整）
- **`@ui-designer` 从 Sonnet 4.6 切换为 Gemini-3.1-Pro-Preview**：MiMo Coding Bench 77.1 全场最高，纯代码生成/UI 设计能力突出
- **`@user-agent` 从 DeepSeek-V4-Pro 切换为 GPT-5.5**：长程推理强（HLE 58.7 第一），模拟挑剔用户需批判性思维+大上下文一次消化整份规范

### v2.6 变更（2026-04-29，模型重新洗牌）
- **`@dev` 从 Sonnet 4.6 切换为 GPT-5.3-Codex**：代码生成天然场景，免费不消耗额度
- **`@qa` 主审从 GPT-5.3-Codex 切换为 Sonnet 4.6**：原 dev 模型释放到审阅角色，代码审阅经验丰富；GLM-5.1 二审不变
- **`@lightweight-developer` 从 MiMo-V2.5-Pro 切换为 MiniMax-M2.7**：简单 CRUD/样板代码速度优先（10×），按量计费极便宜；MiMo 的数学/推理强项在简单任务中无法发挥
- **`@explore` 从 MiniMax-M2.7 切换为 Claude Haiku 4.5 (x0.33)**：探索读大量代码，固定费率比按 tokens 计费更省；"量大管饱"策略
- **MiMo-V2.5-Pro 移出模型池**：中文弱项（C-Eval 91.5 第4）、常识推理弱项（HellaSwag 89.8）与中文项目高频角色不匹配；按量(¥7/21)成本较高

### v2.5 变更（2026-04-29，MiMo-V2.5-Pro 入列）
- **`@lightweight-developer` 从 GLM-5.1 切换为 MiMo-V2.5-Pro**：τ3-bench 72.9（Agent/工具调用并列第一）、SWE-bench 57.2（仅差 Opus 0.1），按量计费（输入¥7/M、输出¥21/M）；占比 50% 的最高频角色，Agent/工具调用能力最强

### v2.4 变更（2026-04-29，模型选型优化）
- **`@pm` 从 GPT-5.5 切换为 Kimi-K2.6**：任务拆解/DoR 是结构化工作，Kimi 结构最严谨（基准 90 分），按量计费对高频角色更经济
- **`@integration-debugger` 从 GPT-5.4 切换为 DeepSeek-V4-Pro**：修复不存在的模型引用；排障需逻辑推理+中文日志理解，DeepSeek 两项都强且按量计费对低频角色友好
- **`@user-agent` 从 GPT-5.4 切换为 DeepSeek-V4-Pro**：修复不存在的模型引用；模拟中国测评工程师天然契合中文模型语感
- **修复 AGENTS.md 汇总表中的模型名**：统一使用 `(vendor)` 格式，消除 `glm-5.1-billing` / `GPT-5.4` 等非标准名称
- **修正开发梯队描述**：`@lightweight-developer` 实际使用 GLM-5.1（按量计费），非 Sonnet 4.6

### v2.3 变更（2026-04-27，lead 模型切换）
- **`@lead` 切换为 DeepSeek-V4-Pro**：强推理 + 中文原生，更适合意图分析与编排规划；Claude Sonnet 4.6 专注 dev/lightweight-developer/ui-designer

### v2.2 变更（2026-04-23，Opus 4.6 退场）
- **Opus 4.6 全面下线**：`@dev` 改为 Claude Sonnet 4.6（x1）
- **新模型按基准测试结果落位**（详见 [REPORT.md](../../temp/benchmark/REPORT.md)）：
  - **Kimi-K2.6**（总分 90）→ `@doc`、`@crypto-evaluation-expert`：代码/文档结构最严谨
  - **GLM-5.1**（总分 88）→ `@security-reviewer`、`@qa` 二审：注释/可解释性最强
  - **MiniMax-M2.7**（总分 86，速度 10×）→ `@explore`：只读探索对速度最敏感
- **Claude Haiku 4.5 退出生产路径**：原 `@explore` 角色由 MiniMax 接管
- **`@user-agent` 升级为 GPT-5.4**：测评视角更挑剔
- **`@ui-designer` 改为 Sonnet 4.6**：与 dev 链路对齐，便于一体化迭代

---

## 开发梯队分流（28 原则）

| 占比目标 | 梯队 | 触发条件 |
|---------|------|---------|
| ~50% | `@lightweight-developer` (MiniMax-M2.7 / 按量计费) | 单/少文件、样板、UI 文案、简单 CRUD |
| ~35% | `@dev` (GPT-5.3-Codex / 免费) | 1-2 模块复杂逻辑、中等风险修复、TDD |
| ~15% | `@super-dev` (Opus 4.7 / x7.5) | 跨 3+ 模块 / 核心算法 / 密码学核心 / 并发关键 / 不可逆变更 |

**升级链**：`@lightweight-developer` → `@dev` → `@super-dev`，**禁止跳级降级**（避免硬扛或浪费）。
**与 @architect 协作**：架构决策由 `@architect` 出方案，`@super-dev` 负责执行；`@super-dev` 不替代架构决策权。

---

## 工作流（v2.3 强制前置流程）

### 核心原则
> **所有"动手类"任务（编码 / 修 bug / 重构 / 配置变更）必须经过 5 个固定阶段，禁止跳过。**
> 纯查询、纯文档、紧急热修复有专门的例外路径（见后）。

### 五阶段标准流程

```
┌─────────────────────────────────────────────────────────────────┐
│  0. 入口：@lead 意图识别 → @pm 建任务卡（含 DoR）                │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  1. 探索 @explore（强制前置）                                    │
│     - 现状代码结构、依赖、相关日志、历史经验                     │
│     - 输出：影响面清单、相关文件列表、关键约束                   │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  2. 方案 @architect（条件触发）                                  │
│     触发条件（满足任一即必需）：                                 │
│       • 涉及模块/文件 ≥ 3 个                                     │
│       • 跨层修改、引入新模块、不可逆变更                         │
│       • 数据库 schema、契约接口、并发模型变更                    │
│     输出必须包含：                                               │
│       ① 技术方案（含备选与权衡）                                 │
│       ② **工作量评估**（人天 / 复杂度等级）                      │
│       ③ **阶段拆分**（是否分多阶段、是否需多个 dev 并行）        │
│       ④ 风险与回滚                                               │
│     UI/UX 相关：必须并行触发 @ui-designer 出设计 +              │
│                 @user-agent 模拟用户视角提意见                   │
│     安全/密码学相关：必须并行触发 @security-reviewer +          │
│                       @crypto-evaluation-expert 提前介入         │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  3. 实施（按 28 原则分流）                                       │
│     • 单/少文件、低风险           → @lightweight-developer │
│     • 1-2 模块、中等复杂           → @dev           │
│     • 3+ 模块/核心算法/不可逆      → @super-dev     │
│     多阶段任务由 @pm 协调多个 dev 并行/串行执行                  │
│     联调故障：@integration-debugger 介入定位根因后再回开发       │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  4. QA 强制独立审阅（禁止开发者自审）                       │
│     双 QA 分流（杜绝同模型自审）：                             │
│     • @dev (GPT-5.3-Codex) 开发   → @qa-sonnet (Sonnet 4.6)  │
│     • @lightweight-developer (MiniMax) 开发 → @qa (GPT-5.3-Codex) │
│     • @super-dev (Claude Opus) 开发 → @qa (GPT-5.3-Codex)    │
│     二审：GLM-5.1（注释/文档/可解释性维度）                    │
│     安全敏感：+ @security-reviewer                             │
│     密评相关：+ @crypto-evaluation-expert                      │
│     用户体验关键：+ @user-agent 终验                           │
│     FAIL → 回原开发者修复 → 再次 QA（不跳级）                    │
└─────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────┐
│  5. 归档 @doc（强制收尾，禁止跳过）                              │
│     • 同步 Context.md / Tasks.md / Map.yaml / 设计文档          │
│     • **核对现有文档与新代码是否冲突**（防止文档与代码打架）     │
│     • 已过时的文档必须更新或标注 [DEPRECATED]                    │
│     • 经验沉淀到 Doc/Memory/Self-reflection.md                   │
│     • 任务卡置 DONE                                              │
└─────────────────────────────────────────────────────────────────┘
```

### 例外路径（仅以下场景可绕过部分前置）

| 场景 | 简化流程 | 必须保留 |
|------|---------|---------|
| 纯查询 / 信息获取 | @lead 直答 或 @explore | 无 |
| 纯文档维护 | @doc | doc 自检 |
| Typo / 文案 / 常量微调 | @lightweight-developer → @qa → @doc | qa + doc |
| 紧急热修复（线上故障） | @integration-debugger → @dev/@super-dev → @qa | qa；doc 24h 内补 |
| 跨模块故障定位 | @integration-debugger → 走标准 5 阶段 | 全部 |

### 阶段触发矩阵（@lead 决策依据）

| 维度 | 走标准 5 阶段 | 走例外路径 |
|------|--------------|-----------|
| 涉及文件数 | ≥ 2 | 1 且非核心 |
| 是否跨模块 | 是 | 否 |
| 是否改 schema/契约 | 是 | 否 |
| 是否影响 UI/UX | 是 | 否 |
| 是否安全/密码学 | 是 | 否 |
| 是否不可逆 | 是 | 否 |
| 风险等级 | 中/高 | 极低 |

> **任一维度命中"标准"即走 5 阶段**；只有全部命中"例外"才允许简化。

### 阶段强制门禁（缺失即阻断）

| 门禁 | 检查项 | 责任人 | 缺失后果 |
|------|--------|--------|---------|
| 探索 → 方案/实施 | explore 输出的影响面清单 | @lead | 退回 explore |
| 方案 → 实施 | 工作量评估 + 阶段拆分（≥3 模块时） | @architect | 退回 architect |
| 方案 → 实施（UI 类） | ui-designer 设计稿 + user-agent 反馈 | @lead | 退回方案阶段 |
| 实施 → QA | DoD 完整 + 测试通过 | 开发者 | 拒收 |
| QA → doc | QA 报告 PASS | @qa | 阻止 doc |
| doc 完成 | 文档与代码一致性核对 | @doc | 任务不可关闭 |

---

## 升级链交接（开发梯队内部）
| 交接 | 检查项 | 责任人 |
|------|--------|--------|
| pm → 开发梯队 | DoR 完整 + 复杂度评级 | pm |
| lightweight-developer → dev | 升级原因明确（复杂度超出） | lightweight-developer |
| dev → super-dev | 升级原因明确 + architect 方案已就绪 | dev |

---

## 项目经验注入

### 架构约束
- 依赖方向：UI → Application → Core/Domain → Infrastructure，禁止逆向
- - - - 新 Report 实体必须同步登记 ProjectDbContext 和 ProjectSQliteContext 的 DbSet

### 跨语言/跨进程
- C# long → JavaScript 需字符串传递
- WebView2 postMessage 直接传对象，不 JSON.stringify
- 第三方 DOM 库切换数据时优先测试引擎完整重建路径
- ---

最后更新: 2026-04-29
系统版本: v2.8 (双 QA：@qa Codex 审查 MiniMax/Claude，@qa-sonnet Sonnet 审查 Codex；杜绝同模型自审)

