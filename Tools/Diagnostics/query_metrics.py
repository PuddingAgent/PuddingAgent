#!/usr/bin/env python3
"""Query Pudding SQLite metrics for local telemetry analysis.

The first version reads the structured tables that already exist today:
runtime_activity, session_event_log, and TokenUsageEvents. It intentionally
uses only Python stdlib so it can run in the repo .venv without extra packages.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
from collections import defaultdict
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Any, Iterable

from pudding_paths import resolve_data_paths

DEFAULT_DB = resolve_data_paths().platform_db_file()


def table_exists(conn: sqlite3.Connection, table_name: str) -> bool:
    row = conn.execute(
        "select 1 from sqlite_master where type='table' and name=?",
        (table_name,),
    ).fetchone()
    return row is not None


def query_tool_usage(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    metrics = _load_telemetry_metrics(
        db_path,
        days=days,
        session_id=session_id,
        category="tool",
    )
    metrics = [
        row for row in metrics
        if row.get("name") in ("tool.call", "tool.execution")
    ]
    if metrics:
        return _summarize_tool_metrics(metrics)

    activities = _load_runtime_activities(
        db_path,
        days=days,
        session_id=session_id,
        component="tool_runner",
        operation="execute_tool",
    )

    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in activities:
        metadata = row.get("metadata") or {}
        tool_name = metadata.get("tool_name") or "unknown"
        grouped[str(tool_name)].append(row)

    result = []
    for tool_name, rows in sorted(grouped.items()):
        durations = [_int_or_none(row.get("durationMs")) for row in rows]
        durations = [value for value in durations if value is not None]
        success_count = sum(1 for row in rows if row.get("status") == "succeeded")
        failure_count = sum(1 for row in rows if row.get("status") == "failed")
        result.append({
            "toolName": tool_name,
            "calls": len(rows),
            "success": success_count,
            "failed": failure_count,
            "successRate": _safe_ratio(success_count, len(rows)),
            "avgDurationMs": _avg(durations),
            "p95DurationMs": _percentile(durations, 95),
            "maxDurationMs": max(durations) if durations else None,
            "avgFailuresBeforeSuccess": _avg_failures_before_success(rows),
            "lastError": _last_error(rows),
        })
    return result


def query_tool_output_stats(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
    min_chars: int = 0,
    limit: int = 50,
) -> list[dict[str, Any]]:
    metrics = _load_telemetry_metrics(
        db_path,
        days=days,
        session_id=session_id,
        category="tool",
    )
    rows = []
    for row in metrics:
        if row.get("name") not in ("tool.call", "tool.execution"):
            continue
        dimensions = row.get("dimensions") or {}
        output_chars = _int_or_zero(dimensions.get("output_char_count") or dimensions.get("output_length"))
        error_chars = _int_or_zero(dimensions.get("error_char_count") or dimensions.get("error_length"))
        total_chars = _int_or_zero(dimensions.get("total_text_char_count")) or output_chars + error_chars
        if total_chars < min_chars:
            continue
        rows.append({
            "metricId": row.get("metricId"),
            "timestamp": row.get("timestamp"),
            "sessionId": row.get("sessionId"),
            "workspaceId": row.get("workspaceId"),
            "toolName": dimensions.get("tool_name") or "unknown",
            "status": row.get("status"),
            "durationMs": row.get("durationMs"),
            "outputChars": output_chars,
            "outputLines": _int_or_zero(dimensions.get("output_line_count")),
            "errorChars": error_chars,
            "errorLines": _int_or_zero(dimensions.get("error_line_count")),
            "totalTextChars": total_chars,
            "totalTextLines": _int_or_zero(dimensions.get("total_text_line_count")),
            "outputSizeLevel": dimensions.get("output_size_level") or "unknown",
            "toolStage": dimensions.get("tool_stage"),
            "errorCode": row.get("errorCode"),
            "errorMessage": row.get("errorMessage"),
        })
    rows.sort(key=lambda item: (item["totalTextChars"], item.get("durationMs") or 0), reverse=True)
    return rows[:limit]


def query_llm_latency(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    metrics = _load_telemetry_metrics(
        db_path,
        days=days,
        session_id=session_id,
        category="llm",
    )
    activities = _load_runtime_activities(
        db_path,
        days=days,
        session_id=session_id,
        component="llm_gateway",
    )
    token_rows = _load_token_usage_events(db_path, days=days, session_id=session_id)

    grouped: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in activities:
        metadata = row.get("metadata") or {}
        provider = metadata.get("provider_id") or metadata.get("endpoint") or "unknown"
        model = metadata.get("model") or "unknown"
        grouped[(str(provider), str(model))].append(row)

    token_grouped: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in token_rows:
        token_grouped[(row.get("ProviderId") or "unknown", row.get("ModelId") or "unknown")].append(row)

    if metrics:
        grouped.clear()
        for row in metrics:
            dimensions = row.get("dimensions") or {}
            provider = dimensions.get("provider_id") or dimensions.get("endpoint") or "unknown"
            model = dimensions.get("model") or "unknown"
            grouped[(str(provider), str(model))].append(row)

    keys = set(grouped) | set(token_grouped)
    result = []
    for provider, model in sorted(keys):
        rows = grouped.get((provider, model), [])
        durations = [_int_or_none(row.get("durationMs")) for row in rows]
        durations = [value for value in durations if value is not None]
        successes = sum(1 for row in rows if row.get("status") == "succeeded")
        failures = sum(1 for row in rows if row.get("status") == "failed")
        usage_rows = token_grouped.get((provider, model), [])
        result.append({
            "provider": provider,
            "model": model,
            "calls": len(rows),
            "success": successes,
            "failed": failures,
            "successRate": _safe_ratio(successes, len(rows)),
            "avgDurationMs": _avg(durations),
            "p95DurationMs": _percentile(durations, 95),
            "maxDurationMs": max(durations) if durations else None,
            "tokenEvents": len(usage_rows),
            "promptTokens": sum(_int_or_zero(row.get("PromptTokens")) for row in usage_rows),
            "completionTokens": sum(_int_or_zero(row.get("CompletionTokens")) for row in usage_rows),
            "totalTokens": sum(_int_or_zero(row.get("TotalTokens")) for row in usage_rows),
            "totalCost": _sum_decimal_text(row.get("TotalCost") for row in usage_rows),
        })
    return result


def query_cache_hit_rate(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 30,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    rows = _load_token_usage_events(db_path, days=days, session_id=session_id)
    grouped: dict[tuple[str, str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        provider = row.get("ProviderId") or "unknown"
        model = row.get("ModelId") or "unknown"
        prefix = row.get("PrefixHash") or "no-prefix"
        grouped[(provider, model, prefix)].append(row)

    result = []
    for (provider, model, prefix), items in sorted(grouped.items()):
        hit = sum(_int_or_zero(row.get("CacheHitTokens")) for row in items)
        miss = sum(_int_or_zero(row.get("CacheMissTokens")) for row in items)
        eligible = sum(_int_or_zero(row.get("CacheEligibleTokens")) for row in items)
        result.append({
            "provider": provider,
            "model": model,
            "prefixHash": prefix,
            "events": len(items),
            "cacheHitTokens": hit,
            "cacheMissTokens": miss,
            "cacheEligibleTokens": eligible,
            "hitRate": _safe_ratio(hit, eligible),
            "totalTokens": sum(_int_or_zero(row.get("TotalTokens")) for row in items),
            "totalCost": _sum_decimal_text(row.get("TotalCost") for row in items),
            "firstChangeReason": _first_present(row.get("PrefixChangeReason") for row in items),
        })
    return result


def query_context_layer_stats(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 30,
    session_id: str | None = None,
    provider_id: str | None = None,
    model_id: str | None = None,
) -> list[dict[str, Any]]:
    rows = _load_context_layer_events(
        db_path,
        days=days,
        session_id=session_id,
        provider_id=provider_id,
        model_id=model_id,
    )
    total_layer_tokens = sum(_int_or_zero(row.get("token_count")) for row in rows)
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        grouped[row.get("layer_name") or "unknown"].append(row)

    result = []
    for layer_name, items in sorted(
        grouped.items(),
        key=lambda pair: (
            min(_int_or_zero(row.get("layer_order")) for row in pair[1]),
            pair[0],
        ),
    ):
        tokens = [_int_or_zero(row.get("token_count")) for row in items]
        hit_rates = [
            float(row.get("estimated_cache_hit_rate"))
            for row in items
            if row.get("estimated_cache_hit_rate") not in (None, "")
        ]
        hit_tokens = sum(_int_or_zero(row.get("estimated_cache_hit_tokens")) for row in items)
        miss_tokens = sum(_int_or_zero(row.get("estimated_cache_miss_tokens")) for row in items)
        changed = sum(1 for row in items if _boolish(row.get("is_changed")))
        change_reasons: dict[str, int] = defaultdict(int)
        for row in items:
            reason = row.get("change_reason")
            if reason:
                change_reasons[str(reason)] += 1
        result.append({
            "layerName": layer_name,
            "layerOrder": min(_int_or_zero(row.get("layer_order")) for row in items),
            "layerRole": _first_present(row.get("layer_role") for row in items),
            "calls": len(items),
            "tokenCount": sum(tokens),
            "tokenShare": _safe_ratio(sum(tokens), total_layer_tokens),
            "avgTokens": _avg(tokens),
            "medianTokens": _median_number(tokens),
            "p95Tokens": _percentile(tokens, 95),
            "estimatedHitTokens": hit_tokens,
            "estimatedMissTokens": miss_tokens,
            "avgCacheHitRate": _avg_float(hit_rates),
            "medianCacheHitRate": _median_number(hit_rates),
            "changeCount": changed,
            "changeRate": _safe_ratio(changed, len(items)),
            "distinctHashes": len({row.get("content_hash") for row in items if row.get("content_hash")}),
            "changeReasons": dict(sorted(change_reasons.items())),
        })
    return result


def query_session_efficiency(
    db_path: str | Path = DEFAULT_DB,
    *,
    session_id: str | None = None,
    days: int = 7,
    limit: int = 50,
) -> list[dict[str, Any]]:
    events = _load_session_events(db_path, days=days, session_id=session_id, limit=100000)
    activities = _load_runtime_activities(db_path, days=days, session_id=session_id)
    metrics = _load_telemetry_metrics(db_path, days=days, session_id=session_id)
    token_rows = _load_token_usage_events(db_path, days=days, session_id=session_id)

    sessions = sorted({row.get("sessionId") for row in events + activities + metrics + token_rows if row.get("sessionId")})
    if session_id and session_id not in sessions:
        sessions.append(session_id)

    result = []
    for sid in sessions[:limit]:
        session_events = [row for row in events if row.get("sessionId") == sid]
        session_activities = [row for row in activities if row.get("sessionId") == sid]
        session_metrics = [row for row in metrics if row.get("sessionId") == sid]
        session_tokens = [row for row in token_rows if row.get("SessionId") == sid]
        timestamps = (
            [row.get("timestamp") for row in session_events]
            + [row.get("timestamp") for row in session_activities]
            + [row.get("timestamp") for row in session_metrics]
        )
        timestamps = [value for value in timestamps if value]
        started = min(timestamps) if timestamps else None
        ended = max(timestamps) if timestamps else None
        duration = _duration_ms(started, ended)
        delta_chars = sum(_payload_text_length(row.get("payload")) for row in session_events if row.get("eventType") == "delta")
        thinking_chars = sum(_payload_text_length(row.get("payload")) for row in session_events if row.get("eventType") == "thinking")
        tool_rows = [
            row for row in session_activities
            if row.get("component") == "tool_runner" and row.get("operation") == "execute_tool"
        ]
        tool_metrics = [
            row for row in session_metrics
            if row.get("category") == "tool" and row.get("name") == "tool.call"
        ]
        llm_rows = [row for row in session_activities if row.get("component") == "llm_gateway"]
        llm_metrics = [row for row in session_metrics if row.get("category") == "llm"]
        result.append({
            "sessionId": sid,
            "workspaceId": _first_present(row.get("workspaceId") for row in session_events + session_activities + session_metrics),
            "startedAt": started,
            "endedAt": ended,
            "durationMs": duration,
            "events": len(session_events),
            "deltaChars": delta_chars,
            "thinkingChars": thinking_chars,
            "llmCalls": len(llm_metrics) or len(llm_rows),
            "toolCalls": len(tool_metrics) or len(tool_rows),
            "toolFailures": sum(1 for row in (tool_metrics or tool_rows) if row.get("status") == "failed"),
            "tokens": sum(_int_or_zero(row.get("TotalTokens")) for row in session_tokens),
            "cacheHitRate": _safe_ratio(
                sum(_int_or_zero(row.get("CacheHitTokens")) for row in session_tokens),
                sum(_int_or_zero(row.get("CacheEligibleTokens")) for row in session_tokens),
            ),
            "charsPerSecond": _safe_ratio(delta_chars, duration / 1000 if duration else None),
        })
    result.sort(key=lambda row: row.get("startedAt") or "", reverse=True)
    return result[:limit]


def query_telemetry_summary(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    rows = _load_telemetry_metrics(db_path, days=days, session_id=session_id)
    grouped: dict[tuple[str, str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        grouped[(row.get("category") or "unknown", row.get("name") or "unknown", row.get("status") or "")].append(row)

    result = []
    for (category, name, status), items in sorted(grouped.items()):
        durations = [_int_or_none(row.get("durationMs")) for row in items]
        durations = [value for value in durations if value is not None]
        result.append({
            "category": category,
            "name": name,
            "status": status,
            "count": len(items),
            "sessions": len({row.get("sessionId") for row in items if row.get("sessionId")}),
            "avgDurationMs": _avg(durations),
            "p95DurationMs": _percentile(durations, 95),
            "maxDurationMs": max(durations) if durations else None,
            "lastError": _last_error(items),
        })
    return result


def query_subconscious_jobs(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    rows = _load_telemetry_metrics(
        db_path,
        days=days,
        session_id=session_id,
        category="memory",
    )
    rows = [
        row for row in rows
        if str(row.get("name") or "").startswith("subconscious_job.")
    ]

    grouped: dict[tuple[str, str], list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        dimensions = row.get("dimensions") or {}
        job_type = dimensions.get("job_type") or "unknown"
        source_hook_name = dimensions.get("source_hook_name") or "unknown"
        grouped[(str(job_type), str(source_hook_name))].append(row)

    result = []
    for (job_type, source_hook_name), items in sorted(grouped.items()):
        names = [str(row.get("name") or "") for row in items]
        enqueued = sum(1 for name in names if name == "subconscious_job.enqueue")
        leases = sum(1 for name in names if name == "subconscious_job.lease")
        completed = sum(1 for name in names if name == "subconscious_job.complete")
        retried = sum(1 for name in names if name == "subconscious_job.retry")
        dead_lettered = sum(1 for name in names if name == "subconscious_job.dead_letter")
        schedule_skips = sum(1 for name in names if name == "subconscious_job.schedule_skip")
        skip_reasons: dict[str, int] = defaultdict(int)
        for row in items:
            skip_reason = (row.get("dimensions") or {}).get("skip_reason")
            if skip_reason:
                skip_reasons[str(skip_reason)] += 1
        job_ids = {
            (row.get("dimensions") or {}).get("job_id")
            for row in items
            if (row.get("dimensions") or {}).get("job_id")
        }
        result.append({
            "jobType": job_type,
            "sourceHookName": source_hook_name,
            "events": len(items),
            "jobs": len(job_ids) or len(items),
            "enqueued": enqueued,
            "leases": leases,
            "completed": completed,
            "retried": retried,
            "deadLettered": dead_lettered,
            "scheduleSkips": schedule_skips,
            "skipReasons": dict(sorted(skip_reasons.items())),
            "sessions": len({row.get("sessionId") for row in items if row.get("sessionId")}),
            "completionRate": _safe_ratio(completed, enqueued),
            "retryRate": _safe_ratio(retried, enqueued),
            "deadLetterRate": _safe_ratio(dead_lettered, enqueued),
            "lastError": _last_error(items),
        })
    return result


def query_event_stats(
    db_path: str | Path = DEFAULT_DB,
    *,
    days: int = 7,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    rows = _load_session_events(db_path, days=days, session_id=session_id, limit=100000)
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        grouped[row.get("eventType") or "unknown"].append(row)
    return [
        {
            "eventType": event_type,
            "count": len(items),
            "sessions": len({row.get("sessionId") for row in items if row.get("sessionId")}),
        }
        for event_type, items in sorted(grouped.items())
    ]


def _summarize_tool_metrics(metrics: list[dict[str, Any]]) -> list[dict[str, Any]]:
    grouped: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in metrics:
        dimensions = row.get("dimensions") or {}
        tool_name = dimensions.get("tool_name") or "unknown"
        grouped[str(tool_name)].append(row)

    result = []
    for tool_name, rows in sorted(grouped.items()):
        durations = [_int_or_none(row.get("durationMs")) for row in rows]
        durations = [value for value in durations if value is not None]
        success_count = sum(1 for row in rows if row.get("status") == "succeeded")
        failure_count = sum(1 for row in rows if row.get("status") == "failed")
        estimated_input_tokens = sum(_int_or_zero((row.get("dimensions") or {}).get("estimated_input_tokens")) for row in rows)
        estimated_output_tokens = sum(_int_or_zero((row.get("dimensions") or {}).get("estimated_output_tokens")) for row in rows)
        output_chars = sum(_int_or_zero((row.get("dimensions") or {}).get("output_char_count") or (row.get("dimensions") or {}).get("output_length")) for row in rows)
        error_chars = sum(_int_or_zero((row.get("dimensions") or {}).get("error_char_count") or (row.get("dimensions") or {}).get("error_length")) for row in rows)
        total_text_chars = sum(_int_or_zero((row.get("dimensions") or {}).get("total_text_char_count")) for row in rows)
        oversized_results = sum(1 for row in rows if ((row.get("dimensions") or {}).get("output_size_level") in ("warning", "critical")))
        result.append({
            "toolName": tool_name,
            "calls": len(rows),
            "success": success_count,
            "failed": failure_count,
            "successRate": _safe_ratio(success_count, len(rows)),
            "avgDurationMs": _avg(durations),
            "p95DurationMs": _percentile(durations, 95),
            "maxDurationMs": max(durations) if durations else None,
            "estimatedInputTokens": estimated_input_tokens,
            "estimatedOutputTokens": estimated_output_tokens,
            "totalOutputChars": output_chars,
            "totalErrorChars": error_chars,
            "totalTextChars": total_text_chars or output_chars + error_chars,
            "oversizedResults": oversized_results,
            "avgFailuresBeforeSuccess": _avg_failures_before_success(rows),
            "lastError": _last_error(rows),
        })
    return result


def _load_telemetry_metrics(
    db_path: str | Path,
    *,
    days: int,
    session_id: str | None = None,
    category: str | None = None,
    name: str | None = None,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        if not table_exists(conn, "telemetry_metric_events"):
            return []
        clauses: list[str] = []
        args: list[Any] = []
        if session_id:
            clauses.append("session_id = ?")
            args.append(session_id)
        if category:
            clauses.append("category = ?")
            args.append(category)
        if name:
            clauses.append("name = ?")
            args.append(name)
        where = "where " + " and ".join(clauses) if clauses else ""
        sql = f"""
            select metric_id, trace_id, correlation_id, session_id, workspace_id,
                   execution_id, category, name, status, occurred_at_utc,
                   duration_ms, count_value, numeric_value, unit, severity,
                   summary, dimensions_json, debug_json, error_code, error_message
            from telemetry_metric_events
            {where}
            order by occurred_at_utc asc
        """
        rows = []
        for row in conn.execute(sql, args):
            item = {
                "source": "telemetry_metric",
                "timestamp": row["occurred_at_utc"],
                "metricId": row["metric_id"],
                "traceId": row["trace_id"],
                "correlationId": row["correlation_id"],
                "sessionId": row["session_id"],
                "workspaceId": row["workspace_id"],
                "executionId": row["execution_id"],
                "category": row["category"],
                "name": row["name"],
                "status": row["status"],
                "durationMs": row["duration_ms"],
                "countValue": row["count_value"],
                "numericValue": row["numeric_value"],
                "unit": row["unit"],
                "severity": row["severity"],
                "summary": row["summary"],
                "dimensions": _parse_json(row["dimensions_json"]) or {},
                "debug": _parse_json(row["debug_json"]),
                "errorCode": row["error_code"],
                "errorMessage": row["error_message"],
            }
            if _within_days(item["timestamp"], days):
                rows.append(item)
        return rows
    finally:
        conn.close()


def _load_runtime_activities(
    db_path: str | Path,
    *,
    days: int,
    session_id: str | None = None,
    component: str | None = None,
    operation: str | None = None,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        if not table_exists(conn, "runtime_activity"):
            return []
        clauses: list[str] = []
        args: list[Any] = []
        if session_id:
            clauses.append("session_id = ?")
            args.append(session_id)
        if component:
            clauses.append("component = ?")
            args.append(component)
        if operation:
            clauses.append("operation = ?")
            args.append(operation)
        where = "where " + " and ".join(clauses) if clauses else ""
        sql = f"""
            select activity_id, trace_id, correlation_id, session_id, workspace_id,
                   execution_id, component, operation, status, started_at_utc,
                   ended_at_utc, duration_ms, severity, summary, metadata_json,
                   error_code, error_message
            from runtime_activity
            {where}
            order by started_at_utc asc
        """
        rows = []
        for row in conn.execute(sql, args):
            item = {
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
                "metadata": _parse_json(row["metadata_json"]) or {},
                "errorCode": row["error_code"],
                "errorMessage": row["error_message"],
            }
            if _within_days(item["timestamp"], days):
                rows.append(item)
        return rows
    finally:
        conn.close()


def _load_session_events(
    db_path: str | Path,
    *,
    days: int,
    session_id: str | None = None,
    limit: int = 100000,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        if not table_exists(conn, "session_event_log"):
            return []
        where = "where session_id = ?" if session_id else ""
        args: list[Any] = [session_id] if session_id else []
        args.append(limit)
        sql = f"""
            select session_id, workspace_id, sequence_num, event_type, data,
                   recorded_at, trace_id, correlation_id, execution_id,
                   component, operation
            from session_event_log
            {where}
            order by recorded_at asc, sequence_num asc
            limit ?
        """
        rows = []
        for row in conn.execute(sql, args):
            item = {
                "source": "session_event",
                "timestamp": row["recorded_at"],
                "traceId": row["trace_id"],
                "correlationId": row["correlation_id"],
                "sessionId": row["session_id"],
                "workspaceId": row["workspace_id"],
                "executionId": row["execution_id"],
                "component": row["component"],
                "operation": row["operation"],
                "sequenceNum": row["sequence_num"],
                "eventType": row["event_type"],
                "payload": _parse_json(row["data"]),
            }
            if _within_days(item["timestamp"], days):
                rows.append(item)
        return rows
    finally:
        conn.close()


def _load_token_usage_events(
    db_path: str | Path,
    *,
    days: int,
    session_id: str | None = None,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        if not table_exists(conn, "TokenUsageEvents"):
            return []
        where = "where SessionId = ?" if session_id else ""
        args: list[Any] = [session_id] if session_id else []
        sql = f"""
            select SourceType, SourceId, WorkspaceId, SessionId, ProviderId, ModelId,
                   OccurredAtUtc, YearMonth, PromptTokens, CompletionTokens,
                   TotalTokens, CacheHitTokens, CacheMissTokens, CacheEligibleTokens,
                   CacheHitRate, InputCost, OutputCost, CacheHitCost, TotalCost,
                   PrefixHash, SystemPromptHash, ToolSpecHash, MemoryHash,
                   FewShotHash, PrefixChangeReason, PrefixMessageCount, PrefixToolCount
            from TokenUsageEvents
            {where}
            order by OccurredAtUtc asc
        """
        rows = []
        for row in conn.execute(sql, args):
            item = dict(row)
            item["sessionId"] = item.get("SessionId")
            item["timestamp"] = item.get("OccurredAtUtc")
            if _within_days(item["OccurredAtUtc"], days):
                rows.append(item)
        return rows
    finally:
        conn.close()


def _load_context_layer_events(
    db_path: str | Path,
    *,
    days: int,
    session_id: str | None = None,
    provider_id: str | None = None,
    model_id: str | None = None,
) -> list[dict[str, Any]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        if not table_exists(conn, "context_layer_metric_events"):
            return []
        clauses: list[str] = []
        args: list[Any] = []
        if session_id:
            clauses.append("session_id = ?")
            args.append(session_id)
        if provider_id:
            clauses.append("provider_id = ?")
            args.append(provider_id)
        if model_id:
            clauses.append("model_id = ?")
            args.append(model_id)
        where = "where " + " and ".join(clauses) if clauses else ""
        sql = f"""
            select source_type, source_id, workspace_id, session_id, provider_id,
                   model_id, occurred_at_utc, assembler_version, layout_version,
                   layer_name, layer_order, layer_role, token_count, char_count,
                   content_hash, previous_hash, is_changed, change_reason,
                   starts_at_token, ends_at_token, is_cache_eligible,
                   estimated_cache_hit_tokens, estimated_cache_miss_tokens,
                   estimated_cache_hit_rate, confidence, truncated_tokens,
                   truncated_reason, created_at_utc
            from context_layer_metric_events
            {where}
            order by occurred_at_utc asc, layer_order asc, layer_name asc
        """
        rows = []
        for row in conn.execute(sql, args):
            item = dict(row)
            item["sessionId"] = item.get("session_id")
            item["timestamp"] = item.get("occurred_at_utc")
            if _within_days(item["occurred_at_utc"], days):
                rows.append(item)
        return rows
    finally:
        conn.close()


def _within_days(timestamp: str | None, days: int) -> bool:
    if days <= 0 or not timestamp:
        return True
    parsed = _parse_datetime(timestamp)
    if parsed is None:
        return True
    return parsed >= datetime.now(UTC) - timedelta(days=days)


def _parse_datetime(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        normalized = value.replace("Z", "+00:00")
        parsed = datetime.fromisoformat(normalized)
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=UTC)
        return parsed.astimezone(UTC)
    except ValueError:
        return None


def _duration_ms(start: str | None, end: str | None) -> int | None:
    start_dt = _parse_datetime(start)
    end_dt = _parse_datetime(end)
    if start_dt is None or end_dt is None:
        return None
    return max(0, int((end_dt - start_dt).total_seconds() * 1000))


def _parse_json(value: str | None) -> Any:
    if not value:
        return None
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return value


def _payload_text_length(payload: Any) -> int:
    if isinstance(payload, dict):
        for key in ("delta", "text", "reply", "content"):
            value = payload.get(key)
            if isinstance(value, str):
                return len(value)
        return len(json.dumps(payload, ensure_ascii=False, separators=(",", ":")))
    if isinstance(payload, str):
        return len(payload)
    if payload is None:
        return 0
    return len(str(payload))


def _safe_ratio(numerator: float | int | None, denominator: float | int | None) -> float | None:
    if numerator is None or denominator in (None, 0):
        return None
    return round(float(numerator) / float(denominator), 6)


def _avg(values: list[int]) -> int | None:
    if not values:
        return None
    return int(round(sum(values) / len(values)))


def _percentile(values: list[int], percentile: int) -> int | None:
    if not values:
        return None
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, round((percentile / 100) * (len(ordered) - 1))))
    return ordered[index]


def _median_number(values: list[int] | list[float]) -> float | int | None:
    if not values:
        return None
    ordered = sorted(values)
    midpoint = len(ordered) // 2
    if len(ordered) % 2 == 1:
        return ordered[midpoint]
    return round((ordered[midpoint - 1] + ordered[midpoint]) / 2, 6)


def _avg_float(values: list[float]) -> float | None:
    if not values:
        return None
    return round(sum(values) / len(values), 6)


def _boolish(value: Any) -> bool:
    if isinstance(value, bool):
        return value
    if isinstance(value, int):
        return value != 0
    if isinstance(value, str):
        return value.strip().lower() in ("1", "true", "yes")
    return False


def _avg_failures_before_success(rows: list[dict[str, Any]]) -> float | None:
    by_session: dict[str, list[dict[str, Any]]] = defaultdict(list)
    for row in rows:
        by_session[row.get("sessionId") or "no-session"].append(row)

    failure_counts = []
    for session_rows in by_session.values():
        pending_failures = 0
        for row in sorted(session_rows, key=lambda item: item.get("timestamp") or ""):
            if row.get("status") == "failed":
                pending_failures += 1
            elif row.get("status") == "succeeded":
                failure_counts.append(pending_failures)
                pending_failures = 0

    if not failure_counts:
        return None
    return round(sum(failure_counts) / len(failure_counts), 3)


def _last_error(rows: list[dict[str, Any]]) -> str | None:
    failed = [row for row in rows if row.get("status") == "failed"]
    if not failed:
        return None
    row = sorted(failed, key=lambda item: item.get("timestamp") or "")[-1]
    return row.get("errorMessage") or row.get("summary")


def _int_or_none(value: Any) -> int | None:
    if value is None or value == "":
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _int_or_zero(value: Any) -> int:
    parsed = _int_or_none(value)
    return parsed if parsed is not None else 0


def _sum_decimal_text(values: Iterable[Any]) -> str:
    total = 0.0
    for value in values:
        if value in (None, ""):
            continue
        try:
            total += float(value)
        except (TypeError, ValueError):
            continue
    return f"{total:.6f}"


def _first_present(values: Iterable[Any]) -> Any:
    for value in values:
        if value not in (None, ""):
            return value
    return None


def print_table(rows: list[dict[str, Any]]) -> None:
    if not rows:
        print("(no rows)")
        return
    headers = list(rows[0].keys())
    print("\t".join(headers))
    for row in rows:
        print("\t".join("" if row.get(key) is None else str(row.get(key)) for key in headers))


def main() -> int:
    parser = argparse.ArgumentParser(description="Query Pudding telemetry metrics from SQLite.")
    parser.add_argument("--db", default=str(DEFAULT_DB), help="Path to pudding_platform.db")
    parser.add_argument("--format", choices=("table", "json"), default="table")
    sub = parser.add_subparsers(dest="command", required=True)

    def add_common(p: argparse.ArgumentParser, default_days: int) -> None:
        p.add_argument("--days", type=int, default=default_days, help="Lookback window in days; 0 means all rows.")
        p.add_argument("--session-id", help="Filter by session_id.")

    p_tool = sub.add_parser("tool-usage", help="Summarize tool calls from runtime_activity.")
    add_common(p_tool, 7)

    p_tool_output = sub.add_parser("tool-output", help="List tool output size metrics from telemetry_metric_events.")
    add_common(p_tool_output, 7)
    p_tool_output.add_argument("--min-chars", type=int, default=0, help="Only include rows with at least this many total text chars.")
    p_tool_output.add_argument("--limit", type=int, default=50)

    p_llm = sub.add_parser("llm-latency", help="Summarize LLM gateway latency and token usage.")
    add_common(p_llm, 7)

    p_cache = sub.add_parser("cache-hit-rate", help="Summarize prefix cache hit rates from TokenUsageEvents.")
    add_common(p_cache, 30)

    p_context = sub.add_parser("context-layers", help="Summarize context layer token shares and estimated cache hit rates.")
    add_common(p_context, 30)
    p_context.add_argument("--provider-id", help="Filter by provider_id.")
    p_context.add_argument("--model-id", help="Filter by model_id.")

    p_session = sub.add_parser("session-efficiency", help="Summarize per-session efficiency.")
    add_common(p_session, 7)
    p_session.add_argument("--limit", type=int, default=50)

    p_events = sub.add_parser("event-stats", help="Count session_event_log events by type.")
    add_common(p_events, 7)

    p_telemetry = sub.add_parser("telemetry-summary", help="Summarize telemetry_metric_events by category/name/status.")
    add_common(p_telemetry, 7)

    p_subconscious = sub.add_parser("subconscious-jobs", help="Summarize subconscious job queue metrics.")
    add_common(p_subconscious, 7)

    args = parser.parse_args()
    if args.command == "tool-usage":
        rows = query_tool_usage(args.db, days=args.days, session_id=args.session_id)
    elif args.command == "tool-output":
        rows = query_tool_output_stats(
            args.db,
            days=args.days,
            session_id=args.session_id,
            min_chars=args.min_chars,
            limit=args.limit)
    elif args.command == "llm-latency":
        rows = query_llm_latency(args.db, days=args.days, session_id=args.session_id)
    elif args.command == "cache-hit-rate":
        rows = query_cache_hit_rate(args.db, days=args.days, session_id=args.session_id)
    elif args.command == "context-layers":
        rows = query_context_layer_stats(
            args.db,
            days=args.days,
            session_id=args.session_id,
            provider_id=args.provider_id,
            model_id=args.model_id)
    elif args.command == "session-efficiency":
        rows = query_session_efficiency(args.db, days=args.days, session_id=args.session_id, limit=args.limit)
    elif args.command == "event-stats":
        rows = query_event_stats(args.db, days=args.days, session_id=args.session_id)
    elif args.command == "telemetry-summary":
        rows = query_telemetry_summary(args.db, days=args.days, session_id=args.session_id)
    elif args.command == "subconscious-jobs":
        rows = query_subconscious_jobs(args.db, days=args.days, session_id=args.session_id)
    else:
        raise AssertionError(args.command)

    if args.format == "json":
        print(json.dumps(rows, ensure_ascii=False, indent=2))
    else:
        print_table(rows)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
