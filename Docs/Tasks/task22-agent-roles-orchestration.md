# Task 22 - Agent 角色与编排系统方案（Pudding）
状态：`design`  
优先级：`P0`  
最后更新：2026-02-20

## 1. 目标
- 在不照搬外部命名的前提下，形成 Pudding 自有的角色体系与编排模式。
- 角色能力可配置（模板化），编排可观测（状态、任务、验收标准）。
- 支持多 Agent 协作与中断恢复（Continue Executor 思路）。

## 2. 参考提炼（用于启发，不直接复制）
- 参考文档强调“编排优先于单轮对话”：任务拆分、并行执行、持续推进。
- Leader 强调契约与执行监督，不直接吞并所有具体实现。
- Worker 强调边界执行与验证闭环。
- 规划与审阅应独立成角色，避免“边写边拍脑袋”。

参考：
- https://github.com/code-yeongyu/oh-my-opencode/blob/dev/README.zh-cn.md
- https://github.com/code-yeongyu/oh-my-opencode/blob/dev/docs/guide/understanding-orchestration-system.md

## 3. Pudding 角色命名（建议）
说明：内部 ID 保持简洁稳定；展示名采用更直观、可解释命名。

| Internal ID | 展示名（中文） | 展示名（英文） | 核心职责 |
|---|---|---|---|
| `leader` | 协调者 | Coordinator | 目标澄清、任务分解、编排推进、收敛决策 |
| `worker` | 实现者 | Implementer | 按约束实现功能并完成自检 |
| `explore` | 探索者 | Explorer | 快速扫描代码库与约定，识别风险点 |
| `researcher` | 研究员 | Research Analyst | 方案调研、最佳实践、风险与取舍 |
| `planner` | 规划师 | Solution Planner | 需求澄清、计划生成、验收标准定义 |
| `reviewer` | 质量审阅者 | Quality Reviewer | 代码审阅、回归风险、测试缺口识别 |

可扩展角色（后续）：
- `guardian`（边界守卫，权限/安全策略）
- `maintainer`（技术债与长期质量治理）

## 4. 工作模式（Orchestration Modes）
### 4.1 标准模式（默认）
1. `planner` 先产出执行计划（任务+验收标准+护栏）
2. `explore` 读取项目结构与约束
3. `leader` 分配任务给 `worker`（可并行）
4. `reviewer` 审阅并给出阻断/非阻断意见
5. `leader` 决策合并与收尾

### 4.2 快速模式（小变更）
1. `leader` 直接给 `worker`
2. `reviewer` 最小审阅
3. 自动完成总结与待办更新

### 4.3 研究模式（方案不明确）
1. `researcher` 先行输出候选方案
2. `planner` 转化为任务计划
3. 进入标准模式

## 5. 关键机制（v1 -> v2）
### v1（当前应优先落地）
- 角色模板默认集（leader/worker/explore/researcher/planner/reviewer）
- Leader 的任务状态机：`todo -> in_progress -> review -> done/blocked`
- Continue Executor：发现未完成任务时强制续跑
- Reviewer 审阅门禁：阻断项不允许直接完成

### v2（增强）
- 哈希锚定编辑（防止陈旧行写入）
- LSP/AstGrep 语义定位与重构安全网
- 多 Agent 可视化拓扑（右栏/子面板）

## 6. 模板与配置建议
- Agent 模板来自配置（`agentTemplates`）+ 项目级 Prompt（`.pudding/prompts/*.md`）。
- 每个角色支持：
  - conscious/subconscious 模型
  - 策略（verbosity、budget、可见性）
  - 启用的 hooks 与工具白名单

## 7. 验收标准（DoD）
1. 新项目首次启动，默认具备 6 个角色模板。
2. 可以查看每个角色当前任务状态与最近动作。
3. `leader` 可恢复中断任务（continue）。
4. `reviewer` 可输出阻断级问题并阻止直接完成。
5. UI 可显示当前编排阶段与活跃角色。
