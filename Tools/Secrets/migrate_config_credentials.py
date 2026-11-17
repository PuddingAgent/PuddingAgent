"""Migrate local config credentials through the authenticated KeyVault API.

Read admin username/password JSON from stdin. Never print credentials or HTTP bodies.
Run voice first; only run all after a deployed voice smoke has succeeded.
Recovery copies are DPAPI protected. Historical backups require an explicit deletion flag.
"""
import argparse
import base64
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


def dpapi(mode, value, entropy):
    script = r"""$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
$r=[Console]::In.ReadToEnd()|ConvertFrom-Json
$entropy=[Text.Encoding]::UTF8.GetBytes($r.entropy)
if($r.mode -eq 'protect'){
 $bytes=[Text.Encoding]::UTF8.GetBytes($r.value)
 $result=[Security.Cryptography.ProtectedData]::Protect($bytes,$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
 [Console]::Write([Convert]::ToBase64String($result))
}else{
 $bytes=[Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($r.value),$entropy,[Security.Cryptography.DataProtectionScope]::CurrentUser)
 [Console]::Write([Text.Encoding]::UTF8.GetString($bytes))
}
"""
    result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-Command", script],
                            input=json.dumps(dict(mode=mode, value=value, entropy=entropy)),
                            text=True, encoding="utf-8", capture_output=True)
    if result.returncode:
        raise RuntimeError("DPAPI operation failed; no plaintext recovery copy was written")
    return result.stdout


def replace_credential_strings(original, replacements):
    """Replace selected JSON string tokens without reserializing numbers or other fields."""
    text = original.decode("utf-8-sig")
    decoder = json.JSONDecoder()
    spans = []
    found = set()

    def whitespace(position):
        while position < len(text) and text[position].isspace():
            position += 1
        return position

    def visit(position, location):
        position = whitespace(position)
        opening = text[position]
        if opening in "{[":
            closing = "}" if opening == "{" else "]"
            cursor = whitespace(position + 1)
            index = 0
            keys = set()
            while text[cursor] != closing:
                if opening == "{":
                    key, cursor = decoder.raw_decode(text, cursor)
                    if not isinstance(key, str) or key in keys:
                        raise RuntimeError("Duplicate or invalid JSON property; repair configuration before migration")
                    keys.add(key)
                    cursor = whitespace(cursor)
                    if text[cursor] != ":":
                        raise RuntimeError("Invalid JSON property delimiter")
                    cursor += 1
                else:
                    key = index
                    index += 1
                cursor = whitespace(visit(cursor, location + (key,)))
                if text[cursor] == closing:
                    break
                if text[cursor] != ",":
                    raise RuntimeError("Invalid JSON collection delimiter")
                cursor = whitespace(cursor + 1)
            return cursor + 1
        value, end = decoder.raw_decode(text, position)
        if location in replacements:
            if not isinstance(value, str):
                raise RuntimeError("Credential replacement target is not a JSON string")
            spans.append((position, end, json.dumps(replacements[location], ensure_ascii=False)))
            found.add(location)
        return end

    if whitespace(visit(0, ())) != len(text) or found != set(replacements):
        raise RuntimeError("Credential JSON locations could not be verified")
    for start, end, replacement in reversed(spans):
        text = text[:start] + replacement + text[end:]
    prefix = b"\xef\xbb\xbf" if original.startswith(b"\xef\xbb\xbf") else b""
    return prefix + text.encode("utf-8")


def atomic_write(path, data, expected=None):
    if expected is not None and path.read_bytes() != expected:
        raise RuntimeError("Configuration changed concurrently; refusing to overwrite it")
    temporary = path.with_name(path.name + ".vault-tmp")
    try:
        with temporary.open("xb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        raise RuntimeError("Redirect refused for credential request")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", required=True, type=pathlib.Path)
    parser.add_argument("--base-url", default="http://127.0.0.1")
    parser.add_argument("--stage", required=True, choices=["voice", "jwt", "all", "audit"])
    parser.add_argument("--evidence-dir", required=True, type=pathlib.Path)
    parser.add_argument("--delete-historical-backups", action="store_true")
    parser.add_argument("--business-only", action="store_true", help="Defer bootstrap credentials until Desktop and Guardian readers are deployed")
    args = parser.parse_args()
    origin = urllib.parse.urlparse(args.base_url)
    if origin.scheme != "http" or origin.hostname not in ("127.0.0.1", "localhost") or origin.path not in ("", "/") or origin.username or origin.password or origin.query or origin.fragment:
        raise RuntimeError("Only a loopback Core origin is accepted")
    root = args.data_root.resolve(strict=True)
    args.evidence_dir.mkdir(parents=True, exist_ok=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    bearer = None

    def api(method, route, body=None):
        headers = {"Content-Type": "application/json"}
        if bearer:
            headers["Authorization"] = "Bearer " + bearer
        request = urllib.request.Request(args.base_url.rstrip("/") + route, method=method,
                   headers=headers, data=None if body is None else json.dumps(body).encode())
        try:
            with opener.open(request, timeout=45) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            raise RuntimeError(f"API request rejected: {method} {route.split('?')[0]} HTTP {error.code}") from None

    admin = json.load(sys.stdin)
    login = api("POST", "/api/login/account", dict(username=admin["username"], password=admin["password"], type="account"))
    del admin
    if login.get("status") != "ok" or not login.get("token"):
        raise RuntimeError("Administrator authentication failed")
    bearer = login.pop("token")
    existing = {entry["name"]: entry for entry in api("GET", "/api/keyvault/secrets")}
    references = []
    deferred = []
    fields = re.compile(r"^(apiKey|appSecret|password|controlToken|token)$", re.I)
    files = [root / "config/voice/providers.json"] if args.stage == "voice" else [
        root / "config" / name for name in ("voice/providers.json", "llm.providers.json", "search.providers.json",
        "feishu.json", "caldav.json", "system.json", "security.json")]
    if args.stage != "voice":
        files += sorted((root / "channels").glob("*/manifest.json"))
        files += sorted((root / "agents").glob("*/secrets/home-assistant.json"))
    if args.stage == "jwt":
        files = [root / "config/system.json", root / "config/security.json"]

    def candidates(value, at=()):
        if isinstance(value, dict):
            for key, item in value.items():
                location = at + (key,)
                secret = fields.fullmatch(key) or tuple(str(x).lower() for x in location) == ("jwt", "key")
                if secret and isinstance(item, str) and item.strip() and not item.startswith("${"):
                    yield value, key, item, location
                else:
                    yield from candidates(item, location)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                yield from candidates(item, at + (index,))

    for file in files:
        if not file.exists():
            continue
        original = file.read_bytes()
        document = json.loads(original.decode("utf-8-sig"))
        relative = file.relative_to(root).as_posix()
        pending = list(candidates(document))
        if args.business_only or args.stage == "jwt":
            selected = []
            for item in pending:
                field = ".".join(map(str, item[3]))
                excluded = field.lower() in ("desktop.core.controltoken", "jwt.key") if args.business_only else field.lower() != "jwt.key"
                if excluded:
                    deferred.append(dict(file=relative, field=field))
                else:
                    selected.append(item)
            pending = selected
        plaintext = [item for item in pending if not item[2].startswith("{{vault:")]
        if args.stage == "audit":
            references += [dict(file=relative, field=".".join(map(str, item[3])), kind="vault" if item[2].startswith("{{vault:") else "literal") for item in pending]
            continue
        if plaintext:
            backup = args.evidence_dir / (re.sub(r"[^A-Za-z0-9._-]", "_", relative) + ".dpapi")
            if not backup.exists():
                protected = dpapi("protect", original.decode("utf-8"), "PuddingAgent.MigrationRecovery.v1")
                atomic_write(backup, base64.b64decode(protected))
        replacements = {}
        for container, key, value, location in pending:
            field = ".".join(map(str, location))
            name = re.sub(r"[^A-Za-z0-9._-]", ".", "cfg." + relative + "." + field)
            reference = "{{vault:" + name + "}}"
            bootstrap = field.lower() in ("desktop.core.controltoken", "jwt.key")
            if value.startswith("{{vault:"):
                reference = value
                name = reference[len("{{vault:"):-2]
                resolved = api("POST", "/api/keyvault/inject", dict(text=reference))["text"]
                if not resolved or "{{vault:" in resolved:
                    raise RuntimeError("An existing vault reference cannot be resolved")
            else:
                if name in existing:
                    resolved = api("GET", "/api/keyvault/secrets/" + existing[name]["keyVaultId"] + "?confirm=true")["value"]
                    if resolved != value:
                        raise RuntimeError("Vault name collision: existing value differs")
                else:
                    existing[name] = api("POST", "/api/keyvault/secrets", dict(name=name, value=value,
                        description="Migrated from " + relative + ":" + field,
                        category="bootstrap" if bootstrap else "configuration", tags=["config-migration"]))
                resolved = api("POST", "/api/keyvault/inject", dict(text=reference))["text"]
                if resolved != value:
                    raise RuntimeError("Vault roundtrip verification failed; configuration was not replaced")
            if bootstrap:
                cache = root / "secrets/bootstrap.credentials.dpapi"
                cache.parent.mkdir(parents=True, exist_ok=True)
                entries = json.loads(dpapi("unprotect", base64.b64encode(cache.read_bytes()).decode(), "PuddingAgent.BootstrapCredentials.v1")) if cache.exists() else {}
                entries[name] = resolved
                protected = dpapi("protect", json.dumps(entries), "PuddingAgent.BootstrapCredentials.v1")
                atomic_write(cache, base64.b64decode(protected))
            container[key] = reference
            replacements[location] = reference
            references.append(dict(file=relative, field=field, name=name, kind="vault"))
        if plaintext:
            atomic_write(file, replace_credential_strings(original, replacements), expected=original)

    deleted = []
    if args.delete_historical_backups:
        if args.stage != "all":
            raise RuntimeError("Historical deletion is only allowed after the complete migration")
        backups = sorted((root / "config").rglob("*.before-*.json"))
        # No recursive deletion, no shell-built paths, no active configuration names.
        for file in backups:
            canonical = file.resolve(strict=True)
            if not canonical.is_relative_to(root / "config") or canonical.is_symlink():
                raise RuntimeError("Historical backup path is outside the configuration root")
            deleted.append(dict(file=file.relative_to(root).as_posix(), bytes=file.stat().st_size))
        for entry in deleted:
            (root / entry["file"]).unlink()
    report = dict(stage=args.stage, items=references, itemCount=len(references),
        literalCount=sum(item["kind"] == "literal" for item in references), deletedBackups=deleted,
        deferredBootstrap=deferred,
        vaultEntryCount=len(api("GET", "/api/keyvault/secrets")))
    output = args.evidence_dir / (args.stage + "-result.json")
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("stage", "itemCount", "literalCount", "vaultEntryCount")}, ensure_ascii=False))
    print(json.dumps(dict(deletedBackups=len(deleted))))
    print(json.dumps(dict(deferredBootstrap=len(deferred))))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Do not print HTTP bodies, request payloads, credential values, or arbitrary exception text.
        print("Migration stopped: " + (str(error) if isinstance(error, RuntimeError) else type(error).__name__), file=sys.stderr)
        sys.exit(1)
