# todo_api.py v9.0 — 一键 setup（注册+登录+持久化环境变量），Agent 无需手拼认证命令
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
脚本名称: todo_api.py
版本:    v9.0
功能描述: 与本地 Todo 任务管理系统交互的通用 CLI。
         v9.0 新增 setup 一键配置、--persist 持久化环境变量、
         自动组装认证头，让 Agent 只需按 SKILL 提示调用本脚本即可。
适用场景:
- 一键 setup：注册 agent/human + 持久化凭据到系统环境变量 + 验证
- 查询任务、项目、看板、标签、负责人
- 创建/更新任务、添加备注、QA 门禁、完成/取消
- Agent 协作：认领、释放、心跳、公告板、finish

说明:
- 默认服务地址为 https://todo.morenote.top/
- 使用 Python 标准库实现，无需额外安装第三方依赖
- Windows 环境变量持久化使用 setx；当前会话使用 $env:
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import platform
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, cast

DEFAULT_BASE_URL = "https://todo.morenote.top/"
ENV_PARTICIPANT_ID = "TODO_API_PARTICIPANT_ID"
ENV_PARTICIPANT_NAME = "TODO_API_PARTICIPANT_NAME"
ENV_PARTICIPANT_KEY = "TODO_API_PARTICIPANT_KEY"
ENV_AUTH_TOKEN = "TODO_API_AUTH_TOKEN"
ENV_REGISTRATION_SECRET = "TODO_API_REGISTRATION_SECRET"


def now_iso() -> str:
    return dt.datetime.now(dt.UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def _clean(value: str | None) -> str | None:
    if value is None:
        return None
    stripped = value.strip()
    return stripped or None


def resolve_auth_value(arg_value: str | None, env_key: str) -> str | None:
    return _clean(arg_value) or _clean(os.getenv(env_key))


def build_auth_headers(args: argparse.Namespace) -> dict[str, str]:
    participant_id = resolve_auth_value(getattr(args, "participant_id", None), ENV_PARTICIPANT_ID)
    participant_key = resolve_auth_value(getattr(args, "participant_key", None), ENV_PARTICIPANT_KEY)
    auth_token = resolve_auth_value(getattr(args, "auth_token", None), ENV_AUTH_TOKEN)

    headers: dict[str, str] = {}
    if participant_id:
        headers["X-Participant-Id"] = participant_id
    if participant_key:
        headers["X-Participant-Key"] = participant_key
    if auth_token:
        headers["Authorization"] = f"Bearer {auth_token}"
    return headers


class TodoApiError(RuntimeError):
    """Todo API 调用异常。"""


class TodoApiClient:
    def __init__(self, base_url: str, default_headers: dict[str, str] | None = None) -> None:
        self.base_url = base_url.rstrip("/")
        self.default_headers = dict(default_headers or {})

    def get(self, path: str, params: list[tuple[str, str]] | None = None) -> Any:
        return self._request("GET", path, params=params)

    def post(self, path: str, data: dict[str, Any] | None = None) -> Any:
        return self._request("POST", path, data=data)

    def put(self, path: str, data: dict[str, Any] | None = None) -> Any:
        return self._request("PUT", path, data=data)

    def delete(self, path: str) -> Any:
        return self._request("DELETE", path)

    def _request(
        self,
        method: str,
        path: str,
        params: list[tuple[str, str]] | None = None,
        data: dict[str, Any] | None = None,
    ) -> Any:
        url = f"{self.base_url}{path}"
        if params:
            query = urllib.parse.urlencode(params, doseq=True)
            url = f"{url}?{query}"

        payload: bytes | None = None
        headers = {"Accept": "application/json", **self.default_headers}
        if data is not None:
            payload = json.dumps(data, ensure_ascii=False).encode("utf-8")
            headers["Content-Type"] = "application/json; charset=utf-8"

        request = urllib.request.Request(url=url, data=payload, headers=headers, method=method)

        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                body = response.read().decode("utf-8")
                return json.loads(body) if body else None
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", errors="replace")
            message = detail
            try:
                error_obj = json.loads(detail)
                if isinstance(error_obj, dict):
                    error_dict = cast(dict[str, Any], error_obj)
                    message = str(error_dict.get("error") or error_dict.get("message") or detail)
            except json.JSONDecodeError:
                pass
            raise TodoApiError(f"HTTP {exc.code}: {message}") from exc
        except urllib.error.URLError as exc:
            raise TodoApiError(f"无法连接到 Todo API: {exc}") from exc
        except json.JSONDecodeError as exc:
            raise TodoApiError(f"服务返回了非 JSON 响应: {exc}") from exc


def load_json_payload(json_text: str | None, file_path: str | None) -> dict[str, Any] | None:
    """从 --data 或 --file 加载 JSON 请求体。两者都未提供时返回 None（允许降级到命名参数）。"""
    if json_text and file_path:
        raise TodoApiError("--data 与 --file 不能同时使用")
    if not json_text and not file_path:
        return None

    if json_text:
        try:
            payload_obj: Any = json.loads(json_text)
        except json.JSONDecodeError as exc:
            raise TodoApiError(
                f"--data 不是合法 JSON: {exc}\n"
                "建议：优先改用命名参数，例如：create --title \"标题\" --project 项目 --priority P1；"
                "复杂对象请写入 JSON 文件后用 --file。"
            ) from exc
    else:
        assert file_path is not None
        try:
            with open(file_path, "r", encoding="utf-8") as handle:
                payload_obj = json.load(handle)
        except OSError as exc:
            raise TodoApiError(f"读取 JSON 文件失败: {exc}。请确认 --file 使用的是相对当前目录可访问的路径。") from exc
        except json.JSONDecodeError as exc:
            raise TodoApiError(f"JSON 文件格式不合法: {exc}。请先检查逗号、引号和数组/对象括号是否完整。") from exc

    if not isinstance(payload_obj, dict):
        raise TodoApiError("请求体必须是 JSON 对象")

    return cast(dict[str, Any], payload_obj)


# ── 命名参数模式：将 --title / --project 等平铺参数组装为 dict ──────────

# 字符串字段：CLI 参数名 → JSON 字段名
_TASK_STR_FIELDS: list[tuple[str, str]] = [
    ("title", "title"),
    ("summary", "summary"),
    ("description", "description"),
    ("detail", "detail"),
    ("project", "project"),
    ("task_owner", "owner"),          # 避免与筛选 --owner 冲突，create/update 用 --task-owner
    ("priority", "priority"),
    ("status", "status"),
    ("stage", "stage"),
    ("due_date", "due_date"),
    ("start_date", "start_date"),
    ("goal", "goal"),
    ("out_of_scope", "out_of_scope"),
    ("impact_scope", "impact_scope"),
    ("test_requirements", "test_requirements"),
    ("risk_notes", "risk_notes"),
    ("rollback_plan", "rollback_plan"),
    ("executor_type", "executor_type"),
    ("assigned_agent", "assigned_agent"),
    ("claimed_by", "claimed_by"),
    ("claimed_at", "claimed_at"),
    ("human_owner", "human_owner"),
    ("doc_path", "doc_path"),
    ("task_doc_path", "task_doc_path"),
    ("parent_id", "parent_id"),
    ("prerequisite_task_id", "prerequisite_task_id"),
    ("last_agent_summary", "last_agent_summary"),
]

_TASK_INT_FIELDS: list[tuple[str, str]] = [
    ("lease_minutes", "lease_minutes"),
]

# 列表字段（可重复 --xxx value）：CLI 参数名 → JSON 字段名
_TASK_LIST_FIELDS: list[tuple[str, str]] = [
    ("tag", "tags"),
    ("acceptance_criteria", "acceptance_criteria"),
    ("entry_point", "entry_points"),
    ("depends_on", "depends_on"),
    ("blocks", "blocks"),
]

# 布尔字段
_TASK_BOOL_FIELDS: list[tuple[str, str]] = [
    ("requires_human_confirmation", "requires_human_confirmation"),
    ("reflection_required", "reflection_required"),
    ("docs_synced", "docs_synced"),
]


def add_task_field_args(parser: argparse.ArgumentParser) -> None:
    """为 create/update 注册命名参数，使调用者无需手写 JSON。"""
    group = parser.add_argument_group("任务字段（命名参数模式，与 --data/--file 互斥）")
    for cli_name, _ in _TASK_STR_FIELDS:
        group.add_argument(f"--{cli_name.replace('_', '-')}", dest=f"field_{cli_name}", help=f"设置 {cli_name}")
    for cli_name, _ in _TASK_INT_FIELDS:
        group.add_argument(f"--{cli_name.replace('_', '-')}", dest=f"field_{cli_name}", type=int, help=f"设置 {cli_name}")
    for cli_name, _ in _TASK_LIST_FIELDS:
        group.add_argument(f"--{cli_name.replace('_', '-')}", dest=f"field_{cli_name}", action="append", help=f"设置 {cli_name}（可重复）")
    for cli_name, _ in _TASK_BOOL_FIELDS:
        group.add_argument(f"--{cli_name.replace('_', '-')}", dest=f"field_{cli_name}", action="store_true", default=None, help=f"设置 {cli_name} 为 true")


def build_payload_from_named_args(args: argparse.Namespace) -> dict[str, Any] | None:
    """从命名参数构建请求体 dict。若无任何命名参数被设置则返回 None。"""
    payload: dict[str, Any] = {}
    for cli_name, json_name in _TASK_STR_FIELDS:
        value = getattr(args, f"field_{cli_name}", None)
        if value is not None:
            payload[json_name] = value
    for cli_name, json_name in _TASK_INT_FIELDS:
        value = getattr(args, f"field_{cli_name}", None)
        if value is not None:
            payload[json_name] = value
    for cli_name, json_name in _TASK_LIST_FIELDS:
        value = getattr(args, f"field_{cli_name}", None)
        if value:
            payload[json_name] = value
    for cli_name, json_name in _TASK_BOOL_FIELDS:
        value = getattr(args, f"field_{cli_name}", None)
        if value is not None:
            payload[json_name] = value
    return payload if payload else None


def resolve_payload(args: argparse.Namespace) -> dict[str, Any]:
    """按优先级解析请求体：--data > --file > 命名参数。三者均无则报错。"""
    # 优先级 1：--data / --file
    json_payload = load_json_payload(getattr(args, "data", None), getattr(args, "file", None))
    if json_payload is not None:
        return json_payload

    # 优先级 2：命名参数
    named_payload = build_payload_from_named_args(args)
    if named_payload is not None:
        return named_payload

    raise TodoApiError(
        "必须提供 --data、--file 或至少一个任务字段参数。\n"
        "推荐示例：create --title \"修复登录问题\" --project MPCAL --priority P1 --stage draft\n"
        "更新示例：update task-xxx --stage verifying --last-agent-summary \"已完成自测\""
    )


def add_agent_message(result: Any, message: str) -> Any:
    """给 CLI 输出追加面向 Agent 的鼓励/下一步提示；不修改 API 服务端响应。"""
    if isinstance(result, dict):
        enriched: dict[str, Any] = dict(cast(dict[str, Any], result))
        enriched.setdefault("agent_message", message)
        return enriched
    return {"success": True, "result": result, "agent_message": message}


def recovery_hint(args: argparse.Namespace, error: TodoApiError) -> str:
    text = str(error)
    hints: list[str] = []

    if "无法连接到 Todo API" in text:
        hints.append("服务可能未启动：先运行 health；若仍失败，请部署/启动服务后重试。")
        hints.append("如果服务地址不同，请追加 --base-url https://host 或 http://host:port。")
    if "不是合法 JSON" in text or "JSON 文件格式不合法" in text:
        hints.append("尽量不要手写 JSON：create/update 用 --title、--stage、--last-agent-summary 等命名参数。")
    if "task not found" in text:
        hints.append("任务 ID 可能写错：先运行 list-tasks --search 关键词 或 kanban 查看候选任务。")
    if "非法状态流转" in text:
        hints.append("状态需要按 pending -> progress -> completed -> auditing -> closed 流转；完成任务建议用 finish。")
    if "qa_status" in text:
        hints.append("QA 状态请使用 qa-approve / qa-reject / qa-retest，避免直接写非法枚举。")
    if "X-Participant-Id" in text or "尚未注册" in text:
        hints.append("先完成参与者注册：participant-register-agent 或 participant-register-human。")
        hints.append(f"再设置环境变量：{ENV_PARTICIPANT_ID}=你的ID。")
    if "X-Participant-Key" in text:
        hints.append(f"agent 请求需提供密钥：设置环境变量 {ENV_PARTICIPANT_KEY}=你的密钥，或命令行传 --participant-key。")
    if "token" in text and ("缺少" in text or "过期" in text or "失败" in text):
        hints.append("human 请求请先执行 participant-login 获取 token。")
        hints.append(f"然后设置环境变量 {ENV_AUTH_TOKEN}=登录返回的 token。")
    if getattr(args, "command", "") in {"create", "update"}:
        hints.append("create/update 支持命名参数模式；例如 update task-id --stage verifying --last-agent-summary \"已完成\"。")

    if not hints:
        return text

    return text + "\n建议：\n- " + "\n- ".join(dict.fromkeys(hints))


def finish_task(client: TodoApiClient, args: argparse.Namespace) -> Any:
    """完成任务/里程碑的友好封装：避免 Agent 手动拼 JSON 或卡在状态流转错误里。"""
    participant_id = resolve_auth_value(getattr(args, "participant_id", None), ENV_PARTICIPANT_ID)
    participant_name = resolve_auth_value(getattr(args, "participant_name", None), ENV_PARTICIPANT_NAME)
    effective_agent_id = args.agent_id or participant_id
    effective_actor = participant_name or effective_agent_id or "agent"

    current = client.get(f"/api/tasks/{args.task_id}")
    current_dict = cast(dict[str, Any], current) if isinstance(current, dict) else {}
    task_obj = current_dict.get("task")
    task = cast(dict[str, Any], task_obj) if isinstance(task_obj, dict) else {}
    status = task.get("status")

    # complete 端点要求 pending -> progress -> completed 顺序；这里自动补齐第一步。
    if status == "pending":
        client.put(f"/api/tasks/{args.task_id}", data={"status": "progress"})
        status = "progress"

    if status == "progress":
        client.post(f"/api/tasks/{args.task_id}/complete")

    update_payload: dict[str, Any] = {
        "stage": args.stage,
        "last_agent_summary": args.summary,
        "last_agent_update_at": now_iso(),
    }
    result = client.put(f"/api/tasks/{args.task_id}", data=update_payload)

    if args.note:
        client.post(
            f"/api/tasks/{args.task_id}/notes",
            data={"author": effective_actor, "text": args.note},
        )

    if args.release:
        release_payload: dict[str, Any] = {"reason": args.reason}
        if effective_agent_id:
            release_payload["agent_id"] = effective_agent_id
        result = client.post(f"/api/tasks/{args.task_id}/release", data=release_payload)

    if args.announce_target:
        client.post(
            "/api/bulletins",
            data={
                "type": "handoff",
                "author_agent": effective_actor,
                "target_agent": args.announce_target,
                "text": f"任务 {args.task_id} 已完成/到达里程碑：{args.summary}",
                "related_task_id": args.task_id,
            },
        )

    return add_agent_message(
        result,
        "🎉 做得好，任务/里程碑已记录完成。下一步建议：查看公告板或等待 QA/协作者接手。",
    )


def build_task_filters(args: argparse.Namespace) -> list[tuple[str, str]]:
    params: list[tuple[str, str]] = []
    multi_fields = [
        "status",
        "priority",
        "owner",
        "project",
        "stage",
        "executor_type",
        "tag",
        "qa_status",
        "claimed_by",
    ]

    for field in multi_fields:
        values = cast(list[str], getattr(args, field, None) or [])
        for value in values:
            params.append((field, value))

    if getattr(args, "search", None):
        params.append(("search", args.search))

    return params


def add_task_filter_args(parser: argparse.ArgumentParser) -> None:
    for option in ["status", "priority", "owner", "project", "stage", "executor_type", "tag", "qa_status", "claimed_by"]:
        parser.add_argument(f"--{option}", action="append", help=f"按 {option} 过滤，可重复传入")
    parser.add_argument("--search", help="全文搜索关键词")


def print_output(data: Any, raw: bool) -> None:
    if raw:
        print(json.dumps(data, ensure_ascii=False))
        return

    print(json.dumps(data, ensure_ascii=False, indent=2))


def add_common_output_args(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--raw", action="store_true", help="输出紧凑 JSON，不做缩进")


def build_install_auth_query(args: argparse.Namespace) -> tuple[str, str]:
    participant_id = resolve_auth_value(getattr(args, "participant_id", None), ENV_PARTICIPANT_ID)
    token = resolve_auth_value(getattr(args, "auth_token", None), ENV_AUTH_TOKEN)
    if not participant_id or not token:
        raise TodoApiError(
            "install 下载需要已登录 human 凭据。请先执行 participant-login/setup --human，确保环境变量 "
            f"{ENV_PARTICIPANT_ID} 与 {ENV_AUTH_TOKEN} 可用。"
        )
    return participant_id, token


def fetch_plain_text(
    base_url: str,
    path: str,
    query: str | None = None,
    headers: dict[str, str] | None = None,
) -> str:
    url = f"{base_url.rstrip('/')}{path}"
    if query:
        url = f"{url}?{query}"
    request = urllib.request.Request(url=url, headers={"Accept": "text/plain", **(headers or {})}, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise TodoApiError(f"HTTP {exc.code}: {detail or '下载失败'}") from exc
    except urllib.error.URLError as exc:
        raise TodoApiError(f"无法连接到安装下载接口: {exc}") from exc


def download_file(
    base_url: str,
    path: str,
    output_path: str,
    query: str | None = None,
    headers: dict[str, str] | None = None,
) -> int:
    url = f"{base_url.rstrip('/')}{path}"
    if query:
        url = f"{url}?{query}"
    request = urllib.request.Request(url=url, headers=headers or {}, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            data = response.read()
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise TodoApiError(f"HTTP {exc.code}: {detail or '下载失败'}") from exc
    except urllib.error.URLError as exc:
        raise TodoApiError(f"无法连接到安装下载接口: {exc}") from exc

    os.makedirs(os.path.dirname(output_path) or ".", exist_ok=True)
    with open(output_path, "wb") as handle:
        handle.write(data)
    return len(data)


# ── v9.0 环境变量持久化（Windows）─────────────────────────────────────────────

_IS_WINDOWS = platform.system() == "Windows"


def _persist_env_windows(name: str, value: str) -> bool:
    """使用 setx 持久化用户级环境变量（当前用户立即生效需要新终端）。"""
    if not _IS_WINDOWS:
        return False
    try:
        subprocess.run(
            ["setx", name, value],
            check=True,
            capture_output=True,
            text=True,
            timeout=10,
        )
        return True
    except Exception:
        return False


def _print_powershell_env_commands(vars: dict[str, str]) -> None:
    """打印用于当前 PowerShell 会话的环境变量设置命令。"""
    if not _IS_WINDOWS:
        return
    print("\n--- 复制以下命令到 PowerShell 使当前会话立即可用 ---", file=sys.stderr)
    for name, value in vars.items():
        print(f'$env:{name}="{value}"', file=sys.stderr)
    print("--- 以上命令仅供当前终端，重启后请用 setx 持久化的值 ---\n", file=sys.stderr)


def _persist_and_print_env(vars: dict[str, str]) -> dict[str, bool]:
    """持久化环境变量并打印 PowerShell 即时生效命令。返回各变量持久化是否成功。"""
    results: dict[str, bool] = {}
    for name, value in vars.items():
        ok = _persist_env_windows(name, value)
        results[name] = ok
    _print_powershell_env_commands(vars)
    return results


def _check_env_already_setup(base_url: str, expected_id: str, expected_key: str | None = None, expected_token: str | None = None) -> dict[str, Any] | None:
    """检查环境变量是否已经设置了目标参与者凭据，若是则跳过注册直接自检。

    返回 None 表示环境变量未配置或凭据无效（需要走注册/登录流程）。
    返回 dict 表示已配置且有效，包含 self_check 结果。
    """
    env_id = _clean(os.getenv(ENV_PARTICIPANT_ID))
    if not env_id or env_id != expected_id:
        return None

    # Agent 路径
    if expected_key:
        env_key = _clean(os.getenv(ENV_PARTICIPANT_KEY))
        if not env_key or env_key != expected_key:
            return None
        verify_headers = {"X-Participant-Id": env_id, "X-Participant-Key": env_key}
        kind = "agent"
    # Human 路径
    elif expected_token:
        env_token = _clean(os.getenv(ENV_AUTH_TOKEN))
        if not env_token or env_token != expected_token:
            return None
        verify_headers = {"X-Participant-Id": env_id, "Authorization": f"Bearer {env_token}"}
        kind = "human"
    else:
        return None

    try:
        verify_client = TodoApiClient(base_url, default_headers=verify_headers)
        verify_result = verify_client.get("/api/participants/me")
    except TodoApiError:
        return None

    return {
        "already_registered": True,
        "kind": kind,
        "self_check": verify_result,
    }


def run_setup(args: argparse.Namespace, client: TodoApiClient) -> Any:
    """一键 setup：注册 agent/human → 登录（human）→ 持久化凭据 → 自检。

    Agent 路径：
        python todo_api.py setup --agent --id copilot --name "小青龙" --role developer --intro "负责前后端" --key "strong-key"
    Human 路径：
        python todo_api.py setup --human --username alice --password "pass123" --display-name "王小明"
    """
    base_url = args.base_url

    if args.setup_mode == "agent":
        # 0) 先检查环境变量是否已配置
        already = _check_env_already_setup(base_url, args.id, expected_key=args.key)
        if already and not args.force:
            print(f"环境变量 {ENV_PARTICIPANT_ID}/{ENV_PARTICIPANT_KEY} 已配置且有效，跳过注册。", file=sys.stderr)
            return add_agent_message(
                already,
                "凭据已在环境变量中配置且通过验证，无需重复注册。如仍需重新持久化请加 --force。",
            )

        # 1) 注册 agent（幂等：如已存在会报错，可被 --force 忽略）
        payload = {
            "participant_id": args.id,
            "display_name": args.name,
            "role": args.role,
            "introduction": args.intro,
            "api_key": args.key,
            "registration_secret": args.registration_secret,
        }
        try:
            reg_result = client.post("/api/participants/register-agent", data=payload)
        except TodoApiError as exc:
            if args.force:
                reg_result = {"agent_message": f"注册可能已存在，--force 继续: {exc}"}
            else:
                raise

        # 2) 构建持久化凭据
        env_vars = {
            ENV_PARTICIPANT_ID: args.id,
            ENV_PARTICIPANT_NAME: args.name,
            ENV_PARTICIPANT_KEY: args.key,
        }
        persist_results = _persist_and_print_env(env_vars)

        # 3) 用新凭据自检
        verify_headers = {
            "X-Participant-Id": args.id,
            "X-Participant-Key": args.key,
        }
        verify_client = TodoApiClient(base_url, default_headers=verify_headers)
        verify_result = verify_client.get("/api/participants/me")

        return add_agent_message(
            {
                "success": True,
                "register": reg_result,
                "persist": persist_results,
                "verify": verify_result,
            },
            "Agent 已注册且凭据已持久化。当前终端请执行上面打印的 $env: 命令使环境变量立即可用；新终端将自动生效。",
        )

    elif args.setup_mode == "human":
        username = args.username
        password = args.password

        # 0) 先检查环境变量是否已配置 -- 用已有的 env token 验证
        env_token = _clean(os.getenv(ENV_AUTH_TOKEN))
        env_id = _clean(os.getenv(ENV_PARTICIPANT_ID))
        if env_token and env_id == username:
            already_human = _check_env_already_setup(base_url, username, expected_token=env_token)
            if already_human and not args.force:
                print(f"环境变量 {ENV_PARTICIPANT_ID}/{ENV_AUTH_TOKEN} 已配置且有效，跳过注册与登录。", file=sys.stderr)
                return add_agent_message(
                    already_human,
                    "凭据已在环境变量中配置且通过验证，无需重复操作。如 token 已过期请清空环境变量后重试或加 --force。",
                )

        # 1) 注册 human（幂等）
        reg_payload: dict[str, Any] = {"username": username, "password": password}
        reg_payload["registration_secret"] = args.registration_secret
        if args.display_name:
            reg_payload["display_name"] = args.display_name
        try:
            reg_result = client.post("/api/participants/register-human", data=reg_payload)
        except TodoApiError as exc:
            if args.force:
                reg_result = {"agent_message": f"注册可能已存在，--force 继续: {exc}"}
            else:
                raise

        # 2) 登录获取 token
        login_payload = {"username": username, "password": password}
        login_result = client.post("/api/participants/login", data=login_payload)
        token = login_result.get("token", "")
        display_name = login_result.get("participant", {}).get("display_name", "") or args.display_name or username

        # 3) 持久化
        env_vars: dict[str, str] = {
            ENV_PARTICIPANT_ID: username,
            ENV_PARTICIPANT_NAME: display_name,
            ENV_AUTH_TOKEN: token,
        }
        persist_results = _persist_and_print_env(env_vars)

        # 4) 自检
        verify_headers = {
            "X-Participant-Id": username,
            "Authorization": f"Bearer {token}",
        }
        verify_client = TodoApiClient(base_url, default_headers=verify_headers)
        verify_result = verify_client.get("/api/participants/me")

        return add_agent_message(
            {
                "success": True,
                "register": reg_result,
                "login": login_result,
                "persist": persist_results,
                "verify": verify_result,
            },
            "Human 用户已注册、登录并持久化 token。当前终端请执行上面打印的 $env: 命令使环境变量立即可用。",
        )

    raise TodoApiError(f"未知 setup 模式: {args.setup_mode}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Todo API 通用命令行工具（对应 todo-api/SKILL.md）"
    )
    parser.add_argument(
        "--base-url",
        default=DEFAULT_BASE_URL,
        help=f"Todo API 服务地址，默认 {DEFAULT_BASE_URL}",
    )
    parser.add_argument("--participant-id", help=f"参与者 ID（默认读取环境变量 {ENV_PARTICIPANT_ID}）")
    parser.add_argument("--participant-name", help=f"参与者显示名（默认读取环境变量 {ENV_PARTICIPANT_NAME}）")
    parser.add_argument("--participant-key", help=f"Agent 密钥（默认读取环境变量 {ENV_PARTICIPANT_KEY}）")
    parser.add_argument("--auth-token", help=f"Human 登录 token（默认读取环境变量 {ENV_AUTH_TOKEN}）")
    parser.add_argument(
        "--registration-secret",
        default=os.getenv(ENV_REGISTRATION_SECRET),
        help=f"注册密钥（默认读取环境变量 {ENV_REGISTRATION_SECRET}，仅注册命令使用）",
    )

    subparsers = parser.add_subparsers(dest="command", required=True)

    health_parser = subparsers.add_parser("health", help="健康检查")
    add_common_output_args(health_parser)

    projects_parser = subparsers.add_parser("projects", help="获取项目列表")
    add_common_output_args(projects_parser)

    tags_parser = subparsers.add_parser("tags", help="获取所有标签")
    add_common_output_args(tags_parser)

    owners_parser = subparsers.add_parser("owners", help="获取所有负责人")
    add_common_output_args(owners_parser)

    install_guide_parser = subparsers.add_parser("install-guide", help="读取服务端安装说明（/install/get.md）")
    install_guide_parser.add_argument("--base-url", dest="base_url_override", help="仅对当前子命令覆盖服务地址")
    install_guide_parser.add_argument("--participant-id", dest="participant_id", help=f"仅对当前子命令覆盖参与者 ID（默认读取环境变量 {ENV_PARTICIPANT_ID}）")
    install_guide_parser.add_argument("--auth-token", dest="auth_token", help=f"仅对当前子命令覆盖 human token（默认读取环境变量 {ENV_AUTH_TOKEN}）")
    add_common_output_args(install_guide_parser)

    install_download_parser = subparsers.add_parser("install-download", help="下载最新 SKILL.md 与 todo_api.py 到本地")
    install_download_parser.add_argument("--base-url", dest="base_url_override", help="仅对当前子命令覆盖服务地址")
    install_download_parser.add_argument("--participant-id", dest="participant_id", help=f"仅对当前子命令覆盖参与者 ID（默认读取环境变量 {ENV_PARTICIPANT_ID}）")
    install_download_parser.add_argument("--auth-token", dest="auth_token", help=f"仅对当前子命令覆盖 human token（默认读取环境变量 {ENV_AUTH_TOKEN}）")
    install_download_parser.add_argument("--output-dir", default=".", help="输出目录，默认当前目录")
    install_download_parser.add_argument("--overwrite", action="store_true", help="覆盖已存在文件")
    install_download_parser.add_argument("--with-bundle", action="store_true", help="额外下载 todo-api.bundle.zip")
    add_common_output_args(install_download_parser)

    # ── v9.0 一键 setup ──
    setup_parser = subparsers.add_parser("setup", help="一键注册+登录+持久化环境变量+自检（推荐首次使用执行）")
    setup_parser.add_argument(
        "--agent", dest="setup_mode", action="store_const", const="agent",
        help="Agent 模式：注册 agent + 持久化 ID/Name/Key",
    )
    setup_parser.add_argument(
        "--human", dest="setup_mode", action="store_const", const="human",
        help="Human 模式：注册 human + 登录 + 持久化 Token",
    )
    setup_parser.add_argument("--id", help="participant_id（agent 模式必填）")
    setup_parser.add_argument("--name", help="显示名字（agent 模式必填）")
    setup_parser.add_argument("--role", help="角色（agent 模式必填）")
    setup_parser.add_argument("--intro", help="个人介绍（agent 模式必填）")
    setup_parser.add_argument("--key", help="密钥（agent 模式必填，≥8 位）")
    setup_parser.add_argument("--username", help="用户名（human 模式必填）")
    setup_parser.add_argument("--password", help="密码（human 模式必填，≥6 位）")
    setup_parser.add_argument("--display-name", help="显示名（human 模式可选）")
    setup_parser.add_argument("--force", action="store_true", help="注册已存在时继续（不报错退出）")
    add_common_output_args(setup_parser)

    participant_register_agent_parser = subparsers.add_parser("participant-register-agent", help="注册 agent 参与者")
    participant_register_agent_parser.add_argument("--id", required=True, help="participant_id（唯一）")
    participant_register_agent_parser.add_argument("--name", required=True, help="显示名字（可中文，唯一）")
    participant_register_agent_parser.add_argument("--role", required=True, help="角色，例如 developer / qa / reviewer")
    participant_register_agent_parser.add_argument("--intro", required=True, help="个人介绍/职责说明")
    participant_register_agent_parser.add_argument("--key", required=True, help="密钥字符串，至少 8 位")
    participant_register_agent_parser.add_argument("--persist", action="store_true", help="注册后自动持久化凭据到系统环境变量")
    participant_register_agent_parser.add_argument("--force", action="store_true", help="即使环境变量已配置也重新注册")
    add_common_output_args(participant_register_agent_parser)

    participant_register_human_parser = subparsers.add_parser("participant-register-human", help="注册 human 参与者")
    participant_register_human_parser.add_argument("--username", required=True, help="用户名（同时作为 participant_id）")
    participant_register_human_parser.add_argument("--password", required=True, help="登录密码，至少 6 位")
    participant_register_human_parser.add_argument("--display-name", help="显示名（可选，默认与 username 相同）")
    participant_register_human_parser.add_argument("--persist", action="store_true", help="注册后自动持久化凭据到系统环境变量")
    participant_register_human_parser.add_argument("--force", action="store_true", help="即使环境变量已配置也重新注册")
    add_common_output_args(participant_register_human_parser)

    participant_login_parser = subparsers.add_parser("participant-login", help="human 参与者登录并获取 token")
    participant_login_parser.add_argument("--username", required=True, help="用户名")
    participant_login_parser.add_argument("--password", required=True, help="密码")
    participant_login_parser.add_argument("--persist", action="store_true", help="登录后自动持久化 token 到系统环境变量")
    participant_login_parser.add_argument("--force", action="store_true", help="即使已有有效 token 也重新登录")
    add_common_output_args(participant_login_parser)

    participant_list_parser = subparsers.add_parser("participant-list", help="查看参与者列表（需已鉴权）")
    add_common_output_args(participant_list_parser)

    participant_me_parser = subparsers.add_parser("participant-me", help="查看当前参与者信息（需已鉴权）")
    add_common_output_args(participant_me_parser)

    kanban_parser = subparsers.add_parser("kanban", help="读取看板视图")
    kanban_parser.add_argument(
        "--group-by",
        choices=["status", "priority", "owner", "project", "stage", "executor_type", "tag"],
        default="status",
        help="看板分组方式",
    )
    add_task_filter_args(kanban_parser)
    add_common_output_args(kanban_parser)

    get_task_parser = subparsers.add_parser("get-task", help="读取单个任务")
    get_task_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(get_task_parser)

    list_tasks_parser = subparsers.add_parser("list-tasks", help="按条件筛选任务列表")
    add_task_filter_args(list_tasks_parser)
    add_common_output_args(list_tasks_parser)

    search_parser = subparsers.add_parser("search", help="关键词搜索任务")
    search_parser.add_argument("query", help="搜索关键词")
    add_common_output_args(search_parser)

    create_parser = subparsers.add_parser("create", help="创建任务")
    create_parser.add_argument("--data", help="内联 JSON 请求体")
    create_parser.add_argument("--file", help="JSON 文件路径")
    add_task_field_args(create_parser)
    add_common_output_args(create_parser)

    update_parser = subparsers.add_parser("update", help="部分更新任务")
    update_parser.add_argument("task_id", help="任务 ID")
    update_parser.add_argument("--data", help="内联 JSON 请求体")
    update_parser.add_argument("--file", help="JSON 文件路径")
    add_task_field_args(update_parser)
    add_common_output_args(update_parser)

    delete_parser = subparsers.add_parser("delete", help="删除任务")
    delete_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(delete_parser)

    note_parser = subparsers.add_parser("note", help="给任务添加备注")
    note_parser.add_argument("task_id", help="任务 ID")
    note_parser.add_argument("--author", help="备注作者（默认使用参与者显示名/ID）")
    note_parser.add_argument("--text", required=True, help="备注内容")
    add_common_output_args(note_parser)

    qa_parser = subparsers.add_parser("qa", help="写入 QA 结果")
    qa_parser.add_argument("task_id", help="任务 ID")
    qa_parser.add_argument("--data", help="内联 JSON 请求体")
    qa_parser.add_argument("--file", help="JSON 文件路径")
    add_common_output_args(qa_parser)

    qa_view_parser = subparsers.add_parser("qa-view", help="查看任务 QA 状态")
    qa_view_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(qa_view_parser)

    qa_approve_parser = subparsers.add_parser("qa-approve", help="快捷通过 QA")
    qa_approve_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(qa_approve_parser)

    qa_reject_parser = subparsers.add_parser("qa-reject", help="快捷驳回 QA")
    qa_reject_parser.add_argument("task_id", help="任务 ID")
    qa_reject_parser.add_argument("--notes", help="驳回原因")
    add_common_output_args(qa_reject_parser)

    qa_retest_parser = subparsers.add_parser("qa-retest", help="标记重新测试")
    qa_retest_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(qa_retest_parser)

    complete_parser = subparsers.add_parser("complete", help="快捷完成任务")
    complete_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(complete_parser)

    finish_parser = subparsers.add_parser("finish", help="完成任务/里程碑并写入 Agent 总结（推荐给 Agent 使用）")
    finish_parser.add_argument("task_id", help="任务 ID")
    finish_parser.add_argument("--agent-id", help="执行完成的 Agent ID")
    finish_parser.add_argument("--summary", required=True, help="完成总结，会写入 last_agent_summary")
    finish_parser.add_argument(
        "--stage",
        choices=["implemented", "verifying", "ready_for_qa", "done"],
        default="implemented",
        help="完成后的研发阶段，默认 implemented",
    )
    finish_parser.add_argument("--note", help="可选：追加一条备注")
    finish_parser.add_argument("--release", action="store_true", help="完成后释放任务租约")
    finish_parser.add_argument("--reason", default="任务/里程碑已完成", help="释放租约原因")
    finish_parser.add_argument("--announce-target", help="可选：向目标 Agent 发布 handoff 公告")
    add_common_output_args(finish_parser)

    cancel_parser = subparsers.add_parser("cancel", help="取消任务")
    cancel_parser.add_argument("task_id", help="任务 ID")
    add_common_output_args(cancel_parser)

    claim_parser = subparsers.add_parser("claim", help="认领任务并设置租约")
    claim_parser.add_argument("task_id", help="任务 ID")
    claim_parser.add_argument("--agent-id", help="认领任务的 Agent ID（默认使用 --participant-id）")
    claim_parser.add_argument("--lease-minutes", type=int, help="租约分钟数（默认 60）")
    add_common_output_args(claim_parser)

    release_parser = subparsers.add_parser("release", help="释放任务认领")
    release_parser.add_argument("task_id", help="任务 ID")
    release_parser.add_argument("--agent-id", help="释放任务的 Agent ID（不传则系统释放）")
    release_parser.add_argument("--reason", help="释放原因")
    add_common_output_args(release_parser)

    agents_parser = subparsers.add_parser("agents", help="获取 Agent 心跳状态")
    agents_parser.add_argument("--online-window-minutes", type=int, default=5, help="在线判定窗口分钟数")
    add_common_output_args(agents_parser)

    heartbeat_parser = subparsers.add_parser("heartbeat", help="发送 Agent 心跳并续租已认领任务")
    heartbeat_parser.add_argument("--agent-id", help="Agent ID（默认使用 --participant-id）")
    add_common_output_args(heartbeat_parser)

    bulletins_parser = subparsers.add_parser("bulletins", help="查看公告板")
    bulletins_parser.add_argument("--type", dest="bulletin_type", help="公告类型过滤")
    bulletins_parser.add_argument("--author-agent", help="按发布者过滤")
    bulletins_parser.add_argument("--related-task-id", help="按关联任务过滤")
    bulletins_parser.add_argument("--target-agent", help="按目标 Agent 过滤")
    bulletins_parser.add_argument("--unacknowledged-by", help="仅查看某 Agent 未确认的公告")
    add_common_output_args(bulletins_parser)

    bulletin_post_parser = subparsers.add_parser("bulletin-post", help="发布公告")
    bulletin_post_parser.add_argument("--author-agent", help="发布公告的 Agent（默认使用参与者显示名/ID）")
    bulletin_post_parser.add_argument("--text", required=True, help="公告内容")
    bulletin_post_parser.add_argument("--type", dest="bulletin_type", default="info", help="公告类型（info/warning/request_help/handoff/announcement）")
    bulletin_post_parser.add_argument("--target-agent", help="目标 Agent（可选）")
    bulletin_post_parser.add_argument("--related-task-id", help="关联任务 ID（可选）")
    add_common_output_args(bulletin_post_parser)

    bulletin_ack_parser = subparsers.add_parser("bulletin-ack", help="确认公告")
    bulletin_ack_parser.add_argument("bulletin_id", help="公告 ID")
    bulletin_ack_parser.add_argument("--agent-id", help="确认公告的 Agent（默认使用参与者显示名/ID）")
    add_common_output_args(bulletin_ack_parser)

    bulletin_delete_parser = subparsers.add_parser("bulletin-delete", help="删除公告")
    bulletin_delete_parser.add_argument("bulletin_id", help="公告 ID")
    add_common_output_args(bulletin_delete_parser)

    return parser


def execute_command(args: argparse.Namespace) -> Any:
    base_url = getattr(args, "base_url_override", None) or args.base_url
    auth_headers = build_auth_headers(args)
    client = TodoApiClient(base_url, default_headers=auth_headers)
    participant_id = resolve_auth_value(getattr(args, "participant_id", None), ENV_PARTICIPANT_ID)
    participant_name = resolve_auth_value(getattr(args, "participant_name", None), ENV_PARTICIPANT_NAME)
    effective_actor = participant_name or participant_id or "agent"

    if args.command == "health":
        return client.get("/health")
    if args.command == "projects":
        return client.get("/api/projects")
    if args.command == "tags":
        return client.get("/api/tasks/tags")
    if args.command == "owners":
        return client.get("/api/tasks/owners")
    if args.command == "setup":
        return run_setup(args, client)
    if args.command == "install-guide":
        participant_id, token = build_install_auth_query(args)
        auth_query = urllib.parse.urlencode({"participant_id": participant_id, "token": token})
        auth_headers = {
            "X-Participant-Id": participant_id,
            "Authorization": f"Bearer {token}",
        }
        guide = fetch_plain_text(base_url, "/install/get.md", query=auth_query, headers=auth_headers)
        return {
            "success": True,
            "guide": guide,
        }
    if args.command == "install-download":
        output_dir = os.path.abspath(args.output_dir)
        os.makedirs(output_dir, exist_ok=True)

        targets: list[tuple[str, str]] = [
            ("SKILL.md", "/install/files/SKILL.md"),
            ("todo_api.py", "/install/files/todo_api.py"),
        ]
        participant_id, token = build_install_auth_query(args)
        auth_query = urllib.parse.urlencode({"participant_id": participant_id, "token": token})
        auth_headers = {
            "X-Participant-Id": participant_id,
            "Authorization": f"Bearer {token}",
        }
        if args.with_bundle:
            targets.append(("todo-api.bundle.zip", "/install/files/todo-api.bundle.zip"))

        downloaded: list[dict[str, Any]] = []
        skipped: list[str] = []

        for file_name, path in targets:
            destination = os.path.join(output_dir, file_name)
            if os.path.exists(destination) and not args.overwrite:
                skipped.append(destination)
                continue

            size = download_file(base_url, path, destination, query=auth_query, headers=auth_headers)
            downloaded.append({"path": destination, "bytes": size})

        return add_agent_message(
            {
                "success": True,
                "output_dir": output_dir,
                "downloaded": downloaded,
                "skipped": skipped,
            },
            "⬇️ 安装文件下载完成。若 skipped 非空，可加 --overwrite 覆盖更新。",
        )
    if args.command == "participant-register-agent":
        # 先检查环境变量是否已配置
        already = _check_env_already_setup(args.base_url, args.id, expected_key=args.key)
        if already and not args.force:
            print(f"环境变量 {ENV_PARTICIPANT_ID}/{ENV_PARTICIPANT_KEY} 已配置且有效，跳过注册。", file=sys.stderr)
            return add_agent_message(already, "凭据已在环境变量中配置且通过验证，无需重复注册。")

        payload = {
            "participant_id": args.id,
            "display_name": args.name,
            "role": args.role,
            "introduction": args.intro,
            "api_key": args.key,
            "registration_secret": args.registration_secret,
        }
        result = client.post("/api/participants/register-agent", data=payload)
        if getattr(args, "persist", None):
            _persist_and_print_env({
                ENV_PARTICIPANT_ID: args.id,
                ENV_PARTICIPANT_NAME: args.name,
                ENV_PARTICIPANT_KEY: args.key,
            })
        return add_agent_message(
            result,
            f"✅ Agent 已注册。建议将 {ENV_PARTICIPANT_ID}/{ENV_PARTICIPANT_NAME}/{ENV_PARTICIPANT_KEY} 写入系统环境变量后再执行任务命令。",
        )
    if args.command == "participant-register-human":
        # 先检查环境变量是否已配置
        env_id = _clean(os.getenv(ENV_PARTICIPANT_ID))
        if env_id == args.username and not args.force:
            print(f"环境变量 {ENV_PARTICIPANT_ID}={args.username} 已配置，跳过注册。", file=sys.stderr)
            return add_agent_message(
                {"already_registered": True, "participant_id": args.username},
                "凭据已在环境变量中配置，无需重复注册。如要强制重新注册请加 --force。",
            )

        payload: dict[str, Any] = {
            "username": args.username,
            "password": args.password,
            "registration_secret": args.registration_secret,
        }
        if args.display_name:
            payload["display_name"] = args.display_name
        result = client.post("/api/participants/register-human", data=payload)
        if getattr(args, "persist", None):
            display = args.display_name or args.username
            _persist_and_print_env({
                ENV_PARTICIPANT_ID: args.username,
                ENV_PARTICIPANT_NAME: display,
            })
        return add_agent_message(
            result,
            "✅ Human 用户已注册。下一步请执行 participant-login 获取 token。",
        )
    if args.command == "participant-login":
        # 先检查是否有有效 token
        old_token = _clean(os.getenv(ENV_AUTH_TOKEN))
        env_id = _clean(os.getenv(ENV_PARTICIPANT_ID))
        if old_token and env_id == args.username and not args.force:
            already = _check_env_already_setup(args.base_url, args.username, expected_token=old_token)
            if already:
                print(f"环境变量 {ENV_PARTICIPANT_ID}/{ENV_AUTH_TOKEN} 已有有效 token，跳过登录。", file=sys.stderr)
                return add_agent_message(already, "token 仍有效，无需重复登录。如 token 已过期请加 --force。")

        payload = {
            "username": args.username,
            "password": args.password,
        }
        result = client.post("/api/participants/login", data=payload)
        token = result.get("token", "")
        display_name = result.get("participant", {}).get("display_name", "") or args.username
        if getattr(args, "persist", None):
            _persist_and_print_env({
                ENV_PARTICIPANT_ID: args.username,
                ENV_PARTICIPANT_NAME: display_name,
                ENV_AUTH_TOKEN: token,
            })
        return add_agent_message(
            result,
            f"✅ 登录成功。请设置环境变量：{ENV_PARTICIPANT_ID}={args.username}、{ENV_AUTH_TOKEN}=<token>，可选 {ENV_PARTICIPANT_NAME}=<显示名>。",
        )
    if args.command == "participant-list":
        return client.get("/api/participants")
    if args.command == "participant-me":
        return client.get("/api/participants/me")
    if args.command == "kanban":
        params = [("group_by", args.group_by)] if args.group_by else []
        params.extend(build_task_filters(args))
        return client.get("/api/kanban", params=params)
    if args.command == "get-task":
        return client.get(f"/api/tasks/{args.task_id}")
    if args.command == "list-tasks":
        return client.get("/api/tasks", params=build_task_filters(args))
    if args.command == "search":
        return client.get("/api/tasks/search", params=[("q", args.query)])
    if args.command == "create":
        payload = resolve_payload(args)
        return client.post("/api/tasks", data=payload)
    if args.command == "update":
        payload = resolve_payload(args)
        return client.put(f"/api/tasks/{args.task_id}", data=payload)
    if args.command == "delete":
        return client.delete(f"/api/tasks/{args.task_id}")
    if args.command == "note":
        author = args.author or effective_actor
        return client.post(
            f"/api/tasks/{args.task_id}/notes",
            data={"author": author, "text": args.text},
        )
    if args.command == "qa":
        qa_payload = load_json_payload(args.data, args.file)
        if qa_payload is None:
            raise TodoApiError("qa 命令必须提供 --data 或 --file")
        return client.post(f"/api/tasks/{args.task_id}/qa", data=qa_payload)
    if args.command == "qa-view":
        return client.get(f"/api/tasks/{args.task_id}/qa")
    if args.command == "qa-approve":
        return add_agent_message(client.post(f"/api/tasks/{args.task_id}/qa/approve"), "✅ QA 已通过，辛苦啦！可以推进关闭或交付流程。")
    if args.command == "qa-reject":
        data = {"notes": args.notes} if args.notes else None
        return client.post(f"/api/tasks/{args.task_id}/qa/reject", data=data)
    if args.command == "qa-retest":
        return client.post(f"/api/tasks/{args.task_id}/qa/retest")
    if args.command == "complete":
        return add_agent_message(client.post(f"/api/tasks/{args.task_id}/complete"), "🎉 任务已标记完成，记得同步总结或交给 QA。")
    if args.command == "finish":
        return finish_task(client, args)
    if args.command == "cancel":
        return client.post(f"/api/tasks/{args.task_id}/cancel")
    if args.command == "claim":
        payload: dict[str, Any] = {}
        claim_agent_id = args.agent_id or participant_id
        if claim_agent_id:
            payload["agent_id"] = claim_agent_id
        if args.lease_minutes is not None:
            payload["lease_minutes"] = args.lease_minutes
        return add_agent_message(client.post(f"/api/tasks/{args.task_id}/claim", data=payload), "🤝 任务已认领，记得定期 heartbeat 续租。")
    if args.command == "release":
        payload: dict[str, Any] = {}
        release_agent_id = args.agent_id or participant_id
        if release_agent_id is not None:
            payload["agent_id"] = release_agent_id
        if args.reason is not None:
            payload["reason"] = args.reason
        return add_agent_message(client.post(f"/api/tasks/{args.task_id}/release", data=payload), "🕊️ 任务租约已释放，其他 Agent 可以接手。")
    if args.command == "agents":
        return client.get("/api/agents", params=[("online_window_minutes", str(args.online_window_minutes))])
    if args.command == "heartbeat":
        heartbeat_agent_id = args.agent_id or participant_id
        payload: dict[str, Any] = {}
        if heartbeat_agent_id:
            payload["agent_id"] = heartbeat_agent_id
        return add_agent_message(client.post("/api/agents/heartbeat", data=payload), "💓 心跳已记录，已认领任务会自动续租。")
    if args.command == "bulletins":
        params: list[tuple[str, str]] = []
        if args.bulletin_type:
            params.append(("type", args.bulletin_type))
        if args.author_agent:
            params.append(("author_agent", args.author_agent))
        if args.related_task_id:
            params.append(("related_task_id", args.related_task_id))
        if args.target_agent:
            params.append(("target_agent", args.target_agent))
        if args.unacknowledged_by:
            params.append(("unacknowledged_by", args.unacknowledged_by))
        return client.get("/api/bulletins", params=params)
    if args.command == "bulletin-post":
        author_agent = args.author_agent or effective_actor
        payload = {
            "type": args.bulletin_type,
            "author_agent": author_agent,
            "target_agent": args.target_agent,
            "text": args.text,
            "related_task_id": args.related_task_id,
        }
        return add_agent_message(client.post("/api/bulletins", data=payload), "📌 公告已发布，协作者轮询公告板时会看到它。")
    if args.command == "bulletin-ack":
        ack_actor = args.agent_id or effective_actor
        return add_agent_message(client.post(f"/api/bulletins/{args.bulletin_id}/ack", data={"agent_id": ack_actor}), "👌 公告已确认，协作状态更清晰了。")
    if args.command == "bulletin-delete":
        return client.delete(f"/api/bulletins/{args.bulletin_id}")

    raise TodoApiError(f"不支持的命令: {args.command}")


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()

    try:
        result = execute_command(args)
        print_output(result, getattr(args, "raw", False))
        return 0
    except TodoApiError as exc:
        print(f"✗ {recovery_hint(args, exc)}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
