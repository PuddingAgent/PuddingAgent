# HappyDog 15-Agent 系统概览

## 核心架构

HappyDog 项目采用 **多 Agent 协作架构**，每个 Agent 专注特定领域，通过清晰的交接流程（handoffs）实现高效的团队协作。

## Agent 清单与职责

### 1. **LEAD** — 团队编排者 (Claude Opus 4.6)
- **职责**: 统一入口、意图分析、工作流编排
- **何时使用**: 任何用户请求的第一步
- **可交接**: pm, explore, planner, feature-developer, dev, qa, ui-designer, user-agent, crypto-evaluation-expert
- **核心能力**: 意图路由、流程编排、阶段门禁检查

### 2. **PM** — 项目管理 (GPT-5.4)
- **职责**: 任务拆解、DoR 补全、优先级排序、进度追踪
- **何时使用**: 新需求拆解、进度查询、阻塞协调
- **可交接**: explore (收集背景), planner (制定方案)
- **禁止**: 编写代码、跳过 DoR 让任务开工

### 3. **EXPLORE** — 代码探索 (Claude Haiku 4.5)
- **职责**: 代码库搜索、日志线索提取、依赖关系分析、背景收集
- **何时使用**: 需要理解现有代码结构、查询实现示例、从日志中找异常线索时
- **可交接**: planner (制定方案), integration-debugger (继续排障)
- **特点**: 快速/中等/彻底三种深度探索，支持日志目录只读分析

### 4. **PLANNER** — 规划专家 (GPT-5.4)
- **职责**: 制定详细实施方案、工作量评估、风险预案
- **何时使用**: 理解背景后需要规划实施步骤
- **可交接**: dev (开始编码)
- **输出**: 分步实施计划、关键文件清单、风险评估表

### 5. **DEV** — 核心开发 (Claude Opus 4.6)
- **职责**: 代码编写、单元测试、集成测试、代码交付
- **何时使用**: 复杂逻辑、核心模块、跨模块、高风险开发任务
- **可交接**: qa (审阅), doc (同步文档)
- **依循**: CLAUDE.md 完整工作流

### 6. **FEATURE-DEVELOPER** — 特性开发 (Claude Sonnet 4.6)
- **职责**: 编写简单代码、小型功能、样板代码、低风险修复
- **何时使用**: 简单表单、CRUD、局部 UI 调整、单点修复
- **可交接**: dev (复杂度升级), qa (审阅), doc (同步文档)
- **边界**: 遇到复杂逻辑/跨模块改动立即升级到核心开发

### 7. **TESTER** — TDD 交付 (Claude Sonnet 4.6)
- **职责**: 编写失败测试、测试文档、测试交接
- **何时使用**: 需要先编写规范化测试（TDD）
- **可交接**: dev (编码实现)
- **方法**: 红→绿→重构流程

### 8. **QA** — 独立审阅 (GPT-5.3-Codex)
- **职责**: 代码审查、测试验证、QA 报告、文档检查
- **何时使用**: 代码交付后进行独立审阅
- **可交接**: security-reviewer (安全审查), doc (同步文档)
- **禁止**: 自己审自己的代码

### 9. **SECURITY-REVIEWER** — 安全专家 (GPT-5.3-Codex)
- **职责**: 安全漏洞识别、安全改进建议、密码学合规检查
- **何时使用**: QA 审阅后的深度安全审查
- **可交接**: qa (反馈)
- **聚焦**: 密码学、数据保护、认证授权、注入风险

### 10. **INTEGRATION-DEBUGGER** — 集成排障专家 (GPT-5.4)
- **职责**: 复现跨模块问题、定位根因、梳理调用链、给出修复建议与验证路径
- **何时使用**: UI/服务/消息/数据库/构建链路出现联调故障、偶发异常、环境相关问题时
- **可交接**: dev (修复), architect (架构问题), qa (复测)
- **聚焦**: WPF/Avalonia/WebServer/ZeroMQ/EF Core/Docker 等跨边界排障

### 11. **USER-AGENT** — 最终用户代理 (Claude Sonnet 4.6)
- **职责**: 扮演挑剔的密评工程师，模拟真实测评活动使用软件并提出批评与建议
- **何时使用**: 需要验证工具是否真的好用、是否符合测评工程师工作习惯时
- **可交接**: ui-designer (体验优化), dev (实现修复), pm (需求与优先级整理)
- **聚焦**: 工作流顺手程度、UI可用性、结果可信度、术语理解成本

### 12. **CRYPTO-EVALUATION-EXPERT** — 商用密码应用安全性评估专家 (GPT-5.4)
- **职责**: 依据 Doc/PDF 规范审查代码设计、UI、量化评估逻辑、报告术语和结论
- **何时使用**: 需要判断实现是否偏离密评规范、FAQ 和量化规则时
- **可交接**: qa (综合审查), architect (架构调整), dev (实现修正)
- **聚焦**: D/A/K 维度、测评对象识别、量化评分、合规性判断、证据边界

### 13. **ARCHITECT** — 架构顾问 (GPT-5.4)
- **职责**: 架构决策、影响面评估、技术选型、Map.yaml 维护
- **何时使用**: 涉及架构变更、新模块、跨模块改动
- **可交接**: explore (探索影响代码)
- **输出**: 架构决策记录 (ADR)

### 14. **DOC** — 文档维护 (Claude Haiku 4.5)
- **职责**: 文档同步、一致性检查、日志、反思
- **何时使用**: 交付完成后同步文档
- **维护**: Context.md, Tasks.md, Map.yaml, Skills.md, 日志, 反思
- **原则**: 文档为单一事实源

### 15. **UI-DESIGNER** — 首席 UI/UX 设计顾问 (GPT-5.4)
- **职责**: 评估界面交互风格、维持界面一致性、改善 UI/UX 体验
- **何时使用**: 新增/修改界面、UI 审阅、交互优化、设计系统维护
- **可交接**: dev (实现设计), explore (探索前端组件)
- **技能**: `/ui-ux-pro-max`、`Doc/UI-Guidelines.md`

## 典型工作流

### 功能开发（完整流程）
```
用户请求
    ↓
[LEAD]  分析意图，确定复杂度
    ↓
[PM]    拆解需求 → 补全 DoR → 创建任务卡
    ↓
[ARCHITECT] (如需要) 评估架构影响
    ↓
[EXPLORE]   (如需要) 探索现有代码
    ↓
[PLANNER]   制定详细实施方案
    ↓
任务就绪（DoR 通过）
    ↓
[FEATURE-DEVELOPER / DEV]  按复杂度选择特性开发或核心开发
    ↓
[QA]        独立代码审阅
    ├─ PASS → [DOC] 同步文档 → 完成 ✓
    ├─ PASS_WITH_NOTES → [DEV] 修复 → 回到 QA
    └─ FAIL → [DEV] 修复 → 回到 QA
```

### TDD 流程（测试驱动开发）
```
[TESTER]    编写失败的测试套件
    ↓
[DEV]       编码使测试通过
    ↓
[DEV]       重构和优化
    ↓
[QA]        审阅测试和实现
    ↓
[SECURITY-REVIEWER] 安全审查
    ↓
[DOC]       同步文档
```

### 快速修复流程
```
bug 报告
    ↓
[FEATURE-DEVELOPER] 简单修复 + 测试
    ↓
[QA]    快速审阅
    ↓
[DOC]   文档后补
```

### 集成排障流程
```
运行时故障 / 联调异常 / 偶发问题
    ↓
[LEAD]  判断是否为跨边界问题
    ↓
[INTEGRATION-DEBUGGER]  复现问题 → 缩小范围 → 定位根因
    ↓
    ├─ 代码缺陷 → [DEV] 修复 → [QA] 复测 → [DOC]
    ├─ 架构缺陷 → [ARCHITECT] 评估方案 → [DEV] 修复
    └─ 环境/配置问题 → [DOC] 沉淀排障说明
```

### 用户体验评审流程
```
需求已实现 / 界面初版可用
    ↓
[USER-AGENT] 以密评工程师视角试用并挑刺
    ↓
    ├─ 界面/交互问题 → [UI-DESIGNER] 设计修正 → [DEV] 实现
    ├─ 工作流问题 → [PM] 调整任务与验收标准 → [DEV]
    └─ 结果可信度问题 → [CRYPTO-EVALUATION-EXPERT] 复核规范性
```

### 规范一致性评审流程
```
代码 / UI / 报告 / 计算逻辑
    ↓
[CRYPTO-EVALUATION-EXPERT] 对照 Doc/PDF 规范审查
    ↓
    ├─ 架构/设计偏差 → [ARCHITECT]
    ├─ 实现偏差 → [DEV]
    └─ 综合质量结论 → [QA]
```

## Agent 间通信

### Handoff 机制
每个 Agent 可以通过 `handoffs` 字段将工作交接给其他 Agent：

```yaml
handoffs:
  - label: "下一步操作"
    agent: target_agent
    prompt: "具体指示"
    send: false    # false=需要用户确认，true=自动发送
```

### 交接检查点

| 交接点 | 检查项 | 责任人 |
|-------|-------|-------|
| PM → FEATURE-DEVELOPER/DEV | DoR 完整性 | PM |
| FEATURE-DEVELOPER/DEV → QA | DoD 完整性 | 开发者 |
| QA → DOC | QA 报告完毕 | QA |
| QA → 完成 | 无重大风险 | QA |

## 模型选择策略

| Agent | 模型 | 理由 |
|-------|------|------|
| LEAD | Claude Opus 4.6 | 强编排与复杂问题理解，适合作为统一入口 |
| PM | GPT-5.4 | 结构化规划，任务管理 |
| EXPLORE | Claude Haiku 4.5 | 快速搜索，代码理解 |
| PLANNER | GPT-5.4 | 深度分析，方案生成 |
| DEV | Claude Opus 4.6 | 核心逻辑、复杂实现、高风险开发 |
| FEATURE-DEVELOPER | Claude Sonnet 4.6 | 简单代码、小功能、低风险修复 |
| TESTER | Claude Sonnet 4.6 | 测试代码生成 |
| QA | GPT-5.3-Codex | 细节审查，错误检测 |
| SECURITY-REVIEWER | GPT-5.3-Codex | 安全专业知识深厚 |
| INTEGRATION-DEBUGGER | GPT-5.4 | 强问题分解与跨模块根因定位能力 |
| USER-AGENT | Claude Sonnet 4.6 | 更贴近自然用户表达，适合挑剔体验反馈与场景化批评 |
| CRYPTO-EVALUATION-EXPERT | GPT-5.4 | 适合做规范比对、逻辑审查和结论边界判断 |
| ARCHITECT | GPT-5.4 | 系统思维，架构分析 |
| DOC | Claude Haiku 4.5 | 轻量高频文档同步与一致性维护 |
| UI-DESIGNER | GPT-5.4 | 设计推理与交互方案评估 |

## 最佳实践

### 1. 流程遵循
- 严格遵循从 LEAD 开始的工作流
- 不跳过必要的阶段（DoR → 开发 → DoD → QA → 文档）
- 质量门禁不妥协

### 2. 交接标准
- 每个交接点都有清晰的进入条件
- 交接前确保前置条件满足
- 交接后无需返回监督（Agent 独立负责）

### 3. 信息流
- 任务全景在 Tasks-List 中维护
- 文档为单一事实源（由 DOC 维护）
- 决策记录在 Map.yaml 和 ADR 中

### 4. 异常处理
- QA FAIL 时直接回到 DEV，不绕过
- 风险识别时立即上报给 LEAD
- 阻塞情况由 PM 协调解除

## 入口指南

### 第一次使用
1. **@lead** — "实现帐户管理功能"
2. LEAD 分析意图 → 制定计划
3. 按推荐流程执行

### 常见操作
- 查询进度：`@pm 查看本周任务`
- 代码/日志探索：`@explore 从 Logs 目录中找出项目打开失败的线索`
- 特性开发：`@feature-developer 修复一个表单校验问题`
- 快速修复：`@dev TASK-123`（假设 DoR 已就绪）
- 集成排障：`@integration-debugger 项目打开后主界面卡死`
- 用户挑刺：`@user-agent 以测评工程师身份试用报告生成流程并批评它`
- 规范审查：`@crypto-evaluation-expert 检查量化评估逻辑是否符合 2023 规则`
- 安全审查：`@security-reviewer 最新代码变更`
- UI 设计：`@ui-designer 评审项目列表页交互`
- 文档同步：`@doc 同步今天的变更`

## Agent 间依赖关系

```
                               [LEAD]
                                  |
   +--------+--------+--------+-----------+-----------+----------+----------+
   |        |        |        |           |           |          |          |
 [PM]   [ARCH]  [EXPLORE] [TESTER] [INTEGRATION] [USER]    [EXPERT]    [UI]
   |        |        |        |           |           |          |          |
   +--------+--------+--------+-----------+-----------+----------+          |
                         |                       |                 |          |
                     [PLANNER]                  |                 |          |
                         |                       |                 |          |
          [FEATURE-DEVELOPER] ─────→ [DEV] ─────→ [QA] ─────→ [SECURITY]     |
                         |                       |                              |
                         +-----------------------+----------------------→ [DOC] ←+
```

## 故障排查

### Agent 无法执行
1. 检查 frontmatter 中的 `model` 字段是否有效
2. 检查 `tools` 列表是否包含必需工具
3. 确认 `handoffs` 格式正确

### 流程卡住
1. 检查是否在等待某个交接
2. 确认 QA 报告是否就绪
3. 查看 DoR/DoD 是否满足

### 文档不同步
1. 由 DOC Agent 主动调用进行同步
2. 检查 Context.md/Tasks.md 一致性
3. 更新 Map.yaml 架构图

---

**最后更新**: 2026-04-10
**系统版本**: v1.4 (15-Agent 业务增强系统)
