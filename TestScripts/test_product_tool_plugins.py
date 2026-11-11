"""Windows product smoke. Arguments: Core exe, TextTools project directory, built sample DLL, new evidence directory."""
import io
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import secrets
import sys
from threading import Thread
from urllib.error import HTTPError
from urllib.request import build_opener, ProxyHandler, Request
import uuid
import zipfile

from foundation_core_smoke import smoke


class Model(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        probe = self.server.probe
        tools = [t["function"]["name"] for t in request.get("tools", [])]
        probe["schemas"].append(tools)
        name = probe.get("tool")
        results = [m for m in request["messages"] if m.get("role") == "tool" and m.get("tool_call_id") == probe.get("call_id")]
        answer = {"role": "assistant", "content": '{"status":"DONE","message":"receipt recorded"}'}
        if name and (name in tools or probe.get("force")) and not results:
            answer = {"role": "assistant", "content": None, "tool_calls": [{"id": probe["call_id"], "type": "function",
                      "function": {"name": name, "arguments": json.dumps(probe["args"])}}]}
        if results:
            probe["result"] = results[-1]["content"]
        data = {"id": "product-smoke", "object": "chat.completion", "model": "product-test",
                "choices": [{"index": 0, "message": answer, "finish_reason": "tool_calls" if "tool_calls" in answer else "stop"}],
                "usage": {"prompt_tokens": 123, "completion_tokens": 12, "total_tokens": 135}}
        payload = json.dumps(data).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


def verify_product(exe, sample, dll, run):
    http = build_opener(ProxyHandler({}))
    scope = {"workspaceId": "probe", "agentId": "probe-agent", "sessionId": "probe-session"}
    with ThreadingHTTPServer(("127.0.0.1", 0), Model) as server:
        endpoint = f"http://127.0.0.1:{server.server_port}/v1"
        server.probe = {"schemas": []}
        Thread(target=server.serve_forever, daemon=True).start()

        def configure(data):
            cfg = data / "config"
            cfg.mkdir(exist_ok=True)
            (cfg / "llm.providers.json").write_text(json.dumps({"providers": [{"providerId": "local-test",
                "name": "Isolated smoke", "baseUrl": endpoint, "isEnabled": True,
                "models": [{"modelId": "product-test", "protocol":"openai", "name": "Product smoke", "isEnabled": True}]}]}), encoding="utf-8")

        def verify(base, pid, data):
            token = ""
            evidence = {"corePid": pid, "checks": []}

            def check(ok, label):
                if not ok:
                    raise RuntimeError(label)
                evidence["checks"].append(label)
                (run / "plugins.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")

            def api(path, body=None, media="application/json"):
                payload = body if isinstance(body, bytes) else json.dumps(body).encode() if body is not None else None
                headers = {"Content-Type": media, "X-Workspace-Id": scope["workspaceId"]}
                if token:
                    headers["Authorization"] = "Bearer " + token
                req = Request(base + path, data=payload, headers=headers)
                with http.open(req, timeout=90) as res:
                    return json.load(res)

            try:
                api("/api/tool-plugins")
            except HTTPError as exc:
                check(exc.code in (401, 403), "anonymous management rejected")
            else:
                raise RuntimeError("Anonymous plugin management was accepted")
            credentials = {"userId": "admin", "email": "smoke@example.invalid", "password": "Sm1!" + secrets.token_hex(16)}
            token = api("/api/bootstrap/admin", credentials)["token"]
            catalog = api("/api/tool-plugins")
            check(catalog["coreProcessId"] == pid and catalog["workerInstalled"], "bundled worker installed on the tested Core")
            packages = {p["id"]: p for p in catalog["hostTools"]["packages"]}
            check("pudding.files" in packages and "pudding.shell" in packages, "product packages installed")
            check(not catalog["hostTools"]["unassignedBindings"], "every registered business tool has a product package")
            ids = [name for package in packages.values() for name in package["tools"]]
            check(len(set(ids)) == len(ids) and all(x in ids for x in ("shell", "file_read", "spawn_sub_agent")),
                  "development tools have distinct package ownership")
            available = {d["toolId"]: d for d in api("/api/tools?workspaceId=probe")}
            check(all(available[n]["runtimeStatus"] == "Installed" for n in ("file_read", "shell", "spawn_sub_agent")),
                  "stored agent configurations can resolve installed tool descriptors before mounting")
            defaults = {d["toolId"] for d in api("/api/tools?enabledByDefaultOnly=true")}
            check(not defaults.intersection(ids), "package installation adds no default tool selections")
            content = io.BytesIO()
            with zipfile.ZipFile(content, "w", zipfile.ZIP_DEFLATED) as z:
                z.write(sample / "plugin.json", "plugin.json")
                z.write(dll, "bin/Sample.TextTools.dll")
            boundary = uuid.uuid4().hex
            multipart = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="test.zip"\r\n'
                         'Content-Type: application/zip\r\n\r\n').encode() + content.getvalue() + f"\r\n--{boundary}--\r\n".encode()
            imported = api("/api/tool-plugins/import", multipart, "multipart/form-data; boundary=" + boundary)

            def load(h, grants=(), hooks=False):
                return api("/api/tool-plugins/load", {"contentHash": h, "operationId": uuid.uuid4().hex,
                    "scope": scope, "executeApproved": True, "approvedCapabilities": list(grants), "enableHooks": hooks})

            def unload(a):
                stopped = api("/api/tool-plugins/unload", {"activationId": a["activationId"], "scope": scope})
                check(stopped["state"] == "Unloaded", "unloaded " + a["pluginId"])

            workspace = data / "workspaces" / scope["workspaceId"]
            workspace.mkdir(parents=True, exist_ok=True)
            marker = "plugin-proof-" + uuid.uuid4().hex
            (workspace / "evidence.txt").write_text(marker, encoding="utf-8")

            def dispatch(allowed, name=None, args=None, session=None, force=False, *, allow_write=False, allow_shell=False):
                probe = {"call_id": uuid.uuid4().hex, "schemas": [], "tool": name, "args": args, "force": force}
                server.probe = probe
                response = api("/api/runtime/execute", {"agentInstanceId": scope["agentId"], "agentTemplateId": "probe",
                    "workspaceId": scope["workspaceId"], "sessionId": session or scope["sessionId"],
                    "userId": credentials["userId"], "messageText": "Run the isolated tool check " + uuid.uuid4().hex,
                    "maxRounds": 3, "suppressContextAutoCompaction": True,
                    "capabilityPolicy": {"defaultToolNames": allowed, "allowedToolNames": allowed,
                                         "allowFileWrite": allow_write, "allowShellExecution": allow_shell},
                    "llmProfile": {"providerId": "local-test", "profileId": "local-test", "modelId": "product-test"},
                    "llmConfig": {"endpoint": endpoint, "apiKey": "local-smoke", "modelId": "product-test",
                                  "protocol": "openai", "maxContextTokens": 32768}})
                if not response.get("isSuccess") or not probe["schemas"]:
                    raise RuntimeError(f"Product dispatch failed: {response.get('stopReason')}: {response.get('errorMessage')}")
                return set(probe["schemas"][0]), probe.get("result", "")

            # A package on disk grants neither discovery nor execution.
            names, _ = dispatch(["search_tools", "file_read", "shell"])
            check("search_tools" in names and not {"file_read", "shell"} & names, "unmounted business tools hidden")
            try:
                api("/api/tool-plugins/load", {"contentHash": packages["pudding.shell"]["contentHash"],
                    "scope": scope, "operationId": uuid.uuid4().hex, "executeApproved": False,
                    "approvedCapabilities": [], "enableHooks": False})
            except HTTPError as exc:
                check(exc.code == 400 and json.load(exc)["code"] == "plugin.capability_denied",
                      "unapproved package mount rejected")
            else:
                raise RuntimeError("Unapproved package mount was accepted")
            _, directory = dispatch(["plugin_catalog", "file_read"], "plugin_catalog", {})
            check(packages["pudding.files"]["contentHash"] in directory and packages["pudding.shell"]["contentHash"] not in directory,
                  "agent catalog lists eligible file package and hides Shell package")
            args = {"contentHash": packages["pudding.files"]["contentHash"], "operationId": uuid.uuid4().hex}
            _, receipt = dispatch(["plugin_load", "file_read"], "plugin_load", args)
            mounted = api("/api/tool-plugins")["hostTools"]["activations"]
            check(not mounted and "approval" in receipt.lower(), "agent load without operator approval left no activation")
            files = load(packages["pudding.files"]["contentHash"])
            shell = load(packages["pudding.shell"]["contentHash"])
            names, _ = dispatch(["search_tools", "file_read"], session="other-session")
            check("file_read" in names, "a second session of the same Agent sees its mounted file tool")
            names, _ = dispatch(["search_tools", "shell"])
            check("file_read" not in names and "shell" not in names, "mount does not grant discovery or Shell")
            denied_marker = "shell-must-not-run-" + uuid.uuid4().hex
            _, denied = dispatch(["search_tools", "shell"], "shell",
                                 {"command": "Write-Output '" + denied_marker + "'"}, force=True)
            check(denied and denied_marker not in denied and
                  any(word in denied.lower() for word in ("not found", "not available", "denied", "not exposed", "unknown tool", "not in")),
                  "direct request for ungranted Shell produced a rejection")
            names, result = dispatch(["file_read"], "file_read", {"path": "evidence.txt"})
            check("file_read" in names and marker in result, "agent received fresh file contents through mounted service")
            external = load(imported["contentHash"], ["workspace.read"], hooks=True)
            check(external["state"] == "Active" and external["processId"] != pid and external["processId"] > 0,
                  "external DLL active in separate Worker")
            stats = next(n for n in external["toolNames"] if n.endswith("_text_stats"))
            names, result = dispatch(["plugin_load", stats], stats, {"text": "a\n猫"})
            check(stats in names and '"characters":3' in result.replace(" ", "") and '"lines":2' in result.replace(" ", ""),
                  "agent received actual SDK worker result")
            unload(external)

            def authorize_once(tool):
                api(f"/api/v1/conversations/{scope['sessionId']}/system-commands", {
                    "agentId": scope["agentId"], "clientRequestId": uuid.uuid4().hex,
                    "clientMessageId": uuid.uuid4().hex, "responseMessageId": uuid.uuid4().hex,
                    "commandText": f"/authorize {tool} once"})

            def approved_call(tool, args, **policy):
                authorize_once(tool)
                names, receipt = dispatch([tool], tool, args, **policy)
                check(tool in names, f"{tool} exposed in the authorized test scope")
                return receipt

            # The fixture is authored and compiled through real tools in the same logical
            # workspace. A per-run method result distinguishes this DLL from the uploaded sample.
            manifest = json.loads((sample / "plugin.json").read_text(encoding="utf-8"))
            manifest.update(id="sample.build-proof", requestedCapabilities=[], hooks=[])
            manifest["tools"] = manifest["tools"][:1]
            manifest["tools"][0]["outputSchema"] = {"type": "string"}
            source = """using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Sdk;
namespace Sample.TextTools;
public sealed class TextToolsPlugin : ToolPlugin
{
    public override IReadOnlyCollection<string> ToolIds => ["text_stats"];
    public override ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct) =>
        ValueTask.FromResult(PluginResult.Ok("MARKER/" + call.Arguments.GetProperty("text").GetString()));
}
""".replace("MARKER", marker)
            for path, text in {
                    "author/pkg/plugin.json": json.dumps(manifest, ensure_ascii=False),
                    "author/src/TextTools.csproj": (sample / "TextTools.csproj").read_text(encoding="utf-8"),
                    "author/src/TextToolsPlugin.cs": source}.items():
                approved_call("file_write", {"path": path, "content": text}, allow_write=True)
                check((workspace / path).is_file() and (workspace / path).read_text(encoding="utf-8") == text,
                      "agent wrote " + path + " inside the logical workspace")
            sdk = str(exe.parent / "tools" / "plugin-worker").replace("'", "''")
            command = f"""$ErrorActionPreference = 'Stop'
dotnet build author/src/TextTools.csproj -c Release -o author/out '-p:PuddingPluginSdkDirectory={sdk}' --nologo
if ($LASTEXITCODE) {{ exit $LASTEXITCODE }}
New-Item -ItemType Directory -Path author/pkg/bin -Force | Out-Null
Copy-Item -LiteralPath author/out/Sample.TextTools.dll -Destination author/pkg/bin/Sample.TextTools.dll
Compress-Archive -Path author/pkg/* -DestinationPath author/plugin.zip
Write-Output 'package ready'"""
            receipt = approved_call("shell", {"command": command, "shell": "pwsh", "timeout_seconds": 60}, allow_shell=True)
            package = workspace / "author/plugin.zip"
            check("package ready" in receipt and package.is_file(), "agent compiled and packaged the plugin via Shell")
            with zipfile.ZipFile(package) as archive:
                check(archive.read("bin/Sample.TextTools.dll") == (workspace / "author/out/Sample.TextTools.dll").read_bytes(),
                      "ZIP contains the DLL built by the agent")
            receipt = approved_call("plugin_import", {"path": "author/plugin.zip"}, allow_write=True)
            installed = [p for p in api("/api/tool-plugins")["catalog"]["packages"] if p["id"] == manifest["id"]]
            check(len(installed) == 1 and installed[0]["status"] == "Validated" and
                  installed[0]["contentHash"] in receipt, "agent imported its workspace ZIP and received the installed content hash")
            digest = installed[0]["contentHash"]
            receipt = approved_call("plugin_load", {"contentHash": digest, "operationId": uuid.uuid4().hex})
            check("process execution permission" in receipt and
                  not any(a["pluginId"] == manifest["id"] for a in api("/api/tool-plugins")["activations"]),
                  "approval alone cannot start an SDK worker with process execution disabled")
            receipt = approved_call("plugin_load", {"contentHash": digest, "operationId": uuid.uuid4().hex}, allow_shell=True)
            active = next((a for a in api("/api/tool-plugins")["activations"] if a["pluginId"] == manifest["id"] and a["state"] == "Active"), None)
            check(active and active["activationId"] in receipt and active["processId"] > 0 and active["processId"] != pid,
                  "agent loaded the new DLL into a distinct worker")
            built_tool, = active["toolNames"]
            _, receipt = dispatch(["plugin_load", built_tool], built_tool, {"text": "fresh"})
            check(marker + "/fresh" in receipt, "agent received the unique result from its own compiled code")
            _, receipt = dispatch(["plugin_unload"], "plugin_unload", {"activationId": active["activationId"]})
            check("Unloaded" in receipt and active["activationId"] in receipt, "agent unloaded its SDK plugin")
            names, _ = dispatch(["plugin_load", built_tool, "shell"])
            check(built_tool not in names and "shell" not in names, "next turn hides unloaded SDK tool and ungranted Shell")
            unload(shell)
            _, receipt = dispatch(["plugin_unload"], "plugin_unload", {"activationId": files["activationId"]})
            check("Unloaded" in receipt and files["activationId"] in receipt, "agent unloaded its own file package")
            names, _ = dispatch(["search_tools", "file_read", "plugin_load", stats])
            check("file_read" not in names and stats not in names, "unloaded tools absent from next model request")
            check(api("/api/tool-plugins")["coreProcessId"] == pid, "same Core process after hot unload")
        try:
            return smoke(exe, run, verify, configure)
        finally:
            server.shutdown()


if __name__ == "__main__":
    sys.exit(0 if verify_product(*(Path(arg).resolve() for arg in sys.argv[1:])) else 1)
