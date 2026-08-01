#!/usr/bin/env python3
"""Run Pudding deterministic benchmark cases and persist a comparable baseline.

Examples:
  python Tools/Diagnostics/run_benchmarks.py --dry-run
  python Tools/Diagnostics/run_benchmarks.py --case workspace-markdown-summary
  python Tools/Diagnostics/run_benchmarks.py --repeat 3 --label flash-routing-p2
  python Tools/Diagnostics/run_benchmarks.py --evaluate-run brun_xxx --session-id session_xxx
"""

from __future__ import annotations

import argparse
import json
import statistics
import sys
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import requests


TERMINAL_EVENTS = {"turn.completed", "turn.failed", "turn.cancelled"}


class Api:
    def __init__(self, base_url: str, username: str, password: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.session = requests.Session()
        self.session.headers.update({"Content-Type": "application/json"})

    def login(self) -> None:
        response = self.session.post(
            f"{self.base_url}/api/login/account",
            json={"username": self.username, "password": self.password, "type": "account"},
            timeout=30,
        )
        response.raise_for_status()
        token = response.json().get("token")
        if not token:
            raise RuntimeError("Login response did not contain a token")
        self.session.headers["Authorization"] = f"Bearer {token}"

    @staticmethod
    def _ensure_success(response: requests.Response) -> None:
        if response.ok:
            return
        detail = response.text[:2000].strip()
        raise RuntimeError(f"HTTP {response.status_code} {response.request.method} {response.url}: {detail}")

    def get_json(self, path: str, timeout: int = 30) -> Any:
        response = self.session.get(f"{self.base_url}{path}", timeout=timeout)
        self._ensure_success(response)
        return response.json()

    def post_json(
        self,
        path: str,
        body: Any,
        timeout: int = 60,
        headers: dict[str, str] | None = None,
    ) -> Any:
        response = self.session.post(
            f"{self.base_url}{path}",
            json=body,
            headers=headers,
            timeout=timeout,
        )
        self._ensure_success(response)
        return response.json() if response.content else {}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run deterministic Pudding benchmark cases")
    parser.add_argument("--base-url", default="http://127.0.0.1:5000")
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="Admin@123")
    parser.add_argument("--workspace", default="default")
    parser.add_argument("--agent", help="Agent instance id; defaults to the first enabled workspace agent")
    parser.add_argument("--case", action="append", dest="case_ids", help="Case id; repeatable")
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=3600, help="Per-run terminal wait seconds")
    parser.add_argument("--poll", type=float, default=5.0)
    parser.add_argument("--label", default="local-baseline")
    parser.add_argument("--output", type=Path, default=Path(".tmp-test-out/benchmark-p2"))
    parser.add_argument("--dry-run", action="store_true", help="List selected cases without invoking a model")
    parser.add_argument("--evaluate-run", help="Evaluate an existing brun_* run only")
    parser.add_argument("--session-id", help="Session override for --evaluate-run")
    return parser.parse_args()


def wait_for_terminal(api: Api, session_id: str, timeout_seconds: int, poll_seconds: float) -> str:
    deadline = time.monotonic() + timeout_seconds
    cursor = 0
    while time.monotonic() < deadline:
        while True:
            page = api.get_json(f"/api/sessions/{session_id}/events?from={cursor}&limit=200")
            events = page.get("events", []) if isinstance(page, dict) else []
            event_types = [item.get("type") or item.get("eventType") for item in events]
            terminal = next((event for event in reversed(event_types) if event in TERMINAL_EVENTS), None)
            if terminal:
                return terminal
            sequences = [item.get("sequence") for item in events if isinstance(item.get("sequence"), int)]
            if sequences:
                cursor = max(cursor, max(sequences))
            if not page.get("hasMore") or not events:
                break
        time.sleep(max(poll_seconds, 0.5))
    return "runner.timeout"


def run_case(
    api: Api,
    case: dict[str, Any],
    workspace_id: str,
    agent_id: str,
    timeout_seconds: int,
    poll_seconds: float,
) -> dict[str, Any]:
    session_id = "bench_" + uuid.uuid4().hex
    detail = api.get_json(f"/api/benchmark-cases/{case['id']}")
    prepared = api.post_json(
        f"/api/benchmark-cases/{case['id']}/prepare",
        {"workspaceId": workspace_id, "sessionId": session_id},
    )
    run_id = prepared["runId"]
    request_id = "benchreq_" + uuid.uuid4().hex
    message_id = "benchmsg_" + uuid.uuid4().hex
    metadata = {
        "source": "benchmark_runner",
        "benchmarkCaseId": case["id"],
        "benchmarkTitle": case["title"],
        "benchmarkRunId": run_id,
        "benchmarkSeedId": prepared.get("seed", {}).get("seedId") or "",
        "benchmarkSeedFiles": str(len(prepared.get("seed", {}).get("files", []))),
        "excludeFromLearning": "true",
    }
    accepted = api.post_json(
        f"/api/v1/conversations/{session_id}/turns",
        {
            "clientRequestId": request_id,
            "clientMessageId": message_id,
            "recipients": {"type": "agent", "agentIds": [agent_id]},
            "content": [{"type": "text", "text": detail["prompt"]}],
            "metadata": metadata,
        },
        headers={"X-Workspace-Id": workspace_id},
    )
    terminal = wait_for_terminal(api, session_id, timeout_seconds, poll_seconds)
    evaluation = api.post_json(
        f"/api/benchmark-cases/runs/{run_id}/evaluate",
        {"sessionId": session_id},
    )
    return {
        "caseId": case["id"],
        "title": case["title"],
        "runId": run_id,
        "sessionId": session_id,
        "terminalEvent": terminal,
        "accepted": accepted,
        "evaluation": evaluation,
    }


def median(values: list[float | int]) -> float | None:
    return statistics.median(values) if values else None


def summarize(runs: list[dict[str, Any]]) -> dict[str, Any]:
    evaluations = [run.get("evaluation", {}) for run in runs]
    scored = [item for item in evaluations if item.get("overallScore") is not None]
    passed = [item for item in scored if item.get("status") == "passed"]
    metrics = [item.get("metrics", {}) for item in evaluations]
    return {
        "runs": len(runs),
        "scoredRuns": len(scored),
        "passedRuns": len(passed),
        "passRate": len(passed) / len(scored) if scored else None,
        "medianOverallScore": median([item["overallScore"] for item in scored]),
        "medianDurationMs": median([item["durationMs"] for item in metrics if item.get("durationMs") is not None]),
        "medianTotalTokens": median([item["totalTokens"] for item in metrics if item.get("totalTokens") is not None]),
        "medianCostCny": median([float(item["costCny"]) for item in metrics if item.get("costCny") is not None]),
    }


def write_report(output_dir: Path, payload: dict[str, Any]) -> tuple[Path, Path]:
    output_dir.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    safe_label = "".join(ch if ch.isalnum() or ch in "-_" else "-" for ch in payload["label"])
    stem = f"{timestamp}-{safe_label}"
    json_path = output_dir / f"{stem}.json"
    markdown_path = output_dir / f"{stem}.md"
    json_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    summary = payload["summary"]
    lines = [
        f"# Benchmark baseline: {payload['label']}",
        "",
        f"- Started: {payload['startedAtUtc']}",
        f"- Runs: {summary['runs']}",
        f"- Scored: {summary['scoredRuns']}",
        f"- Passed: {summary['passedRuns']}",
        f"- Pass rate: {summary['passRate'] if summary['passRate'] is not None else 'n/a'}",
        f"- Median score: {summary['medianOverallScore'] if summary['medianOverallScore'] is not None else 'n/a'}",
        f"- Median tokens: {summary['medianTotalTokens'] if summary['medianTotalTokens'] is not None else 'n/a'}",
        f"- Median cost CNY: {summary['medianCostCny'] if summary['medianCostCny'] is not None else 'n/a'}",
        "",
        "| Case | Status | Score | Tokens | Cost CNY | Duration ms | Run |",
        "|---|---:|---:|---:|---:|---:|---|",
    ]
    for run in payload["runs"]:
        evaluation = run.get("evaluation", {})
        metrics = evaluation.get("metrics", {})
        lines.append(
            "| {case} | {status} | {score} | {tokens} | {cost} | {duration} | {run_id} |".format(
                case=run["caseId"],
                status=evaluation.get("status", run.get("terminalEvent", "unknown")),
                score=evaluation.get("overallScore", "n/a"),
                tokens=metrics.get("totalTokens", "n/a"),
                cost=metrics.get("costCny", "n/a"),
                duration=metrics.get("durationMs", "n/a"),
                run_id=run.get("runId", "n/a"),
            )
        )
    markdown_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return json_path, markdown_path


def main() -> int:
    args = parse_args()
    if args.repeat < 1:
        raise SystemExit("--repeat must be >= 1")

    api = Api(args.base_url, args.user, args.password)
    api.login()
    if args.evaluate_run:
        terminal = None
        if args.session_id:
            terminal = wait_for_terminal(api, args.session_id, args.timeout, args.poll)
        evaluation = api.post_json(
            f"/api/benchmark-cases/runs/{args.evaluate_run}/evaluate",
            {"sessionId": args.session_id},
        )
        if terminal:
            evaluation["runnerTerminalEvent"] = terminal
        run = {
            "caseId": evaluation.get("caseId", "unknown"),
            "title": evaluation.get("caseId", "existing run"),
            "runId": args.evaluate_run,
            "sessionId": evaluation.get("sessionId") or args.session_id,
            "terminalEvent": terminal,
            "evaluation": evaluation,
        }
        now = datetime.now(timezone.utc).isoformat()
        payload = {
            "schemaVersion": 1,
            "label": args.label,
            "baseUrl": args.base_url,
            "workspaceId": evaluation.get("workspaceId") or args.workspace,
            "agentId": args.agent,
            "startedAtUtc": now,
            "completedAtUtc": now,
            "summary": summarize([run]),
            "runs": [run],
        }
        json_path, markdown_path = write_report(args.output, payload)
        print(json.dumps(evaluation, ensure_ascii=False, indent=2))
        print(f"JSON: {json_path.resolve()}")
        print(f"Markdown: {markdown_path.resolve()}")
        return 0 if evaluation.get("status") == "passed" else 2

    catalog = api.get_json("/api/benchmark-cases")
    agent_id = args.agent
    if not agent_id:
        agents = api.get_json(f"/api/workspaces/{args.workspace}/agents")
        selected_agent = next(
            (item for item in agents if item.get("isEnabled") is True and item.get("isFrozen") is not True),
            None,
        )
        if not selected_agent:
            raise SystemExit(f"No enabled agent found in workspace {args.workspace}")
        agent_id = selected_agent["agentId"]
    requested = set(args.case_ids or [])
    selected = [
        case for case in catalog
        if (case["id"] in requested if requested else case.get("hasEvaluation") is True)
    ]
    missing = requested - {case["id"] for case in selected}
    if missing:
        raise SystemExit(f"Unknown benchmark case(s): {', '.join(sorted(missing))}")
    if not selected:
        raise SystemExit("No deterministic benchmark cases selected")

    print("Selected deterministic cases:")
    for case in selected:
        print(f"  - {case['id']} ({case['difficulty']}) x{args.repeat}")
    if args.dry_run:
        return 0

    started_at = datetime.now(timezone.utc).isoformat()
    runs: list[dict[str, Any]] = []
    for repetition in range(1, args.repeat + 1):
        for case in selected:
            print(f"[{repetition}/{args.repeat}] running {case['id']}...", flush=True)
            try:
                result = run_case(
                    api,
                    case,
                    args.workspace,
                    agent_id,
                    args.timeout,
                    args.poll,
                )
                runs.append(result)
                evaluation = result["evaluation"]
                print(
                    f"  {evaluation.get('status')} score={evaluation.get('overallScore')} "
                    f"tokens={evaluation.get('metrics', {}).get('totalTokens')}"
                )
            except Exception as exc:  # keep the unattended suite moving and retain the error
                print(f"  ERROR: {exc}", file=sys.stderr)
                runs.append({"caseId": case["id"], "title": case["title"], "error": str(exc)})

    payload = {
        "schemaVersion": 1,
        "label": args.label,
        "baseUrl": args.base_url,
        "workspaceId": args.workspace,
        "agentId": agent_id,
        "startedAtUtc": started_at,
        "completedAtUtc": datetime.now(timezone.utc).isoformat(),
        "summary": summarize(runs),
        "runs": runs,
    }
    json_path, markdown_path = write_report(args.output, payload)
    print(f"JSON: {json_path.resolve()}")
    print(f"Markdown: {markdown_path.resolve()}")
    summary = payload["summary"]
    return 0 if summary["scoredRuns"] > 0 and summary["passedRuns"] == summary["scoredRuns"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
