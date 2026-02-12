#!/usr/bin/env python3
"""
Extract high-signal diagnostics for one Pudding chat session.

Usage:
    python TestScripts/diagnose_session_logs.py <session-id>
    python TestScripts/diagnose_session_logs.py <session-id> --json
    python TestScripts/diagnose_session_logs.py <session-id> --data-dir data --max-errors 10
"""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_DATA_DIR = ROOT / "data"
TRUNCATE_AT = 260


@dataclass
class ToolCall:
    seq: int
    name: str
    command: str | None
    arguments: dict[str, Any] | None
    recorded_at: str | None


@dataclass
class ToolResult:
    seq: int
    name: str
    exit_code: int | None
    output: str | None
    error: str | None
    recorded_at: str | None
    paired_command: str | None = None


@dataclass
class SessionDiagnostics:
    session_id: str
    data_dir: Path
    jsonl_path: Path
    has_jsonl: bool = False
    timeline_path: Path | None = None
    session_log_path: Path | None = None
    message_counts: Counter[str] = field(default_factory=Counter)
    event_counts: Counter[str] = field(default_factory=Counter)
    tool_calls: list[ToolCall] = field(default_factory=list)
    tool_results: list[ToolResult] = field(default_factory=list)
    approval_events: list[dict[str, Any]] = field(default_factory=list)
    tickets: list[dict[str, Any]] = field(default_factory=list)
    timeline_counts: Counter[str] = field(default_factory=Counter)
    timeline_errors: list[dict[str, Any]] = field(default_factory=list)
    session_log_findings: list[str] = field(default_factory=list)
    usage: dict[str, Any] = field(default_factory=dict)
    sqlite_path: Path | None = None
    sqlite: dict[str, Any] = field(default_factory=dict)

    @property
    def failed_tool_results(self) -> list[ToolResult]:
        return [result for result in self.tool_results if result.exit_code not in (None, 0)]

    @property
    def tool_call_counts(self) -> Counter[str]:
        return Counter(call.name for call in self.tool_calls)

    @property
    def tool_result_counts(self) -> Counter[str]:
        return Counter(result.name for result in self.tool_results)

    @property
    def approval_counts(self) -> Counter[str]:
        return Counter(str(event.get("eventType", "")) for event in self.approval_events)


def load_session_diagnostics(session_id: str, data_dir: Path = DEFAULT_DATA_DIR, max_errors: int = 8) -> SessionDiagnostics:
    data_dir = data_dir.resolve()
    diag = SessionDiagnostics(
        session_id=session_id,
        data_dir=data_dir,
        jsonl_path=data_dir / "jsonl" / f"{session_id}.jsonl",
    )
    load_jsonl_events(diag, max_errors=max_errors)
    load_approval_events(diag)
    load_tickets(diag)
    load_timeline(diag, max_errors=max_errors)
    load_sqlite_diagnostics(diag, max_rows=max_errors)
    load_session_log_findings(diag, max_errors=max_errors)
    return diag


def load_jsonl_events(diag: SessionDiagnostics, max_errors: int) -> None:
    if not diag.jsonl_path.exists():
        return

    diag.has_jsonl = True
    pending_calls: dict[str, list[ToolCall]] = defaultdict(list)
    for record in iter_jsonl(diag.jsonl_path):
        record_type = str(record.get("type", ""))
        event_type = str(record.get("eventType", ""))
        if record_type:
            diag.message_counts[record_type] += 1
        if event_type:
            diag.event_counts[event_type] += 1

        if record_type == "assistant":
            usage = parse_json_value(record.get("usageJson"))
            if isinstance(usage, dict):
                diag.usage = normalize_usage(usage)

        if event_type == "done":
            data = parse_json_value(record.get("data"))
            usage = data.get("usage") if isinstance(data, dict) else None
            if isinstance(usage, dict):
                diag.usage = normalize_usage(usage)

        if event_type not in ("tool_call", "tool_result"):
            continue

        data = parse_json_value(record.get("data"))
        if not isinstance(data, dict):
            continue

        if event_type == "tool_call":
            arguments = parse_json_value(data.get("arguments"))
            arguments_dict = arguments if isinstance(arguments, dict) else None
            command = extract_command_from_arguments(arguments_dict)
            call = ToolCall(
                seq=int(record.get("sequenceNum") or 0),
                name=str(data.get("name") or ""),
                command=command,
                arguments=arguments_dict,
                recorded_at=str(record.get("recordedAt") or ""),
            )
            diag.tool_calls.append(call)
            pending_calls[call.name].append(call)
            continue

        result = ToolResult(
            seq=int(record.get("sequenceNum") or 0),
            name=str(data.get("name") or ""),
            exit_code=parse_int(data.get("exitCode")),
            output=data.get("output") if data.get("output") is not None else None,
            error=data.get("error") if data.get("error") is not None else None,
            recorded_at=str(record.get("recordedAt") or ""),
        )
        if pending_calls[result.name]:
            result.paired_command = pending_calls[result.name].pop(0).command
        diag.tool_results.append(result)

        if len(diag.failed_tool_results) >= max_errors:
            # Keep reading for counts; only expensive nested processing is already done.
            pass


def load_approval_events(diag: SessionDiagnostics) -> None:
    path = diag.data_dir / "runtime" / "tool-approval" / "audit-events.jsonl"
    if not path.exists():
        return
    for event in iter_jsonl(path):
        if event.get("sessionId") == diag.session_id:
            diag.approval_events.append(event)


def load_tickets(diag: SessionDiagnostics) -> None:
    path = diag.data_dir / "runtime" / "tool-approval" / "tickets.json"
    if not path.exists():
        return
    data = parse_json_value(path.read_text(encoding="utf-8"))
    for ticket in iter_ticket_records(data):
        if nested_get(ticket, "identity", "sessionId") == diag.session_id:
            diag.tickets.append(ticket)


def load_timeline(diag: SessionDiagnostics, max_errors: int) -> None:
    base = diag.data_dir / "logs" / "diagnostics" / "session-timeline"
    if not base.exists():
        return

    paths = list(base.glob(f"**/{diag.session_id}.jsonl"))
    if not paths:
        return

    diag.timeline_path = max(paths, key=lambda p: p.stat().st_mtime)
    for record in iter_jsonl(diag.timeline_path):
        status = str(record.get("status") or "")
        if status:
            diag.timeline_counts[status] += 1
        if status.lower() in ("failed", "error") or record.get("errorMessage"):
            if len(diag.timeline_errors) < max_errors:
                diag.timeline_errors.append(record)


def load_session_log_findings(diag: SessionDiagnostics, max_errors: int) -> None:
    session_log_dir = diag.data_dir / "logs" / "sessions" / diag.session_id
    if not session_log_dir.exists():
        return

    logs = sorted(session_log_dir.glob("session-*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    if not logs:
        return

    diag.session_log_path = logs[0]
    pattern = re.compile(r"\[(ERR|WRN)\]|approval required|TicketMismatch|UnicodeEncodeError", re.IGNORECASE)
    for line in diag.session_log_path.read_text(encoding="utf-8", errors="replace").splitlines():
        if pattern.search(line):
            diag.session_log_findings.append(line)
            if len(diag.session_log_findings) >= max_errors:
                break


def load_sqlite_diagnostics(diag: SessionDiagnostics, max_rows: int) -> None:
    db_path = diag.data_dir / "pudding_platform.db"
    if not db_path.exists():
        return

    diag.sqlite_path = db_path
    conn = sqlite3.connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        payload: dict[str, Any] = {}

        if sqlite_table_exists(conn, "ChatMessages"):
            chat_rows = sqlite_rows(
                conn,
                """
                select Id, SessionId, Role, Content, CreatedAt
                from ChatMessages
                where SessionId = ?
                order by CreatedAt asc, Id asc
                """,
                (diag.session_id,),
            )
            payload["chatMessages"] = {
                "count": len(chat_rows),
                "byRole": dict(Counter(str(row.get("Role") or "") for row in chat_rows)),
                "recent": [
                    {
                        "id": row.get("Id"),
                        "role": row.get("Role"),
                        "createdAt": row.get("CreatedAt"),
                        "preview": truncate(row.get("Content")),
                    }
                    for row in chat_rows[-max_rows:]
                ],
            }
        else:
            chat_rows = []

        if sqlite_table_exists(conn, "session_sub_agents"):
            payload["subAgents"] = [
                {
                    "subSessionId": row.get("sub_session_id"),
                    "status": row.get("status"),
                    "success": row.get("success"),
                    "task": truncate(row.get("task_summary")),
                    "reply": truncate(row.get("reply_summary")),
                    "spawnedAt": row.get("spawned_at"),
                    "completedAt": row.get("completed_at"),
                }
                for row in sqlite_rows(
                    conn,
                    """
                    select parent_session_id, sub_session_id, status, success,
                           task_summary, reply_summary, spawned_at, completed_at
                    from session_sub_agents
                    where parent_session_id = ?
                    order by spawned_at desc
                    limit ?
                    """,
                    (diag.session_id, max_rows),
                )
            ]

        room_rows: list[dict[str, Any]] = []
        if sqlite_table_exists(conn, "room_messages"):
            room_rows = sqlite_rows(
                conn,
                """
                select message_id, workspace_id, room_id, from_kind, from_id,
                       from_display_name, audience, visibility, content, created_at
                from room_messages
                where content like ? or from_id like ?
                order by created_at asc
                limit ?
                """,
                (f"%{diag.session_id}%", f"{diag.session_id}-sub-%", max_rows),
            )
            payload["roomMessages"] = [room_message_to_dict(row) for row in room_rows]

        if room_rows and sqlite_table_exists(conn, "message_deliveries"):
            message_ids = [str(row["message_id"]) for row in room_rows if row.get("message_id")]
            placeholders = ",".join("?" for _ in message_ids)
            delivery_rows = sqlite_rows(
                conn,
                f"""
                select delivery_id, message_id, target_kind, target_id, status,
                       attempt_count, created_at, updated_at, ack_at, last_error
                from message_deliveries
                where message_id in ({placeholders})
                order by created_at asc
                """,
                tuple(message_ids),
            )
            payload["messageDeliveries"] = [
                {
                    "deliveryId": row.get("delivery_id"),
                    "messageId": row.get("message_id"),
                    "target": f"{row.get('target_kind')}:{row.get('target_id')}",
                    "status": row.get("status"),
                    "attempts": row.get("attempt_count"),
                    "ackAt": row.get("ack_at"),
                    "lastError": truncate(row.get("last_error")),
                }
                for row in delivery_rows
            ]

        if sqlite_table_exists(conn, "session_event_log"):
            payload["sqliteEventCounts"] = {
                str(row["event_type"]): row["count"]
                for row in sqlite_rows(
                    conn,
                    """
                    select event_type, count(*) as count
                    from session_event_log
                    where session_id = ?
                    group by event_type
                    order by event_type asc
                    """,
                    (diag.session_id,),
                )
            }
            payload["parentContinuations"] = build_parent_continuation_diagnostics(
                conn,
                diag.session_id,
                chat_rows,
                max_rows,
            )

        payload["gaps"] = infer_sqlite_gaps(payload)
        diag.sqlite = payload
    finally:
        conn.close()


def sqlite_table_exists(conn: sqlite3.Connection, table_name: str) -> bool:
    row = conn.execute(
        "select 1 from sqlite_master where type = 'table' and name = ?",
        (table_name,),
    ).fetchone()
    return row is not None


def sqlite_rows(conn: sqlite3.Connection, sql: str, params: tuple[Any, ...] = ()) -> list[dict[str, Any]]:
    return [dict(row) for row in conn.execute(sql, params).fetchall()]


def room_message_to_dict(row: dict[str, Any]) -> dict[str, Any]:
    item = {
        "messageId": row.get("message_id"),
        "from": f"{row.get('from_kind')}:{row.get('from_id')}",
        "audience": row.get("audience"),
        "visibility": row.get("visibility"),
        "createdAt": row.get("created_at"),
        "preview": truncate(row.get("content")),
    }
    envelope = parse_pudding_message(row.get("content"))
    if envelope is not None:
        item["envelope"] = envelope
    return item


def parse_pudding_message(content: Any) -> dict[str, Any] | None:
    if not isinstance(content, str) or "<pudding-message" not in content:
        return None

    try:
        root = ET.fromstring(content)
    except ET.ParseError as exc:
        return {"parseError": str(exc)}

    if root.tag != "pudding-message":
        return None

    envelope: dict[str, Any] = {
        "version": root.attrib.get("version"),
        "messageType": xml_child_text(root, "meta/message-type"),
        "messageId": xml_child_text(root, "meta/message-id"),
        "sentAt": xml_child_text(root, "meta/sent-at"),
    }

    from_node = root.find("meta/from")
    if from_node is None:
        from_node = root.find("from")
    if from_node is not None:
        envelope["from"] = xml_endpoint_to_dict(from_node)

    to_node = root.find("meta/to")
    if to_node is None:
        to_node = root.find("to")
    if to_node is not None:
        envelope["to"] = xml_endpoint_to_dict(to_node)

    constraints = [
        (node.text or "").strip()
        for node in [*root.findall("constraints/instruction"), *root.findall("constraints/constraint")]
        if (node.text or "").strip()
    ]
    if constraints:
        envelope["constraints"] = constraints

    context_node = root.find("context")
    if context_node is not None:
        envelope["contextFormat"] = context_node.attrib.get("format")
        envelope["contextPreview"] = truncate((context_node.text or "").strip())

    return {key: value for key, value in envelope.items() if value not in (None, "", [], {})}


def xml_child_text(root: ET.Element, path: str) -> str | None:
    node = root.find(path)
    if node is None or node.text is None:
        return None
    value = node.text.strip()
    return value or None


def xml_endpoint_to_dict(node: ET.Element) -> dict[str, str]:
    return {
        key: value
        for key, value in {
            "kind": node.attrib.get("kind"),
            "id": node.attrib.get("id"),
            "displayName": node.attrib.get("display-name"),
        }.items()
        if value
    }


def build_parent_continuation_diagnostics(
    conn: sqlite3.Connection,
    session_id: str,
    chat_rows: list[dict[str, Any]],
    max_rows: int,
) -> list[dict[str, Any]]:
    rows = sqlite_rows(
        conn,
        """
        select sequence_num, event_type, data, recorded_at, sub_agent_id, operation
        from session_event_log
        where session_id = ?
          and sub_agent_id like ?
          and event_type in ('thinking', 'delta', 'usage', 'done', 'error', 'cancelled')
        order by sequence_num asc
        """,
        (session_id, f"{session_id}-sub-%"),
    )

    by_sub_agent: dict[str, dict[str, Any]] = {}
    for row in rows:
        sub_agent_id = str(row.get("sub_agent_id") or "")
        if not sub_agent_id:
            continue
        item = by_sub_agent.setdefault(sub_agent_id, {
            "subAgentId": sub_agent_id,
            "messageId": None,
            "firstSeq": row.get("sequence_num"),
            "lastSeq": row.get("sequence_num"),
            "firstRecordedAt": row.get("recorded_at"),
            "lastRecordedAt": row.get("recorded_at"),
            "eventCounts": Counter(),
            "replyPreview": "",
            "replyMaterialized": False,
        })
        event_type = str(row.get("event_type") or "")
        item["eventCounts"][event_type] += 1
        item["lastSeq"] = row.get("sequence_num")
        item["lastRecordedAt"] = row.get("recorded_at")
        data = parse_json_value(row.get("data"))
        if isinstance(data, dict):
            if not item["messageId"] and isinstance(data.get("messageId"), str):
                item["messageId"] = data.get("messageId")
            if event_type == "done":
                reply = data.get("reply")
                if isinstance(reply, str):
                    item["replyPreview"] = truncate(reply)
                    item["replyMaterialized"] = any(
                        row.get("Role") == "agent" and row.get("Content") == reply
                        for row in chat_rows
                    )

    result = []
    for item in by_sub_agent.values():
        item["eventCounts"] = dict(item["eventCounts"])
        result.append(item)
    return result[-max_rows:]


def infer_sqlite_gaps(payload: dict[str, Any]) -> list[str]:
    gaps: list[str] = []
    delivered = [
        delivery for delivery in payload.get("messageDeliveries", [])
        if delivery.get("status") == "delivered"
    ]
    continuations = payload.get("parentContinuations", [])
    unmaterialized = [
        item for item in continuations
        if item.get("eventCounts", {}).get("done") and not item.get("replyMaterialized")
    ]
    if delivered and unmaterialized:
        gaps.append(
            "sub-agent result delivery was acknowledged and parent continuation produced done, "
            "but the continuation reply is not materialized in ChatMessages"
        )
    if payload.get("roomMessages") and not payload.get("messageDeliveries"):
        gaps.append("room_messages exist for this session/sub-agent, but matching message_deliveries were not found")
    if payload.get("subAgents") and not payload.get("roomMessages"):
        gaps.append("session_sub_agents exist, but no matching Message Fabric room_messages were found")
    return gaps


def to_jsonable(diag: SessionDiagnostics, max_errors: int) -> dict[str, Any]:
    return {
        "sessionId": diag.session_id,
        "paths": {
            "jsonl": str(diag.jsonl_path) if diag.has_jsonl else None,
            "timeline": str(diag.timeline_path) if diag.timeline_path else None,
            "sessionLog": str(diag.session_log_path) if diag.session_log_path else None,
            "sqlite": str(diag.sqlite_path) if diag.sqlite_path else None,
        },
        "usage": diag.usage,
        "counts": {
            "messages": dict(diag.message_counts),
            "events": dict(diag.event_counts),
            "toolCalls": dict(diag.tool_call_counts),
            "toolResults": dict(diag.tool_result_counts),
            "failedToolResults": len(diag.failed_tool_results),
            "approvalEvents": dict(diag.approval_counts),
            "tickets": len(diag.tickets),
            "timeline": dict(diag.timeline_counts),
        },
        "sqlite": diag.sqlite,
        "failures": [tool_result_to_dict(result) for result in diag.failed_tool_results[:max_errors]],
        "approvalTimeline": [approval_event_to_dict(event) for event in diag.approval_events[: max_errors * 2]],
        "tickets": [ticket_to_dict(ticket) for ticket in diag.tickets],
        "timelineErrors": [timeline_error_to_dict(record) for record in diag.timeline_errors[:max_errors]],
        "sessionLogFindings": [truncate(line) for line in diag.session_log_findings[:max_errors]],
    }


def render_text(diag: SessionDiagnostics, max_errors: int) -> str:
    payload = to_jsonable(diag, max_errors=max_errors)
    lines: list[str] = []
    lines.append(f"Session: {diag.session_id}")
    lines.append(f"Data dir: {diag.data_dir}")
    lines.append("")
    lines.append("Paths:")
    for name, value in payload["paths"].items():
        lines.append(f"  {name}: {value or '-'}")
    lines.append("")
    lines.append("Usage:")
    if diag.usage:
        lines.append(
            "  tokens: prompt={promptTokens} completion={completionTokens} total={totalTokens} cacheHit={promptCacheHitTokens} cacheMiss={promptCacheMissTokens}".format(
                **with_usage_defaults(diag.usage)
            )
        )
    else:
        lines.append("  -")
    lines.append("")
    lines.append("Tool calls:")
    for name, count in sorted(diag.tool_call_counts.items()):
        result_count = diag.tool_result_counts.get(name, 0)
        lines.append(f"  {name}: calls={count} results={result_count}")
    lines.append(f"  failed results: {len(diag.failed_tool_results)}")
    lines.append("")
    lines.append("Failures:")
    if diag.failed_tool_results:
        for result in diag.failed_tool_results[:max_errors]:
            lines.append(f"  seq={result.seq} tool={result.name} exit={result.exit_code}")
            if result.paired_command:
                lines.append(f"    command: {truncate(result.paired_command)}")
            if result.error:
                lines.append(f"    error: {truncate(result.error)}")
            if result.output:
                lines.append(f"    output: {truncate(result.output)}")
    else:
        lines.append("  -")
    lines.append("")
    lines.append("Approval events:")
    if diag.approval_events:
        for event_type, count in sorted(diag.approval_counts.items()):
            lines.append(f"  {event_type}: {count}")
        mismatches = [event for event in diag.approval_events if event.get("eventType") == "TicketMismatch"]
        for event in mismatches[:max_errors]:
            lines.append(f"  mismatch: tool={event.get('toolId')} command={truncate(event.get('command'))}")
            lines.append(f"    reason: {truncate(event.get('reason'))}")
    else:
        lines.append("  -")
    lines.append("")
    lines.append("Tickets:")
    if diag.tickets:
        for ticket in diag.tickets:
            item = ticket_to_dict(ticket)
            lines.append(
                "  {ticketId}: tool={toolId} status={status} scope={scope} remaining={remainingUses} command={command}".format(
                    **item
                )
            )
    else:
        lines.append("  -")
    lines.append("")
    lines.append("Timeline status:")
    if diag.timeline_counts:
        for status, count in sorted(diag.timeline_counts.items()):
            lines.append(f"  {status}: {count}")
    else:
        lines.append("  -")
    if diag.timeline_errors:
        lines.append("Timeline errors:")
        for record in diag.timeline_errors[:max_errors]:
            lines.append(
                f"  {record.get('recordedAtUtc')} {record.get('component')}.{record.get('operation')} "
                f"{record.get('status')} {truncate(record.get('errorMessage'))}"
            )
    lines.append("")
    lines.append("SQLite materialization:")
    if diag.sqlite:
        chat = diag.sqlite.get("chatMessages") or {}
        lines.append(
            f"  ChatMessages: count={chat.get('count', 0)} byRole={chat.get('byRole', {})}"
        )
        sub_agents = diag.sqlite.get("subAgents") or []
        lines.append(f"  SubAgents: {len(sub_agents)}")
        for item in sub_agents[:max_errors]:
            lines.append(
                f"    {item.get('subSessionId')} status={item.get('status')} success={item.get('success')} "
                f"reply={item.get('reply')}"
            )
        room_messages = diag.sqlite.get("roomMessages") or []
        lines.append(f"  Message Fabric room_messages: {len(room_messages)}")
        for item in room_messages[:max_errors]:
            lines.append(
                f"    {item.get('messageId')} from={item.get('from')} visibility={item.get('visibility')} "
                f"preview={item.get('preview')}"
            )
        deliveries = diag.sqlite.get("messageDeliveries") or []
        lines.append(f"  Message Fabric deliveries: {len(deliveries)}")
        for item in deliveries[:max_errors]:
            lines.append(
                f"    {item.get('deliveryId')} target={item.get('target')} status={item.get('status')} "
                f"attempts={item.get('attempts')} ackAt={item.get('ackAt')}"
            )
        continuations = diag.sqlite.get("parentContinuations") or []
        lines.append(f"  Parent continuations from sub-agent notifications: {len(continuations)}")
        for item in continuations[:max_errors]:
            lines.append(
                f"    {item.get('subAgentId')} messageId={item.get('messageId')} "
                f"events={item.get('eventCounts')} materialized={item.get('replyMaterialized')}"
            )
            if item.get("replyPreview"):
                lines.append(f"      reply: {item.get('replyPreview')}")
        gaps = diag.sqlite.get("gaps") or []
        if gaps:
            lines.append("  Gaps:")
            for gap in gaps[:max_errors]:
                lines.append(f"    - {gap}")
    else:
        lines.append("  -")
    lines.append("")
    lines.append("Session log findings:")
    if diag.session_log_findings:
        for finding in diag.session_log_findings[:max_errors]:
            lines.append(f"  {truncate(finding)}")
    else:
        lines.append("  -")
    return "\n".join(lines)


def iter_jsonl(path: Path):
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            value = parse_json_value(line)
            if isinstance(value, dict):
                yield value


def parse_json_value(value: Any) -> Any:
    if not isinstance(value, str):
        return value
    try:
        return json.loads(value)
    except json.JSONDecodeError:
        return None


def parse_int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def extract_command_from_arguments(arguments: dict[str, Any] | None) -> str | None:
    if not arguments:
        return None
    for key in ("command", "cmd", "input", "command_name"):
        value = arguments.get(key)
        if isinstance(value, str) and value.strip():
            return value
    requested = arguments.get("requested_arguments_json")
    requested_json = parse_json_value(requested)
    if isinstance(requested_json, dict):
        return extract_command_from_arguments(requested_json)
    return None


def normalize_usage(usage: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in usage.items():
        normalized = key[:1].lower() + key[1:] if key else key
        result[normalized] = value
    return result


def with_usage_defaults(usage: dict[str, Any]) -> dict[str, Any]:
    defaults = {
        "promptTokens": "-",
        "completionTokens": "-",
        "totalTokens": "-",
        "promptCacheHitTokens": "-",
        "promptCacheMissTokens": "-",
    }
    defaults.update(usage)
    return defaults


def nested_get(value: dict[str, Any], *keys: str) -> Any:
    current: Any = value
    for key in keys:
        if not isinstance(current, dict):
            return None
        current = current.get(key)
    return current


def iter_ticket_records(data: Any):
    if isinstance(data, list):
        for item in data:
            if isinstance(item, dict):
                yield item
        return

    if not isinstance(data, dict):
        return

    items = data.get("items")
    if isinstance(items, list):
        for item in items:
            if isinstance(item, dict):
                yield item
        return

    for item in data.values():
        if isinstance(item, dict):
            yield item


def tool_result_to_dict(result: ToolResult) -> dict[str, Any]:
    return {
        "seq": result.seq,
        "tool": result.name,
        "exitCode": result.exit_code,
        "command": result.paired_command,
        "error": truncate(result.error),
        "output": truncate(result.output),
        "recordedAt": result.recorded_at,
    }


def approval_event_to_dict(event: dict[str, Any]) -> dict[str, Any]:
    return {
        "eventType": event.get("eventType"),
        "toolId": event.get("toolId"),
        "command": event.get("command"),
        "ticketId": event.get("ticketId"),
        "allowlistRuleId": event.get("allowlistRuleId"),
        "decision": event.get("decision"),
        "reason": truncate(event.get("reason")),
        "createdAtUtc": event.get("createdAtUtc"),
    }


def ticket_to_dict(ticket: dict[str, Any]) -> dict[str, Any]:
    request = ticket.get("request") if isinstance(ticket.get("request"), dict) else {}
    requested_args = parse_json_value(request.get("requestedArgumentsJson"))
    command = describe_ticket_request(request, requested_args if isinstance(requested_args, dict) else None)
    return {
        "ticketId": ticket.get("ticketId"),
        "toolId": ticket.get("toolId"),
        "status": ticket.get("status"),
        "scope": ticket.get("scope"),
        "remainingUses": ticket.get("remainingUses"),
        "command": truncate(command),
    }


def describe_ticket_request(request: dict[str, Any], requested_args: dict[str, Any] | None) -> str:
    command = extract_command_from_arguments(requested_args)
    if command:
        return command

    if requested_args:
        for key in ("path", "file", "target", "url"):
            value = requested_args.get(key)
            if isinstance(value, str) and value.strip():
                return f"{key}={value}"

    command_name = request.get("commandName")
    if isinstance(command_name, str) and command_name.strip():
        target_resources = request.get("targetResources")
        if isinstance(target_resources, list) and target_resources:
            target = next((item for item in target_resources if isinstance(item, str) and item.strip()), "")
            if target:
                return f"{command_name} -> {target}"
        return command_name

    operation_steps = request.get("operationSteps")
    if isinstance(operation_steps, list):
        for step in operation_steps:
            if not isinstance(step, dict):
                continue
            step_command = step.get("command")
            if isinstance(step_command, str) and step_command.strip():
                return step_command

    return ""


def timeline_error_to_dict(record: dict[str, Any]) -> dict[str, Any]:
    return {
        "recordedAtUtc": record.get("recordedAtUtc"),
        "component": record.get("component"),
        "stage": record.get("stage"),
        "operation": record.get("operation"),
        "status": record.get("status"),
        "errorCode": record.get("errorCode"),
        "errorMessage": truncate(record.get("errorMessage")),
    }


def truncate(value: Any, max_length: int = TRUNCATE_AT) -> str:
    if value is None:
        return ""
    text = str(value).replace("\r", "\\r").replace("\n", "\\n").strip()
    if len(text) <= max_length:
        return text
    return text[:max_length] + "..."


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Diagnose one Pudding session from local JSONL and log files.")
    parser.add_argument("session_id", help="Session id, for example 3835ea4c18fd4361a0393b3c19259e80")
    parser.add_argument("--data-dir", default=str(DEFAULT_DATA_DIR), help="Pudding data directory. Default: ./data")
    parser.add_argument("--max-errors", type=int, default=8, help="Maximum findings to print per section.")
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON.")
    return parser.parse_args()


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = parse_args()
    data_dir = Path(args.data_dir)
    diag = load_session_diagnostics(args.session_id, data_dir=data_dir, max_errors=args.max_errors)
    if args.json:
        print(json.dumps(to_jsonable(diag, max_errors=args.max_errors), ensure_ascii=False, indent=2))
    else:
        print(render_text(diag, max_errors=args.max_errors))
    if not diag.has_jsonl:
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
