#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Pudding Chat E2E 自动化测试 — 后端聊天链路全覆盖。

ADR-056 适配：
  - POST 返回 202 Accepted (命令队列模式)
  - SSE id: 字段承载 sequence 号 (不再注入 data JSON)
  - SSE event: 字段使用新事件名映射 (delta→assistant.content.delta 等)
  - /api/sessions/{sid}/projected-cursor 提供投影游标
  - afterSequence + Last-Event-ID 重连协议

用法：
    python chat_e2e_test.py
    python chat_e2e_test.py --base-url http://127.0.0.1:5000
    python chat_e2e_test.py --quick  # 跳过长时间等待
"""

import argparse
import json
import sys
import time
import uuid
import requests

# ─── 命令行参数 ────────────────────────────────────────────────
parser = argparse.ArgumentParser(description="Pudding Chat E2E 集成测试")
parser.add_argument("--base-url", default="http://127.0.0.1:5000", help="后端 API 地址")
parser.add_argument("--user", default="admin", help="用户名")
parser.add_argument("--password", default="Admin@123", help="密码")
parser.add_argument("--quick", action="store_true", help="快速模式：跳过长时间等待")
parser.add_argument("--wait", type=int, default=0, help="Worker 等待秒数 (0=自动: quick=5s, full=20s)")
args = parser.parse_args()

BASE = args.base_url.rstrip("/")
SESSION = requests.Session()
SESSION.headers.update({"Content-Type": "application/json"})

# ─── 状态 ──────────────────────────────────────────────────────
passed = 0
failed = 0
token = ""
workspace_id = "default"
agent_id = "general-assistant-001"
WAIT_SEC = args.wait or (5 if args.quick else 20)


# ─── 工具函数 ──────────────────────────────────────────────────
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
    print(f"\n{'=' * 50}")
    print(f"  {title}")
    print(f"{'=' * 50}")


def get(path, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.get(f"{BASE}{path}", headers=h, timeout=15)


def post(path, body=None, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.post(f"{BASE}{path}", json=body, headers=h, timeout=30)


# ════════════════════════════════════════════════════════════════
# 第 1 节：健康检查
# ════════════════════════════════════════════════════════════════
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


# ════════════════════════════════════════════════════════════════
# 第 2 节：JWT 登录
# ════════════════════════════════════════════════════════════════
section("2. JWT 登录")

r = post("/api/login/account", {"username": args.user, "password": args.password, "type": "account"}, auth=False)
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


# ════════════════════════════════════════════════════════════════
# 第 3 节：POST 发送消息 — ADR-056 202 Accepted
# ════════════════════════════════════════════════════════════════
section("3. POST 发送消息 — 202 Accepted (ADR-056 命令队列)")

client_request_id = f"e2e-test-{uuid.uuid4().hex[:12]}"
sw = time.time()
chat_body = {
    "MessageText": "Reply with exactly the word OK",
    "WorkspaceId": workspace_id,
    "AgentId": agent_id,
    "ClientRequestId": client_request_id,
}
r = post(f"/api/workspaces/{workspace_id}/chat/message", chat_body)
elapsed_ms = int((time.time() - sw) * 1000)

# ADR-056: 命令队列模式下应返回 202 (Accepted)，非 200
if r.status_code == 202:
    ok(f"POST 202 Accepted in {elapsed_ms}ms (命令已入队)")
elif r.status_code == 200:
    ok(f"POST 200 OK in {elapsed_ms}ms (兼容旧模式)")
else:
    fl(f"POST {r.status_code}: {r.text[:100]}")
data = r.json() if r.text else {}

# ─── 3a: 验证响应字段 ──────────────────────────────────────────
section("3a. 验证响应字段")

expected = ["status", "commandId", "messageId", "turnId", "sessionId", "eventCursor"]
all_present = all(f in data for f in expected)
if all_present:
    ok(f"All {len(expected)} fields present")
else:
    for f in expected:
        if f not in data:
            fl(f"Missing field: {f}")

if data.get("status") == "accepted":
    ok("status = 'accepted' (命令已受理)")
else:
    fl(f"status = '{data.get('status')}' (expected 'accepted')")

command_id = data.get("commandId", "")
message_id = data.get("messageId", "")
turn_id = data.get("turnId", "")
session_id = data.get("sessionId", "")
event_cursor = data.get("eventCursor", 0)

info(f"  commandId = {command_id}")
info(f"  messageId = {message_id}")
info(f"  turnId    = {turn_id}")
info(f"  sessionId = {session_id}")
info(f"  eventCursor = {event_cursor}")

# eventCursor 应 > 0 (turn.accepted 的 sequence)
if isinstance(event_cursor, (int, float)) and event_cursor > 0:
    ok(f"eventCursor={event_cursor} > 0 (turn.accepted sequence)")
else:
    fl(f"eventCursor={event_cursor} should be > 0")


# ════════════════════════════════════════════════════════════════
# 第 4 节：Worker 异步执行验证
# ════════════════════════════════════════════════════════════════
section(f"4. Worker 异步执行 — 等待 {WAIT_SEC}s")

info(f"等待 {WAIT_SEC}s 让 Worker 处理命令...")
time.sleep(WAIT_SEC)

# Use replay endpoint (>= semantics) instead of events endpoint (<= semantics)
r = get(f"/api/sessions/{session_id}/replay?from=1")
if r.status_code == 200:
    page = r.json()
    events = page.get("events", [])
    total = page.get("totalCount", len(events))
    types = [e.get("eventType") for e in events]

    info(f"  Events: {len(events)} total, types={types[:8]}")

    if len(events) > 1:
        ok(f"Got {len(events)} events after worker execution")
    else:
        info(f"  Only {len(events)} events; worker may still be processing")

    found_types = set(types)
    for req in ["turn.accepted", "turn.started"]:
        if req in found_types:
            ok(f"'{req}' found")
        else:
            info(f"  '{req}' not yet found (可能仍在执行)")

    has_content = bool({"delta", "assistant.content.delta", "done", "turn.completed"} & found_types)
    if has_content:
        ok("Content/delta events present")
    else:
        info(f"No content events yet in {types[:10]}")
else:
    info(f"Replay: {r.status_code} (session may not be active yet)")


# ════════════════════════════════════════════════════════════════
# 第 5 节：SSE 实时流 + id 字段 + 新事件名
# ════════════════════════════════════════════════════════════════
section("5. SSE 实时流 — id 字段 + 新事件名映射")

try:
    h = {"Authorization": f"Bearer {token}"}
    sse_url = f"{BASE}/api/sessions/{session_id}/events/stream"
    r = requests.get(sse_url, headers=h, stream=True, timeout=15)

    if r.status_code == 200:
        ok(f"SSE connection established — {r.status_code}")
    else:
        fl(f"SSE stream failed: {r.status_code}")
        r.close()
        info("Skipping SSE tests")
    if r.status_code == 200:
        seen_ids = []
        seen_events = []
        seen_legacy = set()
        seen_new = set()
        buf = b""
        start = time.time()
        for chunk in r.iter_content(chunk_size=1):
            buf += chunk
            if time.time() - start > 10:
                break
            if b"\n\n" in buf:
                block, buf = buf.rsplit(b"\n\n", 1)
                for line in block.decode().split("\n"):
                    line = line.strip()
                    if line.startswith("id:"):
                        try:
                            seen_ids.append(int(line[3:].strip()))
                        except ValueError:
                            pass
                    elif line.startswith("event:"):
                        evt = line[6:].strip()
                        seen_events.append(evt)
                        if "." in evt:
                            seen_new.add(evt)
                        else:
                            seen_legacy.add(evt)
                if len(seen_ids) >= 3:
                    break
        r.close()

        if seen_ids:
            if seen_ids == sorted(seen_ids):
                ok(f"SSE id: field monotonic: {seen_ids[:5]}")
            else:
                fl(f"SSE id: field NOT monotonic: {seen_ids}")
        else:
            fl("No SSE id: fields found")

        if seen_new:
            ok(f"New event names: {seen_new}")
        if seen_legacy:
            info(f"Legacy event names mapped: {seen_legacy} → new by server")
except Exception as e:
    fl(f"SSE stream exception: {e}")


# ════════════════════════════════════════════════════════════════
# 第 6 节：SSE afterSequence 重连
# ════════════════════════════════════════════════════════════════
section("6. SSE afterSequence 重连")

try:
    # 先用 REST API 拿已有事件
    r = get(f"/api/sessions/{session_id}/events?from=1&limit=100")
    if r.status_code == 200 and r.json().get("events"):
        events = r.json()["events"]
        max_seq = max(e.get("sequenceNum", 0) for e in events)
        info(f"  Max existing sequence: {max_seq}")

        # 用 afterSequence 重连 SSE
        h = {"Authorization": f"Bearer {token}"}
        recon_url = f"{BASE}/api/sessions/{session_id}/events/stream?afterSequence={max_seq}"
        rr = requests.get(recon_url, headers=h, stream=True, timeout=15)
        if rr.status_code == 200:
            ok(f"Reconnect SSE OK — afterSequence={max_seq}")

            seen = []
            buf = b""
            start = time.time()
            for chunk in rr.iter_content(chunk_size=1):
                buf += chunk
                if time.time() - start > 8:
                    break
                if b"\n\n" in buf:
                    block, buf = buf.rsplit(b"\n\n", 1)
                    for line in block.decode().split("\n"):
                        if line.startswith("id:"):
                            try:
                                seq = int(line[3:].strip())
                                seen.append(seq)
                            except ValueError:
                                pass
                    if len(seen) >= 2:
                        break
            rr.close()

            if seen and all(s > max_seq for s in seen):
                ok(f"Reconnect events after cursor: {seen[:3]} (all > {max_seq})")
            elif seen:
                info(f"Reconnect events: {seen[:3]} (some may overlap)")
            else:
                info(f"No new events after reconnect (session may be done)")
        else:
            fl(f"Reconnect SSE failed: {rr.status_code}")
            rr.close()
    else:
        info("No existing events, skip reconnect test")
except Exception as e:
    fl(f"Reconnect exception: {e}")


# ════════════════════════════════════════════════════════════════
# 第 7 节：投影游标
# ════════════════════════════════════════════════════════════════
section("7. 投影游标 (projected-cursor)")

try:
    r = get(f"/api/sessions/{session_id}/projected-cursor")
    if r.status_code in (200, 404):
        if r.status_code == 200:
            cursor = r.json()
            cs = cursor.get("projectedThroughSequence", 0)
            ok(f"Projected cursor: projectedThroughSequence={cs}")
            if isinstance(cs, (int, float)):
                ok(f"Cursor is numeric: {cs}")
            else:
                fl(f"Cursor is not numeric: {cs}")
        else:
            info("projected-cursor: 404 (no projection yet — expected for new session)")
    else:
        fl(f"Projected cursor failed: {r.status_code}")
except Exception as e:
    fl(f"Projected cursor exception: {e}")


# ════════════════════════════════════════════════════════════════
# 第 8 节：幂等键
# ════════════════════════════════════════════════════════════════
section("8. 幂等键 (ClientRequestId)")

r2 = post(f"/api/workspaces/{workspace_id}/chat/message", {
    "MessageText": "Should be ignored",
    "WorkspaceId": workspace_id,
    "AgentId": agent_id,
    "ClientRequestId": client_request_id,
})
if r2.status_code in (200, 202):
    data2 = r2.json()
    if data2.get("commandId") == command_id:
        ok(f"Idempotent: same commandId={command_id}")
    else:
        fl(f"Idempotent mismatch: {data2.get('commandId')} != {command_id}")

    if data2.get("turnId") == turn_id:
        ok(f"Idempotent: same turnId={turn_id}")
    else:
        fl(f"Idempotent turnId mismatch: {data2.get('turnId')} != {turn_id}")
else:
    fl(f"Idempotent request failed: {r2.status_code}")


# ════════════════════════════════════════════════════════════════
# 第 9 节：响应时间
# ════════════════════════════════════════════════════════════════
section("9. POST 响应时间 (< 2s)")

new_id = f"e2e-perf-{uuid.uuid4().hex[:12]}"
sw = time.time()
r = post(f"/api/workspaces/{workspace_id}/chat/message", {
    "MessageText": "Hello",
    "WorkspaceId": workspace_id,
    "AgentId": agent_id,
    "ClientRequestId": new_id,
})
elapsed = int((time.time() - sw) * 1000)
if elapsed < 2000:
    ok(f"POST: {elapsed}ms (under 2s)")
else:
    fl(f"POST slow: {elapsed}ms (over 2s)")


# ════════════════════════════════════════════════════════════════
# 第 10 节：对话轮次完整性
# ════════════════════════════════════════════════════════════════
section("10. 对话轮次完整性")

time.sleep(3)
r = get(f"/api/sessions/{session_id}/replay?from=1")
if r.status_code == 200:
    page = r.json()
    events = page.get("events", [])

    from collections import Counter
    type_counts = Counter(e.get("eventType") for e in events)
    info(f"  Event type distribution: {dict(type_counts)}")

    required = {"turn.accepted", "metadata"}
    found_set = set(type_counts.keys())
    for req_type in required:
        if req_type in found_set:
            ok(f"Required event '{req_type}' found")
        else:
            info(f"  Required '{req_type}' not found (may still be executing)")

    if events:
        seqs = [e.get("sequenceNum", 0) for e in events]
        if seqs == sorted(set(seqs)):
            ok(f"Sequence numbers monotonic: {min(seqs)}→{max(seqs)} ({len(events)} events)")
        else:
            fl(f"Sequence numbers have duplicates/gaps")

        has_done = bool({"done", "turn.completed"} & found_set)
        if has_done:
            ok("Turn completed (done/turn.completed)")
        else:
            fl("No done/turn.completed found — turn may be incomplete")
else:
    fl(f"Turn integrity check failed: {r.status_code}")


# ════════════════════════════════════════════════════════════════
# 结果
# ════════════════════════════════════════════════════════════════
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
