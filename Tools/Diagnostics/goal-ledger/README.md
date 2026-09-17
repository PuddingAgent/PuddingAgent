# Goal 账本只读探针（goal-ledger）

## 这是什么

从平台 SQLite 账本（`goal_runs` / `goal_acceptance_contracts` / `goal_check_records` / `goal_iterations` / `tasks`）
采集 **canonical 事实** 的只读探针，产出机器可读 JSON，用于验收断言与回归比对。

**它是受版本控制的证据工具**，不是临时脚本——验收报告引用的是本目录下的路径，
而不是 `temp/` 下不可追溯的一次性脚本。

## 为什么必须走这条路

受控检查（CheckRunner）与 Agent 会话操作 **同一** `PuddingPlatformTests.csproj` 的 `obj/bin` 与测试宿主。
两者并发会互相污染，已造成两次现场事故：

1. **iter5 `non_zero_exit_code`**：会话侧 `dotnet test` 与受控检查同时运行 ⇒ 检查以 exit 1 结束。
2. **epoch5/7/9 `status=leased` 悬挂**：检查执行期间 Core 被部署重启打断 ⇒ 报告永不落库 ⇒
   结算恒报 `check_results_pending`，且同 epoch 内被 `dedup_key` 冻结无法自愈。

因此**取证默认不走构建/测试**，只读账本。

## 用法

```bash
python Tools/Diagnostics/goal-ledger/goal_ledger_probe.py \
  --db D:\data\databases\pudding_platform.db \
  --goal-run-id 7ef90f2c66f449389fc0046eb9662cce \
  --task-id 30eb371e7527424c83107dde5bf99d94 \
  --task-id b849ef8d750143fdaaa2a70d7b024a6c \
  --out temp/goal-ledger.json
```

* `--db` 默认 `D:\data\databases\pudding_platform.db`。
* `--task-id` 可重复，用于附带任务终态/版本/progress。
* `--out` 为 UTF-8 JSON 输出路径（`temp/` 已被 gitignore，适合放采集结果）。

## 输出与退出码

| 退出码 | 含义 |
|---|---|
| 0 | 采集完成 |
| 1 | 参数/IO 错误（如 DB 不存在） |
| 2 | 目标 GoalRun 不存在 |

控制台**只打印 ASCII 摘要**（Windows 控制台默认 cp936，直接打印中文会 `UnicodeEncodeError`）。
完整事实（含合同 criteria/checks、检查 report_json）写入 `--out` 指定的 UTF-8 JSON。

## 关键字段

| 字段 | 含义 |
|---|---|
| `goal.phase_name` | `GoalPhase` 可读名（`GoalContracts.cs:7-17`：Active=1 / Paused=2 / Blocked=3 / BudgetExhausted=4 / **Completed=5** / Cancelled=6 / Failed=7） |
| `contracts[].criteria` / `contracts[].checks` | 该 activation epoch 的**实际冻结合同**（判断"Goal 通过了什么"的唯一依据） |
| `check_records[].status` | `pending` / `leased` / `finished` |
| `check_records[].report` | 检查真实报告（`executed_test_count` / `passed_test_count` / `failure_code` 等） |
| `check_records[].input_fingerprint` | 决定"旧结论能否复用"的输入身份（见 ADR-092 §10） |
| `leased_check_count` | >0 说明存在悬挂租约 ⇒ **禁止部署重启与并发构建** |

## 安全边界

* 严格只读（`file:...?mode=ro`），不做任何写入/迁移/修复。
* 不修改任务或 Goal 终态——终态只能由 canonical 命令与结算器写。
* `leased_check_count > 0` 时禁止触发构建/测试或重启。
