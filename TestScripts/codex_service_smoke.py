#!/usr/bin/env python3
"""Process-level smoke test for the independent Pudding Codex MCP service."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import time
import urllib.request
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
DOTNET = shutil.which("dotnet") or "dotnet"
SERVICE_DLL = ROOT / "Source" / "PuddingCodexService" / "bin" / "Debug" / "net10.0" / "PuddingCodexService.dll"
MCP_CLI_DLL = ROOT / "Tests" / "Mcp.Cli" / "bin" / "Debug" / "net10.0" / "Mcp.Cli.dll"
ENDPOINT = "http://127.0.0.1:5199/mcp"


def wait_for_health(url: str, timeout_seconds: float = 15) -> None:
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as response:
                if response.status == 200:
                    return
        except OSError:
            pass
        time.sleep(0.25)
    raise RuntimeError(f"Codex Service did not become healthy: {url}")


def main() -> int:
    if not SERVICE_DLL.exists() or not MCP_CLI_DLL.exists():
        raise FileNotFoundError("Build PuddingCodexService and Tests/Mcp.Cli before running this smoke test.")

    with tempfile.TemporaryDirectory(prefix="pudding-codex-service-") as temp_dir:
        temp = Path(temp_dir)
        environment = os.environ.copy()
        environment.update(
            {
                "ASPNETCORE_URLS": "http://127.0.0.1:5199",
                "PUDDING_DATA_ROOT": str(temp / "data"),
                "PUDDING_REPOSITORY_ROOT": str(ROOT),
                "PUDDING_SUPERVISOR_RUN_DIR": str(temp / "run"),
                "PUDDING_CODEX_COMMAND": DOTNET,
                "PUDDING_CODEX_ARGUMENTS_JSON": (
                    '["' + str(MCP_CLI_DLL).replace("\\", "\\\\") + '","--stdio-server"]'
                ),
            }
        )
        with (temp / "service.out.log").open("wb") as stdout, (temp / "service.err.log").open("wb") as stderr:
            service = subprocess.Popen(
                [DOTNET, str(SERVICE_DLL)],
                cwd=ROOT,
                env=environment,
                stdout=stdout,
                stderr=stderr,
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
                start_new_session=os.name != "nt",
            )
            try:
                wait_for_health("http://127.0.0.1:5199/health")
                completed = subprocess.run(
                    [DOTNET, str(MCP_CLI_DLL), "--codex-service-smoke", ENDPOINT],
                    cwd=ROOT,
                    check=False,
                    text=True,
                )
                if completed.returncode != 0:
                    return completed.returncode
                self_heal = subprocess.run(
                    [DOTNET, str(MCP_CLI_DLL), "--codex-service-self-heal-smoke", ENDPOINT],
                    cwd=ROOT,
                    check=False,
                    text=True,
                )
                return self_heal.returncode
            finally:
                service.terminate()
                try:
                    service.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    service.kill()
                    service.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
