#!/usr/bin/env python3
"""Export a local diagnostics bundle for one Pudding chat session."""

from __future__ import annotations

import argparse
import json
import shutil
import zipfile
from datetime import datetime
from pathlib import Path

import query_timeline
from pudding_paths import default_output_root, resolve_data_paths


DEFAULT_DATA_ROOT = resolve_data_paths().data_root
DEFAULT_OUTPUT_ROOT = default_output_root()


def export_bundle(
    *,
    session_id: str,
    data_root: str | Path = DEFAULT_DATA_ROOT,
    output_root: str | Path = DEFAULT_OUTPUT_ROOT,
    frontend_perf: str | Path | None = None,
) -> Path:
    paths = resolve_data_paths(data_root)
    data_root = paths.data_root
    output_root = Path(output_root)
    db_path = paths.platform_db_file()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    bundle_root = output_root / f"session-{session_id}-{stamp}"
    bundle_root.mkdir(parents=True, exist_ok=True)

    timeline = query_timeline.load_timeline(db_path, session_id=session_id, limit=5000)
    (bundle_root / "sqlite-timeline.json").write_text(
        json.dumps(timeline, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    _copy_if_exists(data_root / "jsonl" / f"{session_id}.jsonl", bundle_root / "session-events.jsonl")
    _copy_tree_if_exists(paths.session_logs_root / session_id, bundle_root / "serilog-session")
    _copy_timeline_jsonl(paths.diagnostics_logs_root / "session-timeline", session_id, bundle_root)
    _copy_tree_if_exists(paths.diagnostics_logs_root / "proxy", bundle_root / "proxy")

    if frontend_perf:
        _copy_if_exists(Path(frontend_perf), bundle_root / "frontend-perf.json")

    manifest = {
        "schemaVersion": 1,
        "sessionId": session_id,
        "createdAtLocal": datetime.now().isoformat(timespec="seconds"),
        "dataRoot": str(data_root),
        "timelineRows": len(timeline),
    }
    (bundle_root / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    zip_path = output_root / f"{bundle_root.name}.zip"
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in bundle_root.rglob("*"):
            if path.is_file():
                archive.write(path, path.relative_to(bundle_root))
    return zip_path


def _copy_if_exists(source: Path, target: Path) -> None:
    if not source.exists():
        return
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)


def _copy_tree_if_exists(source: Path, target: Path) -> None:
    if not source.exists():
        return
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target)


def _copy_timeline_jsonl(source_root: Path, session_id: str, bundle_root: Path) -> None:
    if not source_root.exists():
        return
    target_root = bundle_root / "diagnostic-timeline"
    for source in source_root.glob(f"*/{session_id}.jsonl"):
        target = target_root / source.parent.name / source.name
        _copy_if_exists(source, target)


def main() -> int:
    parser = argparse.ArgumentParser(description="Export a Pudding diagnostics bundle.")
    parser.add_argument("--session-id", required=True)
    parser.add_argument("--data-root", default=None)
    parser.add_argument("--output-root", default=str(DEFAULT_OUTPUT_ROOT))
    parser.add_argument("--frontend-perf", help="Optional frontend pudding-perf JSON file")
    args = parser.parse_args()

    zip_path = export_bundle(
        session_id=args.session_id,
        data_root=args.data_root,
        output_root=args.output_root,
        frontend_perf=args.frontend_perf,
    )
    print(zip_path)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
