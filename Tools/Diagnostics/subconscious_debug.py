"""Call the dedicated subconscious debug API.

Examples:
  python Tools/Diagnostics/subconscious_debug.py status
  python Tools/Diagnostics/subconscious_debug.py stop --reason "manual debug"
  python Tools/Diagnostics/subconscious_debug.py start --reason "resume"
  python Tools/Diagnostics/subconscious_debug.py trigger --session-id debug-1 --last-user-message "..." --last-assistant-reply "..." --wait
  python Tools/Diagnostics/subconscious_debug.py hook-session-compressed --session-id debug-1 --summary-preview "..." --wait
  python Tools/Diagnostics/subconscious_debug.py job --source-compaction-id <compactionId>
  python Tools/Diagnostics/subconscious_debug.py result --job-id <jobId>
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from dataclasses import dataclass
from typing import Any
from urllib import error, request
from urllib.parse import urlencode


DEFAULT_BASE_URL = "http://localhost"
DEFAULT_USERNAME = "admin"
DEFAULT_PASSWORD = "Admin@123456"


@dataclass(frozen=True)
class ApiResponse:
    status: int
    body: Any


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Control and inspect the subconscious runtime debug API."
    )
    parser.add_argument(
        "command",
        choices=("status", "start", "stop", "trigger", "hook-session-compressed", "job", "result"),
        help="Debug command to run.",
    )
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--token", help="Bearer token. If omitted, the script logs in.")
    parser.add_argument("--username", default=DEFAULT_USERNAME)
    parser.add_argument("--password", default=DEFAULT_PASSWORD)
    parser.add_argument("--reason", default="manual diagnostics")
    parser.add_argument("--requested-by", default="subconscious_debug.py")
    parser.add_argument("--workspace-id", default="default")
    parser.add_argument("--session-id")
    parser.add_argument("--agent-id", default="codex-debug-agent")
    parser.add_argument("--agent-template-id", default="global:general-assistant")
    parser.add_argument("--last-user-message")
    parser.add_argument("--last-assistant-reply")
    parser.add_argument("--new-session-id")
    parser.add_argument("--summary-preview")
    parser.add_argument("--source-event-id")
    parser.add_argument("--source-compaction-id")
    parser.add_argument("--idempotency-key")
    parser.add_argument("--job-id")
    parser.add_argument("--wait", action="store_true")
    parser.add_argument("--timeout-seconds", type=float, default=90.0)
    parser.add_argument("--poll-seconds", type=float, default=2.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    base_url = args.base_url.rstrip("/")
    token = args.token or login(base_url, args.username, args.password)

    if args.command == "status":
        response = call_api(base_url, "GET", "/api/debug/subconscious/debug", token)
    elif args.command in ("start", "stop"):
        response = call_api(
            base_url,
            "POST",
            f"/api/debug/subconscious/{args.command}",
            token,
            {
                "reason": args.reason,
                "requestedBy": args.requested_by,
            },
        )
    elif args.command == "result":
        if not args.job_id:
            raise SystemExit("--job-id is required for result.")
        response = call_api(
            base_url,
            "GET",
            f"/api/debug/subconscious/jobs/{args.job_id}/result",
            token,
        )
    elif args.command == "job":
        response = lookup_job(base_url, token, args)
    elif args.command == "hook-session-compressed":
        if not args.session_id:
            raise SystemExit("--session-id is required for hook-session-compressed.")
        response = call_api(
            base_url,
            "POST",
            "/api/debug/subconscious/hooks/session-compressed",
            token,
            {
                "workspaceId": args.workspace_id,
                "originalSessionId": args.session_id,
                "newSessionId": args.new_session_id,
                "agentId": args.agent_id,
                "agentTemplateId": args.agent_template_id,
                "compactionId": args.source_compaction_id,
                "reason": args.reason,
                "summaryPreview": args.summary_preview or args.last_assistant_reply,
            },
        )
        if args.wait and 200 <= response.status < 300:
            response = wait_for_hook_result(
                base_url,
                token,
                response,
                timeout_seconds=args.timeout_seconds,
                poll_seconds=args.poll_seconds,
            )
    else:
        if not args.session_id:
            raise SystemExit("--session-id is required for trigger.")
        response = call_api(
            base_url,
            "POST",
            "/api/debug/subconscious/trigger",
            token,
            {
                "workspaceId": args.workspace_id,
                "sessionId": args.session_id,
                "agentId": args.agent_id,
                "agentTemplateId": args.agent_template_id,
                "lastUserMessage": args.last_user_message,
                "lastAssistantReply": args.last_assistant_reply,
                "sourceEventId": args.source_event_id,
                "sourceCompactionId": args.source_compaction_id,
                "idempotencyKey": args.idempotency_key,
            },
        )
        if args.wait and 200 <= response.status < 300:
            response = wait_for_result(
                base_url,
                token,
                response,
                timeout_seconds=args.timeout_seconds,
                poll_seconds=args.poll_seconds,
            )

    print(json.dumps(response.body, indent=2, ensure_ascii=False))
    return 0 if 200 <= response.status < 300 else 1


def lookup_job(base_url: str, token: str, args: argparse.Namespace) -> ApiResponse:
    params: dict[str, str] = {}
    if args.job_id:
        params["jobId"] = args.job_id
    if args.idempotency_key:
        params["idempotencyKey"] = args.idempotency_key
    if args.source_compaction_id:
        params["sourceCompactionId"] = args.source_compaction_id
    if args.workspace_id:
        params["workspaceId"] = args.workspace_id
    if args.session_id:
        params["sessionId"] = args.session_id
    if args.source_compaction_id:
        params["sourceHookName"] = "session.compressed"

    if not any(key in params for key in ("jobId", "idempotencyKey", "sourceCompactionId")):
        raise SystemExit("--job-id, --idempotency-key or --source-compaction-id is required for job.")

    return call_api(
        base_url,
        "GET",
        f"/api/debug/subconscious/jobs/lookup?{urlencode(params)}",
        token,
    )


def wait_for_hook_result(
    base_url: str,
    token: str,
    hook_response: ApiResponse,
    timeout_seconds: float,
    poll_seconds: float,
) -> ApiResponse:
    if not isinstance(hook_response.body, dict):
        return hook_response

    compaction_id = hook_response.body.get("sourceCompactionId")
    session_id = hook_response.body.get("sessionId")
    workspace_id = hook_response.body.get("workspaceId")
    if not compaction_id:
        return hook_response

    deadline = time.monotonic() + timeout_seconds
    last_job_response: ApiResponse | None = None
    while time.monotonic() < deadline:
        params = {
            "sourceHookName": "session.compressed",
            "sourceCompactionId": str(compaction_id),
        }
        if session_id:
            params["sessionId"] = str(session_id)
        if workspace_id:
            params["workspaceId"] = str(workspace_id)

        last_job_response = call_api(
            base_url,
            "GET",
            f"/api/debug/subconscious/jobs/lookup?{urlencode(params)}",
            token,
        )
        if last_job_response.status == 200 and isinstance(last_job_response.body, dict):
            result_response = wait_for_result(
                base_url,
                token,
                last_job_response,
                timeout_seconds=max(0.2, deadline - time.monotonic()),
                poll_seconds=poll_seconds,
            )
            if result_response.status == 200 and isinstance(result_response.body, dict):
                return ApiResponse(
                    200,
                    {
                        "hook": hook_response.body,
                        "job": last_job_response.body,
                        "result": result_response.body.get("result"),
                    },
                )
            return ApiResponse(
                result_response.status,
                {
                    "hook": hook_response.body,
                    "job": last_job_response.body,
                    "resultPoll": result_response.body,
                },
            )
        time.sleep(max(0.2, poll_seconds))

    return ApiResponse(
        408,
        {
            "hook": hook_response.body,
            "lastJobPollStatus": last_job_response.status if last_job_response else None,
            "error": "Timed out waiting for subconscious hook job.",
        },
    )


def wait_for_result(
    base_url: str,
    token: str,
    trigger_response: ApiResponse,
    timeout_seconds: float,
    poll_seconds: float,
) -> ApiResponse:
    if not isinstance(trigger_response.body, dict):
        return trigger_response

    job_id = trigger_response.body.get("jobId")
    if not job_id:
        return trigger_response

    deadline = time.monotonic() + timeout_seconds
    last_response: ApiResponse | None = None
    while time.monotonic() < deadline:
        last_response = call_api(
            base_url,
            "GET",
            f"/api/debug/subconscious/jobs/{job_id}/result",
            token,
        )
        if last_response.status == 200:
            return ApiResponse(
                200,
                {
                    "trigger": trigger_response.body,
                    "result": last_response.body,
                },
            )
        time.sleep(max(0.2, poll_seconds))

    return ApiResponse(
        408,
        {
            "trigger": trigger_response.body,
            "lastPollStatus": last_response.status if last_response else None,
            "error": "Timed out waiting for subconscious job result.",
        },
    )


def login(base_url: str, username: str, password: str) -> str:
    response = call_api(
        base_url,
        "POST",
        "/api/login/account",
        token=None,
        payload={
            "username": username,
            "password": password,
            "type": "account",
        },
    )

    if response.status < 200 or response.status >= 300:
        raise SystemExit(f"Login failed with HTTP {response.status}: {response.body}")

    token = response.body.get("token") if isinstance(response.body, dict) else None
    if not token:
        raise SystemExit("Login response did not include a token.")
    return str(token)


def call_api(
    base_url: str,
    method: str,
    path: str,
    token: str | None,
    payload: dict[str, Any] | None = None,
) -> ApiResponse:
    data = None
    headers = {"Accept": "application/json"}
    if payload is not None:
        data = json.dumps(payload).encode("utf-8")
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    req = request.Request(
        f"{base_url}{path}",
        data=data,
        method=method,
        headers=headers,
    )

    try:
        with request.urlopen(req, timeout=30) as resp:
            return ApiResponse(resp.status, decode_body(resp.read()))
    except error.HTTPError as exc:
        return ApiResponse(exc.code, decode_body(exc.read()))
    except error.URLError as exc:
        raise SystemExit(f"Request failed: {exc}") from exc


def decode_body(raw: bytes) -> Any:
    if not raw:
        return None
    text = raw.decode("utf-8")
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return text


if __name__ == "__main__":
    sys.exit(main())
