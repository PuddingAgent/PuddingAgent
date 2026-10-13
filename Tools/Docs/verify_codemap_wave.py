#!/usr/bin/env python3
"""code_map 波次「内容守恒」校验器（与 code_map_check.py 互补）。

为什么需要它
------------
`code_map_check.py` 只校验**格式**（行长 / 单元格长度等），**不校验内容是否被截断**。
2026-10-14 实测：一次 wave 自报「仅软折行、信息零丢失」，实际把 3 条长纯段落尾部截断
（370/365/444 → 289/283/287 字符），而格式门禁仍然 `error 0 / PASS`。
本工具用**内容守恒判据**补上这个盲区。

为什么判据是「滑动窗口命中率」
------------------------------
以下口径都实测不可靠，不要再用：
  * 逐字 `in`：软折行插入换行即假报丢失；
  * `startswith` 前缀二分：`in` **不满足单调性**（长前缀可能出现在别处），结论无意义；
  * 反引号配对「去折行」：当**原文件本身**就含奇数反引号的行（表格里出现单个反引号等）时，
    会把无关行粘在一起，造出大量假 token ⇒ 本工具据此**把 token 检查降级为信息项（不参与门禁）**。
⇒ 门禁判据只用：把原文按 `window` 字符切窗、以 `step` 步长滑动，统计每窗是否出现在新文本中。
   对软折行鲁棒、不依赖单调性、不受配对影响。

注意（换行）：
  必须用 `newline=''` 打开文件，否则 Python 会做**通用换行转换**（\r\n → \n），
  行尾风格检查会得到假值（实测：PS 报 crlf=335，而默认文本模式读出来是「0 CRLF / 335 LF」）。

退出码：0 = 全部守恒；1 = 发现丢失（可作门禁）。
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

TABLE_SEP = re.compile(r"^\|[\s\-:|]+\|$")
TOKEN = re.compile(r"`([^`\n]+)`")


def read_text(path: Path) -> str:
    """newline='' ⇒ 不做通用换行转换，保留真实行尾。"""
    with open(path, "r", encoding="utf-8", newline="") as fh:
        return fh.read()


def stripped(s: str) -> str:
    """去掉全部空白（含全角空格），用于跨软折行的内容比对。"""
    return re.sub(r"[\s\u3000]", "", s)


def window_hits(needle: str, hay: str, win: int, step: int) -> tuple[int, int, list[str]]:
    """返回 (总窗数, 命中窗数, 未命中窗样本)。needle/hay 均应为已 strip 的文本。"""
    if len(needle) <= win:
        ok = needle in hay
        return 1, (1 if ok else 0), ([] if ok else [needle])
    total = hit = 0
    misses: list[str] = []
    for i in range(0, len(needle) - win + 1, step):
        total += 1
        if needle[i:i + win] in hay:
            hit += 1
        elif len(misses) < 5:
            misses.append(f"offset={i}: {needle[i:i + win]}")
    return total, hit, misses


def defold(lines: list[str]) -> list[str]:
    """把被软折行拆开的反引号跨度重新拼回（按反引号奇偶闭合）。仅供信息项使用。"""
    out: list[str] = []
    buf = ""
    for line in lines:
        buf = line if not buf else buf + line
        if buf.count("`") % 2 == 0:
            out.append(buf)
            buf = ""
    if buf:
        out.append(buf)
    return out


def tokens_of(lines: list[str]) -> set[str]:
    got: set[str] = set()
    for line in lines:
        for m in TOKEN.finditer(line):
            v = stripped(m.group(1))
            if v:
                got.add(v)
    return got


def longest_cell(line: str) -> str:
    parts = line.split("|")
    best = ""
    for part in parts[1:-1]:
        cell = part.strip()
        if len(cell) > len(best):
            best = cell
    return best


def main() -> int:
    ap = argparse.ArgumentParser(description="code_map wave content-conservation checker")
    ap.add_argument("--old", required=True, help="改动前的原始文件（备份）")
    ap.add_argument("--new", required=True, help="改动后的当前文件")
    ap.add_argument("--min-cell", type=int, default=80, help="视为「长单元格」的阈值（默认 80）")
    ap.add_argument("--limit", type=int, default=300, help="行长上限（默认 300）")
    ap.add_argument("--window", type=int, default=32, help="滑动窗口宽度（默认 32）")
    ap.add_argument("--step", type=int, default=16, help="滑动步长（默认 16）")
    args = ap.parse_args()

    old_path, new_path = Path(args.old), Path(args.new)
    for p in (old_path, new_path):
        if not p.is_file():
            print(f"FAIL: not a file: {p}")
            return 1

    old_text = read_text(old_path)
    new_text = read_text(new_path)
    old_lines = old_text.splitlines()
    new_lines = new_text.splitlines()
    new_flat = stripped(new_text)

    problems: list[str] = []

    print("== code_map wave content-conservation check ==")
    print(f"old={old_path}  chars={len(old_text)}  lines={len(old_lines)}")
    print(f"new={new_path}  chars={len(new_text)}  lines={len(new_lines)}")

    # ---- 1. 长表格单元格（任意列）----
    checked = bad = 0
    cell_misses: list[str] = []
    for line in old_lines:
        if not line.startswith("|") or TABLE_SEP.match(line):
            continue
        cell = longest_cell(line)
        if len(cell) <= args.min_cell:
            continue
        checked += 1
        total, hit, misses = window_hits(stripped(cell), new_flat, args.window, args.step)
        if hit < total:
            bad += 1
            problems.append(f"long cell truncated ({hit}/{total}): {cell[:50]}")
            cell_misses.extend(f"    MISS(cell) {m}" for m in misses)
    print(f"[1] long table cells (>{args.min_cell}, any column): checked={checked} truncated={bad}")
    for m in cell_misses:
        print(m)

    # ---- 2. 长非表格行（纯段落）----
    checked_p = bad_p = 0
    para_misses: list[str] = []
    for line in old_lines:
        if line.startswith("|"):
            continue
        s = stripped(line)
        if len(s) <= args.limit:
            continue
        checked_p += 1
        total, hit, misses = window_hits(s, new_flat, args.window, args.step)
        if hit < total:
            bad_p += 1
            problems.append(f"long paragraph truncated ({hit}/{total}): {line.strip()[:50]}")
            para_misses.extend(f"    MISS(para) {m}" for m in misses)
    print(f"[2] long non-table paragraphs (>{args.limit}): checked={checked_p} truncated={bad_p}")
    for m in para_misses:
        print(m)

    # ---- 4. 结构：表格行数 / 数据行数守恒 ----
    def counts(lines: list[str]) -> tuple[int, int]:
        tbl = sep = 0
        for line in lines:
            if line.startswith("|"):
                tbl += 1
                if TABLE_SEP.match(line):
                    sep += 1
        return tbl, tbl - sep
    ot, oe = counts(old_lines)
    nt, ne = counts(new_lines)
    print(f"[3] table rows: old={ot}(entries={oe})  new={nt}(entries={ne})")
    if ot != nt or oe != ne:
        problems.append(f"table structure changed: rows {ot}->{nt}, entries {oe}->{ne}")

    # ---- 5. 行长 / 行尾 ----
    max_len = max((len(l) for l in new_lines), default=0)
    crlf = new_text.count("\r\n")
    lone_lf = len(re.findall(r"(?<!\r)\n", new_text))
    print(f"[4] max_line_len={max_len} (limit {args.limit})  crlf={crlf}  lone_lf={lone_lf}")
    if max_len > args.limit:
        problems.append(f"line too long: {max_len} > {args.limit}")

    # ---- 6. token 守恒（信息项：本文件类型下不可靠，不参与门禁）----
    old_tokens = tokens_of(defold(old_lines))
    new_tokens = tokens_of(defold(new_lines))
    lost = sorted(old_tokens - new_tokens)
    print(f"[info] backtick tokens (defold; NOT gating): old={len(old_tokens)} "
          f"new={len(new_tokens)} lost={len(lost)}")
    for t in lost[:5]:
        print(f"    lost? {t[:90]}")

    if problems:
        print("\nVERDICT: FAIL — content conservation violated")
        for p in problems:
            print(f"  - {p}")
        return 1
    print("\nVERDICT: PASS — no truncation, structure preserved, line length ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
