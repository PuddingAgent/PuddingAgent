#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PuddingPlatform API 集成测试
访问地址：http://localhost/api  (nginx → pudding-platform:8080)

前置条件：所有 Docker 容器处于运行状态
用法：
    python PuddingPlatformTest.py
    python PuddingPlatformTest.py --base-url http://localhost/api
    python PuddingPlatformTest.py --user admin --password Admin@123
"""

import argparse
import sys
import time
import requests

# ─── 命令行参数 ───────────────────────────────────────────────
parser = argparse.ArgumentParser(description="PuddingPlatform API 集成测试")
parser.add_argument("--base-url", default="http://localhost/api", help="Platform API 基础 URL")
parser.add_argument("--user",     default="admin",     help="管理员用户名")
parser.add_argument("--password", default="Admin@123", help="管理员密码")
args = parser.parse_args()

BASE = args.base_url.rstrip("/")
SESSION = requests.Session()
SESSION.headers.update({"Content-Type": "application/json"})

# ─── 统计 ─────────────────────────────────────────────────────
passed = 0
failed = 0
token  = ""

# ─── 工具 ─────────────────────────────────────────────────────
def ok(msg):
    global passed
    passed += 1
    print(f"  [PASS] {msg}")

def fail(msg):
    global failed
    failed += 1
    print(f"  [FAIL] {msg}")

def section(title):
    print(f"\n{'═'*50}")
    print(f"  {title}")
    print(f"{'═'*50}")

def get(path, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.get(f"{BASE}{path}", headers=h, timeout=10)

def post(path, body=None, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.post(f"{BASE}{path}", json=body, headers=h, timeout=10)

def put(path, body=None, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.put(f"{BASE}{path}", json=body, headers=h, timeout=10)

def delete(path, auth=True):
    h = {"Authorization": f"Bearer {token}"} if auth and token else {}
    return SESSION.delete(f"{BASE}{path}", headers=h, timeout=10)

# ─── 唯一 ID 生成 ─────────────────────────────────────────────
ts = str(int(time.time()))[-6:]

# ═══════════════════════════════════════════════════════════════
#  1. 认证 Auth
# ═══════════════════════════════════════════════════════════════
section("1 / 认证 Auth")

# 1.1 正确登录
try:
    r = post("/login/account", {"username": args.user, "password": args.password}, auth=False)
    data = r.json()
    if data.get("status") == "ok" and data.get("token"):
        token = data["token"]
        SESSION.headers.update({"Authorization": f"Bearer {token}"})
        ok(f"POST /login/account — 登录成功 authority={data.get('currentAuthority')}")
    else:
        fail(f"POST /login/account — status 不是 ok: {data}")
except Exception as e:
    fail(f"POST /login/account — 异常: {e}")

# 1.2 错误密码
try:
    r = post("/login/account", {"username": args.user, "password": "WrongPwd!!!"}, auth=False)
    data = r.json()
    if data.get("status") == "error":
        ok("POST /login/account (错误密码) — 正确返回 status=error")
    else:
        fail(f"POST /login/account (错误密码) — 期望 error，实际: {data.get('status')}")
except Exception as e:
    fail(f"POST /login/account (错误密码) — 异常: {e}")

# 1.3 currentUser（已登录）
try:
    r = get("/currentUser")
    data = r.json()
    if data.get("userid") and data.get("access"):
        ok(f"GET /currentUser — userid={data['userid']} access={data['access']}")
    else:
        fail(f"GET /currentUser — 数据不完整: {data}")
except Exception as e:
    fail(f"GET /currentUser — 异常: {e}")

# 1.4 currentUser（匿名）
try:
    r = requests.get(f"{BASE}/currentUser", timeout=10)
    data = r.json()
    if data.get("access") == "guest":
        ok("GET /currentUser (匿名) — 正确返回 guest")
    else:
        fail(f"GET /currentUser (匿名) — 期望 guest，实际: {data.get('access')}")
except Exception as e:
    fail(f"GET /currentUser (匿名) — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  2. 用户管理 AppUser
# ═══════════════════════════════════════════════════════════════
section("2 / 用户管理 AppUser")

uid = f"test-u-{ts}"

# 2.1 列出用户
try:
    r = get("/users")
    r.raise_for_status()
    ok(f"GET /users — 共 {len(r.json())} 个用户")
except Exception as e:
    fail(f"GET /users — 异常: {e}")

# 2.2 创建用户
try:
    r = post("/users", {
        "userId": uid, "username": uid,
        "email": f"{uid}@test.local",
        "displayName": "Test User",
        "password": "Test@123456",
        "userType": "SimpleUser"
    })
    data = r.json()
    if data.get("userId") == uid:
        ok(f"POST /users — 创建用户 {uid} 成功")
    else:
        fail(f"POST /users — userId 不匹配: {data}")
except Exception as e:
    fail(f"POST /users — 异常: {e}")

# 2.3 重复创建（期望 409）
try:
    r = post("/users", {
        "userId": uid, "username": uid,
        "email": f"{uid}@test.local",
        "password": "Test@123456",
        "userType": "SimpleUser"
    })
    if r.status_code == 409:
        ok("POST /users (重复) — 正确返回 409 Conflict")
    else:
        fail(f"POST /users (重复) — 期望 409，实际 {r.status_code}")
except Exception as e:
    fail(f"POST /users (重复) — 异常: {e}")

# 2.4 获取单个用户
try:
    r = get(f"/users/{uid}")
    r.raise_for_status()
    ok(f"GET /users/{uid} — 获取成功")
except Exception as e:
    fail(f"GET /users/{uid} — 异常: {e}")

# 2.5 更新用户
try:
    r = put(f"/users/{uid}", {
        "username": uid, "email": f"{uid}@test.local",
        "displayName": "Updated-Display-Name",
        "userType": "SimpleUser", "isEnabled": True
    })
    data = r.json()
    if data.get("displayName") == "Updated-Display-Name":
        ok(f"PUT /users/{uid} — 更新 displayName 成功")
    else:
        fail(f"PUT /users/{uid} — displayName 未更新: {data}")
except Exception as e:
    fail(f"PUT /users/{uid} — 异常: {e}")

# 2.6 修改密码
try:
    r = put(f"/users/{uid}/password", {"newPassword": "NewPass@789"})
    if r.status_code == 204:
        ok(f"PUT /users/{uid}/password — 修改成功（204）")
    else:
        fail(f"PUT /users/{uid}/password — 状态码: {r.status_code}")
except Exception as e:
    fail(f"PUT /users/{uid}/password — 异常: {e}")

# 2.7 删除用户
try:
    r = delete(f"/users/{uid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /users/{uid} — 删除成功")
    else:
        fail(f"DELETE /users/{uid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /users/{uid} — 异常: {e}")

# 2.8 删除后 404
try:
    r = get(f"/users/{uid}")
    if r.status_code == 404:
        ok(f"GET /users/{uid} (已删) — 正确返回 404")
    else:
        fail(f"GET /users/{uid} (已删) — 期望 404，实际 {r.status_code}")
except Exception as e:
    fail(f"GET /users/{uid} (已删) — 异常: {e}")

# 2.9 禁止删除最后一个 Admin
try:
    r = delete("/users/admin")
    if r.status_code == 400:
        ok("DELETE /users/admin — 正确阻止删除最后 Admin（400）")
    else:
        fail(f"DELETE /users/admin — 期望 400，实际 {r.status_code}（危险！）")
except Exception as e:
    fail(f"DELETE /users/admin — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  3. 角色管理 AppRole
# ═══════════════════════════════════════════════════════════════
section("3 / 角色管理 AppRole")

rid = f"test-role-{ts}"

# 3.1 列出角色
try:
    r = get("/roles")
    r.raise_for_status()
    ok(f"GET /roles — 共 {len(r.json())} 个角色")
except Exception as e:
    fail(f"GET /roles — 异常: {e}")

# 3.2 创建角色
try:
    r = post("/roles", {
        "roleId": rid, "name": "Test-Role",
        "description": "integration test",
        "permissions": ["read", "write"]
    })
    data = r.json()
    if data.get("roleId") == rid:
        ok(f"POST /roles — 创建角色 {rid} 成功")
    else:
        fail(f"POST /roles — roleId 不匹配: {data}")
except Exception as e:
    fail(f"POST /roles — 异常: {e}")

# 3.3 获取角色
try:
    r = get(f"/roles/{rid}")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /roles/{rid} — 权限: {data.get('permissions')}")
except Exception as e:
    fail(f"GET /roles/{rid} — 异常: {e}")

# 3.4 更新角色
try:
    r = put(f"/roles/{rid}", {
        "roleId": rid, "name": "Updated-Role-Name",
        "description": "updated",
        "permissions": ["read", "write", "admin"]
    })
    data = r.json()
    if data.get("name") == "Updated-Role-Name":
        ok(f"PUT /roles/{rid} — 更新成功")
    else:
        fail(f"PUT /roles/{rid} — name 未更新: {data}")
except Exception as e:
    fail(f"PUT /roles/{rid} — 异常: {e}")

# 3.5 删除角色
try:
    r = delete(f"/roles/{rid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /roles/{rid} — 删除成功")
    else:
        fail(f"DELETE /roles/{rid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /roles/{rid} — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  4. 团队与工作区 Team / Workspace
# ═══════════════════════════════════════════════════════════════
section("4 / 团队与工作区 Team / Workspace")

tid = f"test-team-{ts}"
wid = f"test-ws-{ts}"

# 4.1 列出团队
try:
    r = get("/teams")
    r.raise_for_status()
    ok(f"GET /teams — 共 {len(r.json())} 个团队")
except Exception as e:
    fail(f"GET /teams — 异常: {e}")

# 4.2 创建团队
try:
    r = post("/teams", {"teamId": tid, "name": "Test-Team",
                        "description": "integration test", "isEnabled": True})
    data = r.json()
    if data.get("teamId") == tid:
        ok(f"POST /teams — 创建团队 {tid} 成功")
    else:
        fail(f"POST /teams — teamId 不匹配: {data}")
except Exception as e:
    fail(f"POST /teams — 异常: {e}")

# 4.3 团队详情
try:
    r = get(f"/teams/{tid}")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /teams/{tid} — 成员:{len(data.get('members',[]))} 工作区:{len(data.get('workspaces',[]))}")
except Exception as e:
    fail(f"GET /teams/{tid} — 异常: {e}")

# 4.4 添加成员
try:
    r = post(f"/teams/{tid}/members", {"userId": "admin", "role": "Member"})
    data = r.json()
    if data.get("userId") == "admin":
        ok(f"POST /teams/{tid}/members — 添加 admin 成功")
    else:
        fail(f"POST /teams/{tid}/members — 响应: {data}")
except Exception as e:
    fail(f"POST /teams/{tid}/members — 异常: {e}")

# 4.5 成员列表
try:
    r = get(f"/teams/{tid}/members")
    r.raise_for_status()
    ok(f"GET /teams/{tid}/members — 共 {len(r.json())} 个成员")
except Exception as e:
    fail(f"GET /teams/{tid}/members — 异常: {e}")

# 4.6 创建工作区
try:
    r = post(f"/teams/{tid}/workspaces", {
        "workspaceId": wid, "teamId": tid,
        "name": "Test-Workspace", "description": "integration test",
        "teamAccessPolicy": "Write", "companyAccessPolicy": "None"
    })
    data = r.json()
    if data.get("workspaceId") == wid:
        ok(f"POST /teams/{tid}/workspaces — 创建工作区 {wid} 成功")
    else:
        fail(f"POST /teams/{tid}/workspaces — workspaceId 不匹配: {data}")
except Exception as e:
    fail(f"POST /teams/{tid}/workspaces — 异常: {e}")

# 4.7 查询工作区
try:
    r = get(f"/teams/workspaces/{wid}")
    r.raise_for_status()
    ok(f"GET /teams/workspaces/{wid} — teamId={r.json().get('teamId')}")
except Exception as e:
    fail(f"GET /teams/workspaces/{wid} — 异常: {e}")

# 4.8 更新工作区
try:
    r = put(f"/teams/workspaces/{wid}", {
        "name": "Updated-Workspace", "description": "updated",
        "teamAccessPolicy": "ReadOnly", "companyAccessPolicy": "None", "isEnabled": True
    })
    data = r.json()
    if data.get("name") == "Updated-Workspace":
        ok(f"PUT /teams/workspaces/{wid} — 更新成功")
    else:
        fail(f"PUT /teams/workspaces/{wid} — name 未更新: {data}")
except Exception as e:
    fail(f"PUT /teams/workspaces/{wid} — 异常: {e}")

# 4.9 删除工作区
try:
    r = delete(f"/teams/workspaces/{wid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /teams/workspaces/{wid} — 删除成功")
    else:
        fail(f"DELETE /teams/workspaces/{wid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /teams/workspaces/{wid} — 异常: {e}")

# 4.10 移除成员
try:
    r = delete(f"/teams/{tid}/members/admin")
    if r.status_code in (200, 204):
        ok(f"DELETE /teams/{tid}/members/admin — 移除成功")
    else:
        fail(f"DELETE /teams/{tid}/members/admin — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /teams/{tid}/members/admin — 异常: {e}")

# 4.11 删除团队
try:
    r = delete(f"/teams/{tid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /teams/{tid} — 删除成功")
    else:
        fail(f"DELETE /teams/{tid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /teams/{tid} — 异常: {e}")

# 4.12 有工作区时禁止删除团队（保护测试）
try:
    tmp_t = f"tmp-t-{ts}"
    tmp_w = f"tmp-w-{ts}"
    post("/teams", {"teamId": tmp_t, "name": "Tmp", "isEnabled": True})
    post(f"/teams/{tmp_t}/workspaces", {
        "workspaceId": tmp_w, "teamId": tmp_t,
        "name": "Tmp-Workspace",
        "teamAccessPolicy": "Write", "companyAccessPolicy": "None"
    })
    r = delete(f"/teams/{tmp_t}")
    if r.status_code == 400:
        ok("DELETE 有工作区的团队 — 正确阻止（400）")
    else:
        fail(f"DELETE 有工作区的团队 — 期望 400，实际 {r.status_code}")
    # 清理
    delete(f"/teams/workspaces/{tmp_w}")
    delete(f"/teams/{tmp_t}")
except Exception as e:
    fail(f"有工作区删团队保护 — 异常: {e}")

# ═══════════════════════════════════════════════════════════════
#  5. LLM 服务商与模型
# ═══════════════════════════════════════════════════════════════
section("5 / LLM 服务商与模型")

pid  = f"test-prov-{ts}"
mid  = f"test-mdl-{ts}"

# 5.1 列出服务商
try:
    r = get("/llm/providers")
    r.raise_for_status()
    ok(f"GET /llm/providers — 共 {len(r.json())} 个服务商")
except Exception as e:
    fail(f"GET /llm/providers — 异常: {e}")

# 5.2 创建服务商
try:
    r = post("/llm/providers", {
        "providerId": pid, "name": "Test-Provider",
        "protocol": "OpenAI", "baseUrl": "https://api.test.local/v1",
        "apiKey": "sk-test-12345", "description": "integration test",
        "isEnabled": True
    })
    data = r.json()
    if data.get("providerId") == pid:
        ok(f"POST /llm/providers — 创建 {pid} 成功")
    else:
        fail(f"POST /llm/providers — providerId 不匹配: {data}")
except Exception as e:
    fail(f"POST /llm/providers — 异常: {e}")

# 5.3 获取详情
try:
    r = get(f"/llm/providers/{pid}")
    r.raise_for_status()
    ok(f"GET /llm/providers/{pid} — hasApiKey={r.json().get('hasApiKey')}")
except Exception as e:
    fail(f"GET /llm/providers/{pid} — 异常: {e}")

# 5.4 更新服务商
try:
    r = put(f"/llm/providers/{pid}", {
        "providerId": pid, "name": "Updated-Provider",
        "protocol": "OpenAI", "baseUrl": "https://api.test.local/v1",
        "description": "updated", "isEnabled": True
    })
    data = r.json()
    if data.get("name") == "Updated-Provider":
        ok(f"PUT /llm/providers/{pid} — 更新成功")
    else:
        fail(f"PUT /llm/providers/{pid} — name 未更新: {data}")
except Exception as e:
    fail(f"PUT /llm/providers/{pid} — 异常: {e}")

# 5.5 设置配额
try:
    r = put(f"/llm/providers/{pid}/quota", {
        "dailyTokenLimit": 1000000, "monthlyTokenLimit": 10000000
    })
    data = r.json()
    if data.get("dailyTokenLimit") == 1000000:
        ok(f"PUT /llm/providers/{pid}/quota — 设置配额成功")
    else:
        fail(f"PUT /llm/providers/{pid}/quota — 值不匹配: {data}")
except Exception as e:
    fail(f"PUT /llm/providers/{pid}/quota — 异常: {e}")

# 5.6 查询配额
try:
    r = get(f"/llm/providers/{pid}/quota")
    r.raise_for_status()
    data = r.json()
    ok(f"GET /llm/providers/{pid}/quota — daily={data.get('dailyTokenLimit')} monthly={data.get('monthlyTokenLimit')}")
except Exception as e:
    fail(f"GET /llm/providers/{pid}/quota — 异常: {e}")

# 5.7 重置每日配额
try:
    r = post(f"/llm/providers/{pid}/quota/reset-daily")
    if r.status_code in (200, 204):
        ok(f"POST /llm/providers/{pid}/quota/reset-daily — 重置成功")
    else:
        fail(f"POST /llm/providers/{pid}/quota/reset-daily — 状态码: {r.status_code}")
except Exception as e:
    fail(f"POST /llm/providers/{pid}/quota/reset-daily — 异常: {e}")

# 5.8 创建模型
try:
    r = post(f"/llm/providers/{pid}/models", {
        "modelId": mid, "name": "Test-Model",
        "description": "integration test model",
        "maxContextTokens": 128000,
        "inputPricePer1MTokens": 0.5, "outputPricePer1MTokens": 1.5,
        "capabilityTags": ["chat", "function_call"],
        "isDeprecated": False, "isDefault": False, "sortOrder": 99
    })
    data = r.json()
    if data.get("modelId") == mid:
        ok(f"POST /llm/providers/{pid}/models — 创建模型 {mid} 成功")
    else:
        fail(f"POST /llm/providers/{pid}/models — modelId 不匹配: {data}")
except Exception as e:
    fail(f"POST /llm/providers/{pid}/models — 异常: {e}")

# 5.9 获取模型列表
try:
    r = get(f"/llm/providers/{pid}/models")
    r.raise_for_status()
    ok(f"GET /llm/providers/{pid}/models — 共 {len(r.json())} 个模型")
except Exception as e:
    fail(f"GET /llm/providers/{pid}/models — 异常: {e}")

# 5.10 更新模型
try:
    r = put(f"/llm/providers/{pid}/models/{mid}", {
        "modelId": mid, "name": "Updated-Model",
        "description": "updated", "maxContextTokens": 200000,
        "inputPricePer1MTokens": 0.6, "outputPricePer1MTokens": 1.8,
        "capabilityTags": ["chat"],
        "isDeprecated": False, "isDefault": True, "sortOrder": 1
    })
    data = r.json()
    if data.get("name") == "Updated-Model":
        ok(f"PUT /llm/providers/{pid}/models/{mid} — 更新成功")
    else:
        fail(f"PUT /llm/providers/{pid}/models/{mid} — name 未更新: {data}")
except Exception as e:
    fail(f"PUT /llm/providers/{pid}/models/{mid} — 异常: {e}")

# 5.11 删除模型
try:
    r = delete(f"/llm/providers/{pid}/models/{mid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /llm/providers/{pid}/models/{mid} — 删除成功")
    else:
        fail(f"DELETE /llm/providers/{pid}/models/{mid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /llm/providers/{pid}/models/{mid} — 异常: {e}")

# 5.12 删除服务商
try:
    r = delete(f"/llm/providers/{pid}")
    if r.status_code in (200, 204):
        ok(f"DELETE /llm/providers/{pid} — 删除成功")
    else:
        fail(f"DELETE /llm/providers/{pid} — 状态码: {r.status_code}")
except Exception as e:
    fail(f"DELETE /llm/providers/{pid} — 异常: {e}")

# ─── 汇总 ─────────────────────────────────────────────────────
total = passed + failed
print(f"\n{'═'*50}")
color = "\033[92m" if failed == 0 else "\033[93m"
reset = "\033[0m"
print(f"{color}  结果：{passed} 通过 / {failed} 失败 / {total} 合计{reset}")
print(f"{'═'*50}\n")

sys.exit(1 if failed > 0 else 0)
