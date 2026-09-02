#!/usr/bin/env python3
"""Task board historical dirty-data audit tool (kanban card 4ed930e7, atomic task 3).

Default mode is DRY-RUN: opens the live pudding_platform.db read-only
(SQLite URI ``mode=ro`` + ``PRAGMA query_only=ON``), runs five detections and
prints / writes a Markdown report. The source database is NEVER written.

``--apply`` enables the repair mode and FORCES ``--backup`` first: the whole
database is copied with the sqlite3 backup API into temp/ and all repair SQL
runs against the backup copy only. (Implemented in this commit; NOT executed.)

Detected classes (baselines recorded on kanban card 4ed930e7 and in
temp/audit-recall-20260902.md, snapshot 2026-09-02):

  a) workspace_tasks status=Completed(8) missing TaskCompleted(10) event     base 31
  b) task_execution_bindings with empty execution_id / session_id            base 33
  c) task_assignment_attempts stuck in Assigned(0)/Accepted(1)               base 36
  d) agent_availability_projection stale rows                                base  1
  e) attempts.status=4 (out of AssignmentStatus range 0..3) -> suspected
     TaskDisposition value leak; dumped for human adjudication only          base 19

Enum constants are copied from source (file:line kept next to each constant):

  - WorkspaceTaskStatus  Source/PuddingCore/Tasks/WorkspaceTaskModels.cs:4
        Backlog=0 Ready=1 Deferred=2 Reserved=3 Assigned=4 NeedsReview=5
        InProgress=6 Blocked=7 Completed=8 Failed=9 Cancelled=10 Archived=11
  - TaskEventType        same file :278 (member TaskCompleted at :313)
        TaskCreated=0 TaskUpdated=1 Ready=2 Deferred=3 Reserved=4 Assigned=5
        Accepted=6 Progressed=7 Blocked=8 AssignmentRejected=9 Completed=10
  - AssignmentStatus     same file :338 (Assigned=0 Accepted=1 Completed=2 Rejected=3)
  - TaskDisposition      same file :63 (Accept=0 Progress=1 Todo=2 Blocked=3
        NeedsApproval=4 Rejected=5 Completed=6)

Safety rules encoded here:

  * IRON RULE (class d): the availability row of agent
    ``default.global_general-assistant.6a8`` bound to task
    ``3bd2a4b0ef5f4bff8f175fb7655927ad`` (active_task_owned, task InProgress)
    is a LEGAL projection and must NEVER enter the repair list.
  * Class e rows are dumped for human adjudication only; --apply never
    touches status=4 attempts.
  * Class c repairs only release attempts whose task already reached a
    terminal status; attempts of still-active tasks are left untouched and
    reported as pending-human.

Only the Python standard library (sqlite3) is used, mirroring
inspect_schema.py in this directory; DB path resolution reuses pudding_paths.py.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import sqlite3
import sys
import uuid
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pudding_paths import default_output_root, resolve_data_paths  # noqa: E402

# --------------------------------------------------------------------------
# Enum constants (sources documented in module docstring)
# --------------------------------------------------------------------------
WS_TASK_STATUS_COMPLETED = 8      # WorkspaceTaskModels.cs:4  (Completed)
WS_TASK_STATUS_IN_PROGRESS = 6    # WorkspaceTaskModels.cs:4  (InProgress)
WS_TASK_STATUS_TERMINAL_BAD = (9, 10, 11)  # Failed / Cancelled / Archived
EVENT_TASK_COMPLETED = 10         # WorkspaceTaskModels.cs:278/:313
ASSIGN_ASSIGNED = 0               # WorkspaceTaskModels.cs:338
ASSIGN_ACCEPTED = 1               # WorkspaceTaskModels.cs:338
ASSIGN_COMPLETED = 2              # WorkspaceTaskModels.cs:338
ASSIGN_REJECTED = 3               # WorkspaceTaskModels.cs:338
DISPOSITION_NEEDS_APPROVAL = 4    # WorkspaceTaskModels.cs:63 (suspected leak)

BASELINE = {"a": 31, "b": 33, "c": 36, "d": 1, "e": 19}

PROTECTED_AGENT_SUFFIX = "default.global_general-assistant.6a8"   # iron rule
PROTECTED_TASK_ID = "3bd2a4b0ef5f4bff8f175fb7655927ad"            # iron rule

TRUNC = 300


# --------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------
def _now() -> str:
    return _dt.datetime.now(_dt.timezone.utc).isoformat(timespec="milliseconds")


def _short(value: Any, limit: int = TRUNC) -> str:
    text = "" if value is None else str(value)
    return text if len(text) <= limit else text[:limit] + "...<truncated>"


def open_readonly(db_path: Path) -> sqlite3.Connection:
    """Open the live DB strictly read-only (URI mode=ro + query_only)."""
    uri = "file:" + db_path.as_posix() + "?mode=ro"
    conn = sqlite3.connect(uri, uri=True)
    conn.execute("PRAGMA query_only=ON")
    return conn


def table_columns(conn: sqlite3.Connection, table: str) -> list[str]:
    return [row[1] for row in conn.execute(f"PRAGMA table_info({table})")]


def require_columns(cols: list[str], table: str, needed: list[str]) -> None:
    missing = [c for c in needed if c not in cols]
    if missing:
        raise SystemExit(
            f"[fatal] table {table} is missing expected column(s) {missing}; "
            f"schema drifted - audit manually before running this tool.")


def _pk_column(conn: sqlite3.Connection, table: str) -> tuple[str | None, bool]:
    """Return (pk column name, is_integer_autopk)."""
    for row in conn.execute(f"PRAGMA table_info({table})"):
        if row[5]:
            return row[1], (row[2] or "").upper().startswith("INT")
    return None, False


def _task_key_column(conn: sqlite3.Connection, cols: list[str]) -> str:
    """Column of workspace_tasks that joins task_events.task_id/attempts.task_id.

    Prefer explicit candidates before falling back to the declared PK."""
    for cand in ("id", "task_id", "workspace_task_id"):
        if cand in cols:
            return cand
    pk = _pk_column(conn, "workspace_tasks")[0]
    if pk is None:
        raise SystemExit(
            f"[fatal] workspace_tasks has neither id-like column nor PK "
            f"(have: {cols}); audit manually before running this tool.")
    return pk


def _title_column(cols: list[str]) -> str | None:
    for cand in ("title", "name", "subject"):
        if cand in cols:
            return cand
    return None


def _time_column(cols: list[str]) -> str | None:
    for candidate in ("created_at", "occurred_at", "created_at_utc", "at",
                      "timestamp"):
        if candidate in cols:
            return candidate
    for col in cols:
        low = col.lower()
        if "at" in low or "time" in low:
            return col
    return None


def _order_by(cols: list[str]) -> str:
    if "sequence" in cols:
        return "sequence"
    tc = _time_column(cols)
    if tc:
        return tc
    return "rowid"


# --------------------------------------------------------------------------
# Detections
# --------------------------------------------------------------------------
def detect_a(conn: sqlite3.Connection) -> dict[str, Any]:
    tcols = table_columns(conn, "workspace_tasks")
    ecols = table_columns(conn, "task_events")
    require_columns(tcols, "workspace_tasks", ["status"])
    require_columns(ecols, "task_events", ["task_id", "event_type"])
    t_pk = _task_key_column(conn, tcols)
    t_title = _title_column(tcols)
    if t_title is None:
        raise SystemExit(
            f"[fatal] workspace_tasks title-like column not found "
            f"(have: {tcols}); audit manually before running this tool.")

    total_completed = conn.execute(
        "SELECT COUNT(*) FROM workspace_tasks WHERE status = ?",
        (WS_TASK_STATUS_COMPLETED,)).fetchone()[0]
    missing = conn.execute(
        f"""SELECT t.{t_pk}, t.{t_title} FROM workspace_tasks t
           WHERE t.status = ?
             AND NOT EXISTS (SELECT 1 FROM task_events e
                             WHERE e.task_id = t.{t_pk} AND e.event_type = ?)
           ORDER BY t.{t_pk}""",
        (WS_TASK_STATUS_COMPLETED, EVENT_TASK_COMPLETED)).fetchall()
    with_event = conn.execute(
        f"""SELECT t.{t_pk}, t.{t_title} FROM workspace_tasks t
           WHERE t.status = ?
             AND EXISTS (SELECT 1 FROM task_events e
                         WHERE e.task_id = t.{t_pk} AND e.event_type = ?)
           ORDER BY t.{t_pk}""",
        (WS_TASK_STATUS_COMPLETED, EVENT_TASK_COMPLETED)).fetchall()

    # positive sample rows = completion-fact template for backfill
    order = _order_by(ecols)
    samples = [dict(zip(ecols, row)) for row in conn.execute(
        f"SELECT * FROM task_events WHERE event_type = ? "
        f"ORDER BY {order} LIMIT 3", (EVENT_TASK_COMPLETED,))]
    return {"count": len(missing), "total_completed": total_completed,
            "missing": missing, "with_event": with_event, "samples": samples}


def detect_b(conn: sqlite3.Connection) -> dict[str, Any]:
    cols = table_columns(conn, "task_execution_bindings")
    require_columns(cols, "task_execution_bindings", ["execution_id", "session_id"])
    empty_exec = conn.execute(
        "SELECT COUNT(*) FROM task_execution_bindings "
        "WHERE execution_id IS NULL OR execution_id = ''").fetchone()[0]
    empty_sess = conn.execute(
        "SELECT COUNT(*) FROM task_execution_bindings "
        "WHERE session_id IS NULL OR session_id = ''").fetchone()[0]
    both_empty = conn.execute(
        "SELECT COUNT(*) FROM task_execution_bindings "
        "WHERE (execution_id IS NULL OR execution_id = '') "
        "AND (session_id IS NULL OR session_id = '')").fetchone()[0]
    any_empty = conn.execute(
        "SELECT COUNT(*) FROM task_execution_bindings "
        "WHERE (execution_id IS NULL OR execution_id = '') "
        "OR (session_id IS NULL OR session_id = '')").fetchone()[0]
    total = conn.execute("SELECT COUNT(*) FROM task_execution_bindings").fetchone()[0]
    rows = [dict(zip(cols, r)) for r in conn.execute(
        "SELECT * FROM task_execution_bindings "
        "WHERE (execution_id IS NULL OR execution_id = '') "
        "OR (session_id IS NULL OR session_id = '') ORDER BY rowid LIMIT 50")]
    return {"count": any_empty, "total": total, "empty_exec": empty_exec,
            "empty_sess": empty_sess, "both_empty": both_empty, "rows": rows}


def detect_c(conn: sqlite3.Connection) -> dict[str, Any]:
    cols = table_columns(conn, "task_assignment_attempts")
    require_columns(cols, "task_assignment_attempts", ["task_id", "status"])
    dist = conn.execute(
        "SELECT status, COUNT(*) FROM task_assignment_attempts "
        "GROUP BY status ORDER BY status").fetchall()
    not_released = conn.execute(
        "SELECT COUNT(*) FROM task_assignment_attempts WHERE status IN (?, ?)",
        (ASSIGN_ASSIGNED, ASSIGN_ACCEPTED)).fetchone()[0]
    agent_col = next((c for c in ("agent_id", "agent", "agent_address")
                      if c in cols), None)
    sel_cols = [c for c in ("id", "task_id", "status", agent_col)
                if c and c in cols]
    rows = [dict(zip(sel_cols, r)) for r in conn.execute(
        f"SELECT {', '.join(sel_cols)} FROM task_assignment_attempts "
        f"WHERE status IN (?, ?) ORDER BY rowid",
        (ASSIGN_ASSIGNED, ASSIGN_ACCEPTED))]
    # join task status for release planning
    tcols = table_columns(conn, "workspace_tasks")
    task_key = _task_key_column(conn, tcols)
    task_title = _title_column(tcols)
    sel_t = f"status, {task_title}" if task_title else "status"
    for row in rows:
        t = conn.execute(f"SELECT {sel_t} FROM workspace_tasks "
                         f"WHERE {task_key} = ?",
                         (row["task_id"],)).fetchone()
        row["task_status"] = t[0] if t else None
        row["task_title"] = (t[1] if (t and task_title) else None) \
            or "<task missing>"
    st4 = conn.execute(
        "SELECT COUNT(*) FROM task_assignment_attempts "
        "WHERE status = 4").fetchone()[0]
    return {"count": not_released, "dist": dist, "rows": rows,
            "status4_count": st4}


def detect_d(conn: sqlite3.Connection) -> dict[str, Any]:
    cols = table_columns(conn, "agent_availability_projection")
    state_col = next((c for c in ("state", "status", "availability_state",
                                  "projection_state") if c in cols), None)
    agent_col = next((c for c in ("agent_id", "agent", "agent_address",
                                  "agent_instance_id") if c in cols), None)
    task_col = next((c for c in ("task_id", "active_task_id", "bound_task_id",
                                 "current_task_id", "task") if c in cols), None)
    need = [c for c in (agent_col, task_col, state_col) if c]
    require_columns(cols, "agent_availability_projection", need)
    task_key = _task_key_column(conn, table_columns(conn, "workspace_tasks"))
    rows_out: list[dict[str, Any]] = []
    protected_hits: list[str] = []
    for row in conn.execute("SELECT * FROM agent_availability_projection"):
        d = dict(zip(cols, row))
        agent = str(d.get(agent_col, ""))
        state = str(d.get(state_col, "")) if state_col else ""
        task_id = d.get(task_col) if task_col else None
        reason: str | None = None
        if state == "agent_configuration_missing":
            reason = ("projection state=agent_configuration_missing "
                      "(host never refreshed)")
        elif state == "active_task_owned":
            t = conn.execute(
                f"SELECT status FROM workspace_tasks WHERE {task_key} = ?",
                (task_id,)).fetchone() if task_id else None
            if t is None:
                reason = ("active_task_owned but bound task not found "
                          "in workspace_tasks")
            elif t[0] != WS_TASK_STATUS_IN_PROGRESS:
                reason = (f"active_task_owned but bound task status={t[0]} "
                          f"(InProgress={WS_TASK_STATUS_IN_PROGRESS})")
        if reason:
            # IRON RULE: never report the legal 6a8 / 3bd2a4b0 projection
            if agent.endswith(PROTECTED_AGENT_SUFFIX) \
                    and str(task_id) == PROTECTED_TASK_ID:
                protected_hits.append(
                    f"{agent} task={task_id} state={state} -> PROTECTED "
                    f"(legal projection, iron rule)")
                continue
            d["_stale_reason"] = reason
            rows_out.append(d)
    pk_col, _ = _pk_column(conn, "agent_availability_projection")
    return {"count": len(rows_out), "rows": rows_out,
            "protected": protected_hits, "columns": cols, "pk": pk_col}


def detect_e(conn: sqlite3.Connection, status4_count: int) -> dict[str, Any]:
    cols = table_columns(conn, "task_assignment_attempts")
    require_columns(cols, "task_assignment_attempts", ["task_id", "status"])
    rows = [dict(zip(cols, r)) for r in conn.execute(
        "SELECT * FROM task_assignment_attempts WHERE status = 4 ORDER BY rowid")]
    by_task: dict[str, dict[str, Any]] = {}
    ecols = table_columns(conn, "task_events")
    order = _order_by(ecols)
    time_col = _time_column(ecols)
    for attempt in rows:
        tid = attempt["task_id"]
        entry = by_task.setdefault(
            tid, {"attempts": [], "events": [], "task": None})
        entry["attempts"].append(attempt)
    tcols = table_columns(conn, "workspace_tasks")
    task_key = _task_key_column(conn, tcols)
    task_title = _title_column(tcols)
    tsel = ", ".join(c for c in (task_key, "status", task_title) if c)
    for tid, entry in by_task.items():
        t = conn.execute(
            f"SELECT {tsel} FROM workspace_tasks WHERE {task_key} = ?",
            (tid,)).fetchone()
        entry["task"] = dict(zip(
            [c for c in (task_key, "status", task_title) if c], t)) if t else None
        if "event_type" in ecols:
            sel_cols = [c for c in ("event_type", time_col, "sequence")
                        if c and c in ecols]
            if sel_cols:
                entry["events"] = [dict(zip(sel_cols, r)) for r in conn.execute(
                    f"SELECT {', '.join(sel_cols)} FROM task_events "
                    f"WHERE task_id = ? ORDER BY {order}", (tid,))]
    return {"count": status4_count, "rows": rows, "by_task": by_task,
            "columns": cols, "disposition_constant": DISPOSITION_NEEDS_APPROVAL}


# --------------------------------------------------------------------------
# Repair plan (dry-run prints SQL; --apply executes on BACKUP COPY only)
# --------------------------------------------------------------------------
def build_fix_plan(data: dict[str, dict[str, Any]]) -> list[dict[str, str]]:
    """Return list of {id, title, sql, note}. SQL is executed only in --apply
    against the temp/ backup copy. Class e is never auto-repaired."""
    plan: list[dict[str, str]] = []
    a = data["a"]
    if a["samples"] and a["missing"]:
        plan.append({
            "id": "a-backfill",
            "title": (f"Backfill TaskCompleted({EVENT_TASK_COMPLETED}) events "
                      f"for {a['count']} Completed tasks"),
            "sql": ("INSERT INTO task_events (...) VALUES (...) -- dynamic "
                    "clone of positive sample event row "
                    f"(template task={a['samples'][0].get('task_id')}); per "
                    "missing task: set task_id, sequence=max+1, new TEXT pk, "
                    "timestamps=now (columns resolved from PRAGMA at runtime)"),
            "note": "template = first positive sample; see class a dump"})
    b = data["b"]
    plan.append({
        "id": "b-drop-empty-bindings",
        "title": (f"Delete {b['both_empty']} binding rows with BOTH ids "
                  f"empty (no fact at all)"),
        "sql": ("DELETE FROM task_execution_bindings "
                "WHERE (execution_id IS NULL OR execution_id = '') "
                "AND (session_id IS NULL OR session_id = '');"),
        "note": "rows where only one id is empty are kept for human review"})
    c = data["c"]
    done_ids = [r["task_id"] for r in c["rows"]
                if r["task_status"] == WS_TASK_STATUS_COMPLETED]
    dead_ids = [r["task_id"] for r in c["rows"]
                if r["task_status"] in WS_TASK_STATUS_TERMINAL_BAD]
    live_rows = [r for r in c["rows"]
                 if r["task_status"] is not None
                 and r["task_status"] not in WS_TASK_STATUS_TERMINAL_BAD
                 and r["task_status"] != WS_TASK_STATUS_COMPLETED]
    missing_task = [r for r in c["rows"] if r["task_status"] is None]
    if done_ids:
        plan.append({
            "id": "c-release-completed",
            "title": (f"Release attempts of {len(done_ids)} Completed tasks "
                      f"-> status=Completed({ASSIGN_COMPLETED})"),
            "sql": (f"UPDATE task_assignment_attempts SET status = "
                    f"{ASSIGN_COMPLETED} WHERE status IN "
                    f"({ASSIGN_ASSIGNED}, {ASSIGN_ACCEPTED}) AND task_id IN "
                    f"({', '.join(repr(i) for i in done_ids)});"),
            "note": "attempt reaches terminal Completed state"})
    if dead_ids:
        plan.append({
            "id": "c-release-dead",
            "title": (f"Release attempts of {len(dead_ids)} Failed/Cancelled/"
                      f"Archived tasks -> Rejected({ASSIGN_REJECTED})"),
            "sql": (f"UPDATE task_assignment_attempts SET status = "
                    f"{ASSIGN_REJECTED} WHERE status IN "
                    f"({ASSIGN_ASSIGNED}, {ASSIGN_ACCEPTED}) AND task_id IN "
                    f"({', '.join(repr(i) for i in dead_ids)});"),
            "note": "terminal tasks can never claim these attempts again"})
    for r in live_rows:
        plan.append({
            "id": "c-keep-live",
            "title": (f"KEEP attempt of still-active task "
                      f"(status={r['task_status']}) - no SQL"),
            "sql": "-- none: live task may legitimately hold this assignment",
            "note": f"task={r['task_id']} title={_short(r['task_title'], 80)}"})
    for r in missing_task:
        plan.append({
            "id": "c-missing-task",
            "title": "Attempt bound to missing task - no SQL",
            "sql": "-- none: task row absent; human decision",
            "note": f"attempt task_id={r['task_id']}"})
    d = data["d"]
    pk = d.get("pk")
    for row in d["rows"]:
        pkv = row.get(pk) if pk else None
        plan.append({
            "id": "d-drop-stale-projection",
            "title": f"Delete stale availability projection row (pk={pkv})",
            "sql": (f"DELETE FROM agent_availability_projection "
                    f"WHERE {pk} = {pkv!r};") if pk
                   else "-- cannot determine pk",
            "note": row["_stale_reason"]})
    plan.append({
        "id": "e-human",
        "title": (f"status=4 attempts x{data['e']['count']} - PENDING HUMAN "
                  f"DECISION, no SQL"),
        "sql": ("-- none: suspected TaskDisposition.NeedsApproval(4) leak "
                "(WorkspaceTaskModels.cs:63); see dry-run dump for evidence"),
        "note": "apply mode never touches status=4 rows"})
    return plan


def _apply_backfill_events(conn: sqlite3.Connection,
                           a: dict[str, Any]) -> int:
    """Clone the positive sample event for every Completed-missing task."""
    ecols = table_columns(conn, "task_events")
    template = a["samples"][0]
    pk, int_pk = _pk_column(conn, "task_events")
    time_col = _time_column(ecols)
    inserted = 0
    for task_id, _title in a["missing"]:
        vals: dict[str, Any] = dict(template)
        vals["task_id"] = task_id
        if "sequence" in ecols:
            mx = conn.execute(
                "SELECT COALESCE(MAX(sequence), 0) FROM task_events "
                "WHERE task_id = ?", (task_id,)).fetchone()[0]
            vals["sequence"] = mx + 1
        if pk and not int_pk:
            vals[pk] = uuid.uuid4().hex
        if time_col:
            vals[time_col] = _now()
        cols = ", ".join(ecols)
        ph = ", ".join("?" for _ in ecols)
        conn.execute(f"INSERT INTO task_events ({cols}) VALUES ({ph})",
                     [vals.get(c) for c in ecols])
        inserted += 1
    return inserted


# --------------------------------------------------------------------------
# Reporting
# --------------------------------------------------------------------------
def run_detections(conn: sqlite3.Connection) -> dict[str, dict[str, Any]]:
    a = detect_a(conn)
    b = detect_b(conn)
    c = detect_c(conn)
    d = detect_d(conn)
    e = detect_e(conn, c["status4_count"])
    return {"a": a, "b": b, "c": c, "d": d, "e": e}


def render_report(data: dict[str, dict[str, Any]], db_path: Path,
                  dry_run: bool) -> str:
    counts = {k: data[k]["count"] for k in "abcde"}
    lines: list[str] = []
    lines.append("# 任务看板历史脏数据审计报告")
    lines.append("")
    lines.append("- 时间：" + _now()
                 + "（UTC，时点快照；宿主持续写入，计数允许漂移）")
    mode = ("dry-run 只读 mode=ro + PRAGMA query_only=ON" if dry_run
            else "apply：备份副本上修复")
    lines.append(f"- 目标库：`{db_path}`（{mode}）")
    lines.append("- 看板卡：4ed930e7 原子任务③；输入："
                 "`temp/audit-recall-20260902.md`")
    lines.append("")
    lines.append("## 汇总对比基线")
    lines.append("")
    lines.append("| 检测 | 基线（卡 4ed930e7） | 盘点报告 2026-09-02 | "
                 "本次实测 | 差异说明 |")
    lines.append("|---|---|---|---|---|")
    recall = {"a": 32, "b": 39, "c": 32, "d": 1, "e": 19}
    notes = {
        "a": "Completed 持续新增，正样本事件亦在增长",
        "b": "空绑定随历史 Completed 无 execution 事实持续累积",
        "c": "释放/新增随调度活动波动",
        "d": "陈旧投影行",
        "e": "status=4 疑似 disposition 泄漏",
    }
    for k in "abcde":
        lines.append(f"| {k}) | {BASELINE[k]} | {recall[k]} | "
                     f"{counts[k]} | {notes[k]} |")
    lines.append("")
    a = data["a"]
    lines.append(f"## a) Completed({WS_TASK_STATUS_COMPLETED}) 缺 "
                 f"TaskCompleted({EVENT_TASK_COMPLETED}) 事件 — {counts['a']} 个"
                 f"（Completed 总数 {a['total_completed']}）")
    lines.append("")
    lines.append("| task_id | title |")
    lines.append("|---|---|")
    for tid, title in a["missing"]:
        lines.append(f"| {tid} | {_short(title, 80)} |")
    lines.append("")
    lines.append(f"正样本（已有完成事实，补种模板，{len(a['with_event'])} 个）：")
    lines.append("")
    for tid, title in a["with_event"]:
        lines.append(f"- {tid} {_short(title, 60)}")
    lines.append("")
    lines.append("补种格式模板（正样本事件行全字段，值已截断显示）：")
    lines.append("")
    lines.append("```json")
    for s in a["samples"]:
        lines.append("{\n" + ",\n".join(
            f'  "{k}": {_short(v, 120)!r}' for k, v in s.items()) + "\n}")
    lines.append("```")
    lines.append("")
    b = data["b"]
    lines.append(f"## b) task_execution_bindings 空值 — 任一空 "
                 f"{counts['b']}/{b['total']}（exec 空 {b['empty_exec']}，"
                 f"session 空 {b['empty_sess']}，皆空 {b['both_empty']}）")
    lines.append("")
    lines.append("样本（最多 50 行，全列，列示前 10 行）：")
    lines.append("")
    for r in b["rows"][:10]:
        lines.append("- " + ", ".join(f"{k}={_short(v, 60)}"
                                      for k, v in r.items()))
    if len(b["rows"]) > 10:
        lines.append(f"- ... 共 {len(b['rows'])} 行，余略")
    lines.append("")
    c = data["c"]
    lines.append(f"## c) task_assignment_attempts 未释放"
                 f"（status 0/1）— {counts['c']}")
    lines.append("")
    lines.append("状态分布：" + ", ".join(f"status={s}:{n}"
                                         for s, n in c["dist"]))
    lines.append("")
    lines.append("| attempt id | task_id | attempt.status | task.status "
                 "| task title |")
    lines.append("|---|---|---|---|---|")
    for r in c["rows"]:
        lines.append(f"| {r.get('id')} | {r['task_id']} | {r['status']} | "
                     f"{r['task_status']} | {_short(r['task_title'], 60)} |")
    lines.append("")
    d = data["d"]
    lines.append(f"## d) agent_availability_projection 陈旧投影 — {counts['d']}")
    lines.append("")
    for p in d["protected"]:
        lines.append(f"- 铁律保护（未列入修复）：{p}")
    for r in d["rows"]:
        lines.append("- " + ", ".join(
            f"{k}={_short(v, 80)}" for k, v in r.items()
            if k != "_stale_reason"))
        lines.append(f"  - 陈旧原因：{r['_stale_reason']}")
    if not d["rows"] and not d["protected"]:
        lines.append("- none")
    lines.append("")
    e = data["e"]
    lines.append(f"## e) attempts.status=4（枚举外值，疑似 disposition 泄漏）"
                 f"— {counts['e']} 行全字段 dump")
    lines.append("")
    lines.append("假设：store 层把 `TaskDisposition.NeedsApproval=4`"
                 "（WorkspaceTaskModels.cs:63）写入 "
                 "`task_assignment_attempts.status`，而 `AssignmentStatus`"
                 "（同文件 :338）合法值仅 0~3。")
    lines.append("")
    for tid, entry in e["by_task"].items():
        t = entry["task"]
        lines.append(f"### task {tid}")
        lines.append("")
        if t:
            title = next((v for k, v in t.items()
                          if k in ("title", "name", "subject")), "")
            lines.append(f"- task.status={t.get('status')} "
                         f"title={_short(title, 80)}")
        else:
            lines.append("- task row MISSING in workspace_tasks")
        lines.append("")
        lines.append("attempts（全字段）：")
        lines.append("")
        lines.append("```json")
        for at in entry["attempts"]:
            lines.append("{\n" + ",\n".join(
                f'  "{k}": {_short(v, 200)!r}' for k, v in at.items())
                + "\n}")
        lines.append("```")
        lines.append("")
        lines.append("事件序列（task_events，按既有列排序）：")
        lines.append("")
        if entry["events"]:
            keys = list(entry["events"][0].keys())
            lines.append("| " + " | ".join(keys) + " |")
            lines.append("|" + "---|" * len(keys))
            for ev in entry["events"]:
                lines.append("| " + " | ".join(_short(ev[k], 40)
                                               for k in keys) + " |")
        else:
            lines.append("- no events")
        lines.append("")
    lines.append("### 证据判读要点")
    lines.append("")
    lines.append("- 若 status=4 行的 task 在 attempt 创建后出现 TaskBlocked(8) "
                 "事件且 task.status=Blocked(7)，则与 disposition="
                 "NeedsApproval(4) 的产物（Blocked + "
                 "blockerKind=approval_required）吻合，支持泄漏假设。")
    lines.append("- 若 status=4 行的 task 无任何 Blocked 痕迹，则假设被证伪，"
                 "需另查写入路径。")
    lines.append("")
    lines.append("## 修复草案（--apply 将在备份副本上执行；dry-run 不执行）")
    lines.append("")
    for item in build_fix_plan(data):
        lines.append(f"### {item['id']}: {item['title']}")
        lines.append("")
        lines.append("```sql")
        lines.append(item["sql"])
        lines.append("```")
        if item.get("note"):
            lines.append(f"- {item['note']}")
        lines.append("")
    lines.append("## 风险与限制")
    lines.append("")
    lines.append("- 运行中库时点快照，计数与基线/盘点报告的漂移不等于新问题。")
    lines.append("- d 类铁律：6a8 + 3bd2a4b0…（InProgress）投影为合法，"
                 "永不列入修复。")
    lines.append("- e 类不自动修复，需人工实锤后另行裁决。")
    lines.append("- --apply 只写 temp/ 备份副本，源库零写入。")
    lines.append("")
    return "\n".join(lines)


# --------------------------------------------------------------------------
# Apply mode
# --------------------------------------------------------------------------
def run_apply(args: argparse.Namespace, db_path: Path) -> int:
    """Repair mode: backup first (forced), then repair the COPY in temp/."""
    if not args.backup:
        print("[abort] --apply requires --backup (forced). Source DB is "
              "never written by this tool.", file=sys.stderr)
        return 2
    root = default_output_root()
    root.mkdir(parents=True, exist_ok=True)
    stamp = _dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    dest = Path(args.backup) if Path(args.backup).is_absolute() \
        else root / args.backup
    if not Path(args.backup).is_absolute() and args.backup == "auto":
        dest = root / f"task-board-backup-{stamp}.db"
    print(f"[apply] backup {db_path} -> {dest}")
    src = open_readonly(db_path)
    dst = sqlite3.connect(str(dest))
    src.backup(dst)
    dst.commit()
    src.close()
    print(f"[apply] backup done ({dest.stat().st_size} bytes); "
          f"running repairs on the COPY")
    conn = dst
    data = run_detections(conn)
    executed = 0
    for item in build_fix_plan(data):
        sql = item["sql"].strip()
        if not sql or sql.startswith("--"):
            print(f"  skip {item['id']}: {item['title']}")
            continue
        if item.get("note") and item["id"] == "a-backfill":
            n = _apply_backfill_events(conn, data["a"])
            print(f"  done {item['id']}: backfilled {n} TaskCompleted events")
            executed += n
            continue
        cur = conn.execute(sql)
        print(f"  done {item['id']}: rows affected={cur.rowcount} "
              f"-- {item['title']}")
        executed += max(cur.rowcount, 0)
    conn.commit()
    conn.close()
    print(f"[apply] complete; {executed} statements/rows affected "
          f"on COPY {dest}")
    print(f"[apply] source DB untouched: {db_path}")
    return 0


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------
def main() -> int:
    parser = argparse.ArgumentParser(
        description="Task board dirty-data audit (default: read-only dry-run).")
    parser.add_argument("--db",
                        default=str(resolve_data_paths().platform_db_file()),
                        help="Path to pudding_platform.db")
    parser.add_argument("--out", default=None,
                        help="Write full Markdown report to this path "
                             "(usually under temp/)")
    parser.add_argument("--apply", action="store_true",
                        help="Repair mode. FORCES --backup; repairs run on "
                             "the backup COPY only.")
    parser.add_argument("--backup", nargs="?", const="auto", default=None,
                        help="Backup file for --apply (default: "
                             "temp/diagnostics/task-board-backup-<ts>.db)")
    args = parser.parse_args()

    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

    db_path = Path(args.db)
    if not db_path.exists():
        raise SystemExit(f"[fatal] database not found: {db_path}")

    if args.apply:
        return run_apply(args, db_path)

    conn = open_readonly(db_path)
    try:
        data = run_detections(conn)
    finally:
        conn.close()

    counts = {k: data[k]["count"] for k in "abcde"}
    print("== task board audit (DRY-RUN, read-only) ==")
    print(f"db: {db_path}")
    for k in "abcde":
        base = BASELINE[k]
        delta = counts[k] - base
        print(f"  {k}) count={counts[k]:>3}  baseline={base:>3}  "
              f"delta={delta:+d}")
    if args.out:
        out_path = Path(args.out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        report = render_report(data, db_path, dry_run=True)
        out_path.write_text(report, encoding="utf-8")
        print(f"report written: {out_path} ({out_path.stat().st_size} bytes)")
    else:
        print("tip: pass --out <path.md> to write the full report")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
