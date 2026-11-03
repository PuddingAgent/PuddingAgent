"""Durable, receipt-checked client for the local PuddingAgent External API v1."""

import argparse
from contextlib import contextmanager
from datetime import datetime, timezone
import http.client
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import sys
import time
from urllib.parse import quote, urlsplit
import uuid


DEFAULT_CREDENTIALS = r"D:\Keys\puddingagent-external-api-gpt.json"
MAX_REPLAY_AGE = 3600  # Server retention is at least one day; never retry indefinitely.


class ClientError(Exception):
    def __init__(self, code, pending=False):
        self.code = code
        self.pending = pending


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def save_json(path, value):
    """Replace the journal atomically, flushing before any subsequent HTTP mutation."""
    temporary = path.with_suffix(".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


@contextmanager
def operation_lock(directory):
    directory.mkdir(parents=True, exist_ok=True)
    with (directory / "operation.lock").open("a+b") as stream:
        if stream.tell() == 0:
            stream.write(b"0")
            stream.flush()
        stream.seek(0)
        try:
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            raise ClientError("operation_in_use") from None
        try:
            yield
        finally:
            if os.name == "nt":
                stream.seek(0)
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


class Client:
    def __init__(self, credential_path, timeout=10):
        credentials = read_json(credential_path)
        self.token = credentials["bearerToken"]
        self.token_id = credentials["tokenId"]
        self.base = credentials["baseUrl"].rstrip("/")
        self.workspace = credentials.get("workspaceId", "default")
        self.url = urlsplit(self.base)
        try:
            loopback = ipaddress.ip_address(self.url.hostname).is_loopback
        except ValueError:
            loopback = False
        if (not loopback or self.url.scheme not in {"http", "https"}
                or self.url.username or self.url.password or self.url.path
                or self.url.query or self.url.fragment):
            raise ClientError("credential_base_url_must_be_loopback_origin")
        if not self.token or "\n" in self.token or "\r" in self.token:
            raise ClientError("invalid_credential")
        self.fingerprint = hashlib.sha256(self.token.encode("utf-8")).hexdigest()
        self.timeout = timeout

    def redact(self, value):
        # Never echo credentials, even if an erroneous server response contains them.
        return json.loads(json.dumps(value, ensure_ascii=False).replace(self.token, "[REDACTED]"))

    def request(self, method, path, body=None, key=None):
        connection_type = (http.client.HTTPSConnection if self.url.scheme == "https"
                           else http.client.HTTPConnection)
        connection = connection_type(self.url.hostname, self.url.port, timeout=self.timeout)
        headers = {"Authorization": "Bearer " + self.token, "Accept": "application/json"}
        if key:
            headers["Idempotency-Key"] = key
        if body is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"
        try:
            connection.request(method, path, body=body, headers=headers)
            response = connection.getresponse()
            raw = response.read(1024 * 1024 + 1)
            if len(raw) > 1024 * 1024:
                raise ClientError("response_too_large", pending=True)
            try:
                payload = self.redact(json.loads(raw))
            except (ValueError, UnicodeError):
                raise ClientError("non_json_response", pending=True) from None
            # http.client follows neither redirects nor proxy environment variables.
            return response.status, payload
        except (OSError, http.client.HTTPException):
            raise ClientError("transport_outcome_unknown", pending=True) from None
        finally:
            connection.close()


def check_http(status, payload, expected):
    if status == expected:
        return
    code = payload.get("code", "") if isinstance(payload, dict) else ""
    if not isinstance(code, str) or not re.fullmatch(r"[a-zA-Z0-9_.-]{1,100}", code):
        code = "http_" + str(status)
    raise ClientError(code, pending=status >= 500 or status in {408, 429}
                      or code == "external.idempotency_in_progress")


def message_route(workspace, agent):
    return f"/api/external/v1/workspaces/{quote(workspace, safe='')}/agents/{quote(agent, safe='')}/messages"


def validate_receipt(receipt, state):
    if (not isinstance(receipt, dict)
            or receipt.get("workspaceId") != state["workspaceId"]
            or receipt.get("agentId") != state["agentId"]
            or not isinstance(receipt.get("messageId"), str)
            or not re.fullmatch(r"extmsg-[0-9a-f]{32}", receipt["messageId"])
            or (state.get("messageId") and receipt["messageId"] != state["messageId"])):
        raise ClientError("receipt_identity_mismatch", pending=True)
    if (not isinstance(receipt.get("deliveryStatus"), str)
            or not isinstance(receipt.get("deliveries"), list)
            or not all(isinstance(item, dict) for item in receipt["deliveries"])):
        raise ClientError("invalid_receipt_schema", pending=True)


def outcome(receipt, wait_for):
    deliveries = receipt.get("deliveries") or []
    delivered = (receipt.get("deliveryStatus") == "accepted"
                 and bool(receipt.get("conversationId")) and bool(deliveries)
                 and all(d.get("status") == "delivered" and d.get("acknowledgedAtUtc")
                         for d in deliveries))
    replied = (delivered and receipt.get("executionStatus") == "succeeded"
               and bool(receipt.get("completedAtUtc"))
               and isinstance(receipt.get("reply"), str) and bool(receipt["reply"].strip())
               and receipt.get("replyIsError") is False)
    if (wait_for == "delivered" and delivered) or replied:
        return 0, "reply_confirmed" if replied else "delivery_confirmed"
    if (receipt.get("deliveryStatus") == "failed" or receipt.get("replyIsError") is True
            or (receipt.get("completedAtUtc")
                and receipt.get("executionStatus") in {"failed", "cancelled", "canceled", "rejected"})):
        return 1, "delivery_or_execution_failed"
    return 2, "pending"


def execute(args):
    client = Client(args.credentials)
    workspace = args.workspace or client.workspace
    if args.action in {"doctor", "agents"}:
        path = ("/api/external/v1/token" if args.action == "doctor" else
                f"/api/external/v1/workspaces/{quote(workspace, safe='')}/agents")
        status, data = client.request("GET", path)
        check_http(status, data, 200)
        if args.action == "doctor":
            if data.get("tokenId") != client.token_id:
                raise ClientError("credential_token_identity_mismatch")
            data = {key: data.get(key) for key in
                    ["tokenId", "name", "scopes", "workspaces", "expiresAtUtc", "status"]}
        else:
            data = [{key: agent.get(key) for key in
                     ["agentId", "displayName", "role", "isEnabled", "isFrozen"]} for agent in data]
        return 0, {"httpStatus": status, "result": data}

    if not args.operation or not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9_-]{0,95}", args.operation):
        raise ClientError("operation_required_use_unique_label_then_reuse_for_resume")
    directory = Path(args.state_dir).resolve() / args.operation
    journal = directory / "state.json"
    with operation_lock(directory):
        state = read_json(journal) if journal.exists() else None
        if state:
            if (state["baseUrl"] != client.base or state["tokenId"] != client.token_id
                    or state["credentialFingerprint"] != client.fingerprint):
                raise ClientError("operation_credential_identity_changed")
            if ((args.workspace and args.workspace != state["workspaceId"])
                    or (args.agent and args.agent != state["agentId"])):
                raise ClientError("operation_target_changed")
            if args.message_file and Path(args.message_file).read_text(encoding="utf-8-sig") != state["content"]:
                raise ClientError("operation_content_changed")
        elif args.action == "status":
            raise ClientError("operation_not_found")
        else:
            if not args.agent or not args.message_file:
                raise ClientError("new_send_requires_agent_and_message_file")
            content = Path(args.message_file).read_text(encoding="utf-8-sig")
            if not content.strip() or len(content.encode("utf-16-le")) // 2 > 65536:
                raise ClientError("message_must_have_1_to_65536_utf16_units")
            if client.token in content:
                raise ClientError("message_contains_credential")
            state = {"schemaVersion": 1, "operation": args.operation,
                     "baseUrl": client.base, "tokenId": client.token_id,
                     "credentialFingerprint": client.fingerprint,
                     "workspaceId": workspace, "agentId": args.agent,
                     "route": message_route(workspace, args.agent), "content": content,
                     "idempotencyKey": "codex-" + str(uuid.uuid4()),
                     "createdAtUtc": utc_now(), "firstPostAtUtc": None,
                     "messageId": None, "receipt": None}
            save_json(journal, state)
        try:
            if not state["messageId"]:
                if args.action == "status":
                    raise ClientError("no_receipt_retry_send_with_same_operation", pending=True)
                if state["firstPostAtUtc"]:
                    age = (datetime.now(timezone.utc) - datetime.fromisoformat(state["firstPostAtUtc"])).total_seconds()
                    if not 0 <= age <= MAX_REPLAY_AGE:
                        raise ClientError("replay_window_closed_reconcile_before_any_new_send", pending=True)
                else:
                    state["firstPostAtUtc"] = utc_now()
                    save_json(journal, state)
                body = json.dumps({"content": state["content"]}, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                status, receipt = client.request("POST", state["route"], body, state["idempotencyKey"])
                check_http(status, receipt, 202)
                validate_receipt(receipt, state)
                state["messageId"] = receipt["messageId"]
                state["receipt"] = receipt
                state["postHttpStatus"] = status
                save_json(journal, state)
            deadline = time.monotonic() + args.wait_seconds
            while True:
                # Derive the route locally; never forward a token to a server-provided Location/statusUrl.
                status, receipt = client.request("GET", state["route"] + "/" + state["messageId"])
                check_http(status, receipt, 200)
                validate_receipt(receipt, state)
                state["receipt"] = receipt
                state["checkedAtUtc"] = utc_now()
                state.pop("lastError", None)
                save_json(journal, state)
                exit_code, result = outcome(receipt, args.wait_for)
                if exit_code != 2 or time.monotonic() >= deadline:
                    return exit_code, {"result": result, "operation": args.operation,
                                       "journal": str(journal), "receipt": receipt}
                time.sleep(min(2, max(0, deadline - time.monotonic())))
        except ClientError as error:
            state["lastError"] = {"code": error.code, "atUtc": utc_now()}
            save_json(journal, state)
            return (2 if error.pending else 1), {"result": "pending" if error.pending else "error",
                    "error": error.code, "operation": args.operation, "journal": str(journal),
                    "messageId": state["messageId"], "lastReceipt": state["receipt"]}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["doctor", "agents", "send", "status"])
    parser.add_argument("--credentials", default=DEFAULT_CREDENTIALS)
    parser.add_argument("--workspace")
    parser.add_argument("--agent")
    parser.add_argument("--message-file")
    parser.add_argument("--operation", help="Unique per logical message; reuse for every retry/status check")
    parser.add_argument("--state-dir", default=".work/pudding-message")
    parser.add_argument("--wait-for", choices=["delivered", "reply"], default="reply")
    parser.add_argument("--wait-seconds", type=int, choices=range(0, 46), default=30, metavar="0..45")
    args = parser.parse_args(argv)
    try:
        code, result = execute(args)
    except ClientError as error:
        code, result = (2 if error.pending else 1), {"result": "error", "error": error.code}
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        # Do not print exception text: malformed credentials/server data can contain secrets.
        code, result = 1, {"result": "error", "error": "invalid_local_file_or_protocol_data"}
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return code


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(main())
