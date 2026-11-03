"""Transport-level regression tests; isolated loopback server, no live credentials."""

from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest

import pudding_message as client


class MessageClientTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(".work/pudding-message-tests")
        scratch.mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name).resolve()
        self.requests = []
        self.mode = "reply"
        self.mid = "extmsg-" + "a" * 32
        case = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
                case.requests.append(("POST", self.path, dict(self.headers), body))
                if case.mode == "disconnect":
                    self.close_connection = True
                    return
                if case.mode == "conflict":
                    self.respond(409, {"code": "external.idempotency_conflict"})
                elif case.mode == "in_progress":
                    self.respond(409, {"code": "external.idempotency_in_progress"})
                elif case.mode == "redirect":
                    self.send_response(307)
                    self.send_header("Location", "http://127.0.0.1:1/stolen")
                    self.end_headers()
                    self.wfile.write(b"{}")
                else:
                    self.respond(202, case.receipt(False))

            def do_GET(self):
                case.requests.append(("GET", self.path, dict(self.headers), None))
                if case.mode == "status_error":
                    self.respond(503, {"code": "server.busy"})
                else:
                    self.respond(200, case.receipt(case.mode != "queued"))

            def respond(self, status, payload):
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(json.dumps(payload).encode())

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.credential = self.root / "credentials.json"
        self.credential.write_text(json.dumps({"bearerToken": "fixture-secret-do-not-log",
                "tokenId": "fixture-token", "baseUrl": f"http://127.0.0.1:{self.server.server_port}"}), encoding="utf-8")
        self.message = self.root / "message.txt"
        self.message.write_text("你好，receipt test", encoding="utf-8")

    def receipt(self, accepted):
        result = {"messageId": self.mid, "workspaceId": "default", "agentId": "test-agent",
                  "deliveryStatus": "accepted" if accepted else "queued",
                  "conversationId": "conversation-1" if accepted else None,
                  "executionStatus": "succeeded" if accepted else None,
                  "completedAtUtc": "2026-11-03T00:00:01Z" if accepted else None,
                  "reply": "ACK" if accepted else None, "replyIsError": False if accepted else None,
                  "deliveries": [{"status": "delivered" if accepted else "queued",
                                  "acknowledgedAtUtc": "2026-11-03T00:00:00Z" if accepted else None}],
                  "statusUrl": "http://127.0.0.1:1/untrusted-location"}
        if accepted and self.mode == "delivery_only":
            result.update(executionStatus="running", completedAtUtc=None, reply=None, replyIsError=None)
        if accepted and self.mode == "failed":
            result.update(executionStatus="failed", reply="error", replyIsError=True)
        if accepted and self.mode == "wrong_identity":
            result["agentId"] = "another-agent"
        if accepted and self.mode == "secret_echo":
            result["reply"] = "fixture-secret-do-not-log"
        if accepted and self.mode == "malformed":
            result["deliveries"] = "not-a-list"
        return result

    def run_client(self, action="send", *extra):
        command = [sys.executable, "-B", str(Path(client.__file__)), action,
                   "--credentials", str(self.credential), "--state-dir", str(self.root / "state"),
                   "--operation", "one", "--wait-seconds", "0"]
        if action == "send":
            command += ["--agent", "test-agent", "--message-file", str(self.message)]
        result = subprocess.run(command + list(extra), capture_output=True, timeout=20)
        self.assertEqual(result.stderr, b"")
        return result.returncode, json.loads(result.stdout)

    def state(self):
        return client.read_json(self.root / "state/one/state.json")

    def test_202_is_pending_then_status_confirms_reply_without_second_post(self):
        self.mode = "queued"
        code, result = self.run_client()
        self.assertEqual((code, result["result"]), (2, "pending"))
        self.mode = "reply"
        code, result = self.run_client("status")
        self.assertEqual((code, result["result"]), (0, "reply_confirmed"))
        self.run_client()  # send on an existing receipt is GET-only too.
        self.assertEqual(sum(r[0] == "POST" for r in self.requests), 1)
        self.assertTrue(all("untrusted-location" not in r[1] for r in self.requests))

    def test_disconnect_replays_same_key_and_exact_body(self):
        self.mode = "disconnect"
        code, result = self.run_client()
        self.assertEqual(code, 2)
        self.assertEqual(result["error"], "transport_outcome_unknown")
        self.mode = "reply"
        self.assertEqual(self.run_client()[0], 0)
        posts = [r for r in self.requests if r[0] == "POST"]
        self.assertEqual(len(posts), 2)
        self.assertEqual(posts[0][2]["Idempotency-Key"], posts[1][2]["Idempotency-Key"])
        self.assertEqual(posts[0][3], posts[1][3])

    def test_retry_cannot_change_body(self):
        self.mode = "disconnect"
        self.run_client()
        self.message.write_text("different", encoding="utf-8")
        code, result = self.run_client()
        self.assertEqual((code, result["error"]), (1, "operation_content_changed"))
        self.assertEqual(len(self.requests), 1)

    def test_expired_ambiguous_send_is_not_reposted(self):
        self.mode = "disconnect"
        self.run_client()
        state = self.state()
        state["firstPostAtUtc"] = (datetime.now(timezone.utc) - timedelta(hours=2)).isoformat()
        client.save_json(self.root / "state/one/state.json", state)
        code, result = self.run_client()
        self.assertEqual(code, 2)
        self.assertIn("replay_window_closed", result["error"])
        self.assertEqual(len(self.requests), 1)

    def test_rotated_bearer_with_stale_token_id_cannot_repost(self):
        self.mode = "disconnect"
        self.run_client()
        credentials = client.read_json(self.credential)
        credentials["bearerToken"] = "replacement-fixture-secret"
        client.save_json(self.credential, credentials)
        code, result = self.run_client()
        self.assertEqual((code, result["error"]), (1, "operation_credential_identity_changed"))
        self.assertEqual(len(self.requests), 1)

    def test_delivery_acceptance_does_not_claim_execution_completion(self):
        self.mode = "delivery_only"
        self.assertEqual(self.run_client()[0], 2)
        code, result = self.run_client("status", "--wait-for", "delivered")
        self.assertEqual((code, result["result"]), (0, "delivery_confirmed"))
        self.assertEqual(result["receipt"]["executionStatus"], "running")

    def test_execution_failure_is_not_success(self):
        self.mode = "failed"
        self.assertEqual(self.run_client()[0], 1)

    def test_conflict_and_in_progress_have_different_results(self):
        self.mode = "in_progress"
        self.assertEqual(self.run_client()[0], 2)
        self.mode = "conflict"
        code, result = self.run_client()
        self.assertEqual((code, result["error"]), (1, "external.idempotency_conflict"))

    def test_redirect_is_never_followed(self):
        self.mode = "redirect"
        self.assertEqual(self.run_client()[0], 1)
        self.assertEqual(len(self.requests), 1)

    def test_receipt_must_match_target(self):
        self.mode = "wrong_identity"
        code, result = self.run_client()
        self.assertEqual((code, result["error"]), (2, "receipt_identity_mismatch"))

    def test_credentials_never_enter_journal_or_output(self):
        self.mode = "secret_echo"
        code, result = self.run_client()
        self.assertEqual(code, 0)
        self.assertNotIn("fixture-secret-do-not-log", json.dumps(result))
        self.assertNotIn("fixture-secret-do-not-log", json.dumps(self.state()))

    def test_malformed_receipt_does_not_report_success(self):
        self.mode = "malformed"
        code, result = self.run_client()
        self.assertEqual((code, result["error"]), (2, "invalid_receipt_schema"))

    def test_status_network_error_preserves_message_identity(self):
        self.mode = "queued"
        self.run_client()
        self.mode = "status_error"
        code, result = self.run_client("status")
        self.assertEqual(code, 2)
        self.assertEqual(result["messageId"], self.mid)
        self.assertEqual(sum(r[0] == "POST" for r in self.requests), 1)

    def test_concurrent_operation_is_rejected_without_network(self):
        with client.operation_lock(self.root / "state/one"):
            code, result = self.run_client()
        self.assertEqual((code, result["error"]), (1, "operation_in_use"))
        self.assertEqual(self.requests, [])


if __name__ == "__main__":
    unittest.main()
