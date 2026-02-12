#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PuddingRuntime API 集成测试

⚠ 注意：PuddingRuntime 在默认 docker-compose.yml 中 **未对外暴露端口**。
  要从宿主机运行此脚本，需选择以下方式之一：

  方式 1 — 临时暴露端口（推荐）
    在 docker-compose.yml 的 pudding-runtime 服务下添加：
      ports:
        - "5002:8080"
    然后重新启动：docker compose up -d pudding-runtime
    运行：python PuddingRuntimeTest.py --base-url http://localhost:5002

  方式 2 — 通过 docker exec 在容器内运行
    docker cp PuddingRuntimeTest.py pudding-runtime:/tmp/
    docker exec pudding-runtime python /tmp/PuddingRuntimeTest.py \
        --base-url http://localhost:8080

  方式 3 — 通过 Controller 间接验证（可在无端口暴露时使用）
    执行 PuddingControllerTest.py，测试 Runtime 节点注册与 messageingress 链路。

前置条件：pudding-runtime 容器运行且可访问目标 --base-url
用法：
    python PuddingRuntimeTest.py --base-url http://localhost:5002
"""

import argparse
import sys
import time
import requests

parser = argparse.ArgumentParser(description="PuddingRuntime API 集成测试")
parser.add_argument(
    "--base-url", default="http://localhost:5002",
    help="Runtime 直连 URL（需暴露端口，默认 http://localhost:5002）"
)
parser.add_argument(
    "--controller-url", default="http://localhost/ingress",
    help="Controller URL（用于注册验证，默认经 nginx 代理）"
)
args = parser.parse_args()

BASE   = args.base_url.rstrip("/")
CTRL   = args.controller_url.rstrip("/")
SESSION = requests.Session()
SESSION.headers.update({"Content-Type": "application/json"})

passed = 0
failed = 0

def ok(msg):
    global passed
    passed += 1
    print(f"  [PASS] {msg}")

def fail(msg):
    global failed
    failed += 1
    print(f"  [FAIL] {msg}")

def info(msg):
    print(f"  [INFO] {msg}")

def section(title):
    print(f"\n{'═'*55}")
    print(f"  {title}")
    print(f"{'═'*55}")

def get(path, base=None):
    return SESSION.get(f"{base or BASE}{path}", timeout=10)

def post(path, body=None, base=None):
    return SESSION.post(f"{base or BASE}{path}", json=body, timeout=10)

def delete(path, base=None):
    return SESSION.delete(f"{base or BASE}{path}", timeout=10)

ts = str(int(time.time()))[-6:]

# ─── 检测 Runtime 是否可达 ────────────────────────────────────
print(f"\n  目标 URL: {BASE}")
print(f"  Controller URL: {CTRL}")
runtime_reachable = False

try:
    r = SESSION.get(f"{BASE}/health", timeout=5)
    if r.status_code == 200 and r.json().get("status") == "healthy":
        runtime_reachable = True
        print(f"  ✓ Runtime 可达\n")
    else:
        print(f"  ✗ Runtime 响应异常（状态码 {r.status_code}）")
except Exception as e:
    print(f"  ✗ Runtime 无法访问：{e}")
    print(f"""
  请确认 Runtime 端口已暴露，或参考脚本顶部说明选择访问方式。
  即使 Runtime 不可直接访问，PuddingControllerTest.py 中的
  runtime-registry 和 messageingress 测试仍可间接验证链路。
""")

# ═══════════════════════════════════════════════════════════════
#  0. 健康检查
# ═══════════════════════════════════════════════════════════════
section("0 / 健康检查 Health")

try:
    r = get("/health")
    if r.status_code == 200:
        data = r.json()
        if data.get("status") == "healthy":
            ok(f"GET /health — Runtime 在线 timestamp={data.get('timestamp')}")
        else:
            fail(f"GET /health — status={data.get('status')}: {data}")
    else:
        fail(f"GET /health — 状态码 {r.status_code}")
except Exception as e:
    fail(f"GET /health — 无法访问: {e}")

# ═══════════════════════════════════════════════════════════════
#  1. Runtime 会话状态
# ═══════════════════════════════════════════════════════════════
section("1 / Runtime 会话 RuntimeSession")

# 1.1 列出所有 Session
try:
    r = get("/api/runtimesession")
    r.raise_for_status()
    sessions = r.json()
    ok(f"GET /api/runtimesession — {len(sessions)} 个 Session（含历史）")
except Exception as e:
    fail(f"GET /api/runtimesession — 异常: {e}")

# 1.2 概况
try:
    r = get("/api/runtimesession/summary")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /api/runtimesession/summary — "
       f"total={data.get('totalSessions')} "
       f"active={data.get('activeSessions')} "
       f"activeAgents={data.get('activeAgents')}")
except Exception as e:
    fail(f"GET /api/runtimesession/summary — 异常: {e}")

# 1.3 查询不存在的 Session（期望 404）
try:
    r = get("/api/runtimesession/nonexistent-session-id-12345")
    if r.status_code == 404:
        ok("GET /api/runtimesession/{nonexistent} — 正确返回 404")
    else:
        fail(f"GET /api/runtimesession/{{nonexistent}} — 期望 404，实际 {r.status_code}")
except Exception as e:
    fail(f"GET /api/runtimesession/{{nonexistent}} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  2. Agent 执行（运行时调度）
# ═══════════════════════════════════════════════════════════════
section("2 / Agent 执行 RuntimeExecute")

# 2.1 列出活跃 Agent Sessions
try:
    r = get("/api/runtime/sessions")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /api/runtime/sessions — 活跃 Agent: {len(data) if isinstance(data, list) else data}")
except Exception as e:
    fail(f"GET /api/runtime/sessions — 异常: {e}")

# 2.2 执行 Agent（无 LLM 密钥，期望 200 但 LLM 会报认证错误）
session_id = f"test-session-{ts}"
try:
    r = post("/api/runtime/execute", {
        "sessionId": session_id,
        "workspaceId": "default",
        "agentTemplateId": "default-assistant",
        "userMessage": "Hello! This is an integration test. Please respond briefly.",
        "channelId": "test",
        "userExternalId": "tester"
    })
    if r.status_code == 200:
        data = r.json()
        ok(f"POST /api/runtime/execute — sessionId={data.get('sessionId')} "
           f"success={data.get('isSuccess')}")
        if not data.get("isSuccess"):
            info(f"  Agent 执行未成功（属正常，可能未配置 LLM 密钥）: {data.get('errorMessage','')[:100]}")
    elif r.status_code in (400, 503):
        ok(f"POST /api/runtime/execute — 返回 {r.status_code}（无 LLM 密钥属正常）")
    else:
        fail(f"POST /api/runtime/execute — 状态码 {r.status_code}: {r.text[:150]}")
except Exception as e:
    fail(f"POST /api/runtime/execute — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  3. 原生能力（嵌入式宿主）
# ═══════════════════════════════════════════════════════════════
section("3 / 原生能力 NativeCapability")

# 3.1 列出已注册原生能力
try:
    r = get("/api/native-capability/list")
    r.raise_for_status()
    caps = r.json()
    ok(f"GET /api/native-capability/list — {len(caps)} 个原生能力")
    for c in caps:
        info(f"  capabilityId={c.get('capabilityId')} name={c.get('name')} requiresApproval={c.get('requiresApproval')}")
except Exception as e:
    fail(f"GET /api/native-capability/list — 异常: {e}")

# 3.2 调用原生能力（demo 能力）
try:
    r = get("/api/native-capability/list")
    if r.status_code == 200:
        caps = r.json()
        demo_cap = caps[0] if caps else None
        if demo_cap:
            cap_id = demo_cap.get("capabilityId")
            resp = post("/api/native-capability/invoke", {
                "capabilityId": cap_id,
                "nodeId": "local",
                "sessionId": session_id,
                "workspaceId": "default",
                "agentTemplateId": "default-assistant",
                "parameters": {}
            })
            if resp.status_code == 200:
                data = resp.json()
                ok(f"POST /api/native-capability/invoke ({cap_id}) — isSuccess={data.get('isSuccess')}")
                if not data.get("isSuccess"):
                    info(f"  调用结果: {data.get('errorMessage','')[:100]}")
            else:
                fail(f"POST /api/native-capability/invoke — 状态码 {resp.status_code}: {resp.text[:100]}")
        else:
            info("GET /api/native-capability/list — 无已注册原生能力，跳过调用测试")
except Exception as e:
    fail(f"原生能力调用测试 — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  4. 通过 Controller 验证 Runtime 注册状态（间接测试）
# ═══════════════════════════════════════════════════════════════
section("4 / 通过 Controller 验证 Runtime 注册（间接）")

try:
    r = SESSION.get(f"{CTRL}/runtime-registry/nodes", timeout=10)
    r.raise_for_status()
    nodes = r.json()
    online = [n for n in nodes if n.get("status") == "Online"]
    ok(f"GET (Controller) /runtime-registry/nodes — "
       f"共 {len(nodes)} 节点，在线 {len(online)} 个")
    for n in nodes:
        status = n.get("status")
        color_start = "\033[92m" if status == "Online" else "\033[93m"
        status_icon = "✓" if status == "Online" else "○"
        print(f"    {color_start}{status_icon} {n.get('nodeId')} "
              f"endpoint={n.get('endpoint')} "
              f"activeSessions={n.get('activeSessionCount')}\033[0m")
except Exception as e:
    info(f"无法访问 Controller: {e}")

# ─── 汇总 ─────────────────────────────────────────────────────
total = passed + failed
print(f"\n{'═'*55}")
if not runtime_reachable:
    print("  ⚠ Runtime 不可直接访问，部分测试可能已失败。")
    print("  参考脚本顶部说明选择访问方式。\n")
color = "\033[92m" if failed == 0 else "\033[93m"
reset = "\033[0m"
print(f"{color}  结果：{passed} 通过 / {failed} 失败 / {total} 合计{reset}")
print(f"{'═'*55}\n")

sys.exit(1 if failed > 0 else 0)
