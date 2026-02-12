import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path


TOOLS_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS_DIR))

import query_timeline


class QueryTimelineTests(unittest.TestCase):
    def test_load_timeline_merges_runtime_activity_and_session_events_by_time(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            db_path = Path(temp_dir) / "pudding_platform.db"
            conn = sqlite3.connect(db_path)
            try:
                conn.execute(
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
                        metadata_json text
                    )
                    """
                )
                conn.execute(
                    """
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
                        parent_execution_id text,
                        sub_agent_id text,
                        component text,
                        operation text
                    )
                    """
                )
                conn.execute(
                    """
                    insert into runtime_activity values (
                        'act1','trace1','trace1','s1','default','exec1',
                        'llm_gateway','chat_stream','started',
                        '2026-05-31T07:17:11.3190000+00:00',null,null,
                        'info','provider request started','{"model":"qwen"}'
                    )
                    """
                )
                conn.execute(
                    """
                    insert into session_event_log (
                        session_id, workspace_id, sequence_num, event_type, data,
                        recorded_at, trace_id, correlation_id, execution_id,
                        parent_execution_id, sub_agent_id, component, operation
                    ) values (
                        's1','default',1,'delta','{"delta":"hi"}',
                        '2026-05-31T07:17:12.0000000+00:00',
                        'trace1','trace1','exec1',null,null,'session_state','append:delta'
                    )
                    """
                )
                conn.commit()
            finally:
                conn.close()

            rows = query_timeline.load_timeline(db_path, session_id="s1")

        self.assertEqual(["runtime_activity", "session_event"], [row["source"] for row in rows])
        self.assertEqual("llm_gateway", rows[0]["component"])
        self.assertEqual("delta", rows[1]["eventType"])
        self.assertEqual("hi", rows[1]["payloadPreview"])


if __name__ == "__main__":
    unittest.main()
