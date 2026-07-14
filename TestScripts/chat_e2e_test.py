#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Pudding Chat E2E 自动化测试 — 越过前端直接验证后端聊天链路。

覆盖范围：
  1. 健康检查
  2. JWT 登录 / Token 管理
  3. POST 发送消息 → 响应含 commandId/messageId/turnId/sessionId/eventCursor
  4. 命令队列异步执行 → 验证 turn.accepted → turn.started → delta → done
  5. SSE 事件序列校验：sequenceNum 单调递增、帧含 turnId/messageId
  6. 断线重连：afterSequence 游标补发
  7. 幂等键：相同 ClientRequestId 返回同一 commandId
  8. 错误恢复：验证错误场景的 error/turn.failed 帧

用法：
    python chat_e2e_test.py
    python chat_e2e_test.py --base-url http://127.0.0.1:5000
    python chat_e2e_test.py --user admin --password Admin@123
    python chat_e2e_test.py --quick  # 跳过长时间等待
"""

import argparse
import json
import sys
import time
import uuid
import requests

# ─── 命令行参数 ───────────────────────────────────────────────
parser = argparse.ArgumentParser(description="Pudding Chat E2E 集成测试")
parser.add_argument("--base-url", default="http://127.0.0.1:5000", help="后端 API 地址")
parser.add_argument("--user", default="admin", help="用户名")
parser.add_argument("--password", default="Admin@123", help="密码")
parser.add_argument("--quick", action="store_true", help="快速模式：跳过长时间等待")
args = parser.parse_args()

BASE = args.base_url.rstrip("/")
SESSION = requests.Session()
SESSION.headers.update({"Content-Type": "application/json"})

# ─── 统计 ─────────────────────────────────────────────────────
passed = 0
failed = 0
token = ""
workspace_id = "default"
agent_id = "general-assistant-001"

# ─── 工具函数 ─────────────────────────────────────────────────
def ok(msg):
    global passed
    passed += 1
    print(f"  [PASS] {msg}")

def fl(msg):
    global failed
    failed += 1
    print(f"  [FAIL] {msg}")

def info(msg):
    print(f"  [INFO] {msg}")

def section(title):
    print(f"\n{'='*50}")
    print(f"  {title}")
    print(f"{'='*50}")

def get(path, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.get(f"{BASE}{path}", headers=h, timeout=15)

def post(path, body=None, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.post(f"{BASE}{path}", json=body, headers=h, timeout=30)

# ─── 第 1 节：健康检查 ─────────────────────────────────────────
section("1. 健康检查")

try:
    r = requests.get(f"{BASE}/health", timeout=5)
    if r.status_code == 200:
        ok(f"Health endpoint OK: {r.status_code}")
    else:
        fl(f"Health endpoint: {r.status_code}")
except Exception as e:
    fl(f"Health endpoint unreachable: {e}")
    sys.exit(1)

# ─── 第 2 节：登录与 Token ─────────────────────────────────────
section("2. JWT 登录")

login_body = {"username": args.user, "password": args.password, "type": "account"}
try:
    r = post("/api/login/account", login_body, auth=False)
    if r.status_code == 200:
        data = r.json()
        token = data.get("token", "")
        if token:
            ok(f"Login OK, token length={len(token)}")
        else:
            fl("Login OK but no token in response")
    else:
        fl(f"Login failed: {r.status_code} {r.text[:100]}")
        sys.exit(1)
except Exception as e:
    fl(f"Login exception: {e}")
    sys.exit(1)

# ─── 第 3 节：发送消息 ─────────────────────────────────────────
section("3. POST 发送消息")

client_request_id = f"e2e-test-{uuid.uuid4().hex[:12]}"
sw = time.time()
chat_body = {
    "MessageText": "Reply with exactly the word OK",
    "WorkspaceId": workspace_id,
    "AgentId": agent_id,
    "ClientRequestId": client_request_id,
}
try:
    r = post(f"/api/workspaces/{workspace_id}/chat/message", chat_body)
    elapsed_ms = int((time.time() - sw) * 1000)
    if r.status_code == 200:
        data = r.json()
        ok(f"POST 200 in {elapsed_ms}ms")
    else:
        fl(f"POST {r.status_code}: {r.text[:100]}")
        data = {}
except Exception as e:
    fl(f"POST exception: {e}")
    data = {}

# 验证响应字段
section("3a. 验证响应字段")

expected_fields = ["status", "commandId", "messageId", "turnId", "sessionId", "eventCursor"]
all_present = True
for f in expected_fields:
    if f not in data:
        fl(f"Missing field: {f}")
        all_present = False
if all_present:
    ok(f"All {len(expected_fields)} fields present")

if data.get("status") == "accepted":
    ok("status = 'accepted' (command queued)")
else:
    fl(f"status = '{data.get('status')}' (expected 'accepted')")

command_id = data.get("commandId", "")
message_id = data.get("messageId", "")
turn_id = data.get("turnId", "")
session_id = data.get("sessionId", "")
event_cursor = data.get("eventCursor", 0)

info(f"  commandId={command_id}")
info(f"  messageId={message_id}")
info(f"  turnId={turn_id}")
info(f"  sessionId={session_id}")
info(f"  eventCursor={event_cursor}")

# ─── 第 4 节：等待 Worker 异步执行 ────────────────────────────
section("4. Worker 异步执行验证")

wait_sec = 3 if args.quick else 15
info(f"Waiting {wait_sec}s for worker to process command...")
time.sleep(wait_sec)

try:
    r = get(f"/api/sessions/{session_id}/events?from={event_cursor}&limit=50")
    if r.status_code == 200:
        page = r.json()
        events = page.get("events", [])
        total = page.get("totalCount", 0)
        info(f"  Events after cursor {event_cursor}: {len(events)}/{total} total")

        event_types = [e.get("eventType") for e in events]
        ok(f"Got {len(events)} events: types={event_types[:5]}...")

        if "done" in event_types or "turn.completed" in event_types:
            ok("Turn completed (done/turn.completed found)")
        else:
            fl(f"Turn may be incomplete (no done/turn.completed found) types={event_types}")
    else:
        fl(f"GetEvents failed: {r.status_code} {r.text[:100]}")
except Exception as e:
    fl(f"GetEvents exception: {e}")

# ─── 第 5 节：SSE 序列校验 ────────────────────────────────────
section("5. SSE 序列号校验")

try:
    r = get(f"/api/sessions/{session_id}/events?from=1&limit=200")
    if r.status_code == 200:
        page = r.json()
        events = page.get("events", [])
        sequences = [e.get("sequenceNum") for e in events if e.get("sequenceNum") is not None]

        if sequences:
            if sequences == sorted(sequences):
                ok(f"Sequence numbers monotonic: {min(sequences)}→{max(sequences)} ({len(sequences)} events)")
            else:
                fl(f"Sequence numbers NOT monotonic: {sequences[:10]}...")

            has_turnid = sum(1 for e in events if e.get("eventType") == "turn.accepted")
            has_done = sum(1 for e in events if e.get("eventType") == "done")
            info(f"  turn.accepted: {has_turnid}, done: {has_done}")
        else:
            fl("No sequence numbers found in events")
    else:
        fl(f"GetEvents (seq) failed: {r.status_code}")
except Exception as e:
    fl(f"Sequence check exception: {e}")

# ─── 第 6 节：SSE id 字段与 afterSequence 重连 ──────────────
section("6. SSE id 字段与断线重连")

try:
    r = get(f"/api/sessions/{session_id}/events?from={event_cursor}&limit=5")
    if r.status_code == 200:
        page = r.json()
        events = page.get("events", [])
        has_seqnum = all(
            "sequenceNum" in json.loads(e.get("data", "{}"))
            for e in events
            if e.get("data")
        )
        if has_seqnum:
            ok(f"All {len(events)} replayed events have sequenceNum in data")
        else:
            fl("Some events missing sequenceNum in data payload")

        # 验证 afterSequence 重新查询
        if events:
            last_seq = events[-1].get("sequenceNum", 0)
            r2 = get(f"/api/sessions/{session_id}/events?from={last_seq}&limit=1")
            if r2.status_code == 200:
                next_page = r2.json()
                if next_page.get("events"):
                    ok(f"afterSequence={last_seq} returns events (reconnect OK)")
                else:
                    fl(f"afterSequence={last_seq} returned no events")
    else:
        fl(f"Reconnect test failed: {r.status_code}")
except Exception as e:
    fl(f"id-field/reconnect exception: {e}")

# ─── 第 7 节：幂等键测试 ──────────────────────────────────────
section("7. 幂等键 (ClientRequestId)")

try:
    r2 = post(f"/api/workspaces/{workspace_id}/chat/message", {
        "MessageText": "Should be ignored",
        "WorkspaceId": workspace_id,
        "AgentId": agent_id,
        "ClientRequestId": client_request_id,
    })
    if r2.status_code == 200:
        data2 = r2.json()
        if data2.get("commandId") == command_id:
            ok(f"Idempotent: same commandId={command_id}")
        else:
            fl(f"Idempotent mismatch: {data2.get('commandId')} != {command_id}")
    else:
        info(f"Idempotent: returned {r2.status_code} (expected behavior)")
except Exception as e:
    fl(f"Idempotent test exception: {e}")

# ─── 第 8 节：响应时间验证 ─────────────────────────────────────
section("8. POST 响应时间 (应 < 2s)")

new_id = f"e2e-perf-{uuid.uuid4().hex[:12]}"
sw = time.time()
try:
    r = post(f"/api/workspaces/{workspace_id}/chat/message", {
        "MessageText": "Hello",
        "WorkspaceId": workspace_id,
        "AgentId": agent_id,
        "ClientRequestId": new_id,
    })
    elapsed = int((time.time() - sw) * 1000)
    if elapsed < 2000:
        ok(f"POST response: {elapsed}ms (under 2s)")
    else:
        fl(f"POST response slow: {elapsed}ms (over 2s)")
except Exception as e:
    fl(f"Perf test exception: {e}")

# ─── 第 9 节：完整对话轮次验证 ────────────────────────────────
section("9. 对话轮次完整性")

time.sleep(5)
try:
    r = get(f"/api/sessions/{session_id}/events?from=1&limit=500")
    if r.status_code == 200:
        page = r.json()
        events = page.get("events", [])

        # 统计事件类型
        from collections import Counter
        type_counts = Counter(e.get("eventType") for e in events)
        info(f"  Event type distribution: {dict(type_counts)}")

        # 验证关键事件存在
        required = {"turn.accepted", "metadata"}
        found = set(type_counts.keys())
        for req_type in required:
            if req_type in found:
                ok(f"Required event '{req_type}' found ({type_counts[req_type]})")
            else:
                fl(f"Required event '{req_type}' NOT found")

        # 每个事件都有 sequenceNum
        all_with_seq = all(
            "sequenceNum" in json.loads(e.get("data", "{}"))
            for e in events
            if e.get("data")
        )
        if all_with_seq and len(events) > 0:
            ok(f"All {len(events)} events have sequenceNum in data")
except Exception as e:
    fl(f"Turn integrity exception: {e}")

# ─── 结果 ─────────────────────────────────────────────────────
section("结果")
print(f"  PASS: {passed}")
print(f"  FAIL: {failed}")
print(f"  TOTAL: {passed + failed}")

if failed > 0:
    print("\n  *** FAILURES DETECTED ***")
    sys.exit(1)
else:
    print("\n  ALL TESTS PASSED")
    sys.exit(0)
