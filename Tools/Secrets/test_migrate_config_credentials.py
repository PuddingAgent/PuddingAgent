import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

SCRIPT = pathlib.Path(__file__).with_name("migrate_config_credentials.py").resolve()
spec = importlib.util.spec_from_file_location("migration", SCRIPT)
migration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(migration)


class MigrationTests(unittest.TestCase):
    def setUp(self):
        scratch = pathlib.Path(".work/keyvault-migration/tests")
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.root = pathlib.Path(self.temporary.name).resolve()
        self.data = self.root / "data"
        (self.data / "config/voice").mkdir(parents=True)
        self.voice = self.data / "config/voice/providers.json"
        self.voice.write_text('{"providers":[{"apiKey":"synthetic-voice-key"}]}', encoding="utf-8")
        self.secrets = {}
        self.broken = False
        test = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def respond(self, value):
                payload = json.dumps(value).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if self.path == "/api/login/account":
                    self.respond(dict(status="ok", token="synthetic-test-jwt"))
                elif self.path == "/api/keyvault/secrets":
                    identifier = str(len(test.secrets) + 1)
                    test.secrets[identifier] = dict(keyVaultId=identifier, **body)
                    self.respond({key: value for key, value in test.secrets[identifier].items() if key != "value"})
                elif self.path == "/api/keyvault/inject":
                    value = body["text"]
                    if not test.broken:
                        for secret in test.secrets.values():
                            value = value.replace("{{vault:" + secret["name"] + "}}", secret["value"])
                    self.respond(dict(text=value))

            def do_GET(self):
                if urlsplit(self.path).path == "/api/keyvault/secrets":
                    self.respond([{key: value for key, value in item.items() if key != "value"} for item in test.secrets.values()])
                else:
                    self.respond(test.secrets[urlsplit(self.path).path.rsplit("/", 1)[1]])

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.temporary.cleanup()

    def run_migration(self, stage, *extra):
        return subprocess.run([sys.executable, "-B", str(SCRIPT), "--data-root", str(self.data),
            "--base-url", "http://127.0.0.1:" + str(self.server.server_port), "--stage", stage,
            "--evidence-dir", str(self.root / "evidence"), *extra],
            input=json.dumps(dict(username="test-admin", password="test-password")),
            capture_output=True, text=True, encoding="utf-8")

    def test_voice_roundtrip_is_idempotent_and_recovery_is_encrypted(self):
        original = self.voice.read_bytes()
        first = self.run_migration("voice")
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertNotIn(b"synthetic-voice-key", self.voice.read_bytes())
        self.assertIn(b"{{vault:", self.voice.read_bytes())
        recovery = next((self.root / "evidence").glob("*.dpapi"))
        self.assertNotIn(b"synthetic-voice-key", recovery.read_bytes())
        import base64
        restored = migration.dpapi("unprotect", base64.b64encode(recovery.read_bytes()).decode(), "PuddingAgent.MigrationRecovery.v1")
        self.assertEqual(restored.encode(), original)
        second = self.run_migration("voice")
        self.assertEqual(second.returncode, 0, second.stderr)
        self.assertEqual(len(self.secrets), 1)

    def test_unresolved_reference_preserves_original_file(self):
        original = self.voice.read_bytes()
        self.broken = True
        result = self.run_migration("voice")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.voice.read_bytes(), original)
        self.assertNotIn("synthetic-voice-key", result.stdout + result.stderr)

    def test_bootstrap_cache_and_historical_backup_cleanup(self):
        system = self.data / "config/system.json"
        system.write_text(json.dumps(dict(desktop=dict(core=dict(controlToken="synthetic-control-token")), Jwt=dict(Key="synthetic-jwt-key"))), encoding="utf-8")
        historical = self.data / "config/voice/providers.before-test.json"
        historical.write_text("broken-json-with-synthetic-old-key", encoding="utf-8")
        result = self.run_migration("all", "--delete-historical-backups")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(historical.exists())
        self.assertNotIn(b"synthetic-control-token", system.read_bytes())
        cache = self.data / "secrets/bootstrap.credentials.dpapi"
        self.assertNotIn(b"synthetic-control-token", cache.read_bytes())
        audit = self.run_migration("audit")
        self.assertEqual(audit.returncode, 0, audit.stderr)
        report = json.loads((self.root / "evidence/audit-result.json").read_text())
        self.assertEqual(report["literalCount"], 0)
        self.assertEqual(report["itemCount"], 3)
        self.assertEqual(report["vaultEntryCount"], 3)

    def test_atomic_write_refuses_concurrent_configuration_change(self):
        original = self.voice.read_bytes()
        with self.assertRaises(RuntimeError):
            migration.atomic_write(self.voice, b"replacement", expected=b"stale")
        self.assertEqual(self.voice.read_bytes(), original)

    def test_business_stage_defers_bootstrap_without_rewriting_it(self):
        system = self.data / "config/system.json"
        original = b'{"desktop":{"core":{"controlToken":"synthetic-control"}},"Jwt":{"Key":"synthetic-jwt"}}'
        system.write_bytes(original)
        result = self.run_migration("all", "--business-only")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(system.read_bytes(), original)
        self.assertFalse((self.data / "secrets/bootstrap.credentials.dpapi").exists())
        report = json.loads((self.root / "evidence/all-result.json").read_text())
        self.assertEqual(len(report["deferredBootstrap"]), 2)
        self.assertEqual(len(self.secrets), 1)

    def test_jwt_stage_keeps_control_token_for_the_old_guardian(self):
        system = self.data / "config/system.json"
        system.write_text('{"desktop":{"core":{"controlToken":"synthetic-control"}},"Jwt":{"Key":"synthetic-jwt"}}', encoding="utf-8")
        result = self.run_migration("jwt")
        self.assertEqual(result.returncode, 0, result.stderr)
        migrated = json.loads(system.read_text())
        self.assertEqual(migrated["desktop"]["core"]["controlToken"], "synthetic-control")
        self.assertTrue(migrated["Jwt"]["Key"].startswith("{{vault:"))
        self.assertEqual(len(self.secrets), 1)
        self.assertTrue((self.data / "secrets/bootstrap.credentials.dpapi").exists())

    def test_only_credential_token_changes_preserving_precision_format_and_bom(self):
        original = b'\xef\xbb\xbf{ "price":1.2345678901234567890123456789, "nested": {"apiKey":"old-secret"} }\r\n'
        result = migration.replace_credential_strings(original, {("nested", "apiKey"): "{{vault:test}}"})
        self.assertEqual(result, original.replace(b'"old-secret"', b'"{{vault:test}}"'))

    def test_duplicate_json_keys_are_rejected_before_configuration_replacement(self):
        with self.assertRaises(RuntimeError):
            migration.replace_credential_strings(b'{"apiKey":"one","apiKey":"two"}', {("apiKey",): "{{vault:test}}"})


if __name__ == "__main__":
    unittest.main()
