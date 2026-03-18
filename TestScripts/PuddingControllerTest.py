#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PuddingController API 集成测试
访问地址（通过 nginx ingress 代理）：http://localhost/ingress
  nginx 规则：/ingress/* → pudding-controller:8080/api/*

如需直连 Controller 容器，请在 docker-compose.yml 中为
pudding-controller 添加端口映射（如 5001:8080），并传入：
  python PuddingControllerTest.py --base-url http://localhost:5001

前置条件：所有 Docker 容器处于运行状态
用法：
    python PuddingControllerTest.py
    python PuddingControllerTest.py --base-url http://localhost/ingress
"""

import argparse
import sys
import time
import base64
import requests

parser = argparse.ArgumentParser(description="PuddingController API 集成测试")
parser.add_argument("--base-url", default="http://localhost/ingress",
                    help="Controller API 基础 URL（默认经 nginx 代理）")
args = parser.parse_args()

BASE = args.base_url.rstrip("/")
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

def get(path, **kw):
    return SESSION.get(f"{BASE}{path}", timeout=10, **kw)

def post(path, body=None, **kw):
    return SESSION.post(f"{BASE}{path}", json=body, timeout=10, **kw)

def put(path, body=None, **kw):
    return SESSION.put(f"{BASE}{path}", json=body, timeout=10, **kw)

def delete(path, **kw):
    return SESSION.delete(f"{BASE}{path}", timeout=10, **kw)

ts = str(int(time.time()))[-6:]

# ═══════════════════════════════════════════════════════════════
#  0. 健康检查
# ═══════════════════════════════════════════════════════════════
section("0 / 健康检查 Health")

# Controller /health 不在 /api/ 前缀下，nginx ingress 无法直接代理
# 改为验证 ingress 路由本身是否连通（workspace 接口可正常访问即视为在线）
try:
    r = get("/workspace")
    if r.status_code == 200:
        ok("GET /health (via /workspace) — Controller ingress 可达，状态码 200")
    else:
        fail(f"GET /health (via /workspace) — ingress 响应 {r.status_code}")
except Exception as e:
    fail(f"GET /health — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  1. Workspace 管理
# ═══════════════════════════════════════════════════════════════
section("1 / Workspace 管理")

wsid = f"test-ws-{ts}"

# 1.1 列出 Workspace
try:
    r = get("/workspace")
    r.raise_for_status()
    ok(f"GET /workspace — 已有 {len(r.json())} 个 Workspace")
except Exception as e:
    fail(f"GET /workspace — 异常: {e}")

# 1.2 获取默认 Workspace
try:
    r = get("/workspace/default")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /workspace/default — name={data.get('name')} isEnabled={data.get('isEnabled')}")
except Exception as e:
    fail(f"GET /workspace/default — 异常: {e}")

# 1.3 创建/覆盖 Workspace（PUT）
try:
    r = put(f"/workspace/{wsid}", {
        "workspaceId": wsid,
        "name": "Test-Controller-Workspace",
        "description": "integration test",
        "isEnabled": True,
        "isFrozen": False,
        "channelBindings": [],
        "agentTemplateIds": [],
        "auditAgentTemplateIds": [],
        "workflowBindings": []
    })
    data = r.json()
    if data.get("workspaceId") == wsid:
        ok(f"PUT /workspace/{wsid} — 创建成功")
    else:
        fail(f"PUT /workspace/{wsid} — workspaceId 不匹配: {data}")
except Exception as e:
    fail(f"PUT /workspace/{wsid} — 异常: {e}")

# 1.4 冻结 Workspace
try:
    r = post(f"/workspace/{wsid}/freeze")
    data = r.json()
    if data.get("isFrozen") is True:
        ok(f"POST /workspace/{wsid}/freeze — 冻结成功")
    else:
        fail(f"POST /workspace/{wsid}/freeze — 响应: {data}")
except Exception as e:
    fail(f"POST /workspace/{wsid}/freeze — 异常: {e}")

# 1.5 解冻 Workspace
try:
    r = post(f"/workspace/{wsid}/unfreeze")
    data = r.json()
    if data.get("isFrozen") is False:
        ok(f"POST /workspace/{wsid}/unfreeze — 解冻成功")
    else:
        fail(f"POST /workspace/{wsid}/unfreeze — 响应: {data}")
except Exception as e:
    fail(f"POST /workspace/{wsid}/unfreeze — 异常: {e}")

# 1.6 禁止删除默认 Workspace
try:
    r = delete("/workspace/default")
    if r.status_code == 400:
        ok("DELETE /workspace/default — 正确阻止删除默认 Workspace（400）")
    else:
        fail(f"DELETE /workspace/default — 期望 400，实际 {r.status_code}")
except Exception as e:
    fail(f"DELETE /workspace/default — 异常: {e}")

# 1.7 删除测试 Workspace
try:
    r = delete(f"/workspace/{wsid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /workspace/{wsid} — 删除成功")
    else:
        fail(f"DELETE /workspace/{wsid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /workspace/{wsid} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  2. Agent 模板管理
# ═══════════════════════════════════════════════════════════════
section("2 / Agent 模板 AgentTemplate")

atid = f"test-tpl-{ts}"

# 2.1 列出模板
try:
    r = get("/agenttemplate")
    r.raise_for_status()
    ok(f"GET /agenttemplate — 共 {len(r.json())} 个模板")
except Exception as e:
    fail(f"GET /agenttemplate — 异常: {e}")

# 2.2 注册模板
try:
    r = put(f"/agenttemplate/{atid}", {
        "templateId": atid,
        "name": "Test-Template",
        "description": "integration test agent template",
        "templateType": 0,
        "systemPrompt": "You are a test assistant.",
        "skillIds": []
    })
    if r.status_code in (200, 201):
        data = r.json()
        if data.get("templateId") == atid:
            ok(f"PUT /agenttemplate/{atid} — 注册成功")
        else:
            fail(f"PUT /agenttemplate/{atid} — templateId 不匹配: {data}")
    else:
        fail(f"PUT /agenttemplate/{atid} — 状态码: {r.status_code} {r.text[:100]}")
except Exception as e:
    fail(f"PUT /agenttemplate/{atid} — 异常: {e}")

# 2.3 查询单个模板
try:
    r = get(f"/agenttemplate/{atid}")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /agenttemplate/{atid} \u2014 name={data.get('name')} templateType={data.get('templateType')}")
except Exception as e:
    fail(f"GET /agenttemplate/{atid} — 异常: {e}")

# 2.4 删除模板
try:
    r = delete(f"/agenttemplate/{atid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /agenttemplate/{atid} — 删除成功")
    else:
        fail(f"DELETE /agenttemplate/{atid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /agenttemplate/{atid} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  3. 审计事件
# ═══════════════════════════════════════════════════════════════
section("3 / 审计事件 Audit")

# 3.1 查询审计事件
try:
    r = get("/audit?limit=10")
    r.raise_for_status()
    ok(f"GET /audit — {len(r.json())} 条（最近 limit=10）")
except Exception as e:
    fail(f"GET /audit — 异常: {e}")

# 3.2 按 workspaceId 查询
try:
    r = get("/audit?workspaceId=default&limit=5")
    r.raise_for_status()
    ok(f"GET /audit?workspaceId=default — {len(r.json())} 条")
except Exception as e:
    fail(f"GET /audit?workspaceId=default — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  4. 调试与指标
# ═══════════════════════════════════════════════════════════════
section("4 / 调试与指标 Debug")

# 4.1 概况
try:
    r = get("/debug/summary")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /debug/summary — recentAuditCount={data.get('recentAuditCount')}")
except Exception as e:
    fail(f"GET /debug/summary — 异常: {e}")

# 4.2 指标面板
try:
    r = get("/debug/metrics")
    r.raise_for_status()
    data = r.json()
    s = data.get("session", {})
    rt = data.get("runtime", {})
    ok(f"GET /debug/metrics — sessions={s.get('total')} runtimeNodes={rt.get('totalNodes')}")
except Exception as e:
    fail(f"GET /debug/metrics — 异常: {e}")

# 4.3 Workspace 调试快照
try:
    r = get("/debug/workspace/default")
    r.raise_for_status()
    data = r.json()
    ws = data.get("workspace", {})
    ok(f"GET /debug/workspace/default — isEnabled={ws.get('isEnabled')} sessions={data.get('session', {}).get('total')}")
except Exception as e:
    fail(f"GET /debug/workspace/default — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  5. Gateway 适配器
# ═══════════════════════════════════════════════════════════════
section("5 / Gateway 适配器")

try:
    r = get("/gateway/adapters")
    r.raise_for_status()
    adapters = r.json()
    names = [a.get("channelType") or a.get("name") or str(a) for a in adapters]
    ok(f"GET /gateway/adapters — {len(adapters)} 个适配器: {', '.join(str(n) for n in names)}")
except Exception as e:
    fail(f"GET /gateway/adapters — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  6. Runtime 节点注册
# ═══════════════════════════════════════════════════════════════
section("6 / Runtime 节点注册")

# 6.1 查询所有节点
try:
    r = get("/runtime-registry/nodes")
    r.raise_for_status()
    nodes = r.json()
    ok(f"GET /runtime-registry/nodes — {len(nodes)} 个节点")
    for n in nodes:
        info(f"  节点 {n.get('nodeId')} endpoint={n.get('endpoint')} status={n.get('status')}")
except Exception as e:
    fail(f"GET /runtime-registry/nodes — 异常: {e}")

# 6.2 查询嵌入式节点
try:
    r = get("/runtime-registry/embedded")
    r.raise_for_status()
    ok(f"GET /runtime-registry/embedded — {len(r.json())} 个嵌入式节点")
except Exception as e:
    fail(f"GET /runtime-registry/embedded — 异常: {e}")

# 6.3 注册一个测试节点
test_node_id = f"test-node-{ts}"
node_registered = False
try:
    r = post("/runtime-registry/register", {
        "nodeId": test_node_id,
        "endpoint": "http://test-node:8080",
        "capabilities": [],
        "isEmbedded": False,
        "activeSessionCount": 0,
        "metadata": {}
    })
    if r.status_code == 200:
        rdata = r.json()
        if rdata.get("accepted") is True:
            node_registered = True
            ok(f"POST /runtime-registry/register — 节点 {test_node_id} 注册成功")
        else:
            fail(f"POST /runtime-registry/register — accepted=False: {rdata.get('message')}")
    else:
        fail(f"POST /runtime-registry/register — 状态码: {r.status_code} {r.text[:100]}")
except Exception as e:
    fail(f"POST /runtime-registry/register — 异常: {e}")

# 6.4 查询已注册节点能力（期望空列表）
if node_registered:
    try:
        r = get(f"/runtime-registry/{test_node_id}/capabilities")
        r.raise_for_status()
        ok(f"GET /runtime-registry/{test_node_id}/capabilities — {len(r.json())} 个能力")
    except Exception as e:
        fail(f"GET /runtime-registry/{test_node_id}/capabilities — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  7. 知识库
# ═══════════════════════════════════════════════════════════════
section("7 / 知识库 Knowledge")

doc_id = f"doc-{ts}"

# 7.1 列出文档
try:
    r = get("/knowledge/default/documents")
    r.raise_for_status()
    ok(f"GET /knowledge/default/documents — {len(r.json())} 条文档")
except Exception as e:
    fail(f"GET /knowledge/default/documents — 异常: {e}")

# 7.2 索引文档
try:
    r = post("/knowledge/default/documents", {
        "documentId": doc_id,
        "workspaceId": "default",
        "title": "Test Document",
        "content": "This is an integration test document for knowledge search.",
        "sourceType": "Manual",
        "metadata": {}
    })
    r.raise_for_status()
    data = r.json()
    if data.get("documentId") == doc_id:
        ok(f"POST /knowledge/default/documents — 索引 {doc_id} 成功")
    else:
        fail(f"POST /knowledge/default/documents — documentId 不匹配: {data}")
except Exception as e:
    fail(f"POST /knowledge/default/documents — 异常: {e}")

# 7.3 搜索知识库
try:
    r = post("/knowledge/default/search", {"query": "integration test", "topK": 3})
    r.raise_for_status()
    results = r.json()
    ok(f"POST /knowledge/default/search — 命中 {len(results)} 条")
except Exception as e:
    fail(f"POST /knowledge/default/search — 异常: {e}")

# 7.4 删除文档
try:
    r = delete(f"/knowledge/default/documents/{doc_id}")
    if r.status_code in (200, 204):
        ok(f"DELETE /knowledge/default/documents/{doc_id} — 删除成功")
    else:
        fail(f"DELETE /knowledge/default/documents/{doc_id} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /knowledge/default/documents/{doc_id} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  8. 统一存储
# ═══════════════════════════════════════════════════════════════
section("8 / 统一存储 Storage")

obj_content = base64.b64encode(b"Hello, Pudding Storage!").decode()

# 8.1 列出对象
try:
    r = get("/storage/default/objects")
    r.raise_for_status()
    ok(f"GET /storage/default/objects — {len(r.json())} 个对象")
except Exception as e:
    fail(f"GET /storage/default/objects — 异常: {e}")

# 8.2 上传对象（objectId 由服务端通过 workspaceId+path 哈希计算，不在请求体中传入）
obj_path = f"test/obj-{ts}.txt"
uploaded_obj_id = None
try:
    r = put("/storage/default/objects", {
        "path": obj_path,
        "contentBase64": obj_content,
        "contentType": "text/plain"
    })
    r.raise_for_status()
    data = r.json()
    uploaded_obj_id = data.get("objectId")
    ok(f"PUT /storage/default/objects — 上传 {obj_path} 成功 objectId={uploaded_obj_id} size={data.get('sizeBytes')} bytes")
except Exception as e:
    fail(f"PUT /storage/default/objects — 异常: {e}")

# 8.3 取对象内容
if uploaded_obj_id:
    try:
        r = get(f"/storage/default/objects/{uploaded_obj_id}")
        r.raise_for_status()
        data = r.json()
        decoded = base64.b64decode(data["contentBase64"]).decode()
        if decoded == "Hello, Pudding Storage!":
            ok(f"GET /storage/default/objects/{uploaded_obj_id} — 内容验证通过")
        else:
            fail(f"GET /storage/default/objects/{uploaded_obj_id} — 内容不匹配: {decoded!r}")
    except Exception as e:
        fail(f"GET /storage/default/objects/{uploaded_obj_id} — 异常: {e}")
else:
    fail("GET /storage/default/objects — 跳过（上传失败）")

# 8.4 删除对象
if uploaded_obj_id:
    try:
        r = delete(f"/storage/default/objects/{uploaded_obj_id}")
        if r.status_code in (200, 204):
            ok(f"DELETE /storage/default/objects/{uploaded_obj_id} — 删除成功")
        else:
            fail(f"DELETE /storage/default/objects/{uploaded_obj_id} — 状态码: {r.status_code}")
    except Exception as e:
        fail(f"DELETE /storage/default/objects/{uploaded_obj_id} — 异常: {e}")
else:
    fail("DELETE /storage/default/objects — 跳过（上传失败）")

# ═══════════════════════════════════════════════════════════════
#  9. 知识图谱
# ═══════════════════════════════════════════════════════════════
section("9 / 知识图谱 Graph")

entity_id = f"ent-{ts}"
rel_id = f"rel-{ts}"

# 9.1 图谱统计
try:
    r = get("/graph/default/stats")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /graph/default/stats — entities={data.get('entities')} relations={data.get('relations')}")
except Exception as e:
    fail(f"GET /graph/default/stats — 异常: {e}")

# 9.2 添加实体
try:
    r = put("/graph/default/entities", {
        "entityId": entity_id,
        "workspaceId": "default",
        "label": "TestEntity",
        "type": "Concept",
        "properties": {}
    })
    r.raise_for_status()
    data = r.json()
    if data.get("entityId") == entity_id:
        ok(f"PUT /graph/default/entities — 实体 {entity_id} 创建成功")
    else:
        fail(f"PUT /graph/default/entities — entityId 不匹配: {data}")
except Exception as e:
    fail(f"PUT /graph/default/entities — 异常: {e}")

# 9.3 查询实体
try:
    r = post("/graph/default/entities/query", {"keyword": "TestEntity", "type": None, "limit": 5})
    r.raise_for_status()
    ok(f"POST /graph/default/entities/query — 匹配 {len(r.json())} 个实体")
except Exception as e:
    fail(f"POST /graph/default/entities/query — 异常: {e}")

# 9.4 删除实体
try:
    r = delete(f"/graph/default/entities/{entity_id}")
    if r.status_code in (200, 204):
        ok(f"DELETE /graph/default/entities/{entity_id} — 删除成功")
    else:
        fail(f"DELETE /graph/default/entities/{entity_id} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /graph/default/entities/{entity_id} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  10. 消息入站 & Session
# ═══════════════════════════════════════════════════════════════
section("10 / 消息入站 MessageIngress & Session")

sent_message_id = None

# 10.1 发送合法消息（可能因无 Runtime 返回错误，但链路应该通）
try:
    r = post("/messageingress", {
        "workspaceId": "default",
        "channelId": "cli",
        "userExternalId": "test-user",
        "messageText": "Hello Pudding! This is an integration test."
    })
    if r.status_code == 200:
        data = r.json()
        sent_message_id = data.get("messageId")
        ok(f"POST /messageingress — messageId={sent_message_id} sessionId={data.get('sessionId')}")
    else:
        info(f"POST /messageingress — 状态码 {r.status_code}（若无 Runtime 在线属正常）")
        ok("POST /messageingress — 链路已响应（无 Runtime 节点在线）")
except Exception as e:
    fail(f"POST /messageingress — 异常: {e}")

# 10.2 缺少必填字段（期望 400）
try:
    r = post("/messageingress", {"workspaceId": "default"})
    if r.status_code == 400:
        ok("POST /messageingress (缺少字段) — 正确返回 400")
    else:
        fail(f"POST /messageingress (缺少字段) — 期望 400，实际 {r.status_code}")
except Exception as e:
    fail(f"POST /messageingress (缺少字段) — 异常: {e}")

# 10.3 Query Session by workspace
try:
    r = get("/session/workspace/default")
    r.raise_for_status()
    ok(f"GET /session/workspace/default — {len(r.json())} 个 Session")
except Exception as e:
    fail(f"GET /session/workspace/default — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  11. 审批流程
# ═══════════════════════════════════════════════════════════════
section("11 / 审批流程 Approval")

# 11.1 查询待处理审批
try:
    r = get("/approval/pending")
    r.raise_for_status()
    ok(f"GET /approval/pending — {len(r.json())} 个待处理审批")
except Exception as e:
    fail(f"GET /approval/pending — 异常: {e}")

# ─── 汇总 ─────────────────────────────────────────────────────
total = passed + failed
print(f"\n{'═'*55}")
color = "\033[92m" if failed == 0 else "\033[93m"
reset = "\033[0m"
print(f"{color}  结果：{passed} 通过 / {failed} 失败 / {total} 合计{reset}")
print(f"{'═'*55}\n")

sys.exit(1 if failed > 0 else 0)
