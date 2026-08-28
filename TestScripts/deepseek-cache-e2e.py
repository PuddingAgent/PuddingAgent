#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Run a two-turn DeepSeek cache smoke against one stable Agent session.

The probe intentionally keeps provider, model, Agent, system prompt, and tool
manifest unchanged. It submits the same small task twice, waits for each turn
to finish, then reads the authoritative gateway usage ledger plus the
prefix-v2 attribution ledger from SQLite.

Examples:
    python TestScripts/deepseek-cache-e2e.py
    python TestScripts/deepseek-cache-e2e.py --agent-id default.audit-agent.001
    python TestScripts/deepseek-cache-e2e.py --min-second-hit-rate 99
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import requests


DEFAULT_DB = r"D:/data/databases/pudding_platform.db"
DEFAULT_AGENT = "default.audit-agent.001"
PROBE_TEXT = "Cache stability probe. Reply with exactly CACHE_OK and do not call tools."


@dataclass(frozen=True)
class LedgerCursor:
    gateway_id: int
    attribution_id: int


def open_ledger(db_path: str) -> sqlite3.Connection:
    path = Path(db_path)
    if not path.exists():
        raise RuntimeError(f"ledger database not found: {db_path}")
    connection = sqlite3.connect(
        f"file:{path.as_posix()}?mode=ro",
        uri=True,
        timeout=10,
    )
    connection.row_factory = sqlite3.Row
    return connection


def ledger_cursor(connection: sqlite3.Connection) -> LedgerCursor:
    gateway_id = connection.execute(
        "SELECT COALESCE(MAX(Id), 0) FROM llm_gateway_usage_events"
    ).fetchone()[0]
    attribution_id = connection.execute(
        "SELECT COALESCE(MAX(Id), 0) FROM TokenUsageEvents"
    ).fetchone()[0]
    return LedgerCursor(int(gateway_id), int(attribution_id))


def gateway_rows(
    connection: sqlite3.Connection,
    session_id: str,
    after_id: int,
) -> list[sqlite3.Row]:
    return connection.execute(
        """
        SELECT Id, operation, provider_id, model_id, occurred_at_utc,
               prompt_tokens, completion_tokens, cache_hit_tokens,
               cache_miss_tokens
        FROM llm_gateway_usage_events
        WHERE Id > ? AND session_id = ?
        ORDER BY Id
        """,
        (after_id, session_id),
    ).fetchall()


def attribution_rows(
    connection: sqlite3.Connection,
    session_id: str,
    after_id: int,
) -> list[sqlite3.Row]:
    return connection.execute(
        """
        SELECT Id, OccurredAtUtc, ProviderId, ModelId, PromptTokens,
               CacheHitTokens, CacheMissTokens, PrefixVersion, PrefixHash,
               SystemPromptHash, ToolSpecHash, PrefixChangeReason,
               PrefixMessageCount, PrefixToolCount, TurnRound, ToolCallCount
        FROM TokenUsageEvents
        WHERE Id > ? AND SessionId = ? AND PrefixVersion = 'prefix-v2'
        ORDER BY Id
        """,
        (after_id, session_id),
    ).fetchall()


def request_json(
    session: requests.Session,
    method: str,
    url: str,
    *,
    timeout: float = 20,
    **kwargs: Any,
) -> Any:
    response = session.request(method, url, timeout=timeout, **kwargs)
    if response.status_code >= 400:
        raise RuntimeError(
            f"{method} {url} failed: HTTP {response.status_code} {response.text[:300]}"
        )
    return response.json() if response.text else None


def get_agent_status(
    session: requests.Session,
    base_url: str,
    workspace_id: str,
    agent_id: str,
) -> dict[str, Any]:
    statuses = request_json(
        session,
        "GET",
        f"{base_url}/api/workspaces/{workspace_id}/agents/status",
    )
    for status in statuses:
        if status.get("agentId") == agent_id:
            return status
    raise RuntimeError(f"agent status not found: {agent_id}")


def wait_for_turn(
    session: requests.Session,
    connection: sqlite3.Connection,
    base_url: str,
    workspace_id: str,
    agent_id: str,
    session_id: str,
    cursor: LedgerCursor,
    timeout_seconds: int,
) -> tuple[list[sqlite3.Row], list[sqlite3.Row]]:
    deadline = time.monotonic() + timeout_seconds
    saw_non_idle = False
    while time.monotonic() < deadline:
        status = get_agent_status(
            session, base_url, workspace_id, agent_id
        ).get("status")
        saw_non_idle = saw_non_idle or status != "idle"
        gateway = gateway_rows(connection, session_id, cursor.gateway_id)
        if gateway and status == "idle":
            # The gateway ledger is authoritative for usage; a short grace
            # period lets the prefix attribution projection commit as well.
            time.sleep(0.5)
            return (
                gateway_rows(connection, session_id, cursor.gateway_id),
                attribution_rows(connection, session_id, cursor.attribution_id),
            )
        time.sleep(0.75 if saw_non_idle else 0.5)
    raise TimeoutError(
        f"turn did not reach idle with a new usage row within {timeout_seconds}s"
    )


def submit_probe_turn(
    session: requests.Session,
    base_url: str,
    workspace_id: str,
    conversation_id: str,
    agent_id: str,
    ordinal: int,
) -> dict[str, Any]:
    suffix = uuid.uuid4().hex[:12]
    payload = {
        "clientRequestId": f"cache-e2e-{ordinal}-{suffix}",
        "clientMessageId": f"cache-e2e-message-{ordinal}-{suffix}",
        "recipients": {"type": "agent", "agentIds": [agent_id]},
        "content": [{"type": "text", "text": PROBE_TEXT}],
        "metadata": {
            "source_type": "cache_e2e",
            "cache_probe_ordinal": str(ordinal),
        },
    }
    return request_json(
        session,
        "POST",
        f"{base_url}/api/v1/conversations/{conversation_id}/turns",
        headers={"X-Workspace-Id": workspace_id},
        json=payload,
        timeout=30,
    )


def summarize_gateway(rows: list[sqlite3.Row]) -> dict[str, Any]:
    prompt = sum(int(row["prompt_tokens"] or 0) for row in rows)
    hit = sum(int(row["cache_hit_tokens"] or 0) for row in rows)
    miss = sum(int(row["cache_miss_tokens"] or 0) for row in rows)
    return {
        "requests": len(rows),
        "provider": sorted({row["provider_id"] for row in rows}),
        "model": sorted({row["model_id"] for row in rows}),
        "operations": sorted({row["operation"] for row in rows}),
        "promptTokens": prompt,
        "cacheHitTokens": hit,
        "cacheMissTokens": miss,
        "hitRate": (hit / prompt * 100.0) if prompt else 0.0,
    }


def latest_attribution(rows: list[sqlite3.Row]) -> dict[str, Any] | None:
    if not rows:
        return None
    row = rows[-1]
    return {
        "prefixVersion": row["PrefixVersion"],
        "prefixChangeReason": row["PrefixChangeReason"],
        "systemPromptHash": row["SystemPromptHash"],
        "toolSpecHash": row["ToolSpecHash"],
        "prefixMessageCount": row["PrefixMessageCount"],
        "prefixToolCount": row["PrefixToolCount"],
        "turnRound": row["TurnRound"],
        "toolCallCount": row["ToolCallCount"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Two-turn DeepSeek cache and prefix-stability smoke"
    )
    parser.add_argument("--base-url", default="http://localhost")
    parser.add_argument("--workspace-id", default="default")
    parser.add_argument("--agent-id", default=DEFAULT_AGENT)
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="Admin@123")
    parser.add_argument("--db", default=DEFAULT_DB)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--min-second-hit-rate", type=float, default=99.0)
    args = parser.parse_args()

    base_url = args.base_url.rstrip("/")
    http = requests.Session()
    http.headers.update({"Content-Type": "application/json"})
    ledger = open_ledger(args.db)

    try:
        login = request_json(
            http,
            "POST",
            f"{base_url}/api/login/account",
            json={
                "username": args.user,
                "password": args.password,
                "type": "account",
            },
        )
        token = login.get("token")
        if not token:
            raise RuntimeError("login succeeded without a bearer token")
        http.headers.update({"Authorization": f"Bearer {token}"})

        agent = request_json(
            http,
            "GET",
            f"{base_url}/api/workspaces/{args.workspace_id}/agents/{args.agent_id}",
        )
        provider = agent.get("preferredProviderId")
        model = agent.get("preferredModelId")
        conversation_id = agent.get("mainSessionId")
        if provider != "deepseek":
            raise RuntimeError(
                f"agent {args.agent_id} uses provider {provider!r}, not 'deepseek'"
            )
        if not conversation_id:
            raise RuntimeError(f"agent {args.agent_id} has no main session")
        initial_status = get_agent_status(
            http, base_url, args.workspace_id, args.agent_id
        ).get("status")
        if initial_status != "idle":
            raise RuntimeError(
                f"agent {args.agent_id} must be idle before the probe; got {initial_status!r}"
            )

        turns: list[dict[str, Any]] = []
        for ordinal in (1, 2):
            cursor = ledger_cursor(ledger)
            acceptance = submit_probe_turn(
                http,
                base_url,
                args.workspace_id,
                conversation_id,
                args.agent_id,
                ordinal,
            )
            gateway, attribution = wait_for_turn(
                http,
                ledger,
                base_url,
                args.workspace_id,
                args.agent_id,
                conversation_id,
                cursor,
                args.timeout,
            )
            turns.append(
                {
                    "ordinal": ordinal,
                    "acceptedSequence": acceptance.get("acceptedSequence"),
                    "turnIds": acceptance.get("turnIds"),
                    "gateway": summarize_gateway(gateway),
                    "prefix": latest_attribution(attribution),
                }
            )

        first = turns[0]
        second = turns[1]
        first_prefix = first["prefix"]
        second_prefix = second["prefix"]
        stable_system = bool(
            first_prefix
            and second_prefix
            and first_prefix["systemPromptHash"]
            == second_prefix["systemPromptHash"]
        )
        stable_tools = bool(
            first_prefix
            and second_prefix
            and first_prefix["toolSpecHash"] == second_prefix["toolSpecHash"]
            and first_prefix["prefixToolCount"]
            == second_prefix["prefixToolCount"]
        )
        second_rate = float(second["gateway"]["hitRate"])
        result = {
            "agentId": args.agent_id,
            "sessionId": conversation_id,
            "provider": provider,
            "model": model,
            "sameTaskText": True,
            "stableSystemPrompt": stable_system,
            "stableToolManifest": stable_tools,
            "turns": turns,
            "minimumSecondHitRate": args.min_second_hit_rate,
            "passed": stable_system
            and stable_tools
            and second_rate >= args.min_second_hit_rate,
        }
        print(json.dumps(result, ensure_ascii=False, indent=2))
        if not result["passed"]:
            print(
                "FAIL: prefix stability or second-turn cache threshold was not met",
                file=sys.stderr,
            )
            return 1
        return 0
    finally:
        ledger.close()
        http.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (RuntimeError, TimeoutError, requests.RequestException) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(2)
