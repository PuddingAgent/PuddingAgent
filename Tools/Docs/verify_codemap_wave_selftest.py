#!/usr/bin/env python3
"""verify_codemap_wave.py 自测（自带夹具，可随时复跑；同时证明该门禁「能取红」）。

跑法：
    python Tools/Docs/verify_codemap_wave_selftest.py

用例：
  G1 内容守恒（单元格缩短 + 原文下沉为段落）⇒ 期望 PASS(0)
  R1 段落尾部被截断 60 字符           ⇒ 期望 FAIL(1)   ← 取红
  R2 单元格尾部被截断                  ⇒ 期望 FAIL(1)   ← 取红
  R3 行长超过 300                      ⇒ 期望 FAIL(1)   ← 取红
  G2 表格行数被改变                    ⇒ 期望 FAIL(1)   ← 取红（结构守恒）
"""

from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOL = HERE / "verify_codemap_wave.py"
PY = sys.executable
# NOTE: fixtures must use NON-repetitive text: with periodic text a truncated tail's 32-char
# window recurs earlier in the document, so a window-hit test would wrongly report "present".
LONG = "".join(f"c{i:03d}" for i in range(60))    # 240 chars, all windows distinct
PARA = "".join(f"p{i:03d}" for i in range(125))   # 500 chars, all windows distinct


def wrap(s: str, n: int = 200) -> str:
    """按固定宽度切行（纯粹插换行，一字符不丢），保证行长不超限。"""
    return "\n".join(s[i:i + n] for i in range(0, len(s), n))


def old_doc() -> str:
    return (
        "## 表\n"
        "| 文件 | 用途 |\n"
        "|---|---|\n"
        f"| a.ts | {LONG} |\n"
        "| b.ts | 短 |\n"
        "\n"
        f"{PARA}\n"
    )


def new_doc(*, truncate_cell: int = 0, truncate_para: int = 0, long_line: int = 0,
            drop_row: bool = False) -> str:
    cell = LONG[: len(LONG) - truncate_cell] if truncate_cell else LONG
    para = PARA[: len(PARA) - truncate_para] if truncate_para else PARA
    # 内容守恒的典型形态：单元格缩短为摘要，原文逐字下沉为表后段落
    rows = "| a.ts | 摘要 |\n| b.ts | 短 |\n"
    if drop_row:
        rows = "| a.ts | 摘要 |\n"
    body = (
        "## 表\n"
        "| 文件 | 用途 |\n"
        "|---|---|\n"
        f"{rows}"
        "\n"
        f"**关键语义**\n**`a.ts`**：{wrap(cell)}\n"
        f"{wrap(para)}\n"
    )
    if long_line:
        body += "x" * long_line + "\n"
    return body


def run(old: str, new: str, tmp: Path) -> tuple[int, str]:
    o, n = tmp / "old.md", tmp / "new.md"
    o.write_text(old, encoding="utf-8", newline="")
    n.write_text(new, encoding="utf-8", newline="")
    proc = subprocess.run(
        [PY, str(TOOL), "--old", str(o), "--new", str(n)],
        capture_output=True, text=True, encoding="utf-8",
    )
    return proc.returncode, (proc.stdout or "") + (proc.stderr or "")


def main() -> int:
    failures = 0
    cases = [
        ("G1 content conserved (cell shortened + text moved)", old_doc(), new_doc(), 0),
        ("R1 paragraph tail truncated (60 chars)", old_doc(), new_doc(truncate_para=60), 1),
        ("R2 cell tail truncated (60 chars)", old_doc(), new_doc(truncate_cell=60), 1),
        ("R3 line length over limit", old_doc(), new_doc(long_line=320), 1),
        ("G2 table row removed (structure change)", old_doc(), new_doc(drop_row=True), 1),
    ]
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for name, old, new, expect in cases:
            rc, out = run(old, new, tmp)
            ok = rc == expect
            verdict = "PASS" if rc == 0 else "FAIL"
            print(f"[{'OK ' if ok else 'BAD'}] {name}: exit={rc} (expect {expect}) verdict={verdict}")
            if not ok:
                failures += 1
                for line in out.splitlines()[-6:]:
                    print(f"       | {line}")
    print(f"\nselftest: {len(cases) - failures}/{len(cases)} ok")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
