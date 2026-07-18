#!/usr/bin/env python3
"""
Pudding Agent local development launcher.

Starts:
  - backend app on 0.0.0.0:5000
  - frontend dev server on 0.0.0.0:8000
  - reverse proxy on 0.0.0.0:80
"""

from __future__ import annotations

import argparse
import http.server
import json
import os
import shutil
import signal
import socket
import socketserver
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path



ROOT = Path(__file__).resolve().parent
RUN_DIR = ROOT / "tmp" / "dev"
DATA_LOG_DIR = ROOT / "data" / "logs"
DEV_UP_LOG_PREFIX = "dev-up"

BACKEND_PORT = 5000
FRONTEND_PORT = 8000
LOOPBACK_HOST = "0.0.0.0"
LOCAL_CONNECT_HOST = "127.0.0.1"  # 代理连接后端/前端时使用，0.0.0.0 不可作为连接目标
PROXY_HOST = "0.0.0.0"
PREFERRED_PROXY_PORT = 80
FALLBACK_PROXY_PORT = None
HEALTH_PATH = "/health"
HEALTH_INITIAL_DELAY_SECONDS = 5
HEALTH_INTERVAL_SECONDS = 5
PROCESS_STOP_TIMEOUT_SECONDS = 10.0

BACKEND_PID_FILE = RUN_DIR / "backend.pid"
FRONTEND_PID_FILE = RUN_DIR / "frontend.pid"
PROXY_PID_FILE = RUN_DIR / "proxy.pid"
SUPERVISOR_PID_FILE = RUN_DIR / "supervisor.pid"
PROXY_PORT_FILE = RUN_DIR / "proxy.port"
HEALTH_STATUS_FILE = RUN_DIR / "health.status.json"
GUARD_STATE_FILE = RUN_DIR / "guard.enabled"

BACKEND_OUT_LOG = RUN_DIR / "backend.out.log"
BACKEND_ERR_LOG = RUN_DIR / "backend.err.log"
FRONTEND_OUT_LOG = RUN_DIR / "frontend.out.log"
FRONTEND_ERR_LOG = RUN_DIR / "frontend.err.log"
PROXY_OUT_LOG = RUN_DIR / "proxy.out.log"
PROXY_ERR_LOG = RUN_DIR / "proxy.err.log"
SUPERVISOR_OUT_LOG = RUN_DIR / "supervisor.out.log"
SUPERVISOR_ERR_LOG = RUN_DIR / "supervisor.err.log"
DEV_LOG_ROTATE_MAX_BYTES = int(os.environ.get("PUDDING_DEV_LOG_ROTATE_MAX_BYTES", str(20 * 1024 * 1024)))
DEV_LOG_ROTATE_BACKUPS = int(os.environ.get("PUDDING_DEV_LOG_ROTATE_BACKUPS", "3"))
DEFAULT_LOG_TAIL_LINES = int(os.environ.get("PUDDING_DEV_LOG_TAIL_LINES", "80"))

BACKEND_PREFIXES = (
    "/api/",
    "/api",
    "/swagger/",
    "/swagger",
    "/health",
    "/healthz",
    "/metrics",
    "/assets/",
    "/assets",
    "/connectors/",
    "/connectors",
    "/session-events/",
    "/session-events",
)

HOP_BY_HOP_HEADERS = {
    "connection",
    "keep-alive",
    "proxy-authenticate",
    "proxy-authorization",
    "te",
    "trailers",
    "transfer-encoding",
    "upgrade",
}


def write_launcher_log(message: str) -> None:
    try:
        DATA_LOG_DIR.mkdir(parents=True, exist_ok=True)
        now = datetime.now().astimezone()
        timestamp = now.isoformat(timespec="seconds")
        with launcher_log_path(now).open("a", encoding="utf-8") as handle:
            handle.write(f"{timestamp} pid={os.getpid()} {message}\n")
    except OSError:
        pass


def launcher_log_path(now: datetime | None = None) -> Path:
    value = now or datetime.now().astimezone()
    return DATA_LOG_DIR / f"{DEV_UP_LOG_PREFIX}-{value.date().isoformat()}.log"


def info(message: str) -> None:
    print(message, flush=True)
    write_launcher_log(message)


def fail(message: str) -> None:
    print(f"X {message}", file=sys.stderr, flush=True)
    write_launcher_log(f"X {message}")
    raise SystemExit(1)


def ensure_run_dir() -> None:
    RUN_DIR.mkdir(parents=True, exist_ok=True)


def resolve_command(name: str) -> str | None:
    if os.name == "nt":
        for candidate in (f"{name}.cmd", f"{name}.exe", name):
            resolved = shutil.which(candidate)
            if resolved:
                return resolved
        return None
    return shutil.which(name)


def require_command(name: str) -> None:
    if resolve_command(name) is None:
        fail(f"Missing command: {name}. Install it and make sure it is in PATH.")


def read_pid(path: Path) -> int | None:
    try:
        raw = path.read_text(encoding="ascii").strip()
        return int(raw) if raw else None
    except (FileNotFoundError, ValueError):
        return None


def write_pid(path: Path, pid: int) -> None:
    path.write_text(str(pid), encoding="ascii")


def is_process_alive(pid: int | None) -> bool:
    if not pid:
        return False
    if os.name == "nt":
        try:
            result = subprocess.run(
                ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
                cwd=ROOT,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                encoding="utf-8",
                errors="ignore",
                check=False,
            )
        except OSError:
            return False
        return f'"{pid}"' in result.stdout
    try:
        os.kill(pid, 0)
        return True
    except (OSError, SystemError):
        return False


def port_owner_pid(port: int) -> int | None:
    """Return the PID that is listening on a TCP port, when the host OS exposes it."""
    if os.name == "nt":
        try:
            result = subprocess.run(
                ["netstat", "-ano", "-p", "tcp"],
                cwd=ROOT,
                stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL,
                text=True,
                encoding="utf-8",
                errors="ignore",
                check=False,
            )
        except OSError:
            return None
        suffix = f":{port}"
        for line in result.stdout.splitlines():
            parts = line.split()
            if len(parts) < 5:
                continue
            proto, local, _remote, state, pid_raw = parts[:5]
            if proto.upper() != "TCP" or state.upper() != "LISTENING":
                continue
            if local.endswith(suffix):
                try:
                    return int(pid_raw)
                except ValueError:
                    return None
        return None

    lsof = shutil.which("lsof")
    if not lsof:
        return None
    try:
        result = subprocess.run(
            [lsof, "-tiTCP:%d" % port, "-sTCP:LISTEN"],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="ignore",
            check=False,
        )
    except OSError:
        return None
    for line in result.stdout.splitlines():
        try:
            return int(line.strip())
        except ValueError:
            continue
    return None


def all_port_pids(port: int) -> list[int]:
    """Return **all** PIDs that have a TCP connection on the given port, in any state."""
    pids: list[int] = []
    if os.name != "nt":
        return pids
    try:
        result = subprocess.run(
            ["netstat", "-ano", "-p", "tcp"],
            cwd=ROOT,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="ignore",
            check=False,
        )
    except OSError:
        return pids
    suffix = f":{port}"
    for line in result.stdout.splitlines():
        parts = line.split()
        if len(parts) < 5:
            continue
        local, _remote, _state, pid_raw = parts[1], parts[2], parts[3], parts[4]
        if not local.endswith(suffix):
            continue
        try:
            pids.append(int(pid_raw))
        except ValueError:
            continue
    return sorted(set(pids))


def force_release_port(port: int) -> None:
    """Kill **every** process holding the port in any state — more aggressive than stop_port_owner."""
    for _ in range(3):
        owners = all_port_pids(port)
        if not owners:
            break
        for pid in owners:
            if not is_process_alive(pid):
                continue
            if os.name == "nt":
                subprocess.run(
                    ["taskkill", "/PID", str(pid), "/F"],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                )
            else:
                try:
                    os.kill(pid, signal.SIGKILL)
                except OSError:
                    pass
        time.sleep(0.3)
    # Wait up to 5s for TIME_WAIT sockets to expire
    deadline = time.time() + 5.0
    while time.time() < deadline:
        if not all_port_pids(port):
            return
        time.sleep(0.3)


def stop_port_owner(name: str, port: int, tracked_pid: int | None = None) -> bool:
    deadline = time.time() + PROCESS_STOP_TIMEOUT_SECONDS
    stopped_any = False
    attempted: set[int] = set()

    while time.time() < deadline:
        owner = port_owner_pid(port)
        if not owner:
            return True
        if tracked_pid and owner == tracked_pid and not is_process_alive(owner):
            return True
        if owner not in attempted:
            attempted.add(owner)
            if stop_process_tree(owner):
                stopped_any = True
                info(f"V Stopped {name} port owner (PID {owner}, port {port})")
        time.sleep(0.2)

    remaining = port_owner_pid(port)
    if remaining:
        info(f"! {name} port {port} is still owned by PID {remaining}, force releasing")
        force_release_port(port)
        remaining = port_owner_pid(port)
    if remaining:
        info(f"! {name} port {port} is still owned by PID {remaining}")
        return False
    return stopped_any or True


def wait_until_port_free(port: int, timeout_seconds: float = 10.0) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if port_owner_pid(port) is None:
            return True
        time.sleep(0.2)
    return port_owner_pid(port) is None


def wait_until_process_exits(pid: int, timeout_seconds: float = PROCESS_STOP_TIMEOUT_SECONDS) -> bool:
    deadline = time.time() + timeout_seconds
    while time.time() < deadline:
        if not is_process_alive(pid):
            return True
        time.sleep(0.2)
    return not is_process_alive(pid)


def stop_process_tree(pid: int | None) -> bool:
    if not pid or not is_process_alive(pid):
        return True

    if os.name == "nt":
        result = subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="ignore",
            check=False,
        )
        if wait_until_process_exits(pid):
            return True
        info(f"! taskkill did not stop PID {pid}; exit={result.returncode}")
        return False

    try:
        os.killpg(pid, signal.SIGTERM)
    except OSError:
        try:
            os.kill(pid, signal.SIGTERM)
        except OSError:
            return True
    if wait_until_process_exits(pid):
        return True
    try:
        os.killpg(pid, signal.SIGKILL)
    except OSError:
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            return not is_process_alive(pid)
    return wait_until_process_exits(pid)


def stop_tracked_process(name: str, pid_file: Path, port: int | None = None) -> None:
    pid = read_pid(pid_file)
    if pid and is_process_alive(pid):
        if stop_process_tree(pid):
            info(f"V Stopped {name} (PID {pid})")
        else:
            fail(f"Failed to stop {name} (PID {pid}). Stop it manually and retry.")
    elif pid:
        info(f"! {name} PID {pid} is no longer running")
    else:
        info(f"! {name} PID is not recorded")
    if port is not None:
        if not stop_port_owner(name, port, tracked_pid=pid):
            fail(f"Failed to free {name} port {port}. Stop the occupying process manually and retry.")
    pid_file.unlink(missing_ok=True)


def can_bind(host: str, port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind((host, port))
            return True
        except OSError:
            return False


def choose_proxy_port(host: str, preferred_port: int, fallback_port: int | None) -> int:
    if can_bind(host, preferred_port):
        return preferred_port
    if fallback_port is not None and can_bind(host, fallback_port):
        return fallback_port
    if fallback_port is None:
        fail(f"Proxy port {preferred_port} is already in use. Stop the occupying process so localhost can be served on port 80.")
    fail(f"Proxy ports {preferred_port} and {fallback_port} are both in use.")


def is_port_free(host: str, port: int) -> bool:
    return can_bind(host, port)


def should_restart_role(role: str, exit_code: int | None, stopping: bool) -> bool:
    return role in {"backend", "frontend", "proxy"} and not stopping


class ChangeDebouncer:
    """Debounces file change events: restarts only fire after *delay_seconds* of silence.

    The ``primed`` flag ensures that repeated changes during the quiet window
    silently reset the deadline without repeatedly logging."""
    def __init__(self, delay_seconds: float) -> None:
        self.delay_seconds = delay_seconds
        self.deadline: float | None = None
        self.primed = False

    def changed(self, now: float) -> bool:
        """Returns True the first time a change arrives in a fresh debounce cycle."""
        first = not self.primed
        self.primed = True
        self.deadline = now + self.delay_seconds
        return first

    def ready(self, now: float) -> bool:
        return self.deadline is not None and now >= self.deadline

    def consume(self) -> bool:
        if self.deadline is None:
            return False
        self.deadline = None
        self.primed = False
        return True


def build_health_url(host: str, port: int, path: str) -> str:
    if not path.startswith("/"):
        path = "/" + path
    authority = host if port == 80 else f"{host}:{port}"
    return f"http://{authority}{path}"


def proxy_display_url(port: int) -> str:
    return "http://localhost" if port == 80 else f"http://localhost:{port}"


def proxy_lan_url_hint(port: int) -> str:
    return "http://<host-lan-ip>" if port == 80 else f"http://<host-lan-ip>:{port}"


def normalize_base_url(base_url: str) -> str:
    return base_url.rstrip("/")


def proxy_target_for_path(path: str, backend_base_url: str, frontend_base_url: str) -> str:
    base = normalize_base_url(backend_base_url) if path.startswith(BACKEND_PREFIXES) else normalize_base_url(frontend_base_url)
    if not path.startswith("/"):
        path = "/" + path
    return base + path


def is_session_events_stream_path(path: str) -> bool:
    route_path = urllib.parse.urlsplit(path).path
    return route_path.startswith("/api/sessions/") and route_path.endswith("/events/stream")


def is_session_replay_path(path: str) -> bool:
    route_path = urllib.parse.urlsplit(path).path
    return route_path.startswith("/api/sessions/") and route_path.endswith("/replay")


def should_log_proxy_diagnostics(path: str) -> bool:
    return is_session_events_stream_path(path) or is_session_replay_path(path)


def session_id_from_session_api_path(path: str) -> str | None:
    parts = urllib.parse.urlsplit(path).path.strip("/").split("/")
    if len(parts) >= 3 and parts[0] == "api" and parts[1] == "sessions":
        return urllib.parse.unquote(parts[2])
    return None


def proxy_diagnostic_jsonl_path() -> Path:
    return DATA_LOG_DIR / "diagnostics" / "proxy" / f"{datetime.now():%Y%m%d}.jsonl"


def write_proxy_diagnostic_event(event: dict[str, object]) -> None:
    record = {
        "schemaVersion": 1,
        "recordKind": "proxy",
        "recordedAtLocal": datetime.now().isoformat(timespec="milliseconds"),
        **event,
    }
    path = proxy_diagnostic_jsonl_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")) + "\n")


def is_event_stream_content_type(content_type: str | None) -> bool:
    return "text/event-stream" in (content_type or "").lower()


def frontend_spa_fallback_path(path: str) -> str:
    parsed = urllib.parse.urlsplit(path)
    route_path = parsed.path or "/"
    if not route_path.startswith("/admin/") or route_path == "/admin/":
        return path
    if "." in route_path.rsplit("/", 1)[-1]:
        return path
    return "/admin/"


def prepare_config() -> None:
    config_dir = Path(r"D:\data\config")
    default_config_dir = ROOT / "Source" / "PuddingAgent" / "default-data" / "config"
    config_dir.mkdir(parents=True, exist_ok=True)
    for name in ("llm.providers.json", "system.json", "security.json", "connectors.json"):
        target = config_dir / name
        source = default_config_dir / name
        if not target.exists() and source.exists():
            shutil.copy2(source, target)
            info(f"i Created default config {target}")

    # Voice config: data/config/voice/providers.json
    voice_config_dir = Path(r"D:\data\config\voice")
    default_voice_config_dir = ROOT / "Source" / "PuddingAgent" / "default-data" / "config" / "voice"
    voice_config_dir.mkdir(parents=True, exist_ok=True)
    for name in ("providers.json",):
        target = voice_config_dir / name
        source = default_voice_config_dir / name
        if not target.exists() and source.exists():
            shutil.copy2(source, target)
            info(f"i Created default voice config {target}")


def utc_timestamp() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())


def probe_health(url: str, timeout_seconds: int = 3) -> dict[str, object]:
    checked_at = utc_timestamp()
    request = urllib.request.Request(url, method="GET")
    try:
        with urllib.request.urlopen(request, timeout=timeout_seconds) as response:
            status_code = response.status
            response.read(1024)
            return {
                "url": url,
                "status_code": status_code,
                "ok": 200 <= status_code < 400,
                "checked_at": checked_at,
                "error": None,
            }
    except urllib.error.HTTPError as exc:
        exc.read(1024)
        return {
            "url": url,
            "status_code": exc.code,
            "ok": False,
            "checked_at": checked_at,
            "error": None,
        }
    except Exception as exc:
        return {
            "url": url,
            "status_code": None,
            "ok": False,
            "checked_at": checked_at,
            "error": str(exc),
        }


def write_health_status(status: dict[str, object]) -> None:
    HEALTH_STATUS_FILE.write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")


def read_health_status() -> dict[str, object] | None:
    try:
        return json.loads(HEALTH_STATUS_FILE.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError):
        return None


def set_guard_enabled(enabled: bool) -> None:
    ensure_run_dir()
    GUARD_STATE_FILE.write_text("1" if enabled else "0", encoding="ascii")


def is_guard_enabled() -> bool:
    try:
        value = GUARD_STATE_FILE.read_text(encoding="ascii").strip().lower()
    except FileNotFoundError:
        return False  # 默认禁用：文件变化不再自动编译重启，必须手动 --restart
    return value not in {"0", "false", "off", "disabled", "no"}




def rotate_log_if_needed(path: Path) -> None:
    if DEV_LOG_ROTATE_MAX_BYTES <= 0 or DEV_LOG_ROTATE_BACKUPS <= 0:
        return
    if not path.exists() or path.stat().st_size < DEV_LOG_ROTATE_MAX_BYTES:
        return

    oldest = path.with_name(f"{path.name}.{DEV_LOG_ROTATE_BACKUPS}")
    oldest.unlink(missing_ok=True)
    for index in range(DEV_LOG_ROTATE_BACKUPS - 1, 0, -1):
        source = path.with_name(f"{path.name}.{index}")
        if source.exists():
            source.replace(path.with_name(f"{path.name}.{index + 1}"))
    path.replace(path.with_name(f"{path.name}.1"))


def open_log(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    rotate_log_if_needed(path)
    return path.open("ab", buffering=0)


def popen_kwargs():
    if os.name == "nt":
        return {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
    return {"start_new_session": True}


def backend_build_command(full_rebuild: bool = False) -> list[str]:
    cmd = [
        resolve_command("dotnet") or "dotnet",
        "build",
        "Source/PuddingAgent/PuddingAgent.csproj",
        "--nologo",
    ]
    if full_rebuild:
        cmd.append("--no-incremental")
    return cmd


def backend_command() -> list[str]:
    return [
        resolve_command("dotnet") or "dotnet",
        str(ROOT / "Source" / "PuddingAgent" / "bin" / "Debug" / "net10.0" / "PuddingAgent.dll"),
    ]


def failed_backend_process() -> subprocess.Popen:
    if os.name == "nt":
        return subprocess.Popen(["cmd", "/c", "exit", "/b", "1"], cwd=ROOT, **popen_kwargs())
    return subprocess.Popen(["sh", "-c", "exit 1"], cwd=ROOT, **popen_kwargs())


def backend_environment() -> dict[str, str]:
    env = os.environ.copy()
    env.update(
        {
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": f"http://{LOOPBACK_HOST}:{BACKEND_PORT}",
            "DOTNET_USE_POLLING_FILE_WATCHER": "1",
            "PUDDING_DATA_ROOT": r"D:\data",
            "Jwt__Key": "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!",
            "Jwt__Issuer": "pudding-platform",
            "Jwt__Audience": "pudding-admin",
            "Jwt__ExpiryHours": "8",
            "ConnectionStrings__Default": r"Data Source=D:\data\databases\pudding_platform.db",
            "PUDDING_LOG_LEVEL": "Debug",
            "PUDDING_DEBUG": "1",
            "PUDDING_TELEMETRY_DEBUG": "1",
            "Pudding__ControllerEndpoint": f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}",
            "Pudding__RuntimeFallbackEndpoint": f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}",
        }
    )
    return env


def run_backend_build(full_rebuild: bool = False) -> int:
    env = backend_environment()
    build_log = open_log(BACKEND_OUT_LOG)
    build_err = open_log(BACKEND_ERR_LOG)
    build = subprocess.run(
        backend_build_command(full_rebuild=full_rebuild),
        cwd=ROOT,
        env=env,
        stdout=build_log,
        stderr=build_err,
        check=False,
    )
    build_log.close()
    build_err.close()
    return build.returncode


def start_backend(full_rebuild: bool = False) -> subprocess.Popen:
    env = backend_environment()
    returncode = run_backend_build(full_rebuild=full_rebuild)
    if returncode != 0:
        info(f"! Backend build failed with exit code {returncode}")
        proc = failed_backend_process()
        write_pid(BACKEND_PID_FILE, proc.pid)
        return proc

    proc = subprocess.Popen(
        backend_command(),
        cwd=ROOT,
        env=env,
        stdout=open_log(BACKEND_OUT_LOG),
        stderr=open_log(BACKEND_ERR_LOG),
        **popen_kwargs(),
    )
    write_pid(BACKEND_PID_FILE, proc.pid)
    info(f"V Backend started (PID {proc.pid})")
    return proc


def start_frontend(no_install: bool) -> subprocess.Popen:
    admin_dir = ROOT / "Source" / "PuddingPlatformAdmin"
    env = os.environ.copy()
    env.update({"REACT_APP_ENV": "dev", "MOCK": "none", "UMI_ENV": "dev"})

    if not no_install and not (admin_dir / "node_modules").exists():
        info("==> Install frontend dependencies")
        subprocess.run([resolve_command("pnpm") or "pnpm", "install"], cwd=admin_dir, env=env, check=True)

    proc = subprocess.Popen(
        [resolve_command("pnpm") or "pnpm", "run", "start:dev", "--", "--host", LOOPBACK_HOST, "--port", str(FRONTEND_PORT)],
        cwd=admin_dir,
        env=env,
        stdout=open_log(FRONTEND_OUT_LOG),
        stderr=open_log(FRONTEND_ERR_LOG),
        **popen_kwargs(),
    )
    write_pid(FRONTEND_PID_FILE, proc.pid)
    info(f"V Frontend dev server started (PID {proc.pid})")
    return proc


def start_proxy(port: int) -> subprocess.Popen:
    proc = subprocess.Popen(
        [
            sys.executable,
            str(Path(__file__).resolve()),
            "--proxy",
            "--proxy-host",
            PROXY_HOST,
            "--proxy-port",
            str(port),
            "--backend-url",
            f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}",
            "--frontend-url",
            f"http://{LOCAL_CONNECT_HOST}:{FRONTEND_PORT}",
        ],
        cwd=ROOT,
        stdout=open_log(PROXY_OUT_LOG),
        stderr=open_log(PROXY_ERR_LOG),
        **popen_kwargs(),
    )
    write_pid(PROXY_PID_FILE, proc.pid)
    PROXY_PORT_FILE.write_text(str(port), encoding="ascii")
    info(f"V Reverse proxy started (PID {proc.pid}, {proxy_display_url(port)})")
    return proc


def format_status_lines(snapshot: dict[str, dict[str, object]]) -> list[str]:
    labels = {
        "supervisor": "Supervisor",
        "guard": "Guard     ",
        "backend": "Backend   ",
        "frontend": "Frontend  ",
        "proxy": "Proxy     ",
    }
    lines: list[str] = []
    guard = snapshot.get("guard", {})
    for role in ("supervisor", "backend", "frontend", "proxy"):
        state = snapshot.get(role, {})
        pid = state.get("pid")
        alive = bool(state.get("alive"))
        line = f"{labels[role]}: running (PID {pid})" if alive and pid else f"{labels[role]}: stopped"
        port = state.get("port")
        if role == "proxy" and alive and port:
            line += f" on {proxy_display_url(int(port))}"
        lines.append(line)
        if role == "supervisor":
            lines.append(f"{labels['guard']}: {'enabled' if guard.get('enabled', True) else 'disabled'}")
    health = snapshot.get("health")
    if not health:
        lines.append("Health    : pending")
    elif health.get("status_code") is not None:
        lines.append(f"Health    : HTTP {health.get('status_code')} from {health.get('url')} at {health.get('checked_at')}")
    else:
        lines.append(f"Health    : ERROR {health.get('error')} from {health.get('url')} at {health.get('checked_at')}")
    return lines


def status_snapshot() -> dict[str, dict[str, object]]:
    try:
        proxy_port = PROXY_PORT_FILE.read_text(encoding="ascii").strip()
    except FileNotFoundError:
        proxy_port = ""
    backend_pid = read_pid(BACKEND_PID_FILE)
    frontend_pid = read_pid(FRONTEND_PID_FILE)
    proxy_pid = read_pid(PROXY_PID_FILE)
    supervisor_pid = read_pid(SUPERVISOR_PID_FILE)
    proxy_port_value = int(proxy_port) if proxy_port.isdigit() else None
    backend_owner = port_owner_pid(BACKEND_PORT)
    frontend_owner = port_owner_pid(FRONTEND_PORT)
    proxy_owner = port_owner_pid(proxy_port_value) if proxy_port_value else None
    return {
        "supervisor": {
            "pid": supervisor_pid,
            "alive": is_process_alive(supervisor_pid),
        },
        "backend": {
            "pid": backend_pid if is_process_alive(backend_pid) else backend_owner,
            "alive": is_process_alive(backend_pid) or backend_owner is not None,
            "tracked_pid": backend_pid,
            "port_owner_pid": backend_owner,
        },
        "frontend": {
            "pid": frontend_pid if is_process_alive(frontend_pid) else frontend_owner,
            "alive": is_process_alive(frontend_pid) or frontend_owner is not None,
            "tracked_pid": frontend_pid,
            "port_owner_pid": frontend_owner,
        },
        "proxy": {
            "pid": proxy_pid if is_process_alive(proxy_pid) else proxy_owner,
            "alive": is_process_alive(proxy_pid) or proxy_owner is not None,
            "tracked_pid": proxy_pid,
            "port_owner_pid": proxy_owner,
            "port": proxy_port_value,
        },
        "guard": {"enabled": is_guard_enabled()},
        "health": read_health_status(),
    }


def show_status() -> None:
    for line in format_status_lines(status_snapshot()):
        info(line)


def write_stdout(text: str) -> None:
    try:
        sys.stdout.write(text)
    except UnicodeEncodeError:
        encoding = sys.stdout.encoding or "utf-8"
        safe_text = text.encode(encoding, errors="replace").decode(encoding, errors="replace")
        try:
            sys.stdout.write(safe_text)
        except OSError:
            return
    except OSError:
        return
    try:
        sys.stdout.flush()
    except OSError:
        return


def tail_file_lines(path: Path, line_count: int, block_size: int = 8192) -> list[str]:
    if line_count <= 0 or not path.exists():
        return []

    blocks: list[bytes] = []
    newline_count = 0
    with path.open("rb") as handle:
        handle.seek(0, os.SEEK_END)
        position = handle.tell()
        while position > 0 and newline_count <= line_count:
            read_size = min(block_size, position)
            position -= read_size
            handle.seek(position)
            block = handle.read(read_size)
            blocks.append(block)
            newline_count += block.count(b"\n")

    data = b"".join(reversed(blocks)).decode("utf-8", errors="replace")
    return data.splitlines()[-line_count:]


def follow_logs(tail_lines: int = 0) -> None:
    paths = [
        launcher_log_path(),
        SUPERVISOR_OUT_LOG,
        SUPERVISOR_ERR_LOG,
        BACKEND_OUT_LOG,
        BACKEND_ERR_LOG,
        FRONTEND_OUT_LOG,
        FRONTEND_ERR_LOG,
        PROXY_OUT_LOG,
        PROXY_ERR_LOG,
    ]
    for path in paths:
        path.touch(exist_ok=True)

    if tail_lines < 0:
        fail("--logs line count must be zero or greater.")

    if tail_lines:
        for path in paths:
            lines = tail_file_lines(path, tail_lines)
            if lines:
                write_stdout(f"\n--- {path.name} (last {tail_lines}) ---\n")
                write_stdout("\n".join(lines[-tail_lines:]) + "\n")

    positions = {path: path.stat().st_size for path in paths}
    info("Following logs. Press Ctrl+C to stop.")
    try:
        while True:
            for path in paths:
                size = path.stat().st_size
                if size < positions[path]:
                    positions[path] = 0
                if size > positions[path]:
                    with path.open("r", encoding="utf-8", errors="replace") as handle:
                        handle.seek(positions[path])
                        data = handle.read()
                        if data:
                            write_stdout(f"\n--- {path.name} ---\n")
                            write_stdout(data)
                        positions[path] = handle.tell()
            time.sleep(1)
    except KeyboardInterrupt:
        pass


def stop_all() -> None:
    try:
        proxy_port = int(PROXY_PORT_FILE.read_text(encoding="ascii").strip())
    except (FileNotFoundError, ValueError):
        proxy_port = PREFERRED_PROXY_PORT
    stop_tracked_process("Supervisor", SUPERVISOR_PID_FILE)
    stop_tracked_process("Proxy", PROXY_PID_FILE, proxy_port)
    stop_tracked_process("Frontend", FRONTEND_PID_FILE, FRONTEND_PORT)
    stop_tracked_process("Backend", BACKEND_PID_FILE, BACKEND_PORT)
    PROXY_PORT_FILE.unlink(missing_ok=True)


def enable_guard() -> None:
    set_guard_enabled(True)
    info("Guard enabled")


def disable_guard() -> None:
    set_guard_enabled(False)
    info("Guard disabled")


class ThreadingHTTPServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


class ReverseProxyHandler(http.server.BaseHTTPRequestHandler):
    backend_url = f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}"
    frontend_url = f"http://{LOCAL_CONNECT_HOST}:{FRONTEND_PORT}"

    protocol_version = "HTTP/1.1"

    def do_GET(self):
        if self.headers.get("Upgrade", "").lower() == "websocket":
            self.tunnel_websocket()
        else:
            self.proxy_http()

    def do_HEAD(self):
        self.proxy_http()

    def do_POST(self):
        self.proxy_http()

    def do_PUT(self):
        self.proxy_http()

    def do_PATCH(self):
        self.proxy_http()

    def do_DELETE(self):
        self.proxy_http()

    def do_OPTIONS(self):
        self.proxy_http()

    def log_message(self, fmt: str, *args) -> None:
        message = "%s - - [%s] %s" % (self.client_address[0], self.log_date_time_string(), fmt % args)
        sys.stdout.write(f"{message}\n")
        sys.stdout.flush()
        write_launcher_log(f"proxy {message}")

    def log_proxy_diagnostic(self, message: str) -> None:
        text = f"[proxy-diagnostic] {message}"
        sys.stdout.write(f"{text}\n")
        sys.stdout.flush()
        write_launcher_log(f"proxy {text}")

    def log_proxy_diagnostic_event(self, stage: str, request_path: str, started_at: float, **fields) -> None:
        elapsed_ms = int((time.perf_counter() - started_at) * 1000)
        write_proxy_diagnostic_event({
            "stage": stage,
            "path": request_path,
            "sessionId": session_id_from_session_api_path(request_path),
            "elapsedMs": elapsed_ms,
            **fields,
        })

    def proxy_http(self) -> None:
        self.close_connection = True
        started_at = time.perf_counter()
        request_path = self.path
        if self.command in ("GET", "HEAD") and not self.path.startswith(BACKEND_PREFIXES):
            request_path = frontend_spa_fallback_path(self.path)
        target = proxy_target_for_path(request_path, self.backend_url, self.frontend_url)
        diagnostic = should_log_proxy_diagnostics(request_path)

        # SSE/streaming 端点需要长连接保持，不可强制 close
        event_stream_expected = is_session_events_stream_path(request_path)
        if event_stream_expected:
            self.close_connection = False
        if diagnostic:
            self.log_proxy_diagnostic(
                f"start method={self.command} path={request_path} target={target}")
            self.log_proxy_diagnostic_event(
                "proxy.request.started",
                request_path,
                started_at,
                method=self.command,
                target=target)
        body = self.read_body()
        headers = self.forward_headers()
        # SSE/streaming 端点需要长连接，移除 close 头避免连接过早断开
        if is_session_events_stream_path(request_path):
            headers.pop("Connection", None)
        request = urllib.request.Request(target, data=body, headers=headers, method=self.command)

        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                content_type = response.headers.get("Content-Type", "")
                event_stream = is_event_stream_content_type(content_type)
                if diagnostic:
                    self.log_proxy_diagnostic(
                        f"upstream method={self.command} path={request_path} status={response.status} "
                        f"contentType={content_type or '-'} eventStream={event_stream}")
                    self.log_proxy_diagnostic_event(
                        "proxy.upstream.response",
                        request_path,
                        started_at,
                        method=self.command,
                        target=target,
                        status=response.status,
                        contentType=content_type or "",
                        eventStream=event_stream)
                self.send_response(response.status, response.reason)
                for key, value in response.headers.items():
                    if key.lower() not in HOP_BY_HOP_HEADERS:
                        self.send_header(key, value)
                if not event_stream_expected and not event_stream:
                    self.send_header("Connection", "close")
                self.end_headers()
                if self.command == "HEAD":
                    return
                if event_stream:
                    self.stream_event_response(response, request_path, target, started_at, diagnostic)
                    return
                total_bytes = 0
                chunk_count = 0
                while True:
                    chunk = response.read(64 * 1024)
                    if not chunk:
                        break
                    chunk_count += 1
                    total_bytes += len(chunk)
                    self.wfile.write(chunk)
                    self.wfile.flush()
                if diagnostic:
                    self.log_proxy_diagnostic(
                        f"done method={self.command} path={request_path} status={response.status} "
                        f"chunks={chunk_count} bytes={total_bytes} elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
                    self.log_proxy_diagnostic_event(
                        "proxy.response.completed",
                        request_path,
                        started_at,
                        method=self.command,
                        target=target,
                        status=response.status,
                        chunks=chunk_count,
                        bytes=total_bytes)
        except urllib.error.HTTPError as exc:
            if diagnostic:
                self.log_proxy_diagnostic(
                    f"http-error method={self.command} path={request_path} status={exc.code} "
                    f"elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
                self.log_proxy_diagnostic_event(
                    "proxy.response.http_error",
                    request_path,
                    started_at,
                    method=self.command,
                    target=target,
                    status=exc.code)
            self.send_response(exc.code, exc.reason)
            for key, value in exc.headers.items():
                if key.lower() not in HOP_BY_HOP_HEADERS:
                    self.send_header(key, value)
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(exc.read())
        except Exception as exc:
            if diagnostic:
                self.log_proxy_diagnostic(
                    f"error method={self.command} path={request_path} target={target} "
                    f"error={exc} elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
                self.log_proxy_diagnostic_event(
                    "proxy.response.error",
                    request_path,
                    started_at,
                    method=self.command,
                    target=target,
                    error=str(exc))
            message = f"Proxy error for {target}: {exc}".encode("utf-8", errors="replace")
            self.send_response(502, "Bad Gateway")
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(message)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(message)

    def stream_event_response(self, response, request_path: str, target: str, started_at: float, diagnostic: bool) -> None:
        total_bytes = 0
        line_count = 0
        event_count = 0
        data_count = 0
        try:
            while True:
                line = response.readline()
                if not line:
                    break
                line_count += 1
                total_bytes += len(line)
                stripped = line.strip()
                if stripped.startswith(b"event:"):
                    event_count += 1
                elif stripped.startswith(b"data:"):
                    data_count += 1
                if diagnostic and (line_count <= 8 or line_count % 20 == 0):
                    label = "event" if stripped.startswith(b"event:") else "data" if stripped.startswith(b"data:") else "blank"
                    self.log_proxy_diagnostic(
                        f"sse-line path={request_path} line={line_count} kind={label} bytes={len(line)} "
                        f"events={event_count} data={data_count} elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
                    self.log_proxy_diagnostic_event(
                        "proxy.sse.line",
                        request_path,
                        started_at,
                        target=target,
                        line=line_count,
                        kind=label,
                        lineBytes=len(line),
                        eventLines=event_count,
                        dataLines=data_count)
                self.wfile.write(line)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError) as exc:
            if diagnostic:
                self.log_proxy_diagnostic(
                    f"sse-client-closed path={request_path} error={exc} lines={line_count} bytes={total_bytes} "
                    f"elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
                self.log_proxy_diagnostic_event(
                    "proxy.sse.client_closed",
                    request_path,
                    started_at,
                    target=target,
                    error=str(exc),
                    lines=line_count,
                    bytes=total_bytes)
            return
        if diagnostic:
            self.log_proxy_diagnostic(
                f"sse-done path={request_path} target={target} lines={line_count} events={event_count} "
                f"data={data_count} bytes={total_bytes} elapsedMs={int((time.perf_counter() - started_at) * 1000)}")
            self.log_proxy_diagnostic_event(
                "proxy.sse.completed",
                request_path,
                started_at,
                target=target,
                lines=line_count,
                eventLines=event_count,
                dataLines=data_count,
                bytes=total_bytes)

    def read_body(self) -> bytes | None:
        length = self.headers.get("Content-Length")
        if not length:
            return None
        return self.rfile.read(int(length))

    def forward_headers(self) -> dict[str, str]:
        headers: dict[str, str] = {}
        for key, value in self.headers.items():
            lower = key.lower()
            if lower not in HOP_BY_HOP_HEADERS and lower != "host":
                headers[key] = value
        headers["X-Forwarded-Host"] = self.headers.get("Host", "")
        headers["X-Forwarded-Proto"] = "http"
        # 强制 close，避免 urllib HTTP/1.1 keep-alive 导致 response.read() 阻塞 EOF
        headers["Connection"] = "close"
        return headers

    def tunnel_websocket(self) -> None:
        target = proxy_target_for_path(self.path, self.backend_url, self.frontend_url)
        parsed = urllib.parse.urlparse(target)
        port = parsed.port or (443 if parsed.scheme == "https" else 80)
        if parsed.scheme != "http":
            self.send_error(502, "WebSocket proxy only supports http upstreams in dev mode")
            return

        upstream = socket.create_connection((parsed.hostname or LOCAL_CONNECT_HOST, port), timeout=30)
        try:
            request_target = urllib.parse.urlunparse(("", "", parsed.path or "/", parsed.params, parsed.query, ""))
            upstream.sendall(f"{self.command} {request_target} {self.request_version}\r\n".encode("ascii"))
            for key, value in self.headers.items():
                upstream.sendall(f"{key}: {value}\r\n".encode("latin-1"))
            upstream.sendall(b"\r\n")
            self.relay_bidirectional(upstream)
        finally:
            upstream.close()

    def relay_bidirectional(self, upstream: socket.socket) -> None:
        sockets = (self.connection, upstream)

        def pump(source: socket.socket, target: socket.socket) -> None:
            try:
                while True:
                    data = source.recv(64 * 1024)
                    if not data:
                        break
                    target.sendall(data)
            except OSError:
                pass
            finally:
                for sock in sockets:
                    try:
                        sock.shutdown(socket.SHUT_RDWR)
                    except OSError:
                        pass

        left = threading.Thread(target=pump, args=(self.connection, upstream), daemon=True)
        right = threading.Thread(target=pump, args=(upstream, self.connection), daemon=True)
        left.start()
        right.start()
        left.join()
        right.join()


def run_proxy(host: str, port: int, backend_url: str, frontend_url: str) -> None:
    ReverseProxyHandler.backend_url = backend_url
    ReverseProxyHandler.frontend_url = frontend_url
    server = ThreadingHTTPServer((host, port), ReverseProxyHandler)
    info(f"Reverse proxy listening on http://{host}:{port}")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


def restart_child(role: str, processes: dict[str, subprocess.Popen], no_install: bool, proxy_port: int, frontend_only: bool = False) -> None:
    info(f"! {role} exited; restarting")
    if role == "backend":
        if frontend_only:
            info("! Backend exited but --frontend-only, not restarting")
            return
        force_release_port(BACKEND_PORT)
        stop_port_owner("Backend", BACKEND_PORT)
        wait_until_port_free(BACKEND_PORT)
        processes[role] = start_backend()
    elif role == "frontend":
        force_release_port(FRONTEND_PORT)
        stop_port_owner("Frontend", FRONTEND_PORT)
        wait_until_port_free(FRONTEND_PORT)
        processes[role] = start_frontend(no_install=True)
    elif role == "proxy":
        force_release_port(proxy_port)
        stop_port_owner("Proxy", proxy_port)
        wait_until_port_free(proxy_port)
        processes[role] = start_proxy(proxy_port)


def health_monitor_loop(url: str, stop_event: threading.Event) -> None:
    if stop_event.wait(HEALTH_INITIAL_DELAY_SECONDS):
        return
    while not stop_event.is_set():
        status = probe_health(url)
        write_health_status(status)
        if status["status_code"] is not None:
            info(f"Health check: HTTP {status['status_code']} from {url}")
        else:
            info(f"Health check: ERROR {status['error']} from {url}")
        stop_event.wait(HEALTH_INTERVAL_SECONDS)


def run_supervisor(no_install: bool, frontend_only: bool = False) -> None:
    ensure_run_dir()
    if not frontend_only:
        require_command("dotnet")
    require_command("pnpm")

    if not frontend_only and not is_port_free(LOOPBACK_HOST, BACKEND_PORT):
        fail(f"Backend port {BACKEND_PORT} is already in use.")
    if not is_port_free(LOOPBACK_HOST, FRONTEND_PORT):
        fail(f"Frontend port {FRONTEND_PORT} is already in use.")

    proxy_port = choose_proxy_port(PROXY_HOST, PREFERRED_PROXY_PORT, FALLBACK_PROXY_PORT)
    prepare_config()
    write_pid(SUPERVISOR_PID_FILE, os.getpid())

    processes = {
        "frontend": start_frontend(no_install=no_install),
        "proxy": start_proxy(proxy_port),
    }
    if frontend_only:
        info("Frontend-only mode (skip backend)")
    else:
        processes["backend"] = start_backend()
    health_url = build_health_url("127.0.0.1", proxy_port, HEALTH_PATH)
    HEALTH_STATUS_FILE.unlink(missing_ok=True)
    health_stop = threading.Event()
    health_thread = threading.Thread(target=health_monitor_loop, args=(health_url, health_stop), daemon=True)
    health_thread.start()

    proxy_url = proxy_display_url(proxy_port)
    info(
        "\nDevelopment environment started:\n"
        f"  Backend API  -> http://localhost:{BACKEND_PORT}\n"
        f"  Frontend Dev -> http://localhost:{FRONTEND_PORT}\n"
        f"  Proxy entry  -> {proxy_url}/admin/user/login\n"
        f"  LAN entry    -> {proxy_lan_url_hint(proxy_port)}/admin/user/login\n\n"
        "Status:\n"
        "  python dev-up.py --status\n\n"
        "Logs:\n"
        "  python dev-up.py --logs\n\n"
        "Stop:\n"
        "  python dev-up.py --down\n"
    )

    stopping = False

    def request_stop(signum, frame) -> None:
        nonlocal stopping
        stopping = True

    if os.name != "nt":
        signal.signal(signal.SIGTERM, request_stop)
    signal.signal(signal.SIGINT, request_stop)

    try:
        while not stopping:
            guard_enabled = is_guard_enabled()
            for role, process in list(processes.items()):
                exit_code = process.poll()
                if exit_code is None:
                    continue
                if guard_enabled and should_restart_role(role, exit_code, stopping):
                    info(f"! {role} exited; restarting")
                    restart_child(role, processes, no_install=True, proxy_port=proxy_port, frontend_only=frontend_only)
            time.sleep(2)
    finally:
        health_stop.set()
        health_thread.join(timeout=2)
        for role, process in list(processes.items()):
            if process.poll() is None:
                stop_process_tree(process.pid)
        for pid_file in (BACKEND_PID_FILE, FRONTEND_PID_FILE, PROXY_PID_FILE, SUPERVISOR_PID_FILE):
            pid_file.unlink(missing_ok=True)
        PROXY_PORT_FILE.unlink(missing_ok=True)
        HEALTH_STATUS_FILE.unlink(missing_ok=True)


def start_supervisor(no_install: bool, frontend_only: bool = False) -> None:
    supervisor_pid = read_pid(SUPERVISOR_PID_FILE)
    if supervisor_pid and is_process_alive(supervisor_pid):
        fail(f"Development supervisor is already running (PID {supervisor_pid}). Use --status, --restart, or --down.")

    proc = subprocess.Popen(
        [
            sys.executable,
            str(Path(__file__).resolve()),
            "--supervisor",
            *(["--no-install"] if no_install else []),
            *(["--frontend-only"] if frontend_only else []),
        ],
        cwd=ROOT,
        stdout=open_log(SUPERVISOR_OUT_LOG),
        stderr=open_log(SUPERVISOR_ERR_LOG),
        **popen_kwargs(),
    )
    write_pid(SUPERVISOR_PID_FILE, proc.pid)
    info(f"V Development supervisor started (PID {proc.pid})")
    info("  Status: python dev-up.py --status")
    info("  Logs:   python dev-up.py --logs")
    info("  Stop:   python dev-up.py --down")


def start_all(no_install: bool, frontend_only: bool = False) -> None:
    ensure_run_dir()
    start_supervisor(no_install=no_install, frontend_only=frontend_only)


# ── Bootstrap / Init ──────────────────────────────────────────

def run_init() -> None:
    """Initialize a fresh development environment: prepare config, bootstrap admin user & workspace."""
    info("==> Initializing development environment ...")

    # 1. Copy default config files
    prepare_config()

    # 2. Build backend
    ensure_run_dir()
    require_command("dotnet")
    info("==> Building backend ...")
    rc = run_backend_build(full_rebuild=False)
    if rc != 0:
        fail(f"Backend build failed with exit code {rc}")

    # 3. Start backend temporarily
    if not is_port_free(LOOPBACK_HOST, BACKEND_PORT):
        info(f"Backend port {BACKEND_PORT} is in use, stopping existing owner...")
        force_release_port(BACKEND_PORT)
        if not wait_until_port_free(BACKEND_PORT, timeout_seconds=10):
            fail(f"Cannot free backend port {BACKEND_PORT}")

    process = start_backend(full_rebuild=False)
    info("Waiting for backend to be ready ...")
    health_url = build_health_url(LOCAL_CONNECT_HOST, BACKEND_PORT, HEALTH_PATH)
    deadline = time.time() + 60
    while time.time() < deadline:
        status = probe_health(health_url, timeout_seconds=3)
        if status["ok"]:
            info(f"Backend ready (HTTP {status['status_code']})")
            break
        time.sleep(2)
    else:
        stop_process_tree(process.pid)
        fail("Backend did not become healthy within 60s")

    # 4. Check if already initialized
    try:
        with urllib.request.urlopen(f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}/api/bootstrap/status", timeout=5) as resp:
            body = resp.read().decode("utf-8", errors="replace")
            data = json.loads(body) if body else {}
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        data = json.loads(body) if body else {}
    except Exception as exc:
        data = {}

    if "needsSetup" not in data and data.get("status") == "error":
        info("i Bootstrap: already initialized, skipping")
    else:
        # 5. Bootstrap: POST /api/bootstrap/complete
        info("==> Bootstrapping admin account and default workspace ...")
        admin_password = os.environ.get("PUDDING_ADMIN_PASSWORD", "Admin@123")
        payload = json.dumps({
            "admin": {
                "userId": "admin",
                "password": admin_password,
                "displayName": "Administrator",
                "email": "admin@localhost"
            },
            "provider": None,
            "defaults": None
        }).encode("utf-8")
        req = urllib.request.Request(
            f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}/api/bootstrap/complete",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST"
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                result = json.loads(resp.read().decode("utf-8", errors="replace"))
                info(f"V Bootstrap completed: {result.get('status')} adminId={result.get('adminId','?')} workspaceId={result.get('workspaceId','?')}")
        except urllib.error.HTTPError as exc:
            body = exc.read().decode("utf-8", errors="replace")
            info(f"Bootstrap returned HTTP {exc.code}: {body[:300]}")
            stop_process_tree(process.pid)
            sys.exit(1 if exc.code >= 400 else 0)

    # 6. Stop temporary backend
    info("Stopping temporary backend ...")
    stop_process_tree(process.pid)
    wait_until_port_free(BACKEND_PORT)
    info("V Initialization complete. Run 'python dev-up.py' to start the development environment.")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Start Pudding Agent local development services.")
    parser.add_argument("--down", action="store_true", help="Stop tracked development processes.")
    parser.add_argument(
        "--logs",
        nargs="?",
        const=DEFAULT_LOG_TAIL_LINES,
        type=int,
        metavar="LINES",
        help=f"Follow backend/frontend/proxy logs; optionally print the last LINES first (default: {DEFAULT_LOG_TAIL_LINES}).",
    )
    parser.add_argument("--status", action="store_true", help="Show tracked process status.")
    parser.add_argument("--restart", action="store_true", help="Stop and start development processes.")
    parser.add_argument("--rebuild", action="store_true", help="Stop, full rebuild (--no-incremental), and start.")
    parser.add_argument("--no-install", action="store_true", help="Skip frontend dependency installation.")
    parser.add_argument("--guard-on", action="store_true", help="Enable supervisor auto-restart and file-change restarts.")
    parser.add_argument("--guard-off", action="store_true", help="Disable supervisor auto-restart and file-change restarts.")
    parser.add_argument("--frontend-only", action="store_true", help="Start frontend + proxy only (backend started manually).")
    parser.add_argument("--init", action="store_true", help="Initialize/bootstrap a fresh development environment (create admin, default workspace, etc.).")
    parser.add_argument("--proxy", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--supervisor", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--proxy-host", default=PROXY_HOST, help=argparse.SUPPRESS)
    parser.add_argument("--proxy-port", type=int, default=PREFERRED_PROXY_PORT, help=argparse.SUPPRESS)
    parser.add_argument("--backend-url", default=f"http://{LOCAL_CONNECT_HOST}:{BACKEND_PORT}", help=argparse.SUPPRESS)
    parser.add_argument("--frontend-url", default=f"http://{LOCAL_CONNECT_HOST}:{FRONTEND_PORT}", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    ensure_run_dir()

    if args.proxy:
        run_proxy(args.proxy_host, args.proxy_port, args.backend_url, args.frontend_url)
        return 0

    if args.supervisor:
        run_supervisor(no_install=args.no_install, frontend_only=args.frontend_only)
        return 0

    if args.guard_on and args.guard_off:
        fail("Use only one of --guard-on or --guard-off.")
    if args.guard_on:
        enable_guard()
        return 0

    if args.guard_off:
        disable_guard()
        return 0

    if args.init:
        run_init()
        return 0

    if args.down or args.restart or args.rebuild:
        stop_all()
        if args.down and not args.restart and not args.rebuild:
            return 0
        if args.rebuild and not args.frontend_only:
            info("==> Full rebuild (--no-incremental) ...")
            rc = run_backend_build(full_rebuild=True)
            if rc != 0:
                fail(f"Full rebuild failed with exit code {rc}")

    # --status / --logs are read-only: only handle when not doing a start cycle
    if not args.restart and not args.rebuild and not args.down:
        if args.status:
            show_status()
            return 0

        if args.logs is not None:
            follow_logs(args.logs)
            return 0

    start_all(no_install=args.no_install, frontend_only=args.frontend_only)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
