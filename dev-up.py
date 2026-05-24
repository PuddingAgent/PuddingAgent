#!/usr/bin/env python3
"""
Pudding Agent local development launcher.

Starts:
  - backend app on 127.0.0.1:5000
  - frontend dev server on 127.0.0.1:8000
  - reverse proxy on 127.0.0.1:80
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
PROXY_HOST = "127.0.0.1"
PREFERRED_PROXY_PORT = 80
FALLBACK_PROXY_PORT = None
HEALTH_PATH = "/health"
HEALTH_INITIAL_DELAY_SECONDS = 5
HEALTH_INTERVAL_SECONDS = 5
WATCH_DEBOUNCE_SECONDS = 5
WATCH_POLL_SECONDS = 1
RESTART_MAX_FAILURES = 5
RESTART_COOLDOWN_SECONDS = 5

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

BACKEND_PREFIXES = (
    "/api/",
    "/api",
    "/swagger/",
    "/swagger",
    "/health",
    "/healthz",
    "/metrics",
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

WATCH_RELATIVE_DIRS = (
    "Source",
    "Tests",
    "TestScripts",
)

WATCH_EXTENSIONS = {
    ".cs",
    ".csproj",
    ".json",
    ".ts",
    ".tsx",
    ".js",
    ".jsx",
    ".css",
    ".less",
    ".html",
    ".md",
    ".yml",
    ".yaml",
}

WATCH_IGNORED_PARTS = {
    ".git",
    ".cache",
    ".umi",
    ".umi-production",
    ".vs",
    ".venv",
    "bin",
    "obj",
    "dist",
    "node_modules",
    "TestResults",
    "tmp",
    "temp",
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
    try:
        os.kill(pid, 0)
        return True
    except (OSError, SystemError):
        return False


def stop_process_tree(pid: int | None) -> None:
    if not pid or not is_process_alive(pid):
        return

    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        return

    try:
        os.killpg(pid, signal.SIGTERM)
    except OSError:
        try:
            os.kill(pid, signal.SIGTERM)
        except OSError:
            return


def stop_tracked_process(name: str, pid_file: Path) -> None:
    pid = read_pid(pid_file)
    if pid and is_process_alive(pid):
        stop_process_tree(pid)
        info(f"V Stopped {name} (PID {pid})")
    elif pid:
        info(f"! {name} PID {pid} is no longer running")
    else:
        info(f"! {name} PID is not recorded")
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
    def __init__(self, delay_seconds: float) -> None:
        self.delay_seconds = delay_seconds
        self.deadline: float | None = None

    def changed(self, now: float) -> None:
        self.deadline = now + self.delay_seconds

    def ready(self, now: float) -> bool:
        return self.deadline is not None and now >= self.deadline

    def consume(self) -> bool:
        if self.deadline is None:
            return False
        self.deadline = None
        return True


class RestartBackoffPolicy:
    def __init__(self, max_failures: int, cooldown_seconds: float) -> None:
        self.max_failures = max_failures
        self.cooldown_seconds = cooldown_seconds
        self._failures: dict[str, int] = {}

    def next_delay(self, role: str, now: float) -> float:
        failures = self._failures.get(role, 0) + 1
        if failures > self.max_failures:
            self._failures[role] = 0
            return self.cooldown_seconds
        self._failures[role] = failures
        return 0

    def reset(self, role: str | None = None) -> None:
        if role is None:
            self._failures.clear()
        else:
            self._failures.pop(role, None)


def build_health_url(host: str, port: int, path: str) -> str:
    if not path.startswith("/"):
        path = "/" + path
    authority = host if port == 80 else f"{host}:{port}"
    return f"http://{authority}{path}"


def proxy_display_url(port: int) -> str:
    return "http://localhost" if port == 80 else f"http://localhost:{port}"


def normalize_base_url(base_url: str) -> str:
    return base_url.rstrip("/")


def proxy_target_for_path(path: str, backend_base_url: str, frontend_base_url: str) -> str:
    base = normalize_base_url(backend_base_url) if path.startswith(BACKEND_PREFIXES) else normalize_base_url(frontend_base_url)
    if not path.startswith("/"):
        path = "/" + path
    return base + path


def frontend_spa_fallback_path(path: str) -> str:
    parsed = urllib.parse.urlsplit(path)
    route_path = parsed.path or "/"
    if not route_path.startswith("/admin/") or route_path == "/admin/":
        return path
    if "." in route_path.rsplit("/", 1)[-1]:
        return path
    return "/admin/"


def prepare_config() -> None:
    config_dir = ROOT / "data" / "config"
    default_config_dir = ROOT / "Source" / "PuddingAgent" / "default-data" / "config"
    config_dir.mkdir(parents=True, exist_ok=True)
    for name in ("llm.providers.json", "system.json", "security.json", "connectors.json"):
        target = config_dir / name
        source = default_config_dir / name
        if not target.exists() and source.exists():
            shutil.copy2(source, target)
            info(f"i Created default config data/config/{name}")


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
        return True
    return value not in {"0", "false", "off", "disabled", "no"}


def watch_file_signature(path: Path) -> tuple[int, int] | None:
    try:
        stat = path.stat()
        return (stat.st_mtime_ns, stat.st_size)
    except OSError:
        return None


def iter_watch_files(root: Path, relative_dirs: tuple[str, ...] = WATCH_RELATIVE_DIRS):
    for relative_dir in relative_dirs:
        base = root / relative_dir
        if not base.exists():
            continue
        for path in base.rglob("*"):
            if not path.is_file():
                continue
            if any(part in WATCH_IGNORED_PARTS for part in path.parts):
                continue
            if path.suffix.lower() in WATCH_EXTENSIONS:
                yield path


def scan_watch_snapshot(root: Path) -> dict[str, tuple[int, int]]:
    snapshot: dict[str, tuple[int, int]] = {}
    for path in iter_watch_files(root):
        signature = watch_file_signature(path)
        if signature is not None:
            snapshot[str(path.relative_to(root))] = signature
    return snapshot


def snapshot_changed(previous: dict[str, tuple[int, int]], current: dict[str, tuple[int, int]]) -> bool:
    return previous != current


def copy_avatar_assets() -> None:
    source = ROOT / "Source" / "PuddingPlatform" / "wwwroot" / "assets" / "agent-avatars"
    target = ROOT / "Source" / "PuddingAgent" / "bin" / "Debug" / "net10.0" / "wwwroot" / "assets" / "agent-avatars"
    if not source.exists():
        return
    target.mkdir(parents=True, exist_ok=True)
    for item in source.iterdir():
        destination = target / item.name
        if item.is_dir():
            if destination.exists():
                shutil.rmtree(destination)
            shutil.copytree(item, destination)
        else:
            shutil.copy2(item, destination)


def open_log(path: Path):
    path.parent.mkdir(parents=True, exist_ok=True)
    return path.open("ab", buffering=0)


def popen_kwargs():
    if os.name == "nt":
        return {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP}
    return {"start_new_session": True}


def backend_build_command() -> list[str]:
    return [
        resolve_command("dotnet") or "dotnet",
        "build",
        "Source/PuddingAgent/PuddingAgent.csproj",
        "--nologo",
    ]


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
            "ASPNETCORE_URLS": f"http://127.0.0.1:{BACKEND_PORT}",
            "DOTNET_USE_POLLING_FILE_WATCHER": "1",
            "PUDDING_DATA_ROOT": str(ROOT / "data"),
            "Jwt__Key": "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!",
            "Jwt__Issuer": "pudding-platform",
            "Jwt__Audience": "pudding-admin",
            "Jwt__ExpiryHours": "8",
            "ConnectionStrings__Default": f"Data Source={ROOT / 'data' / 'pudding_platform.db'}",
            "PUDDING_LOG_LEVEL": "Debug",
            "Pudding__ControllerEndpoint": f"http://127.0.0.1:{BACKEND_PORT}",
            "Pudding__RuntimeFallbackEndpoint": f"http://127.0.0.1:{BACKEND_PORT}",
        }
    )
    return env


def start_backend() -> subprocess.Popen:
    env = backend_environment()
    build_log = open_log(BACKEND_OUT_LOG)
    build_err = open_log(BACKEND_ERR_LOG)
    build = subprocess.run(
        backend_build_command(),
        cwd=ROOT,
        env=env,
        stdout=build_log,
        stderr=build_err,
        check=False,
    )
    build_log.close()
    build_err.close()
    if build.returncode != 0:
        info(f"! Backend build failed with exit code {build.returncode}")
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
        [resolve_command("pnpm") or "pnpm", "run", "start:dev", "--", "--host", "127.0.0.1", "--port", str(FRONTEND_PORT)],
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
            f"http://127.0.0.1:{BACKEND_PORT}",
            "--frontend-url",
            f"http://127.0.0.1:{FRONTEND_PORT}",
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
    return {
        "supervisor": {
            "pid": read_pid(SUPERVISOR_PID_FILE),
            "alive": is_process_alive(read_pid(SUPERVISOR_PID_FILE)),
        },
        "backend": {
            "pid": read_pid(BACKEND_PID_FILE),
            "alive": is_process_alive(read_pid(BACKEND_PID_FILE)),
        },
        "frontend": {
            "pid": read_pid(FRONTEND_PID_FILE),
            "alive": is_process_alive(read_pid(FRONTEND_PID_FILE)),
        },
        "proxy": {
            "pid": read_pid(PROXY_PID_FILE),
            "alive": is_process_alive(read_pid(PROXY_PID_FILE)),
            "port": int(proxy_port) if proxy_port.isdigit() else None,
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
            lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
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
    stop_tracked_process("Supervisor", SUPERVISOR_PID_FILE)
    stop_tracked_process("Proxy", PROXY_PID_FILE)
    stop_tracked_process("Frontend", FRONTEND_PID_FILE)
    stop_tracked_process("Backend", BACKEND_PID_FILE)
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
    backend_url = f"http://127.0.0.1:{BACKEND_PORT}"
    frontend_url = f"http://127.0.0.1:{FRONTEND_PORT}"

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

    def proxy_http(self) -> None:
        self.close_connection = True
        request_path = self.path
        if self.command in ("GET", "HEAD") and not self.path.startswith(BACKEND_PREFIXES):
            request_path = frontend_spa_fallback_path(self.path)
        target = proxy_target_for_path(request_path, self.backend_url, self.frontend_url)
        body = self.read_body()
        headers = self.forward_headers()
        request = urllib.request.Request(target, data=body, headers=headers, method=self.command)

        try:
            with urllib.request.urlopen(request, timeout=300) as response:
                self.send_response(response.status, response.reason)
                for key, value in response.headers.items():
                    if key.lower() not in HOP_BY_HOP_HEADERS:
                        self.send_header(key, value)
                self.send_header("Connection", "close")
                self.end_headers()
                if self.command == "HEAD":
                    return
                while True:
                    chunk = response.read(64 * 1024)
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    self.wfile.flush()
        except urllib.error.HTTPError as exc:
            self.send_response(exc.code, exc.reason)
            for key, value in exc.headers.items():
                if key.lower() not in HOP_BY_HOP_HEADERS:
                    self.send_header(key, value)
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(exc.read())
        except Exception as exc:
            message = f"Proxy error for {target}: {exc}".encode("utf-8", errors="replace")
            self.send_response(502, "Bad Gateway")
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(message)))
            self.send_header("Connection", "close")
            self.end_headers()
            self.wfile.write(message)

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
        return headers

    def tunnel_websocket(self) -> None:
        target = proxy_target_for_path(self.path, self.backend_url, self.frontend_url)
        parsed = urllib.parse.urlparse(target)
        port = parsed.port or (443 if parsed.scheme == "https" else 80)
        if parsed.scheme != "http":
            self.send_error(502, "WebSocket proxy only supports http upstreams in dev mode")
            return

        upstream = socket.create_connection((parsed.hostname or "127.0.0.1", port), timeout=30)
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


def restart_child(role: str, processes: dict[str, subprocess.Popen], no_install: bool, proxy_port: int) -> None:
    info(f"! {role} exited; restarting")
    if role == "backend":
        processes[role] = start_backend()
    elif role == "frontend":
        processes[role] = start_frontend(no_install=True)
    elif role == "proxy":
        processes[role] = start_proxy(proxy_port)


def replace_child(role: str, processes: dict[str, subprocess.Popen], no_install: bool, proxy_port: int, reason: str) -> None:
    process = processes.get(role)
    if process and process.poll() is None:
        info(f"! Restarting {role} because {reason}")
        stop_process_tree(process.pid)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            stop_process_tree(process.pid)
    if role == "backend":
        processes[role] = start_backend()
    elif role == "frontend":
        processes[role] = start_frontend(no_install=True)
    elif role == "proxy":
        processes[role] = start_proxy(proxy_port)


def restart_for_file_changes(processes: dict[str, subprocess.Popen], no_install: bool, proxy_port: int) -> None:
    replace_child("backend", processes, no_install=True, proxy_port=proxy_port, reason="watched files changed")
    replace_child("frontend", processes, no_install=True, proxy_port=proxy_port, reason="watched files changed")


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


def run_supervisor(no_install: bool) -> None:
    ensure_run_dir()
    require_command("dotnet")
    require_command("pnpm")

    if not is_port_free(PROXY_HOST, BACKEND_PORT):
        fail(f"Backend port {BACKEND_PORT} is already in use.")
    if not is_port_free(PROXY_HOST, FRONTEND_PORT):
        fail(f"Frontend port {FRONTEND_PORT} is already in use.")

    proxy_port = choose_proxy_port(PROXY_HOST, PREFERRED_PROXY_PORT, FALLBACK_PROXY_PORT)
    prepare_config()
    copy_avatar_assets()
    write_pid(SUPERVISOR_PID_FILE, os.getpid())

    processes = {
        "backend": start_backend(),
        "frontend": start_frontend(no_install=no_install),
        "proxy": start_proxy(proxy_port),
    }
    health_url = build_health_url(PROXY_HOST, proxy_port, HEALTH_PATH)
    HEALTH_STATUS_FILE.unlink(missing_ok=True)
    health_stop = threading.Event()
    health_thread = threading.Thread(target=health_monitor_loop, args=(health_url, health_stop), daemon=True)
    health_thread.start()
    watch_snapshot = scan_watch_snapshot(ROOT)
    debouncer = ChangeDebouncer(WATCH_DEBOUNCE_SECONDS)
    restart_policy = RestartBackoffPolicy(RESTART_MAX_FAILURES, RESTART_COOLDOWN_SECONDS)

    proxy_url = proxy_display_url(proxy_port)
    info(
        "\nDevelopment environment started:\n"
        f"  Backend API  -> http://localhost:{BACKEND_PORT}\n"
        f"  Frontend Dev -> http://localhost:{FRONTEND_PORT}\n"
        f"  Proxy entry  -> {proxy_url}/admin/user/login\n\n"
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
                    delay = restart_policy.next_delay(role, time.monotonic())
                    if delay > 0:
                        info(f"! {role} failed more than {RESTART_MAX_FAILURES} times; cooling down for {delay:g}s")
                        if health_stop.wait(delay):
                            break
                    restart_child(role, processes, no_install=True, proxy_port=proxy_port)
            if guard_enabled:
                current_snapshot = scan_watch_snapshot(ROOT)
                if snapshot_changed(watch_snapshot, current_snapshot):
                    watch_snapshot = current_snapshot
                    debouncer.changed(time.monotonic())
                    info(f"! Watched files changed; restarting backend/frontend after {WATCH_DEBOUNCE_SECONDS}s of quiet")
                if debouncer.ready(time.monotonic()) and debouncer.consume():
                    restart_for_file_changes(processes, no_install=True, proxy_port=proxy_port)
                    restart_policy.reset()
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


def start_supervisor(no_install: bool) -> None:
    supervisor_pid = read_pid(SUPERVISOR_PID_FILE)
    if supervisor_pid and is_process_alive(supervisor_pid):
        fail(f"Development supervisor is already running (PID {supervisor_pid}). Use --status, --restart, or --down.")

    proc = subprocess.Popen(
        [
            sys.executable,
            str(Path(__file__).resolve()),
            "--supervisor",
            *(["--no-install"] if no_install else []),
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


def start_all(no_install: bool) -> None:
    ensure_run_dir()
    start_supervisor(no_install=no_install)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Start Pudding Agent local development services.")
    parser.add_argument("--down", action="store_true", help="Stop tracked development processes.")
    parser.add_argument(
        "--logs",
        nargs="?",
        const=0,
        type=int,
        metavar="LINES",
        help="Follow backend/frontend/proxy logs; optionally print the last LINES first.",
    )
    parser.add_argument("--status", action="store_true", help="Show tracked process status.")
    parser.add_argument("--restart", action="store_true", help="Stop and start development processes.")
    parser.add_argument("--no-install", action="store_true", help="Skip frontend dependency installation.")
    parser.add_argument("--guard-on", action="store_true", help="Enable supervisor auto-restart and file-change restarts.")
    parser.add_argument("--guard-off", action="store_true", help="Disable supervisor auto-restart and file-change restarts.")
    parser.add_argument("--proxy", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--supervisor", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--proxy-host", default=PROXY_HOST, help=argparse.SUPPRESS)
    parser.add_argument("--proxy-port", type=int, default=PREFERRED_PROXY_PORT, help=argparse.SUPPRESS)
    parser.add_argument("--backend-url", default=f"http://127.0.0.1:{BACKEND_PORT}", help=argparse.SUPPRESS)
    parser.add_argument("--frontend-url", default=f"http://127.0.0.1:{FRONTEND_PORT}", help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    ensure_run_dir()

    if args.proxy:
        run_proxy(args.proxy_host, args.proxy_port, args.backend_url, args.frontend_url)
        return 0

    if args.supervisor:
        run_supervisor(no_install=args.no_install)
        return 0

    if args.guard_on and args.guard_off:
        fail("Use only one of --guard-on or --guard-off.")

    if args.guard_on:
        enable_guard()
        return 0

    if args.guard_off:
        disable_guard()
        return 0

    if args.down or args.restart:
        stop_all()
        if args.down and not args.restart:
            return 0

    if args.status:
        show_status()
        return 0

    if args.logs is not None:
        follow_logs(args.logs)
        return 0

    start_all(no_install=args.no_install)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
