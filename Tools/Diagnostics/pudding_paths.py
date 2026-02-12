"""Shared path resolution for local Pudding diagnostics tools.

This module mirrors the semantics of the C# PuddingDataPaths helper so the
Python diagnostics scripts do not each hard-code their own data layout.
"""

from __future__ import annotations

import json
import os
import importlib.util
from dataclasses import dataclass
from pathlib import Path
from types import ModuleType


REPO_ROOT = Path(__file__).resolve().parents[2]


@dataclass(frozen=True)
class PuddingDataPaths:
    data_root: Path

    @classmethod
    def from_root(cls, root: str | Path) -> "PuddingDataPaths":
        value = Path(root).expanduser()
        if not str(value).strip():
            raise ValueError("Data root cannot be empty.")
        return cls(value.resolve())

    @property
    def config_root(self) -> Path:
        return self.data_root / "config"

    @property
    def logs_root(self) -> Path:
        return self.data_root / "logs"

    @property
    def diagnostics_logs_root(self) -> Path:
        return self.logs_root / "diagnostics"

    @property
    def session_logs_root(self) -> Path:
        return self.logs_root / "sessions"

    @property
    def databases_root(self) -> Path:
        return self.data_root / "databases"

    @property
    def temp_root(self) -> Path:
        return self.data_root / "tmp"

    def system_config_file(self, file_name: str) -> Path:
        return self.config_root / file_name

    def platform_db_file(self) -> Path:
        configured = (
            _platform_db_from_environment(os.environ)
            or _platform_db_from_environment(_dev_up_backend_environment())
            or _platform_db_from_appsettings()
        )
        if configured is not None:
            return configured

        primary = self.databases_root / "pudding_platform.db"
        if primary.exists():
            return primary

        legacy = self.data_root / "pudding_platform.db"
        if legacy.exists():
            return legacy

        return primary


def resolve_data_paths(data_root: str | Path | None = None) -> PuddingDataPaths:
    return PuddingDataPaths.from_root(
        data_root
        or os.environ.get("PUDDING_DATA_ROOT")
        or _dev_up_backend_environment().get("PUDDING_DATA_ROOT")
        or REPO_ROOT / "data"
    )


def default_output_root() -> Path:
    return REPO_ROOT / "temp" / "diagnostics"


def _platform_db_from_appsettings() -> Path | None:
    for path in (
        REPO_ROOT / "Source" / "PuddingAgent" / "appsettings.Development.json",
        REPO_ROOT / "Source" / "PuddingAgent" / "appsettings.json",
    ):
        configured = _read_platform_db_from_json(path)
        if configured is not None:
            return configured
    return None


def _read_platform_db_from_json(path: Path) -> Path | None:
    if not path.exists():
        return None

    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return None

    connection_strings = data.get("ConnectionStrings") or {}
    value = connection_strings.get("Default") or connection_strings.get("PlatformDb")
    if not isinstance(value, str) or not value.strip():
        return None

    db_path = _sqlite_data_source(value)
    if db_path is None:
        return None

    return db_path if db_path.is_absolute() else (path.parent / db_path).resolve()


def _platform_db_from_environment(env: dict[str, str]) -> Path | None:
    for key in (
        "ConnectionStrings__Default",
        "ConnectionStrings__PlatformDb",
        "ConnectionStrings:Default",
        "ConnectionStrings:PlatformDb",
    ):
        value = env.get(key)
        if not isinstance(value, str) or not value.strip():
            continue

        db_path = _sqlite_data_source(value)
        if db_path is not None:
            return db_path if db_path.is_absolute() else (REPO_ROOT / db_path).resolve()
    return None


def _dev_up_backend_environment() -> dict[str, str]:
    module = _load_dev_up_module()
    if module is None or not hasattr(module, "backend_environment"):
        return {}

    try:
        env = module.backend_environment()
    except Exception:
        return {}

    return env if isinstance(env, dict) else {}


def _load_dev_up_module() -> ModuleType | None:
    path = REPO_ROOT / "dev-up.py"
    if not path.exists():
        return None

    spec = importlib.util.spec_from_file_location("pudding_dev_up_paths", path)
    if spec is None or spec.loader is None:
        return None

    module = importlib.util.module_from_spec(spec)
    try:
        spec.loader.exec_module(module)
    except Exception:
        return None
    return module


def _sqlite_data_source(connection_string: str) -> Path | None:
    for part in connection_string.split(";"):
        if "=" not in part:
            continue
        key, value = part.split("=", 1)
        if key.strip().lower().replace(" ", "") == "datasource":
            value = value.strip().strip('"')
            return Path(value) if value else None
    return None
