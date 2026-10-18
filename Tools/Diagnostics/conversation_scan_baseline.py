#!/usr/bin/env python3
"""conversation_scan_baseline.py — 会话读取扫描的可复现合成 fixture。

用途（会话首屏与渐进加载方案 · 阶段 A 证据）：
  在当前 EF 形状（不含候选索引）的 SQLite fixture 上，复刻
  AgentConversationProjectionService.GetConversationAsync 的三条会话级查询，
  分别在「只有现有索引」与「临时加候选索引」两种状态打印
  EXPLAIN QUERY PLAN 与实测耗时，用于证明/复现「按 conversation_id 扫全会话」。

只使用 Python 标准库 sqlite3；数据只写 --db-path（默认 temp/test-out）。
默认 full 规模：conversation_events 3,120,000 行，其中单个目标会话
1,818,000 行（1820 turns），该会话 ChatMessages 1820 行。
--quick 为 1/10 规模，供快速迭代。

示例：
  python Tools/Diagnostics/conversation_scan_baseline.py --quick
  python Tools/Diagnostics/conversation_scan_baseline.py            # full
  python Tools/Diagnostics/conversation_scan_baseline.py --keep --db-path temp/test-out/my.db

注意：fixture 是「查询形状 + 规模 + 索引」的可复现基线，payload 平均体积小于
生产库（生产 10.6 GiB / 3.12M 行 ≈ 3.4 KiB/行）；绝对耗时只能在同一 fixture
内横向比较，不能直接当作生产 SLA。
"""

from __future__ import annotations

import argparse
import json
import os
import random
import sqlite3
import sys
import time

# ── 事件类型（与 Source/PuddingCore/Platform/ConversationContracts.cs 一致） ──
TURN_ACCEPTED = "turn.accepted"
TURN_STARTED = "turn.started"
TURN_COMPLETED = "turn.completed"
TURN_FAILED = "turn.failed"
TURN_CANCELLED = "turn.cancelled"
RUN_LEASE_LOST = "run.lease_lost"
MESSAGE_CREATED = "message.created"
MESSAGE_CONTENT_APPENDED = "message.content.appended"
MESSAGE_THINKING_SUMMARY_APPENDED = "message.thinking_summary.appended"
TOOL_CALL_REQUESTED = "tool.call.requested"
TOOL_CALL_COMPLETED = "tool.call.completed"
TOOL_CALL_FAILED = "tool.call.failed"
USAGE_RECORDED = "usage.recorded"
CONTEXT_ASSEMBLED = "context.assembled"
SUBAGENT_RUN_CREATED = "subagent.run.created"
SUBAGENT_RUN_STARTED = "subagent.run.started"
SUBAGENT_RUN_COMPLETED = "subagent.run.completed"
SUBAGENT_RUN_BUDGET_EXHAUSTED = "subagent.run.budget_exhausted"
SUBAGENT_RUN_FAILED = "subagent.run.failed"
SUBAGENT_RUN_CANCELLED = "subagent.run.cancelled"
SUBAGENT_RUN_TIMED_OUT = "subagent.run.timed_out"
SUBAGENT_RUN_INTERRUPTED = "subagent.run.interrupted"

# GetConversationAsync 的 MessageProjectionEventTypes（4 + 8 子代理 + turn.completed = 13）
MESSAGE_PROJECTION_EVENT_TYPES = [
    MESSAGE_THINKING_SUMMARY_APPENDED,
    TOOL_CALL_REQUESTED,
    TOOL_CALL_COMPLETED,
    TOOL_CALL_FAILED,
    SUBAGENT_RUN_CREATED,
    SUBAGENT_RUN_STARTED,
    SUBAGENT_RUN_COMPLETED,
    SUBAGENT_RUN_BUDGET_EXHAUSTED,
    SUBAGENT_RUN_FAILED,
    SUBAGENT_RUN_CANCELLED,
    SUBAGENT_RUN_TIMED_OUT,
    SUBAGENT_RUN_INTERRUPTED,
    TURN_COMPLETED,
]

# GetConversationAsync 的 TerminalEventTypes（4）
TERMINAL_EVENT_TYPES = [TURN_COMPLETED, TURN_FAILED, TURN_CANCELLED, RUN_LEASE_LOST]

DUPLICATE_PLACEHOLDER = "(duplicate message — already processed)"
DUPLICATE_PLACEHOLDER_LEGACY = "(duplicate message - already processed)"

TARGET_SESSION = "206a9b48ec904ebb93e7541131fbb835"
WORKSPACE = "default"

CONVERSATION_EVENTS_DDL = """
CREATE TABLE conversation_events (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    conversation_id TEXT NOT NULL,
    sequence INTEGER NOT NULL,
    event_id TEXT NOT NULL,
    workspace_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    command_id TEXT NULL,
    run_id TEXT NULL,
    message_id TEXT NULL,
    type TEXT NOT NULL,
    schema_version INTEGER NOT NULL DEFAULT 1,
    payload TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    committed_at TEXT NOT NULL,
    correlation_id TEXT NULL,
    causation_id TEXT NULL,
    producer_event_id TEXT NULL,
    agent_id TEXT NULL,
    source_kind TEXT NULL,
    trace_id TEXT NULL,
    producer_component TEXT NULL
);
"""

CHAT_MESSAGES_DDL = """
CREATE TABLE ChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    WorkspaceId TEXT NOT NULL,
    AgentInstanceId TEXT NOT NULL,
    AgentTemplateId TEXT NOT NULL DEFAULT '',
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    ThinkingJson TEXT NULL,
    UsageJson TEXT NULL,
    turn_id TEXT NULL,
    command_id TEXT NULL,
    user_id TEXT NULL,
    metadata_json TEXT NULL,
    CreatedAt INTEGER NOT NULL,
    content_parts_json TEXT NULL
);
"""

CHAT_COMMANDS_DDL = """
CREATE TABLE chat_execution_commands (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    command_id TEXT NOT NULL,
    batch_id TEXT NOT NULL DEFAULT '',
    client_request_id TEXT NULL,
    workspace_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    message_id TEXT NOT NULL,
    user_message_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    agent_instance_id TEXT NOT NULL,
    user_id TEXT NULL,
    run_id TEXT NULL,
    terminal_sequence INTEGER NULL,
    status TEXT NOT NULL DEFAULT 'completed',
    attempt_count INTEGER NOT NULL DEFAULT 1,
    created_at INTEGER NOT NULL
);
"""

# 现有 EF 形状索引（PlatformDbContext）：不含 Plan §6 的候选索引。
EXISTING_INDEX_DDL = [
    # conversation_events：EF 模型 (PlatformDbContext.cs L587-L596)
    'CREATE UNIQUE INDEX "IX_conversation_events_conversation_id_sequence" '
    'ON "conversation_events" ("conversation_id", "sequence");',
    'CREATE UNIQUE INDEX "IX_conversation_events_event_id" ON "conversation_events" ("event_id");',
    'CREATE INDEX "IX_conversation_events_turn_id_type" ON "conversation_events" ("turn_id", "type");',
    'CREATE INDEX "ix_prune_conversation_events_committed_at" ON "conversation_events" ("committed_at");',
    # ConversationEventStore ensure (ConversationEventStore.cs L457)
    "CREATE INDEX IF NOT EXISTS IX_conversation_events_trace_id_seq "
    "ON conversation_events(trace_id, sequence);",
    # ChatMessages：EF 模型 (PlatformDbContext.cs L431-L443)
    'CREATE UNIQUE INDEX "IX_ChatMessages_message_id" ON "ChatMessages" ("message_id");',
    'CREATE INDEX "IX_ChatMessages_SessionId" ON "ChatMessages" ("SessionId");',
    'CREATE INDEX "IX_ChatMessages_SessionId_CreatedAt" ON "ChatMessages" ("SessionId", "CreatedAt");',
    'CREATE INDEX "IX_ChatMessages_SessionId_turn_id" ON "ChatMessages" ("SessionId", "turn_id");',
    'CREATE INDEX "IX_ChatMessages_WorkspaceId_AgentInstanceId_CreatedAt" '
    'ON "ChatMessages" ("WorkspaceId", "AgentInstanceId", "CreatedAt");',
    # chat_execution_commands：EF 模型（会话/turn 映射所需的最小索引）
    'CREATE UNIQUE INDEX "IX_chat_execution_commands_command_id" ON "chat_execution_commands" ("command_id");',
    'CREATE INDEX "IX_chat_execution_commands_session_id_status" '
    'ON "chat_execution_commands" ("session_id", "status");',
]

# Plan §6 候选索引（只在第二个测量阶段临时创建；fixture 内部使用，不代表已落库）
CANDIDATE_INDEX_DDL = [
    "CREATE INDEX ix_conv_events_conv_message "
    "ON conversation_events(conversation_id, message_id, sequence);",
    "CREATE INDEX ix_conv_events_conv_turn "
    "ON conversation_events(conversation_id, turn_id, sequence);",
]

EVENT_INSERT_SQL = (
    "INSERT INTO conversation_events (conversation_id, sequence, event_id, workspace_id, "
    "turn_id, command_id, run_id, message_id, type, schema_version, payload, occurred_at, "
    "committed_at, trace_id) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)"
)
CHAT_MESSAGE_INSERT_SQL = (
    "INSERT INTO ChatMessages (message_id, SessionId, WorkspaceId, AgentInstanceId, "
    "AgentTemplateId, Role, Content, turn_id, command_id, user_id, CreatedAt) "
    "VALUES (?,?,?,?,?,?,?,?,?,?,?)"
)
CHAT_COMMAND_INSERT_SQL = (
    "INSERT INTO chat_execution_commands (command_id, workspace_id, session_id, message_id, "
    "user_message_id, turn_id, agent_instance_id, created_at) VALUES (?,?,?,?,?,?,?,?)"
)


def _repo_root() -> str:
    here = os.path.abspath(__file__)
    return os.path.dirname(os.path.dirname(os.path.dirname(here)))


def log(msg: str = "") -> None:
    print(msg, flush=True)


class _Timer:
    def __init__(self, label: str):
        self.label = label
        self.ms = 0.0

    def __enter__(self):
        self.t0 = time.perf_counter()
        return self

    def __exit__(self, *exc):
        self.ms = (time.perf_counter() - self.t0) * 1000.0
        log(f"  [{self.label}] {self.ms:10.1f} ms")
        return False


def build_schema(conn: sqlite3.Connection) -> None:
    conn.execute("PRAGMA journal_mode=OFF")
    conn.execute("PRAGMA synchronous=OFF")
    conn.execute("PRAGMA temp_store=MEMORY")
    conn.execute("PRAGMA cache_size=-65536")
    for ddl in (CONVERSATION_EVENTS_DDL, CHAT_MESSAGES_DDL, CHAT_COMMANDS_DDL):
        conn.execute(ddl)


def create_indexes(conn: sqlite3.Connection, ddls: list[str]) -> None:
    for ddl in ddls:
        conn.execute(ddl)
    conn.commit()


# ── 载荷生成：为避免 3.12M 行逐字符随机（过慢），使用大池切片 ──
_CJK = (
    "的一是在不了有和人这中大为上个国我以要他时来用们生到作地于出就分对成会可主发年动同工也能下过子"
    "说产种面而方后多定行学法所民得经十三之进着等部度家电力里如水化高自二理起小物现实加量都两体制"
    "机当使点从业本去把性好应开它合还因由其些然前外天政四日那社义事平形相全表间样与关各重新线内数"
    "正心反你明看原又么利比或但质气第向道命此变条只没结解问意建月公无系军很情者最立代想已通并提直"
    "题党程展五果料象员革位入常文总次品式活设及管特件长求老头基资边流路级少图山统接知较将组见计别"
    "她手角期根论运农指几九区强放决西被干做必战先回则任取据处理世车价受听声觉状华际议"
)
_ASCII = "abcdefghijklmnopqrstuvwxyz0123456789_/\\-."
_RNG_POOL = random.Random(424242)
_CJK_POOL = "".join(_RNG_POOL.choice(_CJK) for _ in range(200_000))
_ASCII_POOL = "".join(_RNG_POOL.choice(_ASCII) for _ in range(200_000))
_HUGE_64K = "x" * 65536
_HUGE_128K = "y" * 131072


def _cjk_slice(rng: random.Random, n: int) -> str:
    off = rng.randrange(0, len(_CJK_POOL) - n)
    return _CJK_POOL[off: off + n]


def _ascii_slice(rng: random.Random, n: int) -> str:
    off = rng.randrange(0, len(_ASCII_POOL) - n)
    return _ASCII_POOL[off: off + n]


def _thinking_payload(rng: random.Random) -> str:
    return '{"delta":"' + _cjk_slice(rng, rng.randint(24, 180)) + '"}'


def _content_payload(rng: random.Random) -> str:
    return '{"delta":"' + _cjk_slice(rng, rng.randint(20, 160)) + '"}'


def _tool_payload(rng: random.Random, tool: str, result: bool) -> str:
    if not result:
        return (
            '{"toolCallId":"call-%d","toolName":"%s","arguments":{"path":"%s","limit":%d}}'
            % (rng.randint(1, 10**9), tool, _ascii_slice(rng, rng.randint(8, 40)), rng.randint(10, 500))
        )
    size = rng.choices([300, 1200, 4000, 8000], weights=[70, 20, 8, 2])[0]
    return '{"toolCallId":"call-%d","toolName":"%s","exitCode":0,"output":"%s"}' % (
        rng.randint(1, 10**9),
        tool,
        _ascii_slice(rng, size),
    )


def _subagent_payload(rng: random.Random, run_id: str, event_type: str) -> str:
    if event_type == SUBAGENT_RUN_CREATED:
        return '{"subRunId":"%s","template":"general-assistant","task":"%s"}' % (
            run_id,
            _cjk_slice(rng, rng.randint(12, 60)),
        )
    if event_type == SUBAGENT_RUN_COMPLETED:
        return '{"subRunId":"%s","status":"succeeded","rounds":%d,"toolCalls":%d}' % (
            run_id,
            rng.randint(1, 8),
            rng.randint(0, 20),
        )
    if event_type == SUBAGENT_RUN_FAILED:
        return '{"subRunId":"%s","status":"failed","errorCode":"timeout"}' % run_id
    return '{"subRunId":"%s"}' % run_id


def _usage_payload(rng: random.Random) -> str:
    return '{"promptTokens":%d,"completionTokens":%d,"cachedTokens":%d}' % (
        rng.randint(800, 3000),
        rng.randint(20, 300),
        rng.randint(0, 2000),
    )


def _context_payload(rng: random.Random) -> str:
    return '{"layers":["system","history","tools"],"tokens":%d}' % rng.randint(10000, 60000)


def _turn_extras(turn_no: int) -> int:
    """turn 内非 body 的固定附加事件数（子代理生命周期 / 巨型 payload）。"""
    extra = 20 if turn_no % 17 == 0 else 0
    extra += 1 if turn_no % 300 == 0 else 0
    extra += 1 if turn_no % 500 == 0 else 0
    return extra


def _turn_sizes(rng: random.Random, turns: int, body_total: int) -> list[int]:
    """为 turns 个 turn 分配 body 事件数，含少量长 Turn；总和精确等于 body_total。"""
    weights = [rng.uniform(0.7, 1.4) for _ in range(turns)]
    long_indexes = rng.sample(range(turns), k=max(3, turns // 200))
    for i, idx in enumerate(long_indexes):
        weights[idx] = [45.0, 30.0, 18.0, 12.0, 8.0][i % 5]
    raw = [max(1, int(round(w / sum(weights) * body_total))) for w in weights]
    diff = body_total - sum(raw)
    adjust = max(range(turns), key=lambda i: raw[i])
    raw[adjust] = max(1, raw[adjust] + diff)
    return raw


def _turn_body_types(rng: random.Random, count: int) -> list[str]:
    """一个 turn 内 body event 类型分布；依据真实库最近 3 个 turn 的观测：
    thinking 约 79%、content 约 6.7%、tool request/result 各约 4.7%、usage 约 4.4%、
    其余 message.created/tool.failed/context 少量。"""
    out: list[str] = []
    for _ in range(count):
        r = rng.random()
        if r < 0.79:
            out.append(MESSAGE_THINKING_SUMMARY_APPENDED)
        elif r < 0.857:
            out.append(MESSAGE_CONTENT_APPENDED)
        elif r < 0.904:
            out.append(TOOL_CALL_REQUESTED)
        elif r < 0.951:
            out.append(TOOL_CALL_COMPLETED)
        elif r < 0.995:
            out.append(USAGE_RECORDED)
        else:
            out.append(rng.choice([MESSAGE_CREATED, CONTEXT_ASSEMBLED, TOOL_CALL_FAILED]))
    return out


def insert_target_session(
    conn: sqlite3.Connection, turns: int, target_events: int, rng: random.Random
) -> int:
    total_extras = sum(_turn_extras(t) for t in range(1, turns + 1))
    # 每个 turn 固定 4 条生命周期事件（turn.accepted / turn.started / message.created / turn.completed）
    body_sizes = _turn_sizes(rng, turns, target_events - 4 * turns - total_extras)
    batch: list[tuple] = []
    seq = 0
    for turn_no in range(1, turns + 1):
        turn_id = "turn-%04d" % turn_no
        msg_id = "msg-a-%04d" % turn_no
        user_msg_id = "msg-u-%04d" % turn_no
        command_id = "cmd-%04d" % turn_no
        root_run = "run-%04d-root" % turn_no
        sub_run = "run-%04d-sub01" % turn_no
        occurred = "2026-10-%02dT%02d:%02d:%02d.000Z" % (
            1 + (turn_no // 1000) % 17,
            (turn_no // 60) % 24,
            turn_no % 60,
            (turn_no * 7) % 60,
        )

        def emit(event_type: str, message_id, run_id, payload: str) -> None:
            nonlocal seq
            batch.append(
                (
                    TARGET_SESSION,
                    seq,
                    "evt-%s-%d" % (TARGET_SESSION[:8], seq),
                    WORKSPACE,
                    turn_id,
                    command_id,
                    run_id,
                    message_id,
                    event_type,
                    1,
                    payload,
                    occurred,
                    occurred,
                    "trace-%04d" % turn_no,
                )
            )
            seq += 1

        emit(TURN_ACCEPTED, user_msg_id, root_run, '{"accepted":true}')
        emit(TURN_STARTED, user_msg_id, root_run, '{"userMessageId":"%s"}' % user_msg_id)
        emit(MESSAGE_CREATED, user_msg_id, root_run, '{"role":"user","messageId":"%s"}' % user_msg_id)
        for body_type in _turn_body_types(rng, body_sizes[turn_no - 1]):
            if body_type == MESSAGE_THINKING_SUMMARY_APPENDED:
                emit(body_type, msg_id, root_run, _thinking_payload(rng))
            elif body_type == MESSAGE_CONTENT_APPENDED:
                emit(body_type, msg_id, root_run, _content_payload(rng))
            elif body_type == TOOL_CALL_REQUESTED:
                tool = rng.choice(["search_grep", "read_file", "list_dir", "shell"])
                emit(body_type, msg_id, root_run, _tool_payload(rng, tool, result=False))
            elif body_type == TOOL_CALL_COMPLETED:
                tool = rng.choice(["search_grep", "read_file", "list_dir", "shell"])
                emit(body_type, msg_id, root_run, _tool_payload(rng, tool, result=True))
            elif body_type == TOOL_CALL_FAILED:
                emit(body_type, msg_id, root_run, '{"toolCallId":"call-1","error":"denied"}')
            elif body_type == USAGE_RECORDED:
                emit(body_type, None, root_run, _usage_payload(rng))
            elif body_type == CONTEXT_ASSEMBLED:
                emit(body_type, None, root_run, _context_payload(rng))
            else:
                emit(body_type, msg_id, root_run, '{"note":"lifecycle"}')

        # 少量 turn 含子代理生命周期（run_id 为子 run，message_id 挂父消息）
        if turn_no % 17 == 0:
            for _ in range(20):
                st = rng.choice(
                    [
                        SUBAGENT_RUN_CREATED,
                        SUBAGENT_RUN_STARTED,
                        SUBAGENT_RUN_COMPLETED,
                        SUBAGENT_RUN_BUDGET_EXHAUSTED,
                        SUBAGENT_RUN_FAILED,
                        SUBAGENT_RUN_CANCELLED,
                        SUBAGENT_RUN_TIMED_OUT,
                        SUBAGENT_RUN_INTERRUPTED,
                    ]
                )
                emit(st, msg_id, sub_run, _subagent_payload(rng, sub_run, st))
        # 极少超大 payload（模拟 context 全量快照 / 超大工具结果）
        if turn_no % 300 == 0:
            emit(CONTEXT_ASSEMBLED, None, root_run, '{"snapshot":"%s"}' % _HUGE_64K)
        if turn_no % 500 == 0:
            emit(TOOL_CALL_COMPLETED, msg_id, root_run, '{"output":"%s"}' % _HUGE_128K)

        emit(TURN_COMPLETED, msg_id, root_run, '{"status":"succeeded"}')

        if len(batch) >= 20000:
            conn.executemany(EVENT_INSERT_SQL, batch)
            batch.clear()
    if batch:
        conn.executemany(EVENT_INSERT_SQL, batch)
        batch.clear()
    return seq


def insert_other_sessions(
    conn: sqlite3.Connection, sessions: int, total_events: int, rng: random.Random
) -> int:
    batch: list[tuple] = []
    per_session = total_events // sessions
    remaining = total_events - per_session * sessions
    written = 0
    for s in range(sessions):
        conv = "conv-%04d-%s" % (s, _ascii_slice(rng, 8))
        n = per_session + (1 if s < remaining else 0)
        for k in range(1, n + 1):
            turn_id = "%s-turn-%04d" % (conv, k // 600)
            ev_type = rng.choices(
                [
                    MESSAGE_THINKING_SUMMARY_APPENDED,
                    MESSAGE_CONTENT_APPENDED,
                    TOOL_CALL_COMPLETED,
                    USAGE_RECORDED,
                    TURN_COMPLETED,
                ],
                weights=[60, 10, 10, 10, 10],
            )[0]
            payload = _thinking_payload(rng) if ev_type != USAGE_RECORDED else _usage_payload(rng)
            occurred = "2026-09T%02d:%02d:%02d.000Z" % (s % 24, k % 60, (k * 7) % 60)
            batch.append(
                (
                    conv,
                    k,
                    "evt-%d-%d" % (s, k),
                    WORKSPACE,
                    turn_id,
                    None,
                    conv + "-run",
                    "msg-%04d" % (k // 600),
                    ev_type,
                    1,
                    payload,
                    occurred,
                    occurred,
                    "trace-o-%d" % s,
                )
            )
            written += 1
            if len(batch) >= 20000:
                conn.executemany(EVENT_INSERT_SQL, batch)
                batch.clear()
    if batch:
        conn.executemany(EVENT_INSERT_SQL, batch)
    return written


def insert_messages(
    conn: sqlite3.Connection, turns: int, other_sessions: int, rng: random.Random
) -> tuple[int, int]:
    msg_rows: list[tuple] = []
    cmd_rows: list[tuple] = []
    for turn_no in range(1, turns + 1):
        created = 1760000000000 + turn_no * 60000
        turn_id = "turn-%04d" % turn_no
        msg_id = "msg-a-%04d" % turn_no
        if turn_no % 97 == 0:
            content = (
                '{"schema":"pudding-message/v1","message_id":"%s","from":{"kind":"agent"},'
                '"to":[],"context":{"text":"%s"}}' % (msg_id, _cjk_slice(rng, 120))
            )
        elif turn_no % 211 == 0:
            content = _cjk_slice(rng, rng.randint(9000, 28000))  # 长消息（> §5.4 8 KiB 预算）
        elif turn_no % 37 == 0:
            content = '{"v":1,"parts":[{"type":"text","text":"%s"}]}' % _cjk_slice(rng, 1200)
        else:
            content = _cjk_slice(rng, rng.randint(20, 400))
        msg_rows.append(
            (
                msg_id,
                TARGET_SESSION,
                WORKSPACE,
                "agent-general",
                "",
                "agent",
                content,
                turn_id,
                "cmd-%04d" % turn_no,
                "single-user",
                created,
            )
        )
        cmd_rows.append(
            (
                "cmd-%04d" % turn_no,
                WORKSPACE,
                TARGET_SESSION,
                msg_id,
                "msg-u-%04d" % turn_no,
                turn_id,
                "agent-general",
                created,
            )
        )
    other_msgs: list[tuple] = []
    for s in range(min(50, other_sessions)):
        conv = "conv-%04d-other" % s
        for k in range(1, 6):
            other_msgs.append(
                (
                    "msg-%s-%d" % (conv, k),
                    conv,
                    WORKSPACE,
                    "agent-general",
                    "",
                    "agent",
                    _cjk_slice(rng, 120),
                    "%s-turn-0000" % conv,
                    None,
                    "single-user",
                    1750000000000 + s * 1000 + k,
                )
            )
    conn.executemany(CHAT_MESSAGE_INSERT_SQL, msg_rows + other_msgs)
    conn.executemany(CHAT_COMMAND_INSERT_SQL, cmd_rows)
    return len(msg_rows), len(other_msgs)


def _candidate_ids(conn: sqlite3.Connection, limit_candidates: int, limit_visible: int):
    import json as _json

    rows = conn.execute(
        """
        SELECT Id, message_id, turn_id, Content
        FROM ChatMessages
        WHERE SessionId = ?
          AND Content <> ?
          AND Content <> ?
        ORDER BY CreatedAt DESC, Id DESC
        LIMIT ?
        """,
        (TARGET_SESSION, DUPLICATE_PLACEHOLDER, DUPLICATE_PLACEHOLDER_LEGACY, limit_candidates),
    ).fetchall()
    rows = list(reversed(rows))
    deduped: list = []
    seen_envelope: set[str] = set()
    for row in rows:
        content = row[3]
        if content.lstrip().startswith("{"):
            try:
                doc = _json.loads(content)
                if isinstance(doc, dict) and "message_id" in doc and "from" in doc and "context" in doc:
                    mid = doc["message_id"]
                    if mid in seen_envelope:
                        continue
                    seen_envelope.add(mid)
            except Exception:
                pass
        deduped.append(row)
    visible = deduped[-limit_visible:]
    message_ids = [r[1] for r in visible]
    turn_ids = [r[2] for r in visible if r[2]]
    root = conn.execute(
        "SELECT run_id, turn_id FROM conversation_events "
        "WHERE conversation_id = ? AND type = ? ORDER BY sequence DESC LIMIT 1",
        (TARGET_SESSION, TURN_STARTED),
    ).fetchone()
    return message_ids, turn_ids, (root[0] if root else None), (root[1] if root else None)


def _measure(conn: sqlite3.Connection, label: str, sql: str, params: list, repeats: int = 2):
    plan_rows = [r[3] for r in conn.execute("EXPLAIN QUERY PLAN " + sql, params).fetchall()]
    times = []
    rows = 0
    for _ in range(repeats):
        t0 = time.perf_counter()
        rows = len(conn.execute(sql, params).fetchall())
        times.append((time.perf_counter() - t0) * 1000.0)
    log(f"  {label}")
    log(f"      plan : {' | '.join(plan_rows)}")
    log(f"      rows : {rows}   run1={times[0]:.1f}ms run2={times[-1]:.1f}ms best={min(times):.1f}ms")
    return {"label": label, "plan": plan_rows, "rows": rows, "best": min(times), "times": times}


def build_queries(conn: sqlite3.Connection):
    message_ids, turn_ids, root_run_id, root_turn_id = _candidate_ids(conn, 60, 20)
    msg_ph = ",".join("?" for _ in message_ids)
    turn_ph = ",".join("?" for _ in turn_ids)
    type_ph = ",".join("?" for _ in MESSAGE_PROJECTION_EVENT_TYPES)
    term_ph = ",".join("?" for _ in TERMINAL_EVENT_TYPES)

    # A. messageProcess（EF 实际形状：SELECT 5 列，ORDER BY sequence）
    q_message_process = (
        "SELECT message_id, run_id, type, sequence, occurred_at FROM conversation_events "
        f"WHERE conversation_id = ? AND message_id IS NOT NULL AND message_id IN ({msg_ph}) "
        f"AND type IN ({type_ph}) ORDER BY sequence"
    )
    # B. terminal（EF 实际形状：整实体 21 列）
    q_terminal = (
        "SELECT * FROM conversation_events "
        f"WHERE conversation_id = ? AND turn_id IN ({turn_ph}) "
        f"AND type IN ({term_ph}) ORDER BY sequence"
    )
    # C. rootTerminalExists（EF 实际形状：EXISTS + run_id）
    q_root_terminal = (
        "SELECT EXISTS(SELECT 1 FROM conversation_events "
        "WHERE conversation_id = ? AND run_id = ? "
        f"AND type IN ({term_ph}))"
    )
    # C2. 同一语义改成 turn 收敛（Plan §6 不建 run_id 索引时的查询侧改写）
    q_root_terminal_by_turn = (
        "SELECT EXISTS(SELECT 1 FROM conversation_events "
        "WHERE conversation_id = ? AND turn_id = ? AND run_id = ? "
        f"AND type IN ({term_ph}))"
    )
    # D. 事件游标 MAX(sequence)
    q_cursor = "SELECT MAX(sequence) FROM conversation_events WHERE conversation_id = ?"
    # E. 最近消息候选窗口（ChatMessages）
    q_messages = (
        "SELECT Id, message_id, Role, Content, turn_id, metadata_json, content_parts_json, CreatedAt "
        "FROM ChatMessages WHERE SessionId = ? AND Content <> ? AND Content <> ? "
        "ORDER BY CreatedAt DESC, Id DESC LIMIT ?"
    )
    # A2/B2：推荐查询形状（VALUES-JOIN，逐值收敛；仅供索引收益与形状改造验证）
    msg_values = ",".join(["(?)"] * len(message_ids))
    q_message_process_join = (
        "WITH m(mid) AS (VALUES " + msg_values + ") "
        "SELECT e.message_id, e.run_id, e.type, e.sequence, e.occurred_at "
        "FROM m JOIN conversation_events e ON e.conversation_id = ? AND e.message_id = m.mid "
        f"WHERE e.type IN ({type_ph}) ORDER BY e.sequence"
    )
    turn_values = ",".join(["(?)"] * len(turn_ids))
    q_terminal_join = (
        "WITH t(tid) AS (VALUES " + turn_values + ") "
        "SELECT e.* FROM t JOIN conversation_events e "
        "ON e.conversation_id = ? AND e.turn_id = t.tid "
        f"WHERE e.type IN ({term_ph}) ORDER BY e.sequence"
    )

    queries = [
        ("A messageProcess (messageIds, 13 types)", q_message_process,
         [TARGET_SESSION] + message_ids + MESSAGE_PROJECTION_EVENT_TYPES),
        ("B terminal (turnIds, 4 types)", q_terminal,
         [TARGET_SESSION] + turn_ids + TERMINAL_EVENT_TYPES),
        ("C rootTerminalExists (run_id, EF 原形)", q_root_terminal,
         [TARGET_SESSION, root_run_id] + TERMINAL_EVENT_TYPES),
        ("C2 rootTerminalExists (turn_id 收敛改写)", q_root_terminal_by_turn,
         [TARGET_SESSION, root_turn_id, root_run_id] + TERMINAL_EVENT_TYPES),
        ("D eventCursor MAX(sequence)", q_cursor, [TARGET_SESSION]),
        ("E ChatMessages candidates LIMIT 60", q_messages,
         [TARGET_SESSION, DUPLICATE_PLACEHOLDER, DUPLICATE_PLACEHOLDER_LEGACY, 60]),
    ]
    # 推荐形状（phase 4 使用；在这些形状下候选索引才被 planner 采用）
    recommended = [
        ("A2 messageProcess VALUES-JOIN", q_message_process_join,
         message_ids + [TARGET_SESSION] + MESSAGE_PROJECTION_EVENT_TYPES),
        ("B2 terminal VALUES-JOIN", q_terminal_join,
         turn_ids + [TARGET_SESSION] + TERMINAL_EVENT_TYPES),
    ]
    meta = {
        "messageIds": len(message_ids),
        "turnIds": len(turn_ids),
        "rootRunId": root_run_id,
        "rootTurnId": root_turn_id,
    }
    return queries, recommended, meta


def run(args: argparse.Namespace) -> int:
    quick = args.quick
    total_events = 312_000 if quick else 3_120_000
    target_events = 181_800 if quick else 1_818_000
    turns = 182 if quick else 1820
    # 真实库 conversation_heads = 138（含目标会话），其它会话按同数量级缩放
    other_sessions = 14 if quick else 137
    other_events = total_events - target_events

    db_path = args.db_path
    if not os.path.isabs(db_path):
        db_path = os.path.join(_repo_root(), db_path)
    db_path = os.path.abspath(db_path)
    os.makedirs(os.path.dirname(db_path), exist_ok=True)
    for suffix in ("", "-wal", "-shm", "-journal"):
        try:
            os.remove(db_path + suffix)
        except OSError:
            pass

    log("=" * 80)
    log("conversation_scan_baseline.py — 会话扫描基线 fixture（仅标准库 sqlite3）")
    log(f"  python sqlite3 = {sqlite3.sqlite_version}")
    log(f"  scale = {'quick' if quick else 'full'}")
    log(
        f"  conversation_events 总数 = {total_events:,}；目标会话 = {target_events:,} "
        f"(turns={turns:,})；目标会话 ChatMessages = {turns:,}；其它会话 = {other_sessions}"
    )
    log(f"  db = {db_path}")
    log("=" * 80)

    rng = random.Random(20261018)
    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA locking_mode=EXCLUSIVE")
    t_start = time.perf_counter()
    with _Timer("build schema"):
        build_schema(conn)
        conn.commit()
    log("  插入目标会话 events ...")
    with _Timer("insert events(target session)"):
        target_written = insert_target_session(conn, turns, target_events, rng)
    log("  插入其它会话 events ...")
    with _Timer("insert events(other sessions)"):
        other_written = insert_other_sessions(conn, other_sessions, other_events, rng)
    log("  插入 ChatMessages / chat_execution_commands ...")
    with _Timer("insert ChatMessages + commands"):
        target_messages, other_messages = insert_messages(conn, turns, other_sessions, rng)
    with _Timer("create existing indexes (EF shape)"):
        create_indexes(conn, EXISTING_INDEX_DDL)
    build_ms = (time.perf_counter() - t_start) * 1000.0
    size_mb = os.path.getsize(db_path) / (1024 * 1024)
    log(f"  fixture size = {size_mb:.1f} MiB，build+insert 总耗时 = {build_ms / 1000.0:.1f}s")

    checks = {
        "conversation_events": conn.execute("SELECT COUNT(*) FROM conversation_events").fetchone()[0],
        "target session events": conn.execute(
            "SELECT COUNT(*) FROM conversation_events WHERE conversation_id = ?", (TARGET_SESSION,)
        ).fetchone()[0],
        "ChatMessages(target)": conn.execute(
            "SELECT COUNT(*) FROM ChatMessages WHERE SessionId = ?", (TARGET_SESSION,)
        ).fetchone()[0],
        "turns(target)": conn.execute(
            "SELECT COUNT(DISTINCT turn_id) FROM conversation_events WHERE conversation_id = ?",
            (TARGET_SESSION,),
        ).fetchone()[0],
    }
    for k, v in checks.items():
        log(f"  check {k:<24} = {v:,}")
    log(f"  written(target/other events) = {target_written:,} / {other_written:,}；"
        f"messages={target_messages:,}+{other_messages:,}")

    queries, recommended, meta = build_queries(conn)
    log("")
    log(f"  查询输入规模: messageIds={meta['messageIds']} turnIds={meta['turnIds']} "
        f"rootRunId={meta['rootRunId']} rootTurnId={meta['rootTurnId']}")
    log("")
    log("-" * 80)
    log("阶段 1：只有现有索引（无候选索引；与生产库一致，sqlite_stat1 不存在）")
    log("-" * 80)
    phase1 = [_measure(conn, *q) for q in queries]

    log("")
    log("-" * 80)
    log("阶段 2：临时创建候选索引后（先不 ANALYZE，检验「只建索引不更新统计」是否被采用）")
    log("-" * 80)
    with _Timer("create candidate indexes"):
        create_indexes(conn, CANDIDATE_INDEX_DDL)
    phase2 = [_measure(conn, *q) for q in queries]

    log("")
    log("-" * 80)
    log("阶段 3：更新统计后（ANALYZE conversation_events；对应 Plan §6 的维护窗口更新统计）")
    log("-" * 80)
    with _Timer("ANALYZE conversation_events"):
        conn.execute("ANALYZE conversation_events")
        conn.commit()
    phase3 = [_measure(conn, *q) for q in queries]

    log("")
    log("-" * 80)
    log("阶段 4：推荐查询形状（VALUES-JOIN 逐值收敛；候选索引 + ANALYZE 状态下）")
    log("-" * 80)
    phase4 = [_measure(conn, *q) for q in recommended]

    log("")
    log("=" * 80)
    log("结论")
    log("=" * 80)
    for p1, p2, p3 in zip(phase1, phase2, phase3):
        speedup = p1["best"] / p3["best"] if p3["best"] > 0 else float("inf")
        log(f"  {p1['label']}")
        log(f"      阶段1 无候选索引        : {p1['best']:10.1f} ms   plan: {' | '.join(p1['plan'])}")
        log(f"      阶段2 有索引未 ANALYZE  : {p2['best']:10.1f} ms   plan: {' | '.join(p2['plan'])}")
        log(f"      阶段3 有索引+ANALYZE    : {p3['best']:10.1f} ms   plan: {' | '.join(p3['plan'])}")
        log(f"      speedup(阶段1/阶段3) = {speedup:.1f}x")
    log("")
    for q in phase4:
        log(f"  推荐形状 {q['label']:<30} : {q['best']:10.1f} ms   plan: {' | '.join(q['plan'])}")
    log("")
    log("  判据提醒：EXPLAIN 出现 `SEARCH ... (conversation_id=?)` 仍可能扫描整个会话；")
    log("  必须看到 message_id/turn_id 等值收敛，不能只看返回条数报绿。")
    log("  重要发现：SQLite 3.53 的 planner 对 `conversation_id=? AND message_id IN (...)`")
    log("  形状不会自动选择 (conversation_id,message_id,sequence)（即使 ANALYZE 后仍是")
    log("  `SEARCH ... (conversation_id=?)`）；索引本身只提供「按 message_id 等值收敛」的能力，")
    log("  查询必须改写为逐值 JOIN / UNION ALL / 逐消息有界查询（阶段 4）才会使用索引。")
    log("  另：C（run_id 原形）即使加了上述两个候选索引也无法收敛，需要 (conversation_id,run_id,")
    log("  sequence) 索引，或按 C2 把查询收敛到 root turn（用 (conversation_id,turn_id,sequence)）。")
    log("")

    conn.close()
    if not args.keep:
        for suffix in ("", "-wal", "-shm", "-journal"):
            try:
                os.remove(db_path + suffix)
            except OSError:
                pass
        log(f"  已删除 fixture DB（--keep 可保留）: {db_path}")
    else:
        log(f"  保留 fixture DB: {db_path}")
    return 0


def main(argv: list[str] | None = None) -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
        sys.stderr.reconfigure(encoding="utf-8")  # type: ignore[attr-defined]
    except Exception:
        pass
    parser = argparse.ArgumentParser(
        description="会话扫描可复现 fixture（阶段 A 证据；只使用标准库 sqlite3）"
    )
    parser.add_argument(
        "--quick", action="store_true", help="1/10 规模（31.2 万 events / 18.18 万会话 events）"
    )
    parser.add_argument(
        "--db-path",
        default=os.path.join("temp", "test-out", "conversation-scan-baseline.db"),
        help="fixture DB 路径（默认 temp/test-out/conversation-scan-baseline.db）",
    )
    parser.add_argument("--keep", action="store_true", help="运行结束后保留 fixture DB")
    args = parser.parse_args(argv)
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
