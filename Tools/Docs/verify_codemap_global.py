#!/usr/bin/env python3
"""code_map 波次「全局守恒」校验器（比 verify_codemap_wave.py 覆盖更宽）。

与 `verify_codemap_wave.py` 的关系
----------------------------------
`verify_codemap_wave.py` 是**定向**检查：只看「长表格单元格(>80)」与「长纯段落(>300)」——
它**覆盖不到**短行、围栏内内容、以及被整行改写后的单元格。
本工具是**全局**检查：按改动形态分型，逐单位核验原文是否仍在改后文件中。

分型规则（重要：检查单位必须匹配改动形态）
----------------------------------------
  · 围栏标记行（```）  → 预期被删（fence-forbidden），**跳过并计数**，不判红；
  · 表格行（以 | 开头）→ 逐**单元格**检查（行被改写时整行已变，只有单元格原文会被迁移）；
  · 其他非空行        → 整**行**检查（长行用 32 字符滑窗兜底，容忍软折行）。

比对口径：两侧都去掉全部空白（含全角空格）后做包含判断；长串用 32 字符滑窗、步长 16 兜底。
该口径对软折行鲁棒，且不受「前缀包含」非单调性的影响。

用法
----
    python Tools/Docs/verify_codemap_global.py --old <改动前备份> --new <当前文件>
    python Tools/Docs/verify_codemap_global.py --selftest

退出码：0 = 全部命中；1 = 有内容丢失。
"""

from __future__ import annotations

import argparse
import re
import sys
import tempfile
from pathlib import Path

# 输出编码防御：本工具会打印**文档内容片段**（可能含 emoji）。
# 若调用方的 stdout 是 GBK/cp936，print 会抛 UnicodeEncodeError 并**吞掉 VERDICT 行**。
# 只改 errors、不动 encoding（与 verify_codemap_wave.py 同款防御）。
for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(errors="replace")
    except (AttributeError, ValueError, OSError):
        pass

FENCE = re.compile(r"^\s*```")
SEP = re.compile(r"^\|[\s\-:|]+\|$")
WIN, STEP = 32, 16


def read_text(path: Path) -> str:
    """newline='' ⇒ 不做通用换行转换，保留真实行尾。"""
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def stripped(s: str) -> str:
    return re.sub(r"[\s\u3000]", "", s)


def present(s: str, hay: str) -> bool:
    if s in hay:
        return True
    if len(s) <= WIN:
        return False
    return all(s[i:i + WIN] in hay for i in range(0, len(s) - WIN + 1, STEP))


def check(old_path: Path, new_path: Path, min_len: int = 8) -> int:
    old = read_text(old_path)
    new_s = stripped(read_text(new_path))

    cells = cells_bad = lines = lines_bad = fences = 0
    misses: list[str] = []

    for no, line in enumerate(old.splitlines(), 1):
        if not line.strip():
            continue
        if FENCE.match(line):
            fences += 1
            continue
        if line.startswith("|"):
            if SEP.match(line):
                continue
            for cell in line.split("|")[1:-1]:
                c = stripped(cell)
                if len(c) < min_len:
                    continue
                cells += 1
                if not present(c, new_s):
                    cells_bad += 1
                    if len(misses) < 12:
                        misses.append(f"L{no} CELL: {c[:70]}")
        else:
            s = stripped(line)
            if len(s) < min_len:
                continue
            lines += 1
            if not present(s, new_s):
                lines_bad += 1
                if len(misses) < 12:
                    misses.append(f"L{no} LINE: {s[:70]}")

    print(f"old={old_path}  new={new_path}")
    print(f"table cells checked={cells} missing={cells_bad}")
    print(f"non-table lines checked={lines} missing={lines_bad}")
    print(f"fence marker lines skipped (expected removed)={fences}")
    for m in misses:
        print(f"  MISSING {m}")
    total = cells_bad + lines_bad
    if total:
        print(f"VERDICT: FAIL — {total} unit(s) lost")
        return 1
    print("VERDICT: PASS — every original cell and non-table line survives (whitespace-insensitive)")
    return 0


def selftest() -> int:
    """自带夹具：非重复文本（重复文本会让被截断的尾部窗口在别处命中 ⇒ 假绿）。"""
    cell = "".join(f"c{i:03d}" for i in range(60))     # 240 chars, all windows distinct
    line = "".join(f"p{i:03d}" for i in range(125))    # 500 chars, all windows distinct

    def build(trunc_cell=0, trunc_line=0, drop_row=False):
        c = cell[: len(cell) - trunc_cell] if trunc_cell else cell
        l = line[: len(line) - trunc_line] if trunc_line else line
        rows = "| a.ts | 摘要 |\n| b.ts | 短 |\n" if not drop_row else "| a.ts | 摘要 |\n"
        return ("## 表\n| 文件 | 用途 |\n|---|---|\n" + rows + "\n"
                f"**关键语义**\n**`a.ts`**：{c}\n{l}\n")

    cases = [
        ("G1 内容守恒（单元格缩短 + 原文下沉）", build(), 0),
        ("R1 单元格尾部被截断 40 字符", build(trunc_cell=40), 1),
        ("R2 非表行尾部被截断 40 字符", build(trunc_line=40), 1),
    ]
    failures = 0
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        for name, new_doc, expect in cases:
            o = tmp / "old.md"
            n = tmp / "new.md"
            o.write_text(build(), encoding="utf-8", newline="")
            n.write_text(new_doc, encoding="utf-8", newline="")
            rc = check(o, n)
            ok = rc == expect
            print(f"[{'OK ' if ok else 'BAD'}] {name}: exit={rc} (expect {expect})\n")
            if not ok:
                failures += 1
    print(f"selftest: {len(cases) - failures}/{len(cases)} ok")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description="code_map wave global content-conservation checker")
    ap.add_argument("--old", help="改动前的原始文件（备份）")
    ap.add_argument("--new", help="改动后的当前文件")
    ap.add_argument("--min-len", type=int, default=8, help="忽略短于此长度的单位（默认 8）")
    ap.add_argument("--selftest", action="store_true", help="跑自带夹具自测（含取红用例）")
    args = ap.parse_args()

    if args.selftest:
        return selftest()
    if not args.old or not args.new:
        ap.error("--old and --new are required (or use --selftest)")
    for p in (args.old, args.new):
        if not Path(p).is_file():
            print(f"FAIL: not a file: {p}")
            return 1
    return check(Path(args.old), Path(args.new), args.min_len)


if __name__ == "__main__":
    sys.exit(main())
