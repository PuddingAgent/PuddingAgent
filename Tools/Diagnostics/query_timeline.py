#!/usr/bin/env python3
"""Query Pudding SQLite diagnostic timeline rows.

The tool intentionally uses only Python stdlib so it works inside the repo
`.venv` without installing extra packages.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path
from typing import Any

from pudding_paths import resolve_data_paths

DEFAULT_DB = resolve_data_paths().platform_db_file()


def table_exists(conn: sqlite3.Connection, table_name: str) -> bool:
    row = conn.execute(
        "select 1 from sqlite_master where type='table' and name=?",
        (table_name,),
    ).fetchone()
    return row is not None


def load_timeline(
    db_path: str | Path = DEFAULT_DB,
    *,
    session_id: str | None = None,
    trace_id: str | None = None,
    limit: int = 500,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")

    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        rows: list[dict[str, Any]] = []
        rows.extend(_load_runtime_activities(conn, session_id=session_id, trace_id=trace_id, limit=limit))
        rows.extend(_load_telemetry_metrics(conn, session_id=session_id, trace_id=trace_id, limit=limit))
        rows.extend(_load_session_events(conn, session_id=session_id, trace_id=trace_id, limit=limit))
    finally:
        conn.close()

    rows.sort(key=lambda row: row.get("timestamp") or "")
    if limit > 0:
        rows = rows[:limit]
    return rows


def _load_runtime_activities(
    conn: sqlite3.Connection,
    *,
    session_id: str | None,
    trace_id: str | None,
    limit: int,
) -> list[dict[str, Any]]:
    if not table_exists(conn, "runtime_activity"):
        return []

    where, args = _where_clause(session_id=session_id, trace_id=trace_id)
    sql = f"""
        select activity_id, trace_id, correlation_id, session_id, workspace_id,
               execution_id, component, operation, status, started_at_utc,
               ended_at_utc, duration_ms, severity, summary, metadata_json
        from runtime_activity
        {where}
        order by started_at_utc asc
        limit ?
    """
    args.append(limit)
    result = []
    for row in conn.execute(sql, args):
        metadata = _parse_json(row["metadata_json"])
        raw_stage = _json_get(metadata, "stage")
        stage = _normalize_stage(raw_stage, row["component"], row["operation"], status=row["status"])
        result.append({
            "source": "runtime_activity",
            "timestamp": row["started_at_utc"],
            "activityId": row["activity_id"],
            "traceId": row["trace_id"],
            "correlationId": row["correlation_id"],
            "sessionId": row["session_id"],
            "workspaceId": row["workspace_id"],
            "executionId": row["execution_id"],
            "component": row["component"],
            "operation": row["operation"],
            "status": row["status"],
            "durationMs": row["duration_ms"],
            "severity": row["severity"],
            "summary": row["summary"],
            "stage": stage,
            "stageOrder": _normalized_stage_order(metadata, stage),
            "stageDetail": _stage_detail(metadata, raw_stage, stage),
            "metadata": metadata,
        })
    return result


def _load_session_events(
    conn: sqlite3.Connection,
    *,
    session_id: str | None,
    trace_id: str | None,
    limit: int,
) -> list[dict[str, Any]]:
    if not table_exists(conn, "session_event_log"):
        return []

    where, args = _where_clause(session_id=session_id, trace_id=trace_id)
    sql = f"""
        select session_id, workspace_id, sequence_num, event_type, data,
               recorded_at, trace_id, correlation_id, execution_id,
               parent_execution_id, sub_agent_id, component, operation
        from session_event_log
        {where}
        order by recorded_at asc, sequence_num asc
        limit ?
    """
    args.append(limit)
    result = []
    for row in conn.execute(sql, args):
        payload = _parse_json(row["data"])
        stage = _normalize_stage(None, row["component"], row["operation"], event_type=row["event_type"])
        result.append({
            "source": "session_event",
            "timestamp": row["recorded_at"],
            "traceId": row["trace_id"],
            "correlationId": row["correlation_id"],
            "sessionId": row["session_id"],
            "workspaceId": row["workspace_id"],
            "executionId": row["execution_id"],
            "parentExecutionId": row["parent_execution_id"],
            "subAgentId": row["sub_agent_id"],
            "component": row["component"],
            "operation": row["operation"],
            "stage": stage,
            "stageOrder": _stage_order(stage),
            "sequenceNum": row["sequence_num"],
            "eventType": row["event_type"],
            "payloadPreview": _payload_preview(payload),
            "payload": payload,
        })
    return result


def _infer_stage(
    component: str | None,
    operation: str | None,
    event_type: str | None = None,
    status: str | None = None,
) -> str:
    if status and status.lower() in {"failed", "error"}:
        return "error"

    value = f"{component or ''} {operation or ''} {event_type or ''}".lower()
    if ".failed" in value or " failed" in value:
        return "error"
    if "ui." in value or "frontend" in value or "paint" in value:
        return "ui_render"
    if "sse" in value or "stream." in value or "fanout" in value or "delta" in value or "deliver" in value:
        return "stream_deliver"
    if "metadata.wait" in value:
        return "dispatch"
    if "metadata.received" in value:
        return "stream_deliver"
    if "done" in value or "completed" in value or "returned" in value:
        return "complete"
    if "request" in value or "http" in value or "chat.post.received" in value:
        return "request"
    if "route" in value or "router" in value or "agent.resolve" in value:
        return "routing"
    if "context" in value or "memory" in value or "history.hydrate" in value or "prompt" in value:
        return "context"
    if "tool" in value or "mcp" in value or "function" in value:
        return "tool"
    if "llm.prepare" in value or "llm_config" in value or "keyvault" in value or "inject_secrets" in value:
        return "llm_prepare"
    if "provider" in value or "llm_gateway" in value or "chat_stream" in value or "chunk" in value:
        return "llm_provider"
    if "append" in value or "persist" in value or "session_state" in value or "write" in value:
        return "stream_persist"
    if "thinking" in value:
        return "stream_deliver"
    if "dispatch" in value or "execution" in value or "run" in value:
        return "dispatch"
    return ""


def _normalize_stage(
    raw_stage: str | None,
    component: str | None,
    operation: str | None,
    event_type: str | None = None,
    status: str | None = None,
) -> str:
    if raw_stage and _stage_order(raw_stage):
        return raw_stage
    if raw_stage:
        return _infer_stage(raw_stage, None, None, status=status)
    return _infer_stage(component, operation, event_type=event_type, status=status)


def _normalized_stage_order(metadata: Any, stage: str) -> str:
    order = _json_get(metadata, "stage_order")
    if order and order != "999":
        return order
    return _stage_order(stage)


def _stage_detail(metadata: Any, raw_stage: str | None, stage: str) -> str | None:
    detail = _json_get(metadata, "stage_detail")
    if detail:
        return detail
    if raw_stage and raw_stage != stage:
        return raw_stage
    return None


def _stage_order(stage: str) -> str:
    orders = {
        "request": "010",
        "routing": "020",
        "dispatch": "030",
        "context": "040",
        "tool": "050",
        "llm_prepare": "060",
        "llm_provider": "070",
        "stream_persist": "080",
        "stream_deliver": "090",
        "ui_render": "100",
        "complete": "110",
        "error": "900",
    }
    return orders.get(stage, "")


def _load_telemetry_metrics(
    conn: sqlite3.Connection,
    *,
    session_id: str | None,
    trace_id: str | None,
    limit: int,
) -> list[dict[str, Any]]:
    if not table_exists(conn, "telemetry_metric_events"):
        return []

    where, args = _where_clause(session_id=session_id, trace_id=trace_id)
    sql = f"""
        select metric_id, trace_id, correlation_id, session_id, workspace_id,
               execution_id, category, name, status, occurred_at_utc,
               duration_ms, count_value, numeric_value, unit, severity,
               summary, dimensions_json, debug_json, error_code, error_message
        from telemetry_metric_events
        {where}
        order by occurred_at_utc asc
        limit ?
    """
    args.append(limit)
    result = []
    for row in conn.execute(sql, args):
        dimensions = _parse_json(row["dimensions_json"])
        raw_stage = _json_get(dimensions, "stage")
        stage = _normalize_stage(raw_stage, row["category"], row["name"], status=row["status"])
        result.append({
            "source": "telemetry_metric",
            "timestamp": row["occurred_at_utc"],
            "metricId": row["metric_id"],
            "traceId": row["trace_id"],
            "correlationId": row["correlation_id"],
            "sessionId": row["session_id"],
            "workspaceId": row["workspace_id"],
            "executionId": row["execution_id"],
            "component": row["category"],
            "operation": row["name"],
            "status": row["status"],
            "durationMs": row["duration_ms"],
            "severity": row["severity"],
            "summary": row["summary"],
            "stage": stage,
            "stageOrder": _normalized_stage_order(dimensions, stage),
            "stageDetail": _stage_detail(dimensions, raw_stage, stage),
            "countValue": row["count_value"],
            "numericValue": row["numeric_value"],
            "unit": row["unit"],
            "metadata": dimensions,
            "debug": _parse_json(row["debug_json"]),
            "errorCode": row["error_code"],
            "errorMessage": row["error_message"],
        })
    return result


def _where_clause(*, session_id: str | None, trace_id: str | None) -> tuple[str, list[Any]]:
    clauses = []
    args: list[Any] = []
    if session_id:
        clauses.append("session_id = ?")
        args.append(session_id)
    if trace_id:
        clauses.append("trace_id = ?")
        args.append(trace_id)
    if not clauses:
        return "", args
    return "where " + " and ".join(clauses), args


def _parse_json(value: str | None) -> Any:
    if not value:
        return None
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def _json_get(value: Any, key: str) -> Any:
    if isinstance(value, dict):
        return value.get(key)
    return None


def _payload_preview(payload: Any) -> str:
    if isinstance(payload, dict):
        for key in ("delta", "reply", "text", "type", "message"):
            value = payload.get(key)
            if isinstance(value, str) and value:
                return value[:120]
        return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))[:120]
    if isinstance(payload, str):
        return payload[:120]
    if payload is None:
        return ""
    return str(payload)[:120]


def print_table(rows: list[dict[str, Any]]) -> None:
    headers = ["timestamp", "source", "stage", "stageOrder", "component", "operation", "status", "eventType", "durationMs", "summary"]
    print("\t".join(headers))
    for row in rows:
        print("\t".join("" if row.get(key) is None else str(row.get(key)) for key in headers))


def main() -> int:
    parser = argparse.ArgumentParser(description="Query Pudding diagnostic timeline from SQLite.")
    parser.add_argument("--db", default=str(DEFAULT_DB), help="Path to pudding_platform.db")
    parser.add_argument("--session-id", help="Filter by session_id")
    parser.add_argument("--trace-id", help="Filter by trace_id")
    parser.add_argument("--limit", type=int, default=500)
    parser.add_argument("--format", choices=("table", "json"), default="table")
    args = parser.parse_args()

    rows = load_timeline(args.db, session_id=args.session_id, trace_id=args.trace_id, limit=args.limit)
    if args.format == "json":
        print(json.dumps(rows, ensure_ascii=False, indent=2))
    else:
        print_table(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
