#!/usr/bin/env python3

from __future__ import annotations

import importlib.util
import json
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "TestScripts" / "diagnose_session_logs.py"


def load_module():
    spec = importlib.util.spec_from_file_location("diagnose_session_logs", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def write_jsonl(path: Path, records: list[dict]):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(json.dumps(record, ensure_ascii=False) for record in records), encoding="utf-8")


def create_sqlite_fixture(path: Path, session_id: str):
    conn = sqlite3.connect(path)
    try:
        conn.executescript(
            """
            create table ChatMessages (
                Id integer primary key,
                SessionId text not null,
                Role text not null,
                Content text not null,
                CreatedAt integer not null
            );
            create table session_sub_agents (
                parent_session_id text not null,
                sub_session_id text not null,
                status text not null,
                success integer,
                task_summary text,
                reply_summary text,
                spawned_at text,
                completed_at text
            );
            create table room_messages (
                message_id text not null,
                workspace_id text not null,
                room_id text not null,
                from_kind text not null,
                from_id text not null,
                from_display_name text,
                audience text not null,
                visibility text not null,
                content text not null,
                created_at integer not null
            );
            create table message_deliveries (
                delivery_id text not null,
                message_id text not null,
                target_kind text not null,
                target_id text not null,
                status text not null,
                attempt_count integer not null,
                created_at integer not null,
                updated_at integer not null,
                ack_at integer,
                last_error text
            );
            create table session_event_log (
                sequence_num integer not null,
                session_id text not null,
                event_type text not null,
                data text,
                recorded_at text,
                sub_agent_id text,
                operation text
            );
            """
        )
        sub_id = f"{session_id}-sub-abc12345"
        message_id = "msg-sub-result"
        reply = "parent continuation reply"
        conn.execute(
            "insert into ChatMessages (Id, SessionId, Role, Content, CreatedAt) values (1, ?, 'user', 'start', 1000)",
            (session_id,),
        )
        conn.execute(
            """
            insert into session_sub_agents
            (parent_session_id, sub_session_id, status, success, task_summary, reply_summary, spawned_at, completed_at)
            values (?, ?, 'completed', 1, 'child task', 'child reply', 't1', 't2')
            """,
            (session_id, sub_id),
        )
        content = f"""<pudding-message version="1">
  <meta>
    <message-type>subagent_result</message-type>
    <message-id>{message_id}</message-id>
    <sent-at>2026-06-13T00:00:00Z</sent-at>
    <from kind="agent" id="{sub_id}" display-name="Sub Agent" />
    <to kind="agent" id="agent-parent" display-name="Parent Agent" />
  </meta>
  <constraints>
    <instruction>Use this sub-agent result as context.</instruction>
  </constraints>
  <context format="text/markdown"><![CDATA[child reply]]></context>
</pudding-message>"""
        conn.execute(
            """
            insert into room_messages
            (message_id, workspace_id, room_id, from_kind, from_id, from_display_name, audience, visibility, content, created_at)
            values (?, 'default', 'room-default', 'agent', ?, 'Sub Agent', 'direct', 'system', ?, 2000)
            """,
            (message_id, sub_id, content),
        )
        conn.execute(
            """
            insert into message_deliveries
            (delivery_id, message_id, target_kind, target_id, status, attempt_count, created_at, updated_at, ack_at, last_error)
            values ('delivery-1', ?, 'agent', 'agent-parent', 'delivered', 1, 2000, 2500, 2500, null)
            """,
            (message_id,),
        )
        conn.execute(
            """
            insert into session_event_log
            (sequence_num, session_id, event_type, data, recorded_at, sub_agent_id, operation)
            values (10, ?, 'delta', ?, 't3', ?, 'chat.stream.delta')
            """,
            (session_id, json.dumps({"delta": "parent", "messageId": message_id}), sub_id),
        )
        conn.execute(
            """
            insert into session_event_log
            (sequence_num, session_id, event_type, data, recorded_at, sub_agent_id, operation)
            values (11, ?, 'done', ?, 't4', ?, 'chat.stream.done')
            """,
            (session_id, json.dumps({"reply": reply, "messageId": message_id}), sub_id),
        )
        conn.commit()
    finally:
        conn.close()


class SessionLogDiagnosticsTests(unittest.TestCase):
    def test_extracts_tool_failures_approval_events_and_usage(self):
        module = load_module()
        session_id = "session-1"
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            write_jsonl(
                data_dir / "jsonl" / f"{session_id}.jsonl",
                [
                    {
                        "type": "event",
                        "eventType": "tool_call",
                        "data": json.dumps({"name": "shell", "arguments": json.dumps({"command": "python app.py"})}),
                        "sequenceNum": 10,
                        "recordedAt": "2026-06-05T00:00:00Z",
                    },
                    {
                        "type": "event",
                        "eventType": "tool_result",
                        "data": json.dumps({"name": "shell", "exitCode": 1, "error": "exit code 1", "output": "Traceback"}),
                        "sequenceNum": 11,
                        "recordedAt": "2026-06-05T00:00:01Z",
                    },
                    {
                        "type": "assistant",
                        "usageJson": json.dumps({"PromptTokens": 10, "CompletionTokens": 2, "TotalTokens": 12}),
                    },
                ],
            )
            write_jsonl(
                data_dir / "runtime" / "tool-approval" / "audit-events.jsonl",
                [
                    {
                        "eventType": "TicketMismatch",
                        "sessionId": session_id,
                        "toolId": "shell",
                        "command": "python app.py",
                        "reason": "approved command does not match",
                    },
                    {"eventType": "TicketMismatch", "sessionId": "other", "toolId": "shell"},
                ],
            )
            tickets = [
                {
                    "ticketId": "tap_1",
                    "identity": {"sessionId": session_id},
                    "toolId": "shell",
                    "status": "Consumed",
                    "scope": "Once",
                    "remainingUses": 0,
                    "request": {"requestedArgumentsJson": json.dumps({"command": "python app.py"})},
                }
            ]
            ticket_path = data_dir / "runtime" / "tool-approval" / "tickets.json"
            ticket_path.parent.mkdir(parents=True, exist_ok=True)
            ticket_path.write_text(json.dumps(tickets), encoding="utf-8")
            write_jsonl(
                data_dir / "logs" / "diagnostics" / "session-timeline" / "20260605" / f"{session_id}.jsonl",
                [
                    {"status": "succeeded", "component": "tool", "operation": "shell"},
                    {"status": "failed", "component": "tool", "operation": "shell", "errorMessage": "exit code 1"},
                ],
            )
            log_path = data_dir / "logs" / "sessions" / session_id / "session-20260605.log"
            log_path.parent.mkdir(parents=True, exist_ok=True)
            log_path.write_text("[WRN] approval required\n[ERR] exit code 1\n", encoding="utf-8")

            diag = module.load_session_diagnostics(session_id, data_dir=data_dir)
            payload = module.to_jsonable(diag, max_errors=4)
            text = module.render_text(diag, max_errors=4)

        self.assertTrue(diag.has_jsonl)
        self.assertEqual(1, payload["counts"]["toolCalls"]["shell"])
        self.assertEqual(1, payload["counts"]["failedToolResults"])
        self.assertEqual(1, payload["counts"]["approvalEvents"]["TicketMismatch"])
        self.assertEqual(1, payload["counts"]["tickets"])
        self.assertEqual(12, payload["usage"]["totalTokens"])
        self.assertIn("python app.py", payload["failures"][0]["command"])
        self.assertIn("TicketMismatch", text)
        self.assertIn("Session log findings", text)

    def test_sqlite_diagnostics_reports_unmaterialized_parent_continuation(self):
        module = load_module()
        session_id = "session-1"
        with tempfile.TemporaryDirectory() as temp_dir:
            data_dir = Path(temp_dir)
            write_jsonl(data_dir / "jsonl" / f"{session_id}.jsonl", [])
            create_sqlite_fixture(data_dir / "pudding_platform.db", session_id)

            diag = module.load_session_diagnostics(session_id, data_dir=data_dir)
            payload = module.to_jsonable(diag, max_errors=4)
            text = module.render_text(diag, max_errors=4)

        sqlite_payload = payload["sqlite"]
        self.assertEqual(1, sqlite_payload["chatMessages"]["count"])
        self.assertEqual(1, len(sqlite_payload["roomMessages"]))
        envelope = sqlite_payload["roomMessages"][0]["envelope"]
        self.assertEqual("1", envelope["version"])
        self.assertEqual("subagent_result", envelope["messageType"])
        self.assertEqual(message_id := "msg-sub-result", envelope["messageId"])
        self.assertEqual(f"{session_id}-sub-abc12345", envelope["from"]["id"])
        self.assertEqual("agent-parent", envelope["to"]["id"])
        self.assertEqual(["Use this sub-agent result as context."], envelope["constraints"])
        self.assertEqual("child reply", envelope["contextPreview"])
        self.assertEqual("delivered", sqlite_payload["messageDeliveries"][0]["status"])
        self.assertFalse(sqlite_payload["parentContinuations"][0]["replyMaterialized"])
        self.assertIn("not materialized in ChatMessages", sqlite_payload["gaps"][0])
        self.assertIn("SQLite materialization", text)
        self.assertIn("materialized=False", text)


if __name__ == "__main__":
    unittest.main()
