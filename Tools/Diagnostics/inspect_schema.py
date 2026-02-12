#!/usr/bin/env python3
"""Inspect Pudding diagnostic SQLite schema."""

from __future__ import annotations

import argparse
import json
import sqlite3
from pathlib import Path

from pudding_paths import resolve_data_paths

DEFAULT_DB = resolve_data_paths().platform_db_file()


def inspect_schema(db_path: str | Path = DEFAULT_DB) -> dict[str, list[dict[str, object]]]:
    db_path = Path(db_path)
    if not db_path.exists():
        raise FileNotFoundError(f"SQLite database not found: {db_path}")

    conn = sqlite3.connect(db_path)
    try:
        tables = [
            row[0]
            for row in conn.execute(
                "select name from sqlite_master where type='table' order by name"
            )
        ]
        schema: dict[str, list[dict[str, object]]] = {}
        for table in tables:
            schema[table] = [
                {
                    "cid": row[0],
                    "name": row[1],
                    "type": row[2],
                    "notnull": bool(row[3]),
                    "default": row[4],
                    "pk": bool(row[5]),
                }
                for row in conn.execute(f'pragma table_info("{table.replace("\"", "\"\"")}")')
            ]
        return schema
    finally:
        conn.close()


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect Pudding SQLite table schema.")
    parser.add_argument("--db", default=str(DEFAULT_DB), help="Path to pudding_platform.db")
    parser.add_argument("--format", choices=("table", "json"), default="table")
    args = parser.parse_args()

    schema = inspect_schema(args.db)
    if args.format == "json":
        print(json.dumps(schema, ensure_ascii=False, indent=2))
        return 0

    for table, columns in schema.items():
        print(f"[{table}]")
        for col in columns:
            marker = " pk" if col["pk"] else ""
            print(f"  {col['name']} {col['type']}{marker}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
