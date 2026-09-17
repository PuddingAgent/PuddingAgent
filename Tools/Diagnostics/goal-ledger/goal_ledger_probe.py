#!/usr/bin/env python3
"""Goal 账本只读探针（canonical 证据采集，受版本控制）。

用途
----
在不启动任何 dotnet 构建/测试的前提下，从平台 SQLite 账本采集 Goal/合同/检查/任务事实，
产出机器可读 JSON 证据，供验收断言与回归比对使用。

为什么需要它
------------
受控检查（CheckRunner）与 Agent 会话共用同一 csproj 的 obj/bin 与测试宿主，
并发操作会污染检查结果（已两次现场事故）。因此取证必须走只读账本路径。

用法
----
    python Tools/Diagnostics/goal-ledger/goal_ledger_probe.py \
        --db D:\\data\\databases\\pudding_platform.db \
        --goal-run-id 7ef90f2c66f449389fc0046eb9662cce \
        --out temp/goal-ledger.json \
        [--task-id 30eb371e7527424c83107dde5bf99d94] [--task-id b849ef8d750143fdaaa2a70d7b024a6c]

约定
----
* 严格只读：`file:...?mode=ro`，不做任何写入/迁移。
* 控制台只打印 ASCII 摘要（Windows 控制台默认 cp936，直接打印中文会 UnicodeEncodeError）；
  完整事实写入 UTF-8 JSON 文件，用 `--out` 指定。
* 退出码：0 = 采集完成；1 = 参数/IO 错误；2 = 目标 GoalRun 不存在。
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys

GOAL_RUN_COLUMNS = (
    "status", "status_reason", "blocked_code", "blocked_message", "resume_policy",
    "activation_epoch", "iterations_started", "iterations_settled",
    "consecutive_same_blocker", "max_iterations", "aggregate_version",
    "objective_version", "updated_at",
)

# GoalPhase（Source/PuddingCore/Goals/GoalContracts.cs:7-17）—— 用于把整数状态翻成可读名。
GOAL_PHASE = {
    1: "Active", 2: "Paused", 3: "Blocked", 4: "BudgetExhausted",
    5: "Completed", 6: "Cancelled", 7: "Failed",
}


def connect_ro(path: str) -> sqlite3.Connection:
    return sqlite3.connect("file:%s?mode=ro" % path.replace("\\", "/"), uri=True)


def fetch(con: sqlite3.Connection, sql: str, params=()) -> list[dict]:
    cur = con.execute(sql, params)
    cols = [d[0] for d in cur.description]
    return [dict(zip(cols, row)) for row in cur.fetchall()]


def safe_json(text):
    try:
        return json.loads(text or "[]")
    except Exception:
        return text


def collect(db: str, goal_run_id: str, task_ids: list[str]) -> dict:
    con = connect_ro(db)
    report: dict = {"db": db, "goal_run_id": goal_run_id, "schema_version": 1}

    goal_rows = fetch(con, "SELECT * FROM goal_runs WHERE goal_run_id = ?", (goal_run_id,))
    if not goal_rows:
        return {"error": "goal_run_not_found", **report}

    raw = goal_rows[0]
    goal = {k: raw.get(k) for k in GOAL_RUN_COLUMNS if k in raw}
    goal["phase_name"] = GOAL_PHASE.get(raw.get("status"), "Unknown(%s)" % raw.get("status"))
    goal["objective"] = str(raw.get("objective"))[:2000]
    report["goal"] = goal

    contracts = fetch(
        con,
        "SELECT activation_epoch, objective_version, source, criteria_json, checks_json "
        "FROM goal_acceptance_contracts WHERE goal_run_id = ? ORDER BY activation_epoch",
        (goal_run_id,),
    )
    report["contracts"] = [
        {
            "activation_epoch": c["activation_epoch"],
            "objective_version": c["objective_version"],
            "source": c["source"],
            "criteria": safe_json(c["criteria_json"]),
            "checks": safe_json(c["checks_json"]),
        }
        for c in contracts
    ]
    report["contract_epochs"] = [c["activation_epoch"] for c in report["contracts"]]

    checks = fetch(
        con,
        "SELECT activation_epoch, iteration_no, check_id, definition_ref, status, failure_code, "
        "attempt_count, lease_until_utc, input_fingerprint, report_json, updated_at_utc "
        "FROM goal_check_records WHERE goal_run_id = ? ORDER BY activation_epoch, check_id",
        (goal_run_id,),
    )
    for c in checks:
        c["report"] = safe_json(c.pop("report_json", None))
    report["check_records"] = checks
    report["check_record_count"] = len(checks)
    report["distinct_input_fingerprints"] = sorted(
        {str(c.get("input_fingerprint")) for c in checks})
    report["distinct_failure_codes"] = sorted({str(c.get("failure_code")) for c in checks})
    report["leased_check_count"] = sum(1 for c in checks if c.get("status") == "leased")

    iterations = fetch(
        con,
        "SELECT iteration_no, activation_epoch, status, created_at_utc FROM goal_iterations "
        "WHERE goal_run_id = ? ORDER BY iteration_no",
        (goal_run_id,),
    )
    report["iterations"] = iterations
    report["iteration_count"] = len(iterations)

    report["tasks"] = []
    for tid in task_ids:
        rows = fetch(con, "SELECT * FROM tasks WHERE task_id = ?", (tid,))
        if not rows:
            report["tasks"].append({"task_id": tid, "error": "not_found"})
            continue
        t = rows[0]
        keep = ("task_id", "status", "board_column", "version", "progress_percent",
                "active_assignment_id", "preferred_agent_id", "auto_dispatch_enabled",
                "parent_task_id", "updated_at_utc")
        report["tasks"].append({k: t.get(k) for k in keep if k in t})

    con.close()
    return report


def main() -> int:
    ap = argparse.ArgumentParser(description="Goal ledger read-only probe")
    ap.add_argument("--db", default=r"D:\data\databases\pudding_platform.db")
    ap.add_argument("--goal-run-id", required=True)
    ap.add_argument("--task-id", action="append", default=[])
    ap.add_argument("--out", default="temp/goal-ledger.json")
    args = ap.parse_args()

    if not os.path.isfile(args.db):
        print("DB_NOT_FOUND=%s" % args.db)
        return 1

    report = collect(args.db, args.goal_run_id, args.task_id)

    out_path = os.path.abspath(args.out)
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8") as fh:
        json.dump(report, fh, ensure_ascii=True, default=str, indent=1)

    if "error" in report:
        print("ERROR=%s" % report["error"])
        return 2

    g = report["goal"]
    print("OUT=%s BYTES=%d" % (out_path, os.path.getsize(out_path)))
    print("GOAL status=%s(%s) phase=%s" % (g.get("status"), g.get("status_name", ""), g["phase_name"]))
    print("GOAL epoch=%s iterations=%s/%s same_blocker=%s blocked=%s" % (
        g.get("activation_epoch"), g.get("iterations_settled"), g.get("iterations_started"),
        g.get("consecutive_same_blocker"), g.get("blocked_code")))
    print("CONTRACTS epochs=%s" % report["contract_epochs"])
    print("CHECKS total=%d leased=%d" % (report["check_record_count"], report["leased_check_count"]))
    print("FAILCODES=%s" % report["distinct_failure_codes"])
    print("FINGERPRINTS=%s" % report["distinct_input_fingerprints"])
    for t in report["tasks"]:
        print("TASK %s status=%s board=%s v=%s prog=%s" % (
            t.get("task_id"), t.get("status"), t.get("board_column"),
            t.get("version"), t.get("progress_percent")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
