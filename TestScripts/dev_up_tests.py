import contextlib
import io
import importlib.util
import socket
import tempfile
import unittest
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


class DevUpSupervisorTests(unittest.TestCase):
    def test_info_writes_launcher_log_under_data_logs(self):
        dev_up = load_dev_up_module()

        with tempfile.TemporaryDirectory() as temp_dir:
            log_path = Path(temp_dir) / "data" / "logs" / "dev-up.log"
            with patch.object(dev_up, "DATA_LOG_DIR", log_path.parent), patch.object(dev_up, "DEV_UP_LOG", log_path):
                with contextlib.redirect_stdout(io.StringIO()):
                    dev_up.info("launcher ready")

            content = log_path.read_text(encoding="utf-8")

        self.assertIn("launcher ready", content)
        self.assertIn("pid=", content)

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
                "Guard     : enabled",
                "Backend   : running (PID 101)",
                "Frontend  : stopped",
                "Proxy     : running (PID 303) on http://localhost:8088",
                "Health    : pending",
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
