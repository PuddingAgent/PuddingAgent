import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIR))

import query_metrics


class QueryMetricsTests(unittest.TestCase):
    def test_tool_usage_summarizes_success_failure_and_retry_recovery(self):
        with _sample_db() as db_path:
            rows = query_metrics.query_tool_usage(db_path, days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("bash", row["toolName"])
        self.assertEqual(3, row["calls"])
        self.assertEqual(2, row["success"])
        self.assertEqual(1, row["failed"])
        self.assertEqual(0.666667, row["successRate"])
        self.assertEqual(0.5, row["avgFailuresBeforeSuccess"])
        self.assertEqual("permission denied", row["lastError"])

    def test_llm_latency_combines_runtime_activity_and_token_usage(self):
        with _sample_db() as db_path:
            rows = query_metrics.query_llm_latency(db_path, days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("https://llm.example/v1", row["provider"])
        self.assertEqual("m1", row["model"])
        self.assertEqual(1, row["calls"])
        self.assertEqual(1200, row["avgDurationMs"])
        self.assertEqual(30, row["totalTokens"])
        self.assertEqual("0.030000", row["totalCost"])

    def test_cache_hit_rate_groups_by_prefix(self):
        with _sample_db() as db_path:
            rows = query_metrics.query_cache_hit_rate(db_path, days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("prefix-a", row["prefixHash"])
        self.assertEqual(10, row["cacheHitTokens"])
        self.assertEqual(5, row["cacheMissTokens"])
        self.assertEqual(0.666667, row["hitRate"])

    def test_context_layer_stats_reports_ratio_cache_and_volatility(self):
        with _sample_db() as db_path:
            rows = query_metrics.query_context_layer_stats(db_path, days=0, provider_id="p1")

        self.assertEqual(2, len(rows))
        by_layer = {row["layerName"]: row for row in rows}
        static = by_layer["L0-STATIC"]
        recent = by_layer["L5-RECENT"]
        self.assertEqual(90, static["tokenCount"])
        self.assertEqual(0.45, static["tokenShare"])
        self.assertEqual(45.0, static["medianTokens"])
        self.assertEqual(1.0, static["medianCacheHitRate"])
        self.assertEqual(0.0, static["changeRate"])
        self.assertEqual(110, recent["tokenCount"])
        self.assertEqual(0.55, recent["tokenShare"])
        self.assertEqual(55.0, recent["medianTokens"])
        self.assertEqual(0.25, recent["medianCacheHitRate"])
        self.assertEqual(0.5, recent["changeRate"])
        self.assertEqual({"history_changed": 1}, recent["changeReasons"])

    def test_session_efficiency_counts_events_tools_and_tokens(self):
        with _sample_db() as db_path:
            rows = query_metrics.query_session_efficiency(db_path, session_id="s1", days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("s1", row["sessionId"])
        self.assertEqual(2, row["events"])
        self.assertEqual(5, row["deltaChars"])
        self.assertEqual(1, row["thinkingChars"])
        self.assertEqual(3, row["toolCalls"])
        self.assertEqual(1, row["toolFailures"])
        self.assertEqual(30, row["tokens"])

    def test_tool_usage_prefers_telemetry_metric_events(self):
        with _sample_db() as db_path:
            _insert_telemetry_rows(db_path)
            rows = query_metrics.query_tool_usage(db_path, days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("browser", row["toolName"])
        self.assertEqual(2, row["calls"])
        self.assertEqual(1, row["success"])
        self.assertEqual(1, row["failed"])
        self.assertEqual(13, row["estimatedInputTokens"])
        self.assertEqual(21, row["estimatedOutputTokens"])
        self.assertEqual(72, row["totalOutputChars"])
        self.assertEqual(9, row["totalErrorChars"])
        self.assertEqual(81, row["totalTextChars"])
        self.assertEqual(1, row["oversizedResults"])

    def test_tool_output_stats_lists_oversized_metric_rows(self):
        with _sample_db() as db_path:
            _insert_telemetry_rows(db_path)
            rows = query_metrics.query_tool_output_stats(db_path, days=0, min_chars=50)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("browser", row["toolName"])
        self.assertEqual("metric-2", row["metricId"])
        self.assertEqual(77, row["totalTextChars"])
        self.assertEqual("warning", row["outputSizeLevel"])

    def test_telemetry_summary_groups_by_category_name_status(self):
        with _sample_db() as db_path:
            _insert_telemetry_rows(db_path)
            rows = query_metrics.query_telemetry_summary(db_path, days=0)

        keys = {(row["category"], row["name"], row["status"]): row for row in rows}
        self.assertEqual(1, keys[("tool", "tool.call", "succeeded")]["count"])
        self.assertEqual(40, keys[("tool", "tool.call", "failed")]["maxDurationMs"])

    def test_subconscious_jobs_summarizes_memory_metrics(self):
        with _sample_db() as db_path:
            _insert_subconscious_job_rows(db_path)
            rows = query_metrics.query_subconscious_jobs(db_path, days=0)

        self.assertEqual(1, len(rows))
        row = rows[0]
        self.assertEqual("memory.consolidate_session", row["jobType"])
        self.assertEqual("session.compressed", row["sourceHookName"])
        self.assertEqual(7, row["events"])
        self.assertEqual(2, row["jobs"])
        self.assertEqual(2, row["enqueued"])
        self.assertEqual(1, row["leases"])
        self.assertEqual(1, row["completed"])
        self.assertEqual(1, row["retried"])
        self.assertEqual(1, row["deadLettered"])
        self.assertEqual(1, row["scheduleSkips"])
        self.assertEqual({"skip_cooldown": 1}, row["skipReasons"])
        self.assertEqual(2, row["sessions"])
        self.assertEqual(0.5, row["completionRate"])
        self.assertEqual(0.5, row["retryRate"])
        self.assertEqual(0.5, row["deadLetterRate"])
        self.assertEqual("model unavailable", row["lastError"])


class _sample_db:
    def __enter__(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        db_path = Path(self.temp_dir.name) / "pudding_platform.db"
        conn = sqlite3.connect(db_path)
        try:
            conn.executescript(
                """
                create table runtime_activity (
                    activity_id text,
                    trace_id text,
                    correlation_id text,
                    session_id text,
                    workspace_id text,
                    execution_id text,
                    component text,
                    operation text,
                    status text,
                    started_at_utc text,
                    ended_at_utc text,
                    duration_ms integer,
                    severity text,
                    summary text,
                    metadata_json text,
                    error_code text,
                    error_message text
                );

                create table session_event_log (
                    id integer primary key,
                    session_id text,
                    workspace_id text,
                    sequence_num integer,
                    event_type text,
                    data text,
                    recorded_at text,
                    trace_id text,
                    correlation_id text,
                    execution_id text,
                    component text,
                    operation text
                );

                create table TokenUsageEvents (
                    SourceType text,
                    SourceId text,
                    WorkspaceId text,
                    SessionId text,
                    ProviderId text,
                    ModelId text,
                    OccurredAtUtc text,
                    YearMonth text,
                    PromptTokens integer,
                    CompletionTokens integer,
                    TotalTokens integer,
                    CacheHitTokens integer,
                    CacheMissTokens integer,
                    CacheEligibleTokens integer,
                    CacheHitRate real,
                    InputCost text,
                    OutputCost text,
                    CacheHitCost text,
                    TotalCost text,
                    PrefixHash text,
                    SystemPromptHash text,
                    ToolSpecHash text,
                    MemoryHash text,
                    FewShotHash text,
                    PrefixChangeReason text,
                    PrefixMessageCount integer,
                    PrefixToolCount integer
                );

                create table telemetry_metric_events (
                    metric_id text,
                    trace_id text,
                    correlation_id text,
                    session_id text,
                    workspace_id text,
                    execution_id text,
                    category text,
                    name text,
                    status text,
                    occurred_at_utc text,
                    duration_ms integer,
                    count_value integer,
                    numeric_value real,
                    unit text,
                    severity text,
                    summary text,
                    dimensions_json text,
                    debug_json text,
                    error_code text,
                    error_message text
                );

                create table context_layer_metric_events (
                    source_type text,
                    source_id text,
                    workspace_id text,
                    session_id text,
                    provider_id text,
                    model_id text,
                    occurred_at_utc text,
                    assembler_version text,
                    layout_version text,
                    layer_name text,
                    layer_order integer,
                    layer_role text,
                    token_count integer,
                    char_count integer,
                    content_hash text,
                    previous_hash text,
                    is_changed integer,
                    change_reason text,
                    starts_at_token integer,
                    ends_at_token integer,
                    is_cache_eligible integer,
                    estimated_cache_hit_tokens integer,
                    estimated_cache_miss_tokens integer,
                    estimated_cache_hit_rate real,
                    confidence text,
                    truncated_tokens integer,
                    truncated_reason text,
                    created_at_utc text
                );
                """
            )
            conn.executemany(
                """
                insert into runtime_activity values (
                    ?, 'trace1', 'trace1', 's1', 'default', 'exec1',
                    ?, ?, ?, ?, ?, ?, 'info', ?, ?, ?, ?
                )
                """,
                [
                    (
                        "tool-1", "tool_runner", "execute_tool", "failed",
                        "2026-05-31T00:00:01+00:00", "2026-05-31T00:00:01.100+00:00", 100,
                        "Tool failed", '{"tool_name":"bash"}', "tool_failed", "permission denied",
                    ),
                    (
                        "tool-2", "tool_runner", "execute_tool", "succeeded",
                        "2026-05-31T00:00:02+00:00", "2026-05-31T00:00:02.200+00:00", 200,
                        "Tool ok", '{"tool_name":"bash"}', None, None,
                    ),
                    (
                        "tool-3", "tool_runner", "execute_tool", "succeeded",
                        "2026-05-31T00:00:03+00:00", "2026-05-31T00:00:03.300+00:00", 300,
                        "Tool ok", '{"tool_name":"bash"}', None, None,
                    ),
                    (
                        "llm-1", "llm_gateway", "chat_stream", "succeeded",
                        "2026-05-31T00:00:04+00:00", "2026-05-31T00:00:05.200+00:00", 1200,
                        "LLM ok", '{"endpoint":"https://llm.example/v1","model":"m1"}', None, None,
                    ),
                ],
            )
            conn.executemany(
                """
                insert into session_event_log (
                    session_id, workspace_id, sequence_num, event_type, data,
                    recorded_at, trace_id, correlation_id, execution_id, component, operation
                ) values (?, ?, ?, ?, ?, ?, 'trace1', 'trace1', 'exec1', 'session_state', ?)
                """,
                [
                    ("s1", "default", 1, "thinking", '{"delta":"t"}', "2026-05-31T00:00:00+00:00", "append:thinking"),
                    ("s1", "default", 2, "delta", '{"delta":"hello"}', "2026-05-31T00:00:06+00:00", "append:delta"),
                ],
            )
            conn.execute(
                """
                insert into TokenUsageEvents values (
                    'chat_message', 'm1', 'default', 's1', 'https://llm.example/v1', 'm1',
                    '2026-05-31T00:00:05+00:00', '2026-05',
                    20, 10, 30, 10, 5, 15, 0.666667,
                    '0.010000', '0.010000', '0.010000', '0.030000',
                    'prefix-a', 'system-a', 'tool-a', null, null, null, 2, 1
                )
                """
            )
            conn.executemany(
                """
                insert into context_layer_metric_events values (
                    'chat_message', ?, 'default', 's1', 'p1', 'm1', ?,
                    'context-pipeline-v1', 'default-linear-v1',
                    ?, ?, ?, ?, 0, ?, ?, ?, ?, 0, 0, 1, ?, ?, ?, 'estimated', 0, null, ?
                )
                """,
                [
                    (
                        "call-1", "2026-05-31T00:00:05+00:00",
                        "L0-STATIC", 0, "stable_prefix", 40, "static-a", None, 0, None,
                        40, 0, 1.0, "2026-05-31T00:00:05+00:00",
                    ),
                    (
                        "call-1", "2026-05-31T00:00:05+00:00",
                        "L5-RECENT", 5, "dynamic_history", 60, "recent-a", None, 0, None,
                        30, 30, 0.5, "2026-05-31T00:00:05+00:00",
                    ),
                    (
                        "call-2", "2026-05-31T00:00:06+00:00",
                        "L0-STATIC", 0, "stable_prefix", 50, "static-a", "static-a", 0, None,
                        50, 0, 1.0, "2026-05-31T00:00:06+00:00",
                    ),
                    (
                        "call-2", "2026-05-31T00:00:06+00:00",
                        "L5-RECENT", 5, "dynamic_history", 50, "recent-b", "recent-a", 1, "history_changed",
                        0, 50, 0.0, "2026-05-31T00:00:06+00:00",
                    ),
                ],
            )
            conn.commit()
        finally:
            conn.close()
        self.db_path = db_path
        return db_path

    def __exit__(self, exc_type, exc, tb):
        self.temp_dir.cleanup()


def _insert_telemetry_rows(db_path: Path) -> None:
    conn = sqlite3.connect(db_path)
    try:
        conn.executemany(
            """
            insert into telemetry_metric_events values (
                ?, 'trace1', 'trace1', 's1', 'default', 'exec1',
                'tool', 'tool.call', ?, ?, ?, 1, null, 'call', 'info',
                ?, ?, null, ?, ?
            )
            """,
            [
                (
                    "metric-1", "succeeded", "2026-05-31T00:00:01+00:00", 20,
                    "Tool ok",
                    '{"tool_name":"browser","estimated_input_tokens":"5","estimated_output_tokens":"8","output_char_count":"4","output_line_count":"1","error_char_count":"0","error_line_count":"0","total_text_char_count":"4","total_text_line_count":"1","output_size_level":"normal"}',
                    None, None,
                ),
                (
                    "metric-2", "failed", "2026-05-31T00:00:02+00:00", 40,
                    "Tool failed",
                    '{"tool_name":"browser","estimated_input_tokens":"8","estimated_output_tokens":"13","output_char_count":"68","output_line_count":"2","error_char_count":"9","error_line_count":"1","total_text_char_count":"77","total_text_line_count":"3","output_size_level":"warning"}',
                    "tool_failed", "bad input",
                ),
            ],
        )
        conn.commit()
    finally:
        conn.close()


def _insert_subconscious_job_rows(db_path: Path) -> None:
    conn = sqlite3.connect(db_path)
    try:
        conn.executemany(
            """
            insert into telemetry_metric_events values (
                ?, 'trace1', 'trace1', ?, 'default', 'exec1',
                'memory', ?, ?, ?, null, 1, null, 'job', ?,
                ?, ?, null, ?, ?
            )
            """,
            [
                (
                    "memory-1", "s1", "subconscious_job.enqueue", "succeeded",
                    "2026-05-31T00:00:01+00:00", "info", "Subconscious job pending",
                    '{"job_id":"job-1","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"pending","retry_count":"0"}',
                    None, None,
                ),
                (
                    "memory-2", "s1", "subconscious_job.lease", "started",
                    "2026-05-31T00:00:02+00:00", "info", "Subconscious job running",
                    '{"job_id":"job-1","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"running","retry_count":"0"}',
                    None, None,
                ),
                (
                    "memory-3", "s1", "subconscious_job.complete", "succeeded",
                    "2026-05-31T00:00:03+00:00", "info", "Subconscious job completed",
                    '{"job_id":"job-1","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"completed","retry_count":"0"}',
                    None, None,
                ),
                (
                    "memory-4", "s2", "subconscious_job.enqueue", "succeeded",
                    "2026-05-31T00:00:04+00:00", "info", "Subconscious job pending",
                    '{"job_id":"job-2","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"pending","retry_count":"0"}',
                    None, None,
                ),
                (
                    "memory-5", "s2", "subconscious_job.retry", "retried",
                    "2026-05-31T00:00:05+00:00", "warning", "Subconscious job retrying",
                    '{"job_id":"job-2","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"dead_letter","retry_count":"3"}',
                    None, "model unavailable",
                ),
                (
                    "memory-6", "s2", "subconscious_job.dead_letter", "failed",
                    "2026-05-31T00:00:06+00:00", "warning", "Subconscious job dead letter",
                    '{"job_id":"job-2","job_type":"memory.consolidate_session","source_hook_name":"session.compressed","job_status":"dead_letter","retry_count":"3"}',
                    "subconscious_job_failed", "model unavailable",
                ),
                (
                    "memory-7", "s2", "subconscious_job.schedule_skip", "deferred",
                    "2026-05-31T00:00:07+00:00", "info", "Subconscious scheduling skipped",
                    '{"job_type":"memory.consolidate_session","source_hook_name":"session.compressed","skip_reason":"skip_cooldown"}',
                    None, None,
                ),
            ],
        )
        conn.commit()
    finally:
        conn.close()


if __name__ == "__main__":
    unittest.main()
