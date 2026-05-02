---
name: dev-workflow
description: Use when implementing any feature, bugfix, or code change in the  project — before writing ANY code. Covers the full development lifecycle. Triggers: new feature, bug fix, refactoring, code review, architectural change.
argument-hint: "描述开发任务，例如 '实现密码模块导出功能' 或 '修复 ZeroMQ 连接断开'"
---

#  研发工作流

## 概述

本技能定义  项目中从**设计 → 探索 → 方案 → 实施 → QA → 归档**的完整研发方法论。
融合 TDD、系统化调试、子Agent协作和结构化收尾。

**核心原则：设计先行，测试驱动，系统化而非随意。**

## 铁律 (Iron Laws)

```
NO CODE WITHOUT DESIGN APPROVAL FIRST
NO PRODUCTION CODE WITHOUT A FAILING TEST FIRST
NO FIX WITHOUT ROOT CAUSE INVESTIGATION FIRST
```

违反铁律的字面 = 违反铁律的精神。没有例外。

## 何时使用

**总是使用：**
- 新功能开发
- Bug 修复
- 重构
- 代码审阅
- 架构变更

**例外（需人类确认）：**
- 一次性原型 / 实验
- 纯配置文件修改
- Typo / 文案修正（走快捷路径）

## 阶段流转

```
设计门禁(Design Gate) → 探索(Explore) → 方案(Plan) → 实施(Implement) → QA → 归档(Archive)
                              ↑                          ↓
                         调试(Debug) ←────────── QA FAIL ←
```

| 阶段 | 关键产出 | 门禁条件 |
|------|---------|---------|
| **设计门禁** | 设计简述（2-3 句话也可） | 用户批准 |
| **探索** | 影响面清单、关键约束 | 完成探索报告 |
| **方案** | 技术方案、任务拆解、验收标准 | DoR 通过 |
| **实施** | 代码 + 测试 + 自审 | 测试全部通过 |
| **QA** | 审阅报告 | PASS / PASS_WITH_NOTES |
| **归档** | 文档同步、反思沉淀 | docs_synced=true |

---

## 阶段 0: 设计门禁 (Design Gate)

<HARD-GATE>
在任何代码编写之前，必须先通过设计门禁。即使任务"简单到不需要设计"也必须完成此步骤。
"简单"任务正是未检查假设导致最多返工的地方。
</HARD-GATE>

### 流程

1. **理解上下文** — 阅读相关代码、文档、最近提交
2. **提问澄清** — 一次一个问题，理解目标/约束/成功标准（优先选择题）
3. **提出方案** — 2-3 种方法及权衡，给出推荐
4. **获得批准** — 展示设计概要，逐段确认
5. **记录设计** — 写入 `Doc/Context.md`，关键决策同步到相关设计文档

### 设计自审清单

- [ ] 无 "TBD"、"TODO"、未完成章节
- [ ] 范围聚焦：能否在一个实施周期完成？
- [ ] 无歧义：任何需求可能存在两种以上解读？
- [ ] 遵循现有架构分层

**只有用户明确批准设计后，才能进入下一阶段。**

---

## 阶段 1: 探索 (Explore)

委派 `@explore` Agent，输出：

- **影响面清单** — 涉及模块、文件列表、依赖关系
- **关键约束** — 架构规则、历史踩坑（查阅 `Doc/Memory/Self-reflection.md`）
- **相关设计文档** — ADR、已有设计文档链接

**动手类任务强制探索，无清单不进实施。**

---

## 阶段 2: 方案 (Plan)

### 技术方案

简单任务（≤2 文件、无新实体、无 schema 变更）→ Lead 自出方案。
复杂任务（≥3 模块 / 跨层 / schema 变更）→ 委派 `@architect`。

方案产出：
- 技术方案（含备选与权衡）
- 工作量评估
- 阶段拆分（按任务拆解为 2-5 分钟的 bite-sized 步骤）
- 风险与回滚策略
- UI 类：并行拉 `@ui-designer` + `@user-agent`
- 安全类：并行拉 `@security-reviewer` + `@crypto-evaluation-expert`

### Plan 编写规范

```
每个步骤是一步动作（2-5 分钟）:
- "写失败的测试" → 运行确认失败 → "写最小实现" → 运行确认通过 → "提交"

每个步骤必须包含：
- 确切文件路径
- 完整代码（不是"类似 Task N"）
- 确切命令及预期输出
- 禁止: "TBD"、"TODO"、"添加适当的错误处理"、"处理边界情况"
```

### DoR 检查（draft → ready）

- [ ] 目标清晰
- [ ] 范围明确（What + Out of Scope）
- [ ] 至少一条验收用例
- [ ] 风险预案（复杂任务）
- [ ] 依赖确认

```bash
# 更新任务卡
python todo-api/todo_api.py update <id> stage=ready \
  --goal "..." --out-of-scope "..." \
  --acceptance-criteria "条件1" --acceptance-criteria "条件2" \
  --risk-notes "..."
```

---

## 阶段 3: 实施 (Implementation)

### 开工门禁（使用 git-workflow）

**REQUIRED SUB-SKILL:** 使用 `git-workflow` 完成开工检查和分支创建。

> **多协作者项目**：以下流程包含协作感知步骤。完整的协作规范见 `git-workflow` 第 0 节「多协作者协作规范」。

```bash
# 0. 协作感知（必须最先执行！）
#    查阅公告板，了解其他参与者的最新动态
python todo-api/todo_api.py bulletins --unacknowledged-by <my-agent-id>
#    检查进行中的任务，避免重复工作
python todo-api/todo_api.py list-tasks --stage in_progress

# 1. 认领任务（声明归属，防止冲突）
python todo-api/todo_api.py claim TASK-xxx \
  --agent-id <my-agent-id> \
  --lease-minutes 120

# 2. 远端同步 + 检查工作区干净
git fetch origin
git status

# 3. 创建功能分支
git checkout master && git pull origin master
git checkout -b feature/TASK-xxx-short-description

# 4. 在任务卡登记分支 + 发开始公告
python todo-api/todo_api.py update TASK-xxx --stage in_progress \
  --last-agent-summary "从 master 创建分支 feature/TASK-xxx-short-description"

python todo-api/todo_api.py bulletin-post \
  --type progress \
  --author-agent <my-agent-id> \
  --text "开始处理 TASK-xxx，分支: feature/TASK-xxx-xxx" \
  --related-task-id TASK-xxx
```

详细分支命名、提交规范、冲突处理、协作规则 → 查阅 `git-workflow`。

### TDD 铁律

```
RED: 写失败的测试 → 确认失败原因正确 → GREEN: 写最小代码通过 → 确认通过 → REFACTOR: 清理 → 提交
```

**写代码之前写测试？删掉代码，从测试开始。**
- 不要"保留参考"、"边写测试边改"、"看一眼"
- 删掉就是删掉

### 28 原则分流

| 占比 | 执行者 | 适用场景 |
|------|--------|---------|
| ~50% | `@lightweight-developer` | 单/少文件、样板、简单 CRUD |
| ~35% | `@dev` | 1-2 模块复杂逻辑、中等风险 |
| ~15% | `@super-dev` | 3+ 模块、核心算法、密码学、不可逆 |

### 子Agent驱动执行（推荐）

当方案有多个独立任务时：

1. **一次性提取所有任务** — 从方案文件读取全部任务文本和上下文
2. **逐任务派发子Agent** — 每个任务使用**全新子Agent**（无上下文污染）
3. **两阶段审阅** — 先做 spec 合规审阅，再做代码质量审阅
4. **处理子Agent状态**：

| 状态 | 处理方式 |
|------|---------|
| `DONE` | 进入 spec 审阅 |
| `DONE_WITH_CONCERNS` | 阅读顾虑，必要时先处理 |
| `NEEDS_CONTEXT` | 提供缺失上下文，重新派发 |
| `BLOCKED` | 评估阻塞原因，升级或拆分 |

**模型选择**：机械实现用便宜模型，判断/集成用标准模型，架构/审阅用最强模型。

### 编码规范

- **最小变更**：不混合功能开发 + 大规模重构
- **遗留标注**：`// TODO(TASK-xxx): 描述` 或 `// FIXME(TASK-xxx): 描述`
- **关键链路日志**：功能关键路径添加 `SimpleLogger` 日志
- **异常处理**：被吞掉的异常必须记录日志，传播的异常通常不记录
- **共享文件预警**：修改 .csproj / 公共接口 / 架构层文件前，**必须先发公告板 warning**（详见 `git-workflow` 第 0.4 节）

### 自审清单（提交前）

- [ ] 每个新函数/方法都有对应测试
- [ ] 亲自看到每个测试失败后才实现
- [ ] 测试失败原因正确（功能缺失，不是 typo）
- [ ] 写了最小代码通过每个测试
- [ ] 所有测试通过，无错误/警告
- [ ] 无调试代码残留
- [ ] 遵循架构分层（UI → Application → Core/Domain → Infrastructure）

---

## 阶段 4: 调试 (Debugging)

当遇到 bug、测试失败或意外行为时，**禁止猜测修复**。

### 四阶段流程

| 阶段 | 关键活动 | 成功标准 |
|------|---------|---------|
| **1. 根因调查** | 读错误信息、稳定复现、检查最近变更、跨组件收集证据 | 理解 WHAT 和 WHY |
| **2. 模式分析** | 找工作中类似的代码、逐行对比差异、理解依赖 | 识别差异点 |
| **3. 假设验证** | 形成单一假设、最小变更测试、一次一个变量 | 假设确认或推翻 |
| **4. 实施修复** | 创建失败测试、单一修复、验证通过 | Bug 解决，测试通过 |

### 调试铁律

```
NO FIXES WITHOUT ROOT CAUSE INVESTIGATION FIRST
```

**已尝试 3 次以上修复仍失败？** STOP。质疑架构，不要继续修补症状。

### 多组件系统诊断

在组件边界添加诊断探针：
```
对每个组件边界：
  - 记录进入组件的数据
  - 记录离开组件的数据
  - 验证环境/配置传播
运行一次收集证据 → 分析确定故障组件 → 深入调查该组件
```

---

## 阶段 5: QA 门禁

### 双 QA 分流（禁止同模型自审）

| 开发模型 | QA 审阅模型 |
|---------|------------|
| @dev (Codex) | @qa-sonnet (Sonnet) |
| @lightweight-developer (MiniMax) | @qa (Codex) |
| @super-dev (Claude Opus) | @qa (Codex) |

二审：GLM-5.1。安全敏感追加 `@security-reviewer`。密评追加 `@crypto-evaluation-expert`。

### QA 操作

```bash
# 提交 QA
python todo-api/todo_api.py finish <id> --agent-id <agent> \
  --summary "实现完成" --stage ready_for_qa --release

# QA 审阅者操作
python todo-api/todo_api.py qa <id> --file qa-result.json
# 或快捷命令
python todo-api/todo_api.py qa-approve <id>
python todo-api/todo_api.py qa-reject <id>
```

| 结论 | 含义 | 后续 |
|------|------|------|
| `PASS` | 通过 | → 归档 |
| `PASS_WITH_NOTES` | 通过，有改进建议 | 修复 → 归档 |
| `FAIL` | 不通过 | 退回原开发者 → 再次 QA（不跳级） |

**QA FAIL 后禁止跳级修复。必须退回原开发者，按原梯队重新交付。**

---

## 阶段 6: 交付与归档 (Finishing)

### 收尾流程

**REQUIRED SUB-SKILL:** 使用 `git-workflow` 完成分支合并、推送和清理。

```bash
# 1. 验证测试
dotnet test 

# 2. 同步文档
# Doc/Context.md：变更摘要、决策、测试结果、遗留项
# Doc/Tasks.md：更新任务状态
# Doc/Map.yaml：仅当影响架构时
# Doc/Memory/YYYY-MM-DD.md：当天工作日志
python todo-api/todo_api.py update <id> docs_synced=true

# 3. 反思沉淀
# 阅读当天日志 → 提炼通用经验 → 更新 Doc/Memory/Self-reflection.md
python todo-api/todo_api.py update <id> reflection_required=true

# 4. 登记 Git 信息到任务卡（完成证据）
python todo-api/todo_api.py note <id> --author dev \
  --text "Git分支: feature/TASK-xxx-xxx | 提交: <sha> feat: XXX (TASK-xxx)"

# 5. 合并回 master 并推送（详见 git-workflow 阶段 4 和 6）
git checkout master && git pull && git merge --no-ff feature/TASK-xxx-xxx
dotnet test && git push origin master

# 6. 发公告板通知下游
python todo-api/todo_api.py bulletin-post \
  --type handoff \
  --author-agent <my-agent-id> \
  --text "TASK-xxx 已合并 master，相关参与者请同步" \
  --related-task-id TASK-xxx

# 7. 清理分支
git branch -d feature/TASK-xxx-xxx
git push origin --delete feature/TASK-xxx-xxx

# 8. 完成任务
python todo-api/todo_api.py finish <id> --agent-id <agent> --summary "完成" --release
```

详细分支合并、冲突解决、推送门禁规则 → 查阅 `git-workflow`。

### 收尾选项

完成后向用户展示4个结构化选项：
1. **合并回主分支**（本地）
2. **推送并创建 PR**
3. **保持分支现状**
4. **丢弃此工作**（需要输入 'discard' 确认）

---

## 常见借口与反驳

| 借口 | 现实 |
|------|------|
| "太简单了不需要设计" | 简单正是未检查假设造成最多浪费的地方 |
| "我先试试再写测试" | 先写的测试证明了正确性。后写的只证明"做了什么" |
| "手动测试过了" | 随意 ≠ 系统化。无记录、无法重跑 |
| "紧急，没时间走流程" | 走流程比胡乱尝试更快。系统化调试 15-30 分钟，随机修复 2-3 小时 |
| "多个修复一起上省时间" | 无法隔离有效修复。引入新 bug |
| "需求太少，不需要完整 DoR" | 模糊的需求边界是返工的根源 |
| "我熟这个代码，跳过探索" | 熟悉 ≠ 知道最近的变更和隐藏约束 |
| "这个检查太啰嗦了" | 花 5 分钟自审，省 30 分钟 QA 返工 |

---

## 红旗信号 — STOP 并回到正确位置

### 设计阶段
- "这个太简单了，直接写代码吧"
- "我很清楚要做什么，不需要提问"
- "边写边设计更高效"
- "方案差不多就行了，开干吧"

**→ 回到阶段 0 设计门禁**

### 实施阶段
- 代码写在测试前面
- 测试第一次就跑通了
- "我就保留这段代码做参考"
- "测试之后补上效果一样"
- "已经花了 X 小时，删掉太浪费"

**→ 删除代码，从 TDD RED 开始**

### 调试阶段
- "快速修一下，后面再调查"
- "试试改 X 看看效果"
- "同时加多个改动，跑测试"
- "应该是 X 的问题，我直接修"
- "再试一次修复"（已经试了 3 次）

**→ 回到调试阶段 1（根因调查）**

---

## 任务阶段快速参考

| 阶段 | 含义 | 进入条件 |
|------|------|---------|
| `draft` | 草稿 | 新建任务默认 |
| `ready` | 就绪 | DoR 检查通过 |
| `in_progress` | 开发中 | 领取任务并开始编码 |
| `blocked` | 阻塞 | 发现依赖/外部阻断 |
| `implemented` | 已实现 | 代码完成 |
| `verifying` | 验证中 | 运行单元测试 |
| `ready_for_qa` | 待 QA | 测试通过 |
| `qa_failed` | QA 未通过 | 审阅发现 P0/P1 |
| `done` | 完成 | QA PASS |
| `cancelled` | 已取消 | 需求变更 |

## executor_type 执行者类型

| 值 | 含义 |
|----|------|
| `agent` | 完全由 Agent 自动执行 |
| `human` | 仅人类执行 |
| `hybrid` | Agent + 人类协作 |

### 必读文档

开工前必读：
1. `Doc/Index.md` — 项目概览
2. `Doc/CLAUDE.md` — 流程规范权威源
3. `Doc/Context.md` — 当前上下文
4. `Doc/Map.yaml` — 架构地图
5. `Doc/Memory/Self-reflection.md` — 避免历史错误

### 关键约束

1. **不跳阶段**：按流转顺序推进
2. **DoR 先行**：draft → ready 前必须满足
3. **文档同步**：交付前 `docs_synced` 必须为 `true`
4. **反思必做**：每日结束前反思沉淀
5. **禁止自审**：开发者不可自行执行 QA
6. **架构分层不可违反**：UI → Application → Core/Domain → Infrastructure
