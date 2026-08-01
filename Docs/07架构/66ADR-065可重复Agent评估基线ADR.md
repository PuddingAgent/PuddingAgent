# ADR-065：可重复 Agent 评估基线

> 状态：P2 已实施  
> 日期：2026-08-01

## 1. 决策

Pudding 复用已有 Hermes case catalog、workspace seed、benchmark run 和 Session diagnostics，建立一条可重复的评估闭环：

```text
case + version + deterministic contract
  -> prepare seed + freeze run snapshot
  -> fresh session executes prompt
  -> collect parent/subagent runtime facts
  -> deterministic artifact/budget checks
  -> persist evaluation JSON
  -> aggregate JSON/Markdown baseline
```

P2 不使用 LLM-as-judge。可由文件、内容、运行事实和预算确定的要求必须使用确定性 oracle；没有 oracle 的 case 返回 `unscored`，不得用“工具未报错”冒充任务完成。

## 2. 评价维度

- 指令完成度：要求的产物存在、为本次 run 新建或修改、包含必要事实且不包含禁止内容。
- 效率：端到端耗时、LLM 轮数、总 Token、缓存命中、人民币边际成本。
- 可靠性：工具失败、会话诊断分数、终态。
- 路由观测：按 main/subagent、provider、model、role/profile 和 agent 聚合调用与成本。role/profile 由 subagent run archive 元数据关联，不通过模型名猜测角色。

当前综合分权重为指令 60%、效率 20%、可靠性 20%。所有合同检查都通过才标记 `passed`；没有产物 oracle 时综合分为空。

## 3. 数据与持久化

`runtime/benchmark-runs/{runId}.json` 固化 case/version/config hash、workspace、session、seed 和 evaluation contract。评价结果原子写入同目录的 `{runId}.evaluation.json`。

事实来源：

- `TokenUsageEvents`：parent + subagent 的 provider/model/token/cache/cost/round；
- `chat_execution_commands`：主 Turn 起止时间与终态；
- `sub_agent_runs`：sub session 对应的 role/profile/agent；
- Session benchmark diagnostics：工具、审批、失败和诊断分；
- workspace artifacts：确定性任务结果。

case 配置仍以 `config/benchmark-cases/hermes-agent-cases.json` 为运行时覆盖，缺失的 evaluation contract 从随程序发布的 default-data 继承。这样可以更新题面与开关，同时保留平台的基础验收合同。

## 4. 评测污染隔离

所有 Benchmark launcher/runner 消息必须携带：

```json
{ "excludeFromLearning": "true" }
```

经验到 SKILL 的 trajectory source 必须排除该标记，避免用测试集训练自进化规则。P2 runner 每次创建 fresh session；workspace 完全隔离、daily summary/压缩记忆的统一排除门禁属于后续 P2.1，完成前不把长上下文 benchmark 当作自学习质量结论。

## 5. 首批 deterministic suite

首批覆盖 6 个有固定 seed 和明确产物的案例：

- `workspace-markdown-summary`
- `workspace-text-index`
- `workspace-report-generator`
- `incident-log-remediation`
- `customer-export-reconciliation`
- `noisy-repo-triage`

其余 case 仍可运行并采集指标，但保持 `unscored`，直到配置稳定 oracle。

## 6. 执行与对比

`Tools/Diagnostics/run_benchmarks.py` 默认选择所有 `hasEvaluation=true` 的案例，支持 case filter、repeat、dry-run、无人值守等待终态和既有 run 重评。每次执行输出结构化 JSON 与 Markdown；随机 LLM 的候选对比建议每 case 至少重复 3 次，比较通过率、中位 Token/成本/耗时，而不是比较单次分数。

P2 的发布门禁先以“可测量、可复现”为主，不立即加入 CI。建议后续门禁：关键案例 3/3、硬指令违规为 0、整体 pass rate 不低于 90%，质量不下降时 Token/成本回退不得超过 10%。

## 7. 首条真实基线（路由优化前）

2026-08-01 使用 `workspace-markdown-summary` 对当前默认 Agent 执行一次真实 smoke：

| 指标 | 结果 |
|---|---:|
| 任务状态 / 总分 | failed / 29 |
| 指令 / 效率 / 可靠性 | 0 / 82 / 62 |
| 端到端耗时 | 142,830 ms |
| LLM 调用 / 轮数 | 20 / 20 |
| 总 Token / 成本 | 873,387 / ¥0.319113 |
| 缓存命中率 | 90.29% |
| 工具调用 / 阻塞失败 | 35 / 8 |
| 模型路由 | deepseek-v4-pro 20 次；deepseek-v4-flash 0 次 |

主命令终态为 `succeeded`，但要求的 workspace `summary.md` 不存在，因此不能视为完成。诊断发现 Agent 把脚本和报告写到了仓库执行根，而不是 benchmark workspace；同时出现 workspace 路径越界、工具参数 JSON 错误及 5 次审批拒绝。基准生成的两个仓库根临时文件在确认来源和时间后已删除。

该基线说明优化目标不能只写成“降低 Pro 占比”：还必须同时验证正确 workspace 产物、阻塞工具失败和 Flash/角色路由占比。原始报告保存在 `.tmp-test-out/benchmark-p2/20260801T115354Z-pro-routing-p2-initial.{json,md}`，run id 为 `brun_f3b4ee04d25448378672926d43aade10`。

## 8. 后续

- P2.1：run 专属 workspace/agent snapshot、Benchmark 对所有记忆学习入口的统一隔离、自动回填 turn/command/trace。
- P2.2：显式 semantic role/invocation telemetry 与 route expectation 门禁。
- P3：定时自值守 suite、baseline/candidate 对比、趋势面板和 Skill canary 自动回滚。
- 需要主观判断的 case 再引入固定版本 judge，并与 deterministic checks 分栏呈现。
