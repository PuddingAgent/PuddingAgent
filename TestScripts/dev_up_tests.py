import contextlib
import io
import importlib.util
import json
import socket
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
DEV_UP = ROOT / "dev-up.py"


def load_dev_up_module():
    spec = importlib.util.spec_from_file_location("dev_up", DEV_UP)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


class DevUpProxyTests(unittest.TestCase):
    def test_proxy_target_routes_api_and_assets_to_expected_servers(self):
        dev_up = load_dev_up_module()

        self.assertEqual(
            "http://127.0.0.1:5000/api/sessions",
            dev_up.proxy_target_for_path("/api/sessions", "http://127.0.0.1:5000", "http://127.0.0.1:8000"),
        )
        self.assertEqual(
            "http://127.0.0.1:5000/swagger/index.html",
            dev_up.proxy_target_for_path("/swagger/index.html", "http://127.0.0.1:5000", "http://127.0.0.1:8000"),
        )
        self.assertEqual(
            "http://127.0.0.1:5000/health",
            dev_up.proxy_target_for_path("/health", "http://127.0.0.1:5000", "http://127.0.0.1:8000"),
        )
        self.assertEqual(
            "http://127.0.0.1:8000/admin/user/login",
            dev_up.proxy_target_for_path("/admin/user/login", "http://127.0.0.1:5000", "http://127.0.0.1:8000"),
        )

    def test_frontend_spa_fallback_rewrites_admin_deep_links_only(self):
        dev_up = load_dev_up_module()

        self.assertEqual("/admin/", dev_up.frontend_spa_fallback_path("/admin/bootstrap"))
        self.assertEqual("/admin/", dev_up.frontend_spa_fallback_path("/admin/workspace/abc?tab=agents"))
        self.assertEqual("/admin/assets/app.js", dev_up.frontend_spa_fallback_path("/admin/assets/app.js"))
        self.assertEqual("/api/bootstrap/status", dev_up.frontend_spa_fallback_path("/api/bootstrap/status"))

    def test_proxy_diagnostics_identifies_session_stream_and_replay_paths(self):
        dev_up = load_dev_up_module()

        self.assertTrue(dev_up.is_session_events_stream_path("/api/sessions/session-1/events/stream"))
        self.assertTrue(dev_up.is_session_events_stream_path("/api/sessions/session-1/events/stream?x=1"))
        self.assertTrue(dev_up.is_session_replay_path("/api/sessions/session-1/replay?from=42&limit=50"))
        self.assertTrue(dev_up.should_log_proxy_diagnostics("/api/sessions/session-1/events/stream"))
        self.assertTrue(dev_up.should_log_proxy_diagnostics("/api/sessions/session-1/replay?from=42"))
        self.assertFalse(dev_up.should_log_proxy_diagnostics("/api/sessions/session-1/state"))

    def test_proxy_diagnostic_jsonl_writes_under_data_logs_diagnostics(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            log_dir = Path(temp_dir) / "data" / "logs"

            class FakeDateTime:
                @staticmethod
                def now():
                    return datetime(2026, 5, 31, 14, 30, 0)

            with patch.object(dev_up, "DATA_LOG_DIR", log_dir), patch.object(dev_up, "datetime", FakeDateTime):
                dev_up.write_proxy_diagnostic_event({
                    "stage": "proxy.request.started",
                    "method": "GET",
                    "path": "/api/sessions/session-1/events/stream",
                    "sessionId": "session-1",
                })

            path = log_dir / "diagnostics" / "proxy" / "20260531.jsonl"
            self.assertTrue(path.exists())
            event = json.loads(path.read_text(encoding="utf-8").strip())
            self.assertEqual(1, event["schemaVersion"])
            self.assertEqual("proxy", event["recordKind"])
            self.assertEqual("proxy.request.started", event["stage"])
            self.assertEqual("session-1", event["sessionId"])

    def test_proxy_detects_event_stream_content_type(self):
        dev_up = load_dev_up_module()

        self.assertTrue(dev_up.is_event_stream_content_type("text/event-stream"))
        self.assertTrue(dev_up.is_event_stream_content_type("text/event-stream; charset=utf-8"))
        self.assertFalse(dev_up.is_event_stream_content_type("application/json"))
        self.assertFalse(dev_up.is_event_stream_content_type(None))

    def test_choose_proxy_port_falls_back_when_preferred_port_is_in_use(self):
        dev_up = load_dev_up_module()

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            occupied_port = listener.getsockname()[1]

            self.assertEqual(
                18088,
                dev_up.choose_proxy_port("127.0.0.1", occupied_port, 18088),
            )

    def test_choose_proxy_port_exits_when_strict_port_is_in_use(self):
        dev_up = load_dev_up_module()

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            occupied_port = listener.getsockname()[1]

            with self.assertRaises(SystemExit):
                dev_up.choose_proxy_port("127.0.0.1", occupied_port, None)

    def test_wait_until_port_listening_detects_ready_service(self):
        dev_up = load_dev_up_module()

        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen()
            port = listener.getsockname()[1]

            self.assertTrue(
                dev_up.wait_until_port_listening(
                    "127.0.0.1",
                    port,
                    timeout_seconds=0.1,
                    poll_interval_seconds=0,
                )
            )

    def test_wait_until_port_listening_stops_when_child_exits(self):
        dev_up = load_dev_up_module()

        class ExitedProcess:
            @staticmethod
            def poll():
                return 17

        with patch.object(dev_up.socket, "create_connection") as connect:
            ready = dev_up.wait_until_port_listening(
                "127.0.0.1",
                5100,
                process=ExitedProcess(),
                timeout_seconds=30,
                poll_interval_seconds=0,
            )

        self.assertFalse(ready)
        connect.assert_not_called()

    def test_resolve_command_prefers_windows_cmd_shim(self):
        dev_up = load_dev_up_module()

        def fake_which(name):
            values = {
                "pnpm.cmd": r"C:\tools\pnpm.cmd",
                "pnpm": r"C:\tools\pnpm",
            }
            return values.get(name)

        with patch.object(dev_up.os, "name", "nt"), patch.object(dev_up.shutil, "which", side_effect=fake_which):
            self.assertEqual(r"C:\tools\pnpm.cmd", dev_up.resolve_command("pnpm"))

    def test_backend_command_runs_compiled_app_directly(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "resolve_command", return_value="dotnet"):
            command = dev_up.backend_command()

        self.assertEqual("dotnet", command[0])
        self.assertTrue(command[1].endswith("PuddingAgent.dll"))
        self.assertNotIn("run", command)
        self.assertNotIn("watch", command)

    def test_backend_build_command_builds_backend_project(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "resolve_command", return_value="dotnet"):
            command = dev_up.backend_build_command()

        self.assertEqual(
            ["dotnet", "build", "Source/PuddingAgent/PuddingAgent.csproj", "--nologo"],
            command,
        )

    def test_backend_build_command_can_target_staging_output(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "resolve_command", return_value="dotnet"):
            command = dev_up.backend_build_command(output_dir=Path(r"C:\staging\task"))

        self.assertIn("--no-restore", command)
        self.assertIn("-p:OutDir=C:\\staging\\task\\", command)

    def test_codex_service_command_runs_compiled_service_directly(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "resolve_command", return_value="dotnet"):
            command = dev_up.codex_service_command()

        self.assertEqual("dotnet", command[0])
        self.assertTrue(command[1].endswith("PuddingCodexService.dll"))

    def test_codex_service_environment_enforces_yolo_mode(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "resolve_command", return_value="codex"):
            environment = dev_up.codex_service_environment()

        self.assertEqual("danger-full-access", environment["CodexService__TaskSandbox"])
        self.assertEqual("never", environment["CodexService__TaskApprovalPolicy"])

    def test_start_proxy_binds_publicly_while_upstreams_stay_loopback(self):
        dev_up = load_dev_up_module()

        popen_calls = []

        class FakeProcess:
            pid = 4242

        def fake_popen(command, **kwargs):
            popen_calls.append((command, kwargs))
            return FakeProcess()

        with tempfile.TemporaryDirectory() as temp_dir:
            run_dir = Path(temp_dir)
            with (
                patch.object(dev_up.subprocess, "Popen", side_effect=fake_popen),
                patch.object(dev_up, "PROXY_PID_FILE", run_dir / "proxy.pid"),
                patch.object(dev_up, "PROXY_PORT_FILE", run_dir / "proxy.port"),
                patch.object(dev_up, "open_log", return_value=io.StringIO()),
                patch.object(dev_up, "popen_kwargs", return_value={}),
                patch.object(dev_up, "info"),
            ):
                dev_up.start_proxy(80)

        command = popen_calls[0][0]

        self.assertEqual("0.0.0.0", command[command.index("--proxy-host") + 1])
        self.assertEqual("80", command[command.index("--proxy-port") + 1])
        self.assertEqual("http://127.0.0.1:5000", command[command.index("--backend-url") + 1])
        self.assertEqual("http://127.0.0.1:8000", command[command.index("--frontend-url") + 1])


class DevUpSupervisorTests(unittest.TestCase):
    def test_status_includes_independent_codex_service(self):
        dev_up = load_dev_up_module()

        lines = dev_up.format_status_lines({
            "backend": {"alive": True, "pid": 10},
            "codex": {"alive": True, "pid": 20},
            "frontend": {"alive": True, "pid": 30},
            "proxy": {"alive": True, "pid": 40, "port": 80},
        })

        self.assertEqual(5, len(lines))
        self.assertEqual("Supervisor: stopped", lines[0])
        self.assertIn("Codex MCP", lines[2])
        self.assertIn("20", lines[2])

    def test_stop_all_stops_supervisor_before_tracked_children(self):
        dev_up = load_dev_up_module()
        calls = []

        with tempfile.TemporaryDirectory() as temp_dir:
            run_dir = Path(temp_dir)
            with (
                patch.object(dev_up, "SUPERVISOR_PID_FILE", run_dir / "supervisor.pid"),
                patch.object(dev_up, "BACKEND_PID_FILE", run_dir / "backend.pid"),
                patch.object(dev_up, "CODEX_SERVICE_PID_FILE", run_dir / "codex.pid"),
                patch.object(dev_up, "FRONTEND_PID_FILE", run_dir / "frontend.pid"),
                patch.object(dev_up, "PROXY_PID_FILE", run_dir / "proxy.pid"),
                patch.object(dev_up, "PROXY_PORT_FILE", run_dir / "proxy.port"),
                patch.object(dev_up, "BACKEND_RESTART_REQUEST_FILE", run_dir / "restart.json"),
                patch.object(
                    dev_up,
                    "stop_tracked_process",
                    side_effect=lambda name, *_args, **_kwargs: calls.append(name),
                ),
                patch.object(dev_up, "find_legacy_supervisor_pid", return_value=None),
            ):
                dev_up.stop_all()

        self.assertEqual(
            ["Supervisor", "Backend", "Codex Service", "Frontend", "Proxy"],
            calls,
        )

    def test_stop_all_stops_legacy_supervisor_discovered_from_child(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            run_dir = Path(temp_dir)
            with (
                patch.object(dev_up, "SUPERVISOR_PID_FILE", run_dir / "supervisor.pid"),
                patch.object(dev_up, "BACKEND_PID_FILE", run_dir / "backend.pid"),
                patch.object(dev_up, "CODEX_SERVICE_PID_FILE", run_dir / "codex.pid"),
                patch.object(dev_up, "FRONTEND_PID_FILE", run_dir / "frontend.pid"),
                patch.object(dev_up, "PROXY_PID_FILE", run_dir / "proxy.pid"),
                patch.object(dev_up, "PROXY_PORT_FILE", run_dir / "proxy.port"),
                patch.object(dev_up, "BACKEND_RESTART_REQUEST_FILE", run_dir / "restart.json"),
                patch.object(dev_up, "find_legacy_supervisor_pid", return_value=777),
                patch.object(dev_up, "is_process_alive", return_value=True),
                patch.object(dev_up, "stop_process_tree", return_value=True) as stop_tree,
                patch.object(dev_up, "stop_tracked_process"),
                patch.object(dev_up, "info"),
            ):
                dev_up.stop_all()

        stop_tree.assert_called_once_with(777)

    def test_legacy_supervisor_requires_dev_up_parent_command(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            backend_pid_file = Path(temp_dir) / "backend.pid"
            backend_pid_file.write_text("123", encoding="ascii")
            with (
                patch.object(dev_up, "BACKEND_PID_FILE", backend_pid_file),
                patch.object(dev_up, "CODEX_SERVICE_PID_FILE", Path(temp_dir) / "codex.pid"),
                patch.object(dev_up, "FRONTEND_PID_FILE", Path(temp_dir) / "frontend.pid"),
                patch.object(dev_up, "PROXY_PID_FILE", Path(temp_dir) / "proxy.pid"),
                patch.object(dev_up, "is_process_alive", return_value=True),
                patch.object(
                    dev_up,
                    "process_parent_and_command",
                    return_value=(456, "python other-worker.py"),
                ) as parent_lookup,
            ):
                self.assertIsNone(dev_up.find_legacy_supervisor_pid())

                parent_lookup.return_value = (
                    456,
                    r"python E:\github\AgentNetworkPlan\PuddingAgent\dev-up.py",
                )
                self.assertEqual(456, dev_up.find_legacy_supervisor_pid())

    def test_supervised_backend_skips_second_build_after_rebuild(self):
        dev_up = load_dev_up_module()

        with patch.object(dev_up, "start_backend") as start_backend:
            dev_up.start_supervised_backend(backend_prebuilt=True)

        start_backend.assert_called_once_with(
            assembly_path=dev_up.default_backend_assembly_path()
        )

    def test_pid_cleanup_does_not_remove_new_owner(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            pid_file = Path(temp_dir) / "supervisor.pid"
            pid_file.write_text("222", encoding="ascii")
            dev_up.unlink_pid_if_owned(pid_file, 111)
            self.assertTrue(pid_file.exists())
            dev_up.unlink_pid_if_owned(pid_file, 222)
            self.assertFalse(pid_file.exists())

    def test_backend_restart_request_waits_until_not_before(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            request_path = Path(temp_dir) / "backend.restart.request.json"
            request_path.write_text(json.dumps({
                "requestId": "11111111111111111111111111111111",
                "taskId": "22222222222222222222222222222222",
                "requestedAtUtc": "2026-07-26T10:00:00+00:00",
                "notBeforeUtc": "2026-07-26T10:00:10+00:00",
            }), encoding="utf-8")
            with patch.object(dev_up, "BACKEND_RESTART_REQUEST_FILE", request_path):
                self.assertIsNone(dev_up.read_due_backend_restart_request(
                    datetime(2026, 7, 26, 10, 0, 9, tzinfo=timezone.utc)))
                due = dev_up.read_due_backend_restart_request(
                    datetime(2026, 7, 26, 10, 0, 10, tzinfo=timezone.utc))

        self.assertEqual("11111111111111111111111111111111", due["requestId"])

    def test_failed_staged_build_does_not_stop_current_backend(self):
        dev_up = load_dev_up_module()

        class FakeProcess:
            pid = 1234

            @staticmethod
            def poll():
                return None

        request = {
            "requestId": "11111111111111111111111111111111",
            "taskId": "22222222222222222222222222222222",
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            run_dir = Path(temp_dir)
            with (
                patch.object(dev_up, "RUN_DIR", run_dir),
                patch.object(dev_up, "BACKEND_STAGING_DIR", run_dir / "staging"),
                patch.object(dev_up, "run_backend_build", return_value=1),
                patch.object(dev_up, "stop_process_tree") as stop_process,
                patch.object(dev_up, "info"),
            ):
                dev_up.perform_backend_restart({"backend": FakeProcess()}, request)

            result = json.loads((run_dir / "backend.restart.result.11111111111111111111111111111111.json")
                                .read_text(encoding="utf-8"))

        stop_process.assert_not_called()
        self.assertEqual("build_failed", result["status"])

    def test_info_writes_launcher_log_under_data_logs(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            log_dir = Path(temp_dir) / "data" / "logs"
            log_path = log_dir / "dev-up-2026-05-24.log"

            class FakeDateTime:
                @staticmethod
                def now():
                    return datetime(2026, 5, 24, 10, 30, 0)

            with patch.object(dev_up, "DATA_LOG_DIR", log_dir), patch.object(dev_up, "datetime", FakeDateTime):
                with contextlib.redirect_stdout(io.StringIO()):
                    dev_up.info("launcher ready")

            content = log_path.read_text(encoding="utf-8")

        self.assertIn("launcher ready", content)
        self.assertIn("pid=", content)

    def test_launcher_log_path_uses_current_date(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            log_dir = Path(temp_dir) / "data" / "logs"

            class FakeDateTime:
                @staticmethod
                def now():
                    return datetime(2026, 5, 24, 23, 59, 0)

            with patch.object(dev_up, "DATA_LOG_DIR", log_dir), patch.object(dev_up, "datetime", FakeDateTime):
                self.assertEqual(log_dir / "dev-up-2026-05-24.log", dev_up.launcher_log_path())

    def test_logs_without_line_argument_defaults_to_tail_lines(self):
        dev_up = load_dev_up_module()

        args = dev_up.parse_args(["--logs"])

        self.assertEqual(dev_up.DEFAULT_LOG_TAIL_LINES, args.logs)

    def test_clear_argument_is_exclusive(self):
        dev_up = load_dev_up_module()

        self.assertTrue(dev_up.parse_args(["--clear"]).clear)
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            dev_up.parse_args(["--clear", "--status"])

    def test_clear_generated_files_removes_only_allowlisted_repository_paths(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            clear_targets = (
                root / "tmp",
                root / ".tmp",
                root / ".tmp-build",
                root / ".tmp-test-out",
                root / ".codex-out",
                root / "data" / "logs",
            )
            for target in clear_targets:
                target.mkdir(parents=True)
                (target / "generated.bin").write_bytes(b"generated")

            preserved = (
                root / "data" / "agents" / "manifest.json",
                root / "Source" / "PuddingAgent" / "bin" / "PuddingAgent.dll",
                root / "publish" / "app" / "PuddingAgent.dll",
                root / "Source" / "PuddingPlatformAdmin" / "node_modules" / "package.json",
            )
            for path in preserved:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("keep", encoding="utf-8")

            with patch.object(dev_up, "write_stdout"):
                dev_up.clear_generated_files(root, require_stopped=False)

            for target in clear_targets:
                self.assertFalse(target.exists(), target)
            for path in preserved:
                self.assertTrue(path.exists(), path)

    def test_clear_generated_files_refuses_while_a_managed_process_is_running(self):
        dev_up = load_dev_up_module()

        with (
            patch.object(dev_up, "status_snapshot", return_value={"backend": {"alive": True}}),
            patch.object(dev_up, "fail", side_effect=SystemExit) as fail,
            self.assertRaises(SystemExit),
        ):
            dev_up.clear_generated_files()

        self.assertIn("Run --down first", fail.call_args.args[0])

    def test_main_clear_does_not_create_run_dir_or_start_services(self):
        dev_up = load_dev_up_module()

        with (
            patch.object(dev_up, "clear_generated_files") as clear,
            patch.object(dev_up, "ensure_run_dir") as ensure_run_dir,
            patch.object(dev_up, "start_all") as start_all,
        ):
            self.assertEqual(0, dev_up.main(["--clear"]))

        clear.assert_called_once_with()
        ensure_run_dir.assert_not_called()
        start_all.assert_not_called()

    def test_tail_file_lines_reads_last_lines_from_large_file(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "large.log"
            path.write_text("\n".join(f"line-{index}" for index in range(200)) + "\n", encoding="utf-8")

            lines = dev_up.tail_file_lines(path, 3, block_size=32)

        self.assertEqual(["line-197", "line-198", "line-199"], lines)

    def test_open_log_rotates_large_dev_log_before_append(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "backend.out.log"
            path.write_bytes(b"x" * 12)
            path.with_name("backend.out.log.1").write_bytes(b"old-1")

            with (
                patch.object(dev_up, "DEV_LOG_ROTATE_MAX_BYTES", 10),
                patch.object(dev_up, "DEV_LOG_ROTATE_BACKUPS", 2),
            ):
                handle = dev_up.open_log(path)
                handle.write(b"new\n")
                handle.close()

            self.assertEqual(b"new\n", path.read_bytes())
            self.assertEqual(b"x" * 12, path.with_name("backend.out.log.1").read_bytes())
            self.assertEqual(b"old-1", path.with_name("backend.out.log.2").read_bytes())

    def test_write_stdout_replaces_unencodable_characters(self):
        dev_up = load_dev_up_module()

        class GbkStdout:
            encoding = "gbk"

            def __init__(self):
                self.value = ""

            def write(self, text):
                text.encode(self.encoding)
                self.value += text

            def flush(self):
                pass

        stream = GbkStdout()

        with patch.object(dev_up.sys, "stdout", stream):
            dev_up.write_stdout("bad \ufffd char")

        self.assertEqual("bad ? char", stream.value)

    def test_supervised_roles_restart_unless_supervisor_is_stopping(self):
        dev_up = load_dev_up_module()

        self.assertTrue(dev_up.should_restart_role("backend", exit_code=1, stopping=False))
        self.assertTrue(dev_up.should_restart_role("frontend", exit_code=0, stopping=False))
        self.assertTrue(dev_up.should_restart_role("proxy", exit_code=1, stopping=False))
        self.assertFalse(dev_up.should_restart_role("backend", exit_code=1, stopping=True))

    def test_status_line_reports_missing_supervisor_and_running_children(self):
        dev_up = load_dev_up_module()
        snapshot = {
            "supervisor": {"pid": None, "alive": False},
            "backend": {"pid": 101, "alive": True},
            "frontend": {"pid": None, "alive": False},
            "proxy": {"pid": 303, "alive": True, "port": 8088},
            "guard": {"enabled": True},
        }

        self.assertEqual(
            [
                "Supervisor: stopped",
                "Backend   : running (PID 101)",
                "Codex MCP : stopped",
                "Frontend  : stopped",
                "Proxy     : running (PID 303) on http://localhost:8088",
            ],
            dev_up.format_status_lines(snapshot),
        )

    def test_health_status_line_reports_last_http_status(self):
        dev_up = load_dev_up_module()
        snapshot = {
            "supervisor": {"pid": 10, "alive": True},
            "backend": {"pid": 101, "alive": True},
            "frontend": {"pid": 202, "alive": True},
            "proxy": {"pid": 303, "alive": True, "port": 8088},
            "guard": {"enabled": False},
            "health": {
                "url": "http://localhost:8088/health",
                "status_code": 404,
                "ok": False,
                "checked_at": "2026-05-24T20:00:00Z",
            },
        }

        self.assertEqual(
            "Health    : HTTP 404 from http://localhost:8088/health at 2026-05-24T20:00:00Z",
            dev_up.format_status_lines(snapshot)[-1],
        )

    def test_build_health_url_uses_proxy_port_and_default_path(self):
        dev_up = load_dev_up_module()

        self.assertEqual(
            "http://127.0.0.1:8088/health",
            dev_up.build_health_url("127.0.0.1", 8088, "/health"),
        )
        self.assertEqual(
            "http://127.0.0.1/health",
            dev_up.build_health_url("127.0.0.1", 80, "/health"),
        )

    def test_debounce_deadline_resets_on_each_change(self):
        dev_up = load_dev_up_module()
        debouncer = dev_up.ChangeDebouncer(delay_seconds=5)

        self.assertIsNone(debouncer.changed(now=10.0))
        self.assertEqual(15.0, debouncer.deadline)
        self.assertIsNone(debouncer.changed(now=13.0))
        self.assertEqual(18.0, debouncer.deadline)
        self.assertFalse(debouncer.ready(now=17.9))
        self.assertTrue(debouncer.ready(now=18.0))
        self.assertTrue(debouncer.consume())
        self.assertIsNone(debouncer.deadline)

    def test_restart_policy_waits_after_five_failures_then_allows_retry(self):
        dev_up = load_dev_up_module()
        policy = dev_up.RestartBackoffPolicy(max_failures=5, cooldown_seconds=5)

        self.assertEqual(0, policy.next_delay("backend", now=0))
        self.assertEqual(0, policy.next_delay("backend", now=1))
        self.assertEqual(0, policy.next_delay("backend", now=2))
        self.assertEqual(0, policy.next_delay("backend", now=3))
        self.assertEqual(0, policy.next_delay("backend", now=4))
        self.assertEqual(5, policy.next_delay("backend", now=5))
        self.assertEqual(0, policy.next_delay("backend", now=10))

    def test_rapid_restart_limiter_stops_fourth_attempt_inside_window(self):
        dev_up = load_dev_up_module()
        limiter = dev_up.RapidRestartLimiter(max_restarts=3, window_seconds=30)

        self.assertTrue(limiter.allow("frontend", now=0))
        self.assertTrue(limiter.allow("frontend", now=5))
        self.assertTrue(limiter.allow("frontend", now=10))
        self.assertFalse(limiter.allow("frontend", now=15))
        self.assertTrue(limiter.allow("frontend", now=31))

    def test_auto_yolo_worker_starts_before_blocking_supervisor(self):
        dev_up = load_dev_up_module()

        with (
            patch.object(dev_up, "stop_all"),
            patch.object(dev_up, "start_all") as start_all,
            patch.object(dev_up.threading, "Thread") as thread_factory,
        ):
            worker = thread_factory.return_value
            self.assertEqual(0, dev_up.main(["--restart", "--auto-yolo"]))

        worker.start.assert_called_once_with()
        start_all.assert_called_once_with(
            no_install=False,
            frontend_only=False,
            backend_prebuilt=False,
        )

    def test_rebuild_marks_backend_prebuilt_for_supervisor(self):
        dev_up = load_dev_up_module()

        with (
            patch.object(dev_up, "stop_all"),
            patch.object(dev_up, "run_backend_build", return_value=0) as build,
            patch.object(dev_up, "start_all") as start_all,
        ):
            self.assertEqual(0, dev_up.main(["--rebuild", "--restart"]))

        build.assert_called_once_with(full_rebuild=True)
        start_all.assert_called_once_with(
            no_install=False,
            frontend_only=False,
            backend_prebuilt=True,
        )

    def test_auto_yolo_uses_repository_signal_without_enqueuing_agent_message(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            checkpoint = root / "checkpoint.json"
            checkpoint.write_text('{"version":1}', encoding="utf-8-sig")
            signal = root / "yolo.signal"
            with (
                patch.object(dev_up, "ROOT", root),
                patch.object(dev_up, "YOLO_SIGNAL_FILE", signal),
                patch.object(dev_up, "PROXY_PORT_FILE", root / "missing-proxy.port"),
                patch.object(
                    dev_up,
                    "probe_health",
                    return_value={"ok": True, "status_code": 200},
                ),
                patch.object(dev_up.urllib.request, "urlopen") as urlopen,
                patch.object(dev_up, "info"),
            ):
                dev_up.do_auto_yolo(dev_up.parse_args(["--auto-yolo"]))

            signal_payload = json.loads(signal.read_text(encoding="utf-8"))
            updated_checkpoint = json.loads(checkpoint.read_text(encoding="utf-8"))

        self.assertEqual("dev-up.py --auto-yolo", signal_payload["source"])
        self.assertEqual("admin", signal_payload["userId"])
        self.assertTrue(updated_checkpoint["auto_yolo"])
        self.assertEqual("restarted", updated_checkpoint["status"])
        urlopen.assert_not_called()

    def test_watch_snapshot_ignores_frontend_generated_umi_files(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source_file = root / "Source" / "PuddingPlatformAdmin" / "src" / "pages" / "index.tsx"
            generated_file = root / "Source" / "PuddingPlatformAdmin" / "src" / ".umi" / "core" / "routes.ts"
            source_file.parent.mkdir(parents=True)
            generated_file.parent.mkdir(parents=True)
            source_file.write_text("export default null;\n", encoding="utf-8")
            generated_file.write_text("export const routes = [];\n", encoding="utf-8")

            snapshot = dev_up.scan_watch_snapshot(root)

        self.assertIn("Source\\PuddingPlatformAdmin\\src\\pages\\index.tsx", snapshot)
        self.assertNotIn("Source\\PuddingPlatformAdmin\\src\\.umi\\core\\routes.ts", snapshot)


if __name__ == "__main__":
    unittest.main()
