#!/usr/bin/env python3
"""Fill in human-readable descriptions for the migrated KeyVault secrets.

The KeyVault migration created one entry per credential with a machine name
(``cfg.config.llm.providers.json.providers.3.apiKey``) and a description that
only repeated that name (``Migrated from ...``). Nobody reading the admin page
could tell what a given secret is for.

This script applies the mapping in ``secret_descriptions.json`` through the
public HTTP API. It never reads or writes secret values: the update request
carries only ``description`` (plus the unchanged ``name``/``category``/``tags``).

Usage (from WSL, stdlib only -- no pip required):

    python3 update_secret_descriptions.py --check --data-root /mnt/d/Data
    python3 update_secret_descriptions.py --apply --base-url http://127.0.0.1:80
    python3 update_secret_descriptions.py --apply --dry-run --base-url ...

``--check`` is offline: it scans the config tree for ``{{vault:...}}`` references
and fails if any of them has no description in the mapping.
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import re
import sys
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
MAPPING_PATH = os.path.join(HERE, "secret_descriptions.json")
REF_RE = re.compile(r"\{\{vault:([^}]+)\}\}")


def load_mapping() -> dict[str, str]:
    with open(MAPPING_PATH, encoding="utf-8") as fh:
        data = json.load(fh)
    if not isinstance(data, dict):
        raise SystemExit("mapping file must be a JSON object")
    return data


def iter_json_files(root: str):
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            if name.endswith(".json"):
                yield os.path.join(dirpath, name)


def scope_roots(data_root: str) -> list[str]:
    """Same scope as the credential migration: config, channels, agents/<id>/secrets.

    Scanning all of the data root would also pick up skill documents that contain
    literal ``{{vault:NAME}}`` examples, which are documentation, not references.
    """
    roots = [os.path.join(data_root, "config"), os.path.join(data_root, "channels")]
    agents = os.path.join(data_root, "agents")
    if os.path.isdir(agents):
        for entry in sorted(os.listdir(agents)):
            secrets = os.path.join(agents, entry, "secrets")
            if os.path.isdir(secrets):
                roots.append(secrets)
    return [r for r in roots if os.path.isdir(r)]


def collect_refs(root: str) -> set[str]:
    """Every distinct ``{{vault:name}}`` reference inside the migration scope."""
    refs: set[str] = set()
    for scope in scope_roots(root):
        for path in iter_json_files(scope):
            try:
                with open(path, encoding="utf-8", errors="replace") as fh:
                    text = fh.read()
            except OSError:
                continue
            for m in REF_RE.finditer(text):
                refs.add(m.group(1).strip())
    return refs


def check(data_root: str, mapping: dict[str, str]) -> int:
    refs = collect_refs(data_root)
    missing = sorted(refs - set(mapping))
    stale = sorted(set(mapping) - refs)
    print(f"references found : {len(refs)}")
    print(f"mapping entries  : {len(mapping)}")
    print(f"covered          : {len(refs & set(mapping))}")
    if missing:
        print("MISSING descriptions for:")
        for name in missing:
            print(f"  - {name}")
    if stale:
        print("mapping entries no longer referenced (ok, but unused):")
        for name in stale:
            print(f"  - {name}")
    return 1 if missing else 0


def api(base_url: str, method: str, path: str, body=None, token: str | None = None):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(base_url.rstrip("/") + path, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw else None
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", "replace")
        raise SystemExit(f"{method} {path} failed: HTTP {exc.code} {detail}")


def login(base_url: str, username: str, password: str) -> str:
    result = api(
        base_url,
        "POST",
        "/api/login/account",
        {"username": username, "password": password, "type": "account"},
    )
    if not result or result.get("status") != "ok" or not result.get("token"):
        raise SystemExit("login failed: " + json.dumps(result, ensure_ascii=False))
    return result["token"]


def apply(base_url: str, username: str, password: str, mapping: dict[str, str], dry_run: bool) -> int:
    token = login(base_url, username, password)
    rows = api(base_url, "GET", "/api/keyvault/secrets", token=token) or []
    by_name = {row.get("name"): row for row in rows}
    print(f"secrets listed   : {len(rows)}")

    changed = 0
    skipped = 0
    unknown = []
    for name, description in mapping.items():
        row = by_name.get(name)
        if row is None:
            unknown.append(name)
            continue
        if (row.get("description") or "") == description:
            skipped += 1
            continue
        if dry_run:
            print(f"[dry-run] would set  {name}\n           -> {description}")
            changed += 1
            continue
        payload = {
            "name": row.get("name"),
            "description": description,
            "category": row.get("category"),
            "tags": row.get("tags") or [],
        }
        api(base_url, "PUT", "/api/keyvault/secrets/" + str(row.get("keyVaultId")), payload, token=token)
        changed += 1
        print(f"updated  {name}")

    print(f"changed={changed} already-correct={skipped} not-found={len(unknown)}")
    for name in unknown:
        print(f"  not found: {name}")
    return 0 if not unknown else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="offline coverage check")
    parser.add_argument("--apply", action="store_true", help="write descriptions through the API")
    parser.add_argument("--dry-run", action="store_true", help="print planned changes only")
    parser.add_argument("--data-root", default=os.environ.get("PUDDING_DATA_ROOT", "/mnt/d/Data"))
    parser.add_argument("--base-url", default=os.environ.get("PUDDING_BASE_URL", "http://127.0.0.1:80"))
    parser.add_argument("--username", default=os.environ.get("PUDDING_ADMIN_USER", "admin"))
    parser.add_argument("--password", default=None, help="omit to be prompted")
    args = parser.parse_args()

    mapping = load_mapping()
    if args.check:
        return check(args.data_root, mapping)
    if args.apply:
        password = args.password
        if password is None:
            password = getpass.getpass("admin password: ")
        return apply(args.base_url, args.username, password, mapping, args.dry_run)
    parser.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
