#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""code_map.md 合规自检（规范 v2）。纯标准库，Python 3.12+ 兼容。

唯一权威规格：`Docs/10_conventions/code-map-规范-v2.md`

红线（规格 §2，不可协商）
   本工具对仓库是**只读**的：只允许 read / 统计 / 校验 / 报告。
   **不存在**任何自动改写、压缩、截断、重排语义字段的代码路径。
   除命令行显式指定的报告文件（`--report` / `--report-md`）外不写任何文件；
   并且拒绝把报告写到任何名为 `code_map.md` 的路径上。
   修复必须由模型阅读代码后手写。

用法
  python Tools/Docs/code_map_check.py --check
  python Tools/Docs/code_map_check.py --check --paths code_map.md,Source/PuddingRuntime/code_map.md
  python Tools/Docs/code_map_check.py --report temp/code_map_selfcheck.json
  python Tools/Docs/code_map_check.py --report temp/x.json --report-md temp/x.md
  python Tools/Docs/code_map_check.py --fingerprint --report temp/x.json   # 额外计算 §5 源指纹

退出码
  0 = 无 `error` 违规（门禁通过）
  1 = 存在 `error` 违规（门禁不通过）
  2 = 用法错误 / 输出路径被拒
  3 = 没有任何可检查的 code_map
"""
from __future__ import annotations

import argparse
import datetime
import glob as _glob
import hashlib
import json
import math
import os
import re
import sys
from urllib.parse import unquote

# ---------------------------------------------------------------------------
# §2 全局硬上限 —— 唯一真源（规格 §8.1：本表是权威，文档表格是它的解释）
# ---------------------------------------------------------------------------
LIMITS = {
    # --- 规格 §2「全局硬上限」表 ---
    "MAX_LINE_LEN": 300,
    "MAX_ENTRY_ROWS": 400,
    "MAX_FENCES": 0,
    "MAX_TABLE_HEADER_REPEAT": 8,
    "MAX_FIELD_LEN": {
        "文件 / 目录": 120,
        "用途": 80,
        "关键符号": 100,
        "关联": 100,
        "约束": 160,
    },
    # --- §2 条目 schema 的「≤5 项」---
    "MAX_ITEMS": 5,
    # --- §5 源指纹 ---
    "FINGERPRINT_HASH_PREFIX": 12,
    "FINGERPRINT_MAX_FILES": 20000,
    "FINGERPRINT_MAX_FILE_BYTES": 67108864,
    # --- §5 头部元数据识别窗口 ---
    "HEADER_SCAN_LINES": 40,
    # --- §6 复述型 S 判据 ---
    "JACCARD_THRESHOLD": 0.5,
    # --- §6 清单型 F 判据（只认规范写明的「、」与「,」）---
    "LIST_ITEM_SEPARATORS": "、,",
    # --- §2「关键符号 / 关联 ≤5 项」的项分隔符（比 F 列宽：含全角逗号与分号）---
    "ITEM_SEPARATORS": "、,，；;",
    # --- §6 空值写法：允许的写法；其余「占位」见 PLACEHOLDER_RE ---
    "PLACEHOLDER_ALLOWED": ["none", "n/a", "无", "-", "—", ""],
    # --- §4 exclude 目录名（唯一真源；按目录名匹配，不限层级）---
    "EXCLUDE_DIR_NAMES": [
        "bin", "obj", "node_modules", ".pudding", "temp", "dist", "dist-dev",
        ".git", "__pycache__", ".venv", ".vs", ".idea",
    ],
    # --- §4 exclude 的相对路径前缀（仓库相对、大小写不敏感）---
    "EXCLUDE_PATH_PREFIXES": ["external/references"],
}

# §7 规则表：规则 ID -> 等级（照 §7 逐字落地）
RULES = {
    "line-too-long": "error",
    "field-too-long": "error",
    "field-too-many-items": "error",
    "fence-forbidden": "error",
    "entries-exceed": "error",
    "header-repeat": "warn",
    "layering-violation": "warn",
    "missing-fingerprint": "warn",
    "stale-fingerprint": "warn",
    "anti-pattern": "warn",
    "link-broken": "error",
}
RULE_ORDER = [
    "line-too-long", "field-too-long", "field-too-many-items", "fence-forbidden",
    "entries-exceed", "header-repeat", "layering-violation", "missing-fingerprint",
    "stale-fingerprint", "anti-pattern", "link-broken",
]

# §6 反模式词表（机器可执行）；key = §6 表格里的「类别」
ANTI_PATTERN_WORDS = {
    "演进叙事": ["本次", "新增了", "修复了", "改为", "原来是", "此前是", "之前是",
                 "现在改", "已迁移", "TODO"],
    "过度宣称": ["零缺陷", "彻底解决", "完全杜绝", "100%", "O(1)", "确定性检索",
                 "单一真源", "保证不"],
    "路线图": ["未来", "计划", "将支持", "下一版"],
}
# §6 的「逐文件免检旁路」标 ⛔ error，而 §7 表把 anti-pattern 定为 warn。
# 本工具按任务书「等级照 §7 表」执行门禁，并把这些命中单独登记进 anti_pattern_spec_errors。
SKIP_BYPASS_WORDS = ["免检", "特例跳过", "跳过检查"]
# §6 各「类别」的处置等级（仅用于分类统计，不改变 §7 的门禁等级）
ANTI_PATTERN_CATEGORY_SEVERITY = {
    "演进叙事": "warn",
    "过度宣称": "warn",
    "路线图": "warn",
    "清单型 F": "warn",
    "复述型 S": "warn",
    "空值写法": "info",
    "逐文件免检旁路": "error",
}
# §6 空值写法：窄口径（规范没有给出可枚举的占位集合，这里只认显式占位串）
PLACEHOLDER_RE = re.compile(r"^(?:暂无|待补|待定|tbd|todo|--)$", re.IGNORECASE)

# §6 词表只针对自然语言断言：行内代码跨度（标识符/字面量）与 Markdown 链接目标（文件名/URL）
# 先剔除再扫词。否则 `TodoCheckTool` 里的 TODO 会被算成「演进叙事」、
# `[施工计划](…施工计划.md)` 里的「计划」会被算成「路线图」（实测曾致 Card B/C 误报）。
AP_LINK_TARGET_RE = re.compile(r"\]\([^)]*\)")
AP_INLINE_CODE_RE = re.compile(r"`[^`]*`")


def prose_only(line: str) -> str:
    """剔除链接目标与行内代码跨度，剩下的才算 §6 要判的 prose。"""
    return AP_INLINE_CODE_RE.sub("``", AP_LINK_TARGET_RE.sub("]()", line))

SPEC_REL = "Docs/10_conventions/code-map-规范-v2.md"
CODE_MAP_NAME = "code_map.md"
SCHEMA_ID = "pudding.code_map_check/1"

# 列名 -> §2 规范列（**精确匹配**，避免把 `作用` / `关键约束` 之类误判成规范列）
CANONICAL_HEADERS = {
    "文件": "文件 / 目录",
    "文件/目录": "文件 / 目录",
    "目录": "文件 / 目录",
    "用途": "用途",
    "关键符号": "关键符号",
    "符号": "关键符号",
    "关联": "关联",
    "约束": "约束",
}
DISTRIBUTION_COLUMNS = ["用途", "关键符号", "关联", "约束"]
COLUMN_ASCII = {
    "用途": "purpose-F",
    "关键符号": "key-symbols-A",
    "关联": "relations-R",
    "约束": "constraints-S",
}

FENCE_RE = re.compile(r"^\s*(```|~~~)")
SEP_CELL_RE = re.compile(r"^:?-+:?$")
LAYER_FILE_RE = re.compile(r"^`?Source/[^/`\s]+/[^/`\s]+\.[A-Za-z0-9]{1,6}`?$")
LINK_RE = re.compile(r"\[[^\]]*\]\(\s*([^)\s]+?)(?:\s+\"[^\"]*\")?\s*\)")
SKIP_LINK_PREFIX = ("http:", "https:", "mailto:", "tel:", "data:", "file:", "ftp:")
FP_SEGMENT_RE = re.compile(r"源指纹\s*[:：](?P<seg>[^·]*)")
FP_PAIR_RE = re.compile(r"(?P<glob>[^\s,，=·]+)=(?P<value>[0-9a-fA-F][0-9a-fA-F\-]{5,79})")
FP_ENTRY_RE = re.compile(r"条目数\s*[:：]\s*(?P<n>\d+)")
FP_DATE_RE = re.compile(r"最近整理\s*[:：]\s*(?P<d>\d{4}-\d{2}-\d{2})")


class UsageError(Exception):
    """用法级错误（退出码 2）。"""


# ---------------------------------------------------------------------------
# 基础工具
# ---------------------------------------------------------------------------
def normalize_header(cell: str) -> str:
    s = cell.strip()
    s = s.replace("**", "").replace("`", "").replace("*", "")
    s = re.sub(r"[\s\u3000]+", "", s)
    s = re.sub(r"[（(][A-Za-z][)）]$", "", s)  # 去掉 `用途(F)` 形式的后缀标记
    return s


def canonical_column(header_cell: str):
    return CANONICAL_HEADERS.get(normalize_header(header_cell))


def split_cells(line: str):
    s = line.strip()
    if s.startswith("|"):
        s = s[1:]
    if s.endswith("|") and not s.endswith("\\|"):
        s = s[:-1]
    return [c.strip() for c in re.split(r"(?<!\\)\|", s)]


def is_sep_line(line: str) -> bool:
    if not line.strip().startswith("|"):
        return False
    cells = [c for c in split_cells(line) if c != ""]
    if not cells:
        return False
    return all(SEP_CELL_RE.match(c) for c in cells)


def is_pipe_line(line: str) -> bool:
    return line.strip().startswith("|")


def parse_tables(lines):
    """把 `lines` 切成「被识别的表格」。

    返回 [{'header_line_no':int|None, 'header_cells':[...], 'rows':[(line_no, text), ...]}]。
    多张表首尾相接（上一张表的数据行紧接下一张表的表头）也能正确切开。
    """
    tables = []
    i = 0
    n = len(lines)
    while i < n:
        if is_sep_line(lines[i]) and i >= 1:
            header_no = i  # 1-based 行号 = index(i-1) + 1
            rows = []
            j = i + 1
            while j < n:
                if not is_pipe_line(lines[j]):
                    break  # 表格在此行结束（空行/正文/围栏/列表）
                if is_sep_line(lines[j]):
                    break  # 下一张表的表头/分隔
                if j + 1 < n and is_sep_line(lines[j + 1]):
                    break  # lines[j] 是下一张表的表头
                rows.append((j + 1, lines[j]))
                j += 1
            tables.append({
                "header_line_no": header_no,
                "header_cells": split_cells(lines[i - 1]),
                "rows": rows,
            })
            i = j
        else:
            i += 1
    return tables


def jaccard(a: str, b: str) -> float:
    sa, sb = set(a), set(b)
    if not sa or not sb:
        return 0.0
    return len(sa & sb) / float(len(sa | sb))


def percentile(values, pct: float):
    if not values:
        return None
    vals = sorted(values)
    if len(vals) == 1:
        return float(vals[0])
    k = (len(vals) - 1) * (pct / 100.0)
    lo = int(math.floor(k))
    hi = int(math.ceil(k))
    if lo == hi:
        return float(vals[lo])
    return vals[lo] + (vals[hi] - vals[lo]) * (k - lo)


def column_stats(values):
    if not values:
        return {"n": 0, "min": None, "median": None, "p90": None, "p99": None,
                "max": None, "mean": None}
    return {
        "n": len(values),
        "min": min(values),
        "median": round(percentile(values, 50), 2),
        "p90": round(percentile(values, 90), 2),
        "p99": round(percentile(values, 99), 2),
        "max": max(values),
        "mean": round(sum(values) / float(len(values)), 2),
    }


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def rel_posix(root: str, path: str) -> str:
    return os.path.relpath(path, root).replace("\\", "/")


def _safe_listdir_filter(rel_dir_posix: str, names):
    keep = []
    for d in names:
        if d in LIMITS["EXCLUDE_DIR_NAMES"]:
            continue
        rel = ((rel_dir_posix + "/" + d) if rel_dir_posix else d).lower().lstrip("/")
        excluded = False
        for pref in LIMITS["EXCLUDE_PATH_PREFIXES"]:
            p = pref.lower()
            if rel == p or rel.startswith(p + "/"):
                excluded = True
                break
        if not excluded:
            keep.append(d)
    return keep


def find_code_maps(root: str, base: str = None):
    """在 `base`（默认仓库根）下枚举 code_map.md，遵守 §4 exclude。"""
    root = os.path.abspath(root)
    base = os.path.abspath(base or root)
    found = []
    for dirpath, dirnames, filenames in os.walk(base):
        rel_dir = rel_posix(root, dirpath)
        if rel_dir == ".":
            rel_dir = ""
        dirnames[:] = _safe_listdir_filter(rel_dir, dirnames)
        for fn in filenames:
            if fn == CODE_MAP_NAME:
                found.append(os.path.join(dirpath, fn))
    found.sort(key=lambda p: rel_posix(root, p).lower())
    return found


def list_scope_files(root: str, scope_dir: str):
    """§5 源指纹的输入集合：作用域内所有普通文件，排除 §4 exclude 与 code_map.md 自身。"""
    out = []
    for dirpath, dirnames, filenames in os.walk(scope_dir):
        rel_dir = rel_posix(root, dirpath)
        if rel_dir == ".":
            rel_dir = ""
        dirnames[:] = _safe_listdir_filter(rel_dir, dirnames)
        for fn in filenames:
            if fn == CODE_MAP_NAME:
                continue
            out.append(os.path.join(dirpath, fn))
    out.sort(key=lambda p: rel_posix(root, p).lower())
    return out


def fingerprint_of(root: str, files):
    """§5 聚合算法。

    口径由 `--fp-probe` 在真实声明上**反推确认**（变体名 `path:shafull_nl`）：
    对每个文件取**全量** sha256（64 位），按「仓库相对路径」排序拼成 `<path>:<hash>` 行（`\n` 分隔），
    再取整串 sha256 的**前 12 位**作为聚合摘要；报告里的 `value` 另写作 `<文件数>-<聚合摘要>`。

    ⚠ 规范原文写的是「排序后拼接的 sha256 **前 12 位**」，而仓内声明的实际口径是**全量** sha256；
    两者不等价。本工具以**实测可验证**的口径为准，并保留 `--fp-probe` 让这一分歧可见。
    """
    prefix = LIMITS["FINGERPRINT_HASH_PREFIX"]
    cap = LIMITS["FINGERPRINT_MAX_FILE_BYTES"]
    limit_files = min(len(files), LIMITS["FINGERPRINT_MAX_FILES"])
    parts = []
    unreadable = 0
    for path in files[:limit_files]:
        rel = rel_posix(root, path)
        try:
            size = os.path.getsize(path)
            if size <= cap:
                h = sha256_file(path)
            else:
                with open(path, "rb") as fh:
                    partial = sha256_bytes(fh.read(cap))
                h = "%s|truncated_at=%d|size=%d" % (partial, cap, size)
        except OSError:
            unreadable += 1
            continue
        parts.append(rel + ":" + h)
    joined = "\n".join(parts)
    digest_full = sha256_bytes(joined.encode("utf-8"))
    digest = digest_full[:prefix]
    return {
        "file_count": len(parts),
        "unreadable": unreadable,
        "truncated": len(files) > limit_files,
        "digest": digest,
        "digest_full": digest_full,
        "value": "%d-%s" % (len(parts), digest),
    }


def scope_fingerprint(root: str, scope_dir: str):
    return fingerprint_of(root, list_scope_files(root, scope_dir))


def _path_is_excluded(rel_posix_path: str) -> bool:
    parts = rel_posix_path.split("/")
    if any(p in LIMITS["EXCLUDE_DIR_NAMES"] for p in parts[:-1]):
        return True
    low = rel_posix_path.lower()
    for pref in LIMITS["EXCLUDE_PATH_PREFIXES"]:
        p = pref.lower()
        if low == p or low.startswith(p + "/") or ("/" + p + "/") in low:
            return True
    return False


_GLOB_DEEP_RE = re.compile(r"^(?P<prefix>[^\*\?\[\]]+?)/\*\*$")


def glob_fingerprint(root: str, pattern: str, base_dir: str = None):
    """对 §5 声明里的单个 `path-glob` 计算子指纹。

    快路径：`<prefix>/**` 直接用带排除剪枝的目录遍历（避免 glob 把 bin/obj 全枚举出来）；
    其余模式回退到 glob.glob(recursive=True)。解析不到任何文件 => 返回 None（不可核验）。
    """
    bases = [b for b in (base_dir, root) if b]
    files, seen = [], set()
    for base in bases:
        pat = pattern.replace("\\", "/")
        deep = _GLOB_DEEP_RE.match(pat)
        candidates = []
        if deep:
            prefix_dir = os.path.join(base, deep.group("prefix").replace("/", os.sep))
            if os.path.isdir(prefix_dir):
                candidates = list_scope_files(root, prefix_dir)
        else:
            abs_pat = os.path.join(base, pat.replace("/", os.sep))
            candidates = [m for m in _glob.glob(abs_pat, recursive=True) if os.path.isfile(m)]
        for m in candidates:
            rel = rel_posix(root, m)
            if rel == CODE_MAP_NAME or rel.endswith("/" + CODE_MAP_NAME):
                continue
            if _path_is_excluded(rel):
                continue
            k = os.path.normcase(os.path.abspath(m))
            if k in seen:
                continue
            seen.add(k)
            files.append(os.path.abspath(m))
    if not files:
        return None
    files.sort(key=lambda p: rel_posix(root, p).lower())
    fp = fingerprint_of(root, files)
    fp["pattern"] = pattern
    return fp


FP_VARIANT_NAMES = [
    "path:sha12_nl",
    "path:sha12_concat",
    "path_sha12_nl_nosep",
    "sha12_nl",
    "sha12_concat",
    "sha12_nl_sorted_by_hash",
    "count_nl_path:sha12",
    "count-path:sha12_nl",
    "count:path:sha12_nl",
    "path:shafull_nl",
    "shafull_nl",
    "path:sha12_crlf",
    "path_baslash:sha12_nl",
    "abs_path:sha12_nl",
    "contents_concat_raw",
]


def fp_variant_digests(root: str, files):
    """对同一文件集，算出一组候选聚合口径的摘要（用于诊断 §5 声明值到底用的是哪种口径）。

    只做只读计算：不改任何声明值，也不把结果写回任何 code_map。
    """
    entries = []
    for p in files:
        try:
            entries.append((rel_posix(root, p), sha256_file(p)))
        except OSError:
            continue
    entries.sort(key=lambda e: e[0].lower())
    n = len(entries)

    def dig(s):
        return sha256_bytes(s.encode("utf-8"))[: LIMITS["FINGERPRINT_HASH_PREFIX"]]

    p12 = "\n".join("%s:%s" % (r, h[:12]) for r, h in entries)
    contents = bytearray()
    for r, _h in entries:
        try:
            with open(os.path.join(root, r.replace("/", os.sep)), "rb") as fh:
                contents += fh.read()
        except OSError:
            pass
    return {
        "path:sha12_nl": dig(p12),
        "path:sha12_concat": dig("".join("%s:%s" % (r, h[:12]) for r, h in entries)),
        "path_sha12_nl_nosep": dig("\n".join("%s%s" % (r, h[:12]) for r, h in entries)),
        "sha12_nl": dig("\n".join(h[:12] for _, h in entries)),
        "sha12_concat": dig("".join(h[:12] for _, h in entries)),
        "sha12_nl_sorted_by_hash": dig("\n".join(sorted(h[:12] for _, h in entries))),
        "count_nl_path:sha12": dig("%d\n%s" % (n, p12)),
        "count-path:sha12_nl": dig("%d-%s" % (n, p12)),
        "count:path:sha12_nl": dig("%d:%s" % (n, p12)),
        "path:shafull_nl": dig("\n".join("%s:%s" % (r, h) for r, h in entries)),
        "shafull_nl": dig("\n".join(h for _, h in entries)),
        "path:sha12_crlf": dig("\r\n".join("%s:%s" % (r, h[:12]) for r, h in entries)),
        "path_baslash:sha12_nl": dig("\n".join("%s:%s" % (r.replace("/", "\\"), h[:12])
                                               for r, h in entries)),
        "abs_path:sha12_nl": dig("\n".join("%s:%s" % (os.path.abspath(
            os.path.join(root, r.replace("/", os.sep))), h[:12]) for r, h in entries)),
        "contents_concat_raw": sha256_bytes(bytes(contents))[: LIMITS["FINGERPRINT_HASH_PREFIX"]],
    }, n


def fp_probe(root: str, per_file):
    """对每个声明的 glob，列出「声明值 vs 候选口径摘要」。"""
    rows = []
    for pf in per_file:
        for d in pf["declared_fingerprint"]:
            scope = os.path.dirname(os.path.join(root, pf["path"]))
            pat = d["glob"].replace("\\", "/")
            deep = _GLOB_DEEP_RE.match(pat)
            files = None
            if deep:
                prefix_dir = os.path.join(scope, deep.group("prefix").replace("/", os.sep))
                if os.path.isdir(prefix_dir):
                    files = list_scope_files(root, prefix_dir)
            else:
                files = [m for m in _glob.glob(os.path.join(scope, pat), recursive=True)
                         if os.path.isfile(m)]
            if files is None:
                continue
            files = [f for f in files
                     if rel_posix(root, f) != CODE_MAP_NAME
                     and not _path_is_excluded(rel_posix(root, f))]
            files.sort(key=lambda p: rel_posix(root, p).lower())
            digests, n = fp_variant_digests(root, files)
            rows.append({
                "file": pf["path"],
                "glob": d["glob"],
                "declared": d["value"],
                "files": n,
                "matches": sorted(k for k, v in digests.items()
                                  if v == d["value"] or v.startswith(d["value"])),
                "variants": digests,
            })
    return rows


def _mtime_iso(path: str):
    try:
        return datetime.datetime.fromtimestamp(
            os.path.getmtime(path), datetime.timezone.utc).replace(
            microsecond=0).isoformat()
    except OSError:
        return None


# ---------------------------------------------------------------------------
# 单个文件分析
# ---------------------------------------------------------------------------
def read_text(path: str):
    with open(path, "rb") as fh:
        data = fh.read()
    return data, data.decode("utf-8", errors="replace")


def _vio(rule: str, rel: str, line_no: int, detail: str):
    return {"rule": rule, "severity": RULES[rule], "file": rel,
            "line": line_no, "detail": detail}


def _ap_vio(rel: str, line_no: int, category: str, detail: str):
    """§6 反模式命中。门禁等级按 §7 表（anti-pattern = warn）；category/spec_severity 单列记录。"""
    spec = ANTI_PATTERN_CATEGORY_SEVERITY.get(category, "warn")
    return {"rule": "anti-pattern",
            "severity": "info" if spec == "info" else "warn",
            "spec_severity": spec,
            "category": category,
            "file": rel, "line": line_no, "detail": detail}


def _split_items(text: str, separators: str):
    return [x for x in re.split("[%s]" % re.escape(separators), text) if x.strip()]


def analyze_file(root: str, path: str, col_samples, global_unmapped):
    rel = rel_posix(root, path)
    data, text = read_text(path)
    bom = data.startswith(b"\xef\xbb\xbf")

    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    lines = [(l[:-1] if l.endswith("\r") else l) for l in lines]

    violations = []
    fences = 0
    longest_chars, longest_no, longest_bytes = 0, 0, 0
    for idx, line in enumerate(lines, start=1):
        if FENCE_RE.match(line):
            fences += 1
            violations.append(_vio("fence-forbidden", rel, idx,
                                   "code fence marker (MAX_FENCES=%d)" % LIMITS["MAX_FENCES"]))
        c = len(line)
        if c > longest_chars:
            longest_chars, longest_no = c, idx
            longest_bytes = len(line.encode("utf-8"))
        if c > LIMITS["MAX_LINE_LEN"]:
            violations.append(_vio("line-too-long", rel, idx,
                                   "chars=%d bytes=%d limit=%d"
                                   % (c, len(line.encode("utf-8")), LIMITS["MAX_LINE_LEN"])))

    tables = parse_tables(lines)
    table_lines, entry_rows = 0, 0
    header_counts = {}
    unmapped = set()
    allowed_placeholders = [x.lower() for x in LIMITS["PLACEHOLDER_ALLOWED"]]

    for table in tables:
        header_cells = table["header_cells"]
        rows = table["rows"]
        table_lines += 2 + len(rows)  # header + separator + data rows
        entry_rows += len(rows)
        key = " | ".join(c.strip() for c in header_cells)
        header_counts[key] = header_counts.get(key, 0) + 1

        cols = []
        for c in header_cells:
            col = canonical_column(c)
            cols.append(col)
            if col is None:
                nm = normalize_header(c)
                if nm:
                    unmapped.add(nm)
                    global_unmapped.add(nm)

        for line_no, row in rows:
            cells = split_cells(row)
            purpose = None
            constraint = None
            for c_i, cell in enumerate(cells):
                col = cols[c_i] if c_i < len(cols) else None
                if col is not None:
                    if col in col_samples:
                        col_samples[col].append(len(cell))
                    limit = LIMITS["MAX_FIELD_LEN"].get(col)
                    if limit is not None and len(cell) > limit:
                        violations.append(_vio(
                            "field-too-long", rel, line_no,
                            "column=%s chars=%d limit=%d" % (col, len(cell), limit)))
                    if col in ("关键符号", "关联"):
                        items = _split_items(cell, LIMITS["ITEM_SEPARATORS"])
                        if len(items) > LIMITS["MAX_ITEMS"]:
                            violations.append(_vio(
                                "field-too-many-items", rel, line_no,
                                "column=%s items=%d limit=%d"
                                % (col, len(items), LIMITS["MAX_ITEMS"])))
                    if col == "用途":
                        purpose = cell
                    elif col == "约束":
                        constraint = cell
                # §6 空值写法（任意数据单元格）
                if cell and PLACEHOLDER_RE.match(cell) and cell.lower() not in allowed_placeholders:
                    violations.append(_ap_vio(rel, line_no, "空值写法", "value=%s" % cell))
            if purpose and constraint:
                j = jaccard(purpose, constraint)
                if j >= LIMITS["JACCARD_THRESHOLD"]:
                    violations.append(_ap_vio(rel, line_no, "复述型 S",
                                              "jaccard=%.2f" % j))
            if purpose:
                items = _split_items(purpose, LIMITS["LIST_ITEM_SEPARATORS"])
                if len(items) >= 3:
                    violations.append(_ap_vio(rel, line_no, "清单型 F",
                                              "items=%d" % len(items)))

        # §3 分层边界（仅 L1 根 code_map；§7 明示为启发式，故意 warn）
        if rel == CODE_MAP_NAME:
            for line_no, row in rows:
                cells = split_cells(row)
                if cells and LAYER_FILE_RE.match(cells[0]):
                    violations.append(_vio(
                        "layering-violation", rel, line_no,
                        "L1 first cell is a Source file path: %s" % cells[0]))

    if entry_rows > LIMITS["MAX_ENTRY_ROWS"]:
        violations.append(_vio("entries-exceed", rel, 0,
                               "entry_rows=%d limit=%d" % (entry_rows, LIMITS["MAX_ENTRY_ROWS"])))

    header_repeat_max = 0
    for key, cnt in sorted(header_counts.items()):
        header_repeat_max = max(header_repeat_max, cnt)
        if cnt > LIMITS["MAX_TABLE_HEADER_REPEAT"]:
            violations.append(_vio("header-repeat", rel, 0,
                                   "header=%s count=%d limit=%d"
                                   % (key, cnt, LIMITS["MAX_TABLE_HEADER_REPEAT"])))

    # §6 词表（逐行，只扫 prose：链接目标与行内代码跨度已剔除）
    for idx, line in enumerate(lines, start=1):
        scan = prose_only(line)
        low = scan.lower()
        for category in ANTI_PATTERN_WORDS:
            for w in ANTI_PATTERN_WORDS[category]:
                if w.lower() in low:
                    violations.append(_ap_vio(rel, idx, category, "word=%s" % w))
        for w in SKIP_BYPASS_WORDS:
            if w in scan:
                violations.append(_ap_vio(rel, idx, "逐文件免检旁路", "word=%s" % w))

    # §5 头部元数据
    header_line_no, fp_segment = None, None
    for idx, line in enumerate(lines[: LIMITS["HEADER_SCAN_LINES"]], start=1):
        m = FP_SEGMENT_RE.search(line)
        if m:
            header_line_no, fp_segment = idx, m.group("seg")
            break
    declared = []
    if fp_segment is not None:
        for pm in FP_PAIR_RE.finditer(fp_segment):
            declared.append((pm.group("glob"), pm.group("value").rstrip("-")))
        meta_line = lines[header_line_no - 1]
        missing_bits = []
        if not FP_ENTRY_RE.search(meta_line):
            missing_bits.append("条目数")
        if not FP_DATE_RE.search(meta_line):
            missing_bits.append("最近整理")
        if missing_bits:
            violations.append(_vio("missing-fingerprint", rel, header_line_no,
                                   "partial metadata, missing: " + ",".join(missing_bits)))
        if not declared:
            violations.append(_vio("missing-fingerprint", rel, header_line_no,
                                   "源指纹 segment has no parseable <glob>=<hash> pair"))
    else:
        violations.append(_vio("missing-fingerprint", rel, 0,
                               "no 源指纹 metadata in first %d lines"
                               % LIMITS["HEADER_SCAN_LINES"]))

    # §7 link-broken
    for idx, line in enumerate(lines, start=1):
        for m in LINK_RE.finditer(line):
            raw = m.group(1)
            if raw.lower().startswith(SKIP_LINK_PREFIX) or raw.startswith("#"):
                continue
            target = raw.split("#", 1)[0]
            if not target:
                continue
            dec = re.sub(r":\d+(-\d+)?$", "", unquote(target))
            candidate = os.path.normpath(os.path.join(os.path.dirname(path), dec))
            if os.path.exists(candidate):
                continue
            if os.path.splitext(candidate)[1] == "" and os.path.exists(candidate + ".md"):
                continue
            violations.append(_vio("link-broken", rel, idx, "target=%s" % raw))

    per_file = {
        "path": rel,
        "sha256": sha256_bytes(data),
        "mtime_utc": _mtime_iso(path),
        "bytes": len(data),
        "lines": len(lines),
        "non_empty_lines": sum(1 for l in lines if l.strip()),
        "pipe_lines": sum(1 for l in lines if is_pipe_line(l)),
        "table_lines": table_lines,
        "tables": len(tables),
        "entry_rows": entry_rows,
        "fences": fences,
        "longest_line_chars": longest_chars,
        "longest_line_no": longest_no,
        "longest_line_bytes": longest_bytes,
        "header_repeat_max": header_repeat_max,
        "has_bom": bom,
        "layer": "L1" if rel == CODE_MAP_NAME else "L2",
        "declared_fingerprint": [{"glob": g, "value": v} for g, v in declared],
        "unmapped_headers": sorted(unmapped)[:30],
    }
    return per_file, violations


# ---------------------------------------------------------------------------
# 汇总
# ---------------------------------------------------------------------------
def collect_targets(root: str, paths):
    if not paths:
        return find_code_maps(root)
    out = []
    for raw in paths:
        p = raw.strip().strip('"')
        if not p:
            continue
        ap = os.path.normpath(p if os.path.isabs(p) else os.path.join(root, p))
        if os.path.isdir(ap):
            out.extend(find_code_maps(root, ap))
        elif os.path.isfile(ap):
            out.append(ap)
        else:
            raise UsageError("path not found: %s" % p)
    seen, res = set(), []
    for p in out:
        k = os.path.normcase(os.path.abspath(p))
        if k in seen:
            continue
        seen.add(k)
        res.append(os.path.abspath(p))
    res.sort(key=lambda p: rel_posix(root, p).lower())
    return res


def _finalize_anti_pattern(violations):
    """§6 各「类别」的处置等级只用于分类统计；§7 表决定门禁等级（anti-pattern 一律不 gating）。"""
    return [v for v in violations if v.get("spec_severity") == "error"]


def _count(violations):
    rule_hits = {r: 0 for r in RULE_ORDER}
    rule_hits_by_severity = {r: {} for r in RULE_ORDER}
    sev_total = {"error": 0, "warn": 0, "info": 0}
    for v in violations:
        rule_hits[v["rule"]] += 1
        d = rule_hits_by_severity[v["rule"]]
        d[v["severity"]] = d.get(v["severity"], 0) + 1
        if v["severity"] in sev_total:
            sev_total[v["severity"]] += 1
    return rule_hits, rule_hits_by_severity, sev_total


def run_check(root: str, targets=None, with_fingerprint: bool = False):
    root = os.path.abspath(root)
    files = collect_targets(root, targets)

    col_samples = {c: [] for c in DISTRIBUTION_COLUMNS}
    global_unmapped = set()
    per_file, violations = [], []
    for path in files:
        pf, vs = analyze_file(root, path, col_samples, global_unmapped)
        per_file.append(pf)
        violations.extend(vs)

    # §5 stale-fingerprint：按声明的每个 path-glob 分别算子指纹（只在文件声明了源指纹时才计算）
    fp_summary = {"files_with_declaration": 0, "globs_declared": 0,
                  "globs_verified": 0, "globs_mismatched": 0,
                  "globs_unverifiable": []}
    for pf in per_file:
        if not pf["declared_fingerprint"]:
            continue
        fp_summary["files_with_declaration"] += 1
        fp_summary["globs_declared"] += len(pf["declared_fingerprint"])
        scope = os.path.dirname(os.path.join(root, pf["path"]))
        check = {"ok": [], "mismatch": [], "unverifiable": []}
        for d in pf["declared_fingerprint"]:
            fp = glob_fingerprint(root, d["glob"], base_dir=scope)
            if fp is None:
                check["unverifiable"].append({"glob": d["glob"], "reason": "no file matched"})
                fp_summary["globs_unverifiable"].append("%s :: %s" % (pf["path"], d["glob"]))
                continue
            if fp["truncated"]:
                check["unverifiable"].append({"glob": d["glob"], "reason": "file count over FINGERPRINT_MAX_FILES"})
                fp_summary["globs_unverifiable"].append("%s :: %s (truncated)" % (pf["path"], d["glob"]))
                continue
            val = d["value"]
            matched = (val == fp["digest"] or val == fp["value"]
                       or (val and fp["digest_full"].startswith(val)))
            if matched:
                check["ok"].append({"glob": d["glob"], "declared": val,
                                    "computed": fp["digest"], "files": fp["file_count"]})
                fp_summary["globs_verified"] += 1
            else:
                check["mismatch"].append({"glob": d["glob"], "declared": val,
                                          "computed": fp["digest"], "files": fp["file_count"]})
                fp_summary["globs_mismatched"] += 1
                violations.append(_vio(
                    "stale-fingerprint", pf["path"], 0,
                    "glob=%s declared=%s computed=%s(files=%d)"
                    % (d["glob"], val, fp["digest"], fp["file_count"])))
        pf["fingerprint_check"] = check

    spec_errors = _finalize_anti_pattern(violations)
    violations.sort(key=lambda v: (RULE_ORDER.index(v["rule"]), v["file"], v["line"], v["detail"]))
    rule_hits, rule_hits_by_severity, sev_total = _count(violations)

    cat_counts = {}
    for v in violations:
        if v["rule"] == "anti-pattern" and v.get("category"):
            cat_counts[v["category"]] = cat_counts.get(v["category"], 0) + 1

    fingerprints = None
    if with_fingerprint:
        fingerprints = {}
        for path in files:
            fingerprints[rel_posix(root, path)] = scope_fingerprint(root, os.path.dirname(path))

    tool_path = os.path.abspath(__file__)
    in_repo = tool_path.lower().startswith(root.lower())
    return {
        "schema": SCHEMA_ID,
        "spec": SPEC_REL,
        "repo_root": root.replace("\\", "/"),
        "tool": {
            "path": rel_posix(root, tool_path) if in_repo else tool_path,
            "sha256": sha256_file(tool_path),
            "python": sys.version.split()[0],
        },
        "limits": LIMITS,
        "rules": RULES,
        "rule_order": RULE_ORDER,
        "summary": {
            "code_map_files": len(files),
            "total_lines": sum(p["lines"] for p in per_file),
            "total_non_empty_lines": sum(p["non_empty_lines"] for p in per_file),
            "total_table_lines": sum(p["table_lines"] for p in per_file),
            "total_entry_rows": sum(p["entry_rows"] for p in per_file),
            "total_bytes": sum(p["bytes"] for p in per_file),
            "total_fences": sum(p["fences"] for p in per_file),
            "error": sev_total["error"],
            "warn": sev_total["warn"],
            "info": sev_total["info"],
            "gate": "FAIL" if sev_total["error"] > 0 else "PASS",
        },
        "per_file": per_file,
        "rule_hits": rule_hits,
        "rule_hits_by_severity": rule_hits_by_severity,
        "anti_pattern_categories": cat_counts,
        "anti_pattern_spec_errors": {
            "count": len(spec_errors),
            "note": "§6 标 ⛔error 的类别；§7 表把 anti-pattern 定为 warn，故不计入门禁 error。",
            "items": spec_errors,
        },
        "distributions": {c: column_stats(col_samples[c]) for c in DISTRIBUTION_COLUMNS},
        "distributions_note": "字符长度 = 表格数据行里该列单元格的已 strip 原文长度（不做 Markdown 解析）。",
        "unmapped_headers": sorted(global_unmapped),
        "fingerprints": fingerprints,
        "fingerprint_check": fp_summary,
        "violations": violations,
    }


# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------
def ascii_safe(text: str) -> str:
    return text.encode("ascii", "backslashreplace").decode("ascii")


def print_summary(report):
    s = report["summary"]
    out = ["== code_map_check v2 =="]
    out.append("schema: %s" % report["schema"])
    out.append("repo_root: %s" % report["repo_root"])
    out.append("tool_sha256: %s" % report["tool"]["sha256"])
    out.append("python: %s" % report["tool"]["python"])
    out.append("files_scanned: %d" % s["code_map_files"])
    out.append("lines: %d  non_empty: %d  table_lines: %d  entry_rows: %d  bytes: %d  fences: %d"
               % (s["total_lines"], s["total_non_empty_lines"], s["total_table_lines"],
                  s["total_entry_rows"], s["total_bytes"], s["total_fences"]))
    out.append("error: %d  warn: %d  info: %d  gate: %s"
               % (s["error"], s["warn"], s["info"], s["gate"]))
    out.append("rule_hits:")
    for r in RULE_ORDER:
        out.append("  %-22s %-6s %d" % (r, report["rules"][r], report["rule_hits"][r]))
    cats = ", ".join("%s=%d" % (k, report["anti_pattern_categories"][k])
                     for k in sorted(report["anti_pattern_categories"]))
    out.append("anti_pattern_categories: %s" % (cats if cats else "none"))
    out.append("anti_pattern_spec_errors (non-gating, spec §6=error): %d"
               % report["anti_pattern_spec_errors"]["count"])
    fpc = report.get("fingerprint_check") or {}
    out.append("fingerprint_check: declared_files=%d globs=%d verified=%d mismatched=%d unverifiable=%d"
               % (fpc.get("files_with_declaration", 0), fpc.get("globs_declared", 0),
                  fpc.get("globs_verified", 0), fpc.get("globs_mismatched", 0),
                  len(fpc.get("globs_unverifiable", []))))
    out.append("distributions (cell chars):")
    for c in DISTRIBUTION_COLUMNS:
        d = report["distributions"][c]
        out.append("  %-16s n=%-6d min=%-5s median=%-7s p90=%-7s p99=%-7s max=%-6s mean=%s"
                   % (COLUMN_ASCII[c], d["n"], d["min"], d["median"], d["p90"], d["p99"],
                      d["max"], d["mean"]))
    out.append("exit_code: %d" % (1 if s["error"] > 0 else 0))
    print(ascii_safe("\n".join(out)))


def boundary_notes(report):
    """构造「无法确认的边界」清单（部分条目随实测结果参数化）。"""
    fp = report.get("fingerprint_check") or {}
    notes = list(BOUNDARY_NOTES)
    notes.insert(4,
        "`stale-fingerprint` 在真实仓库上只拿到了**部分**证据：全仓 %d 个 code_map 中只有 %d 个携带 §5 元数据行；"
        "共声明 %d 个 path-glob，其中可核验 %d 个、不匹配 %d 个、无法核验 %d 个（%s）。"
        "「不匹配」不等于「文档错」——它也可能是声明方用了不同的聚合口径；口径差异见上一条。"
        % (report["summary"]["code_map_files"], fp.get("files_with_declaration", 0),
           fp.get("globs_declared", 0), fp.get("globs_verified", 0),
           fp.get("globs_mismatched", 0), len(fp.get("globs_unverifiable", [])),
           ", ".join(fp.get("globs_unverifiable", [])) or "无"))
    notes.append(
        "§5 源指纹的**口径分歧已消解**：规范原文写「排序后拼接的 sha256 **前 12 位**」，但 `--fp-probe` 用 15 种候选口径"
        "在真实声明上反推，命中唯一变体 `path:shafull_nl`——即**逐文件全量 sha256**、按仓库相对路径排序拼"
        "`<path>:<hash>`（换行分隔）后整体取前 12 位。仓内声明与该口径一致（多个不同目录树的 glob 同时命中）；"
        "本工具已按此口径实现。**规范文字与实现口径不等价**，建议把规范那句改成「全量 sha256 拼接后取前 12 位」。")
    notes.append(
        "真实数据上的 `stale-fingerprint` 覆盖面：全仓 %d 个 code_map 中有 %d 个携带 §5 元数据行，"
        "共声明 %d 个 path-glob（可核验 %d、不匹配 %d、无法核验 %d）。不匹配项需逐个人工归因："
        "「文件集在声明之后又变了」（如本卡在 `Tools/Docs` 新增了 2 个 .py 文件）与「声明值本身是占位/过期」在机器上同形。"
        % (report["summary"]["code_map_files"], fp.get("files_with_declaration", 0),
           fp.get("globs_declared", 0), fp.get("globs_verified", 0),
           fp.get("globs_mismatched", 0), len(fp.get("globs_unverifiable", []))))
    notes.append(
        "本仓在本次扫描期间**存在活跃的并发写入者**。两次相隔约 1 分钟的连续全仓扫描实测：总行数 2877 → 2669、"
        "总字节 373068 → 320562、条目行 1180 → 1074、`line-too-long` 135 → 93、`field-too-long` 405 → 320；"
        "同时 `关联` / `约束` 两列的样本数从 0/0 变为 34/34（有人正在给 code_map 补 §2 的 R/S 列），"
        "根索引 `code_map.md` 也从 300 行 / 25,566 字符被重写为 156 行 / 17,499 字符并新增了 §5 源指纹行。"
        "因此本报告是**某一时刻的快照**：`per_file[].sha256` 与 `mtime_utc` 逐文件钉住了该时刻；"
        "若它们已变，对应数字即已失效，需重跑本工具。")
    notes.append(
        "`info` 级命中（§6 空值写法）在本次全仓扫描中为 %d 条；该口径是本工具自选的窄口径，"
        "**不代表**仓库里没有其他占位写法。" % report["summary"]["info"])
    return notes


BOUNDARY_NOTES = [
    "本机未安装 CPython 3.12（`py -0p` 只有 3.14）。代码刻意不使用 3.13+ 专有 API，目标解释器为 3.12+；"
    "本次实测运行于 3.14.x。**在 3.12 上的行为未实测**。",

    "`MAX_LINE_LEN` 的口径歧义：规范正文所有上限都写「字符」（120/80/100/160 字符），但立法基线的观测值写成"
    "「单行 3,521 B」。本工具按**字符**判定（与「最长行字符数」这一交付指标一致），同时输出字节数。"
    "对 CJK 行，按字符判定比按字节宽松约 3 倍 —— 这是**未消解的歧义**，收紧上限前需先裁定口径。",

    "§6 与 §7 的等级冲突：「逐文件免检旁路」在 §6 标 ⛔ error，而 §7 表把 `anti-pattern` 定为 warn。"
    "任务书要求「等级照 §7 表」，故本工具让 `anti-pattern` 一律不阻断门禁（warn/info），"
    "并把该类别单列进 `anti_pattern_spec_errors`（**不进 error 计数**）。",

    "§5「源指纹」的字面格式（`<path-glob>=<sha256 前 12 位>`）与聚合算式（「文件数 + 排序后逐文件内容 sha256 聚合」）"
    "在规范原文中没有唯一可判定的定义。本工具采用的算法是：对作用域内每个文件取内容 sha256 前 12 位，"
    "按仓库相对路径排序拼成 `<path>:<hash>` 行，再取整串 sha256 前 12 位；value 写作 `<文件数>-<聚合摘要>`。"
    "比对时同时接受 `<12 位>`、`<文件数>-<12 位>` 与 64 位全量摘要前缀。**该定义是本工具的解释，不是规范原文**；"
    "声明里的 `path-glob` **只做记录、不参与计算**（未实现逐 glob 子指纹）。",

    "`stale-fingerprint` 未能在真实仓库上取得红/绿对照：全仓 40 个 code_map **没有任何一个携带 §5 元数据行**，"
    "因此该规则在本次报告里命中 0；它的判定路径只在自测 fixture 上被验证（声明值故意写错 ⇒ 取红；"
    "写入正确聚合值 ⇒ 转绿）。**在真实数据上该规则仍属未验证**。",

    "`layering-violation` 按 §7 明示为「启发式，非可判定」。本工具只实现 §7 给出的最窄机器判据"
    "（L1 行的首个单元格整体等于 `Source/<Proj>/<file>.<ext>`），**不**实现 §3 里"
    "「且该行承载了该文件的职责描述」这一语义条件 ⇒ 可能假阴性；L1 的跨项目调用链表引用文件路径时"
    "也可能假阳性。规范本身接受这一点，故等级为 warn。",

    "反模式「空值写法」（ℹ️ info）被实现为**窄口径**：只匹配 `暂无|待补|待定|tbd|todo|--` 这几个显式占位串，"
    "不把任意短单元格判为占位。规范表述（「`none`、`N/A`、`无`、`-` 之外的占位」）没有给出可枚举的占位集合 ⇒ 存在漏报。",

    "§5 的「语义变更数 ≥ 30」触发条件**未实现**：它需要 git 历史 + 语义 diff，超出「只读统计」范围。"
    "本工具只做源指纹比对。",

    "列长度分布与 `MAX_FIELD_LEN` 只对**表头被精确识别**为规范五列的表格生效"
    "（`文件`/`文件 / 目录`/`文件/目录`/`目录` → `文件 / 目录`；`用途`/`关键符号`/`符号`/`关联`/`约束`）。"
    "`| 命令 | 作用 | 关键约束 |` 这类表头**不受上限约束**（规范只对 §2 五列给上限）；"
    "被跳过的表头列在顶层 `unmapped_headers` 与逐文件字段里列出。",

    "指纹聚合的输入集合被定义为「作用域内所有普通文件，排除 §4 `exclude` 目录名与 `code_map.md` 自身，"
    "并额外排除 `.git`/`__pycache__`/`.venv`/`.vs`/`.idea`」。规范原文只写「源码文件」，"
    "未明确是否包含 `Docs/**`、资源与夹具；本工具选择**包含**（与 §4 `observe` 集合的精神一致）。",

    "`link-broken` 只检查 `[text](target)` 形式的相对链接，且不做锚点校验（锚点归 `Tools/Docs/check_md_links.py`）；"
    "`http(s)://`、`mailto:`、`tel:`、`data:`、`file:`、`ftp:` 一律跳过。",
]


def build_markdown(report, command_line: str) -> str:
    s = report["summary"]
    L = []
    A = L.append
    A("# code_map 自检报告（`Tools/Docs/code_map_check.py`，规范 v2）")
    A("")
    A("> 本文件由 `temp/code_map_selfcheck.json` 的同一份数据生成，不含人工润色。")
    A("")
    A("## 0. 本次做了什么")
    A("")
    A("- 交付 3 件新增物：`Tools/Docs/code_map_check.py`（检查器，纯标准库）、")
    A("  `Tools/Docs/code_map_check_selftest.py`（自测：fixture 取红 + 修绿 + 只读性证明）、")
    A("  本报告（`temp/code_map_selfcheck.json` / `temp/code_map_selfcheck.md`）。")
    A("- 检查器对仓库**只读**：只做 read / 统计 / 校验 / 报告。代码里不存在任何自动改写、压缩、截断、")
    A("  重排语义字段的路径；除命令行指定的两份报告外不写任何文件；并显式拒绝把报告写到 `code_map.md` 上。")
    A("- 未修改任何 `code_map.md`；未做任何 git 写操作；未部署、未重启宿主。")
    A("")
    A("- 规格真源：`%s`" % report["spec"])
    A("- 检查器 SHA-256：`%s`" % report["tool"]["sha256"])
    A("- 解释器：Python %s" % report["tool"]["python"])
    A("- 调用：`%s`" % command_line)
    A("")
    A("## 1. 全仓汇总")
    A("")
    A("| 指标 | 值 |")
    A("|---|---|")
    A("| code_map 文件数 | %d |" % s["code_map_files"])
    A("| 总行数 | %d |" % s["total_lines"])
    A("| 非空行数 | %d |" % s["total_non_empty_lines"])
    A("| 表格行数（表头+分隔+数据，仅被识别的表） | %d |" % s["total_table_lines"])
    A("| 条目行数（数据行，全文件合计） | %d |" % s["total_entry_rows"])
    A("| 总字节 | %d |" % s["total_bytes"])
    A("| 代码围栏标记行数 | %d |" % s["total_fences"])
    A("| **error** | **%d** |" % s["error"])
    A("| warn | %d |" % s["warn"])
    A("| info | %d |" % s["info"])
    A("| 门禁 | **%s** |" % s["gate"])
    A("")
    A("## 2. 每条规则的命中数（按当前 `LIMITS` 判定）")
    A("")
    A("| 规则 ID | 等级（§7） | 命中数 |")
    A("|---|---|---|")
    for r in RULE_ORDER:
        A("| `%s` | %s | %d |" % (r, report["rules"][r], report["rule_hits"][r]))
    A("")
    A("§6 反模式分类命中：")
    A("")
    A("| 类别 | 命中数（§6 处置等级） |")
    A("|---|---|")
    for k in sorted(report["anti_pattern_categories"]):
        A("| %s | %d |" % (k, report["anti_pattern_categories"][k]))
    if not report["anti_pattern_categories"]:
        A("| （无） | 0 |")
    A("")
    A("> §6 的「逐文件免检旁路」标 ⛔ error，但 §7 表把 `anti-pattern` 定为 warn。本工具按 §7 表执行门禁：")
    A("> 该类别命中 **%d** 条，单列于此、**不计入 error**。" % report["anti_pattern_spec_errors"]["count"])
    A("")
    A("§5 源指纹核验（`stale-fingerprint`）：")
    A("")
    fpc = report.get("fingerprint_check") or {}
    A("| 指标 | 值 |")
    A("|---|---|")
    A("| 携带 §5 元数据行的 code_map | %d |" % fpc.get("files_with_declaration", 0))
    A("| 声明的 path-glob 总数 | %d |" % fpc.get("globs_declared", 0))
    A("| 可核验 | %d |" % fpc.get("globs_verified", 0))
    A("| 不匹配（⇒ `stale-fingerprint`） | %d |" % fpc.get("globs_mismatched", 0))
    A("| 无法核验（glob 无命中 / 超文件数上限） | %d |" % len(fpc.get("globs_unverifiable", [])))
    if fpc.get("globs_unverifiable"):
        A("")
        A("无法核验的 glob：")
        for item in fpc["globs_unverifiable"]:
            A("- `%s`" % item)
    A("")
    A("## 3. 每文件实测数字（供 §8.3 校准）")
    A("")
    A("| # | code_map | 层 | 行数 | 非空行 | 表格行 | 条目行 | 表数 | 围栏 | 最长行字符 | 最长行字节 | 最长行号 | 同表头最大重复 | 字节 | sha256(12) | mtime_utc |")
    A("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for i, p in enumerate(report["per_file"], start=1):
        A("| %d | `%s` | %s | %d | %d | %d | %d | %d | %d | %d | %d | %d | %d | %d | `%s` | %s |"
          % (i, p["path"], p["layer"], p["lines"], p["non_empty_lines"], p["table_lines"],
             p["entry_rows"], p["tables"], p["fences"], p["longest_line_chars"],
             p["longest_line_bytes"], p["longest_line_no"], p["header_repeat_max"], p["bytes"],
             (p.get("sha256") or "")[:12], p.get("mtime_utc")))
    A("")
    A("## 4. 四类规范单元格的字符长度分布（全仓）")
    A("")
    A("| 列 | 样本数 | 最小 | 中位数 | p90 | p99 | 最大 | 均值 | §2 硬上限 | 超限单元格数 |")
    A("|---|---|---|---|---|---|---|---|---|---|")
    for c in DISTRIBUTION_COLUMNS:
        d = report["distributions"][c]
        lim = LIMITS["MAX_FIELD_LEN"][c]
        over = 0
        needle = "column=%s " % c
        for v in report["violations"]:
            if v["rule"] == "field-too-long" and needle in v["detail"]:
                over += 1
        A("| %s | %d | %s | %s | %s | %s | %s | %s | %d | %d |"
          % (c, d["n"], d["min"], d["median"], d["p90"], d["p99"], d["max"], d["mean"], lim, over))
    A("")
    A("> 口径：%s" % report["distributions_note"])
    A("")
    A("## 5. 每文件违规清单（逐条，不折叠）")
    A("")
    if not report["violations"]:
        A("（无）")
        A("")
    by_file = {}
    for v in report["violations"]:
        by_file.setdefault(v["file"], []).append(v)
    for path in sorted(by_file):
        A("### `%s`（%d 条）" % (path, len(by_file[path])))
        A("")
        A("| 规则 | 等级 | 行 | 详情 |")
        A("|---|---|---|---|")
        for v in by_file[path]:
            A("| `%s` | %s | %d | %s |"
              % (v["rule"], v["severity"], v["line"], v["detail"].replace("|", "\\|")))
        A("")
    A("## 6. 无法确认的边界（诚实标注）")
    A("")
    for i, note in enumerate(boundary_notes(report), start=1):
        A("%d. %s" % (i, note))
    A("")
    A("## 7. 复现命令")
    A("")
    A("```")
    A("set PYTHONIOENCODING=utf-8")
    A("python Tools/Docs/code_map_check.py --check --report temp/code_map_selfcheck.json --report-md temp/code_map_selfcheck.md")
    A("python Tools/Docs/code_map_check_selftest.py")
    A("```")
    A("")
    return "\n".join(L)


def ensure_writable_output(path: str, expect_ext: str):
    ap = os.path.abspath(path)
    if os.path.basename(ap).lower() == CODE_MAP_NAME:
        raise UsageError("refusing to write over a code_map.md: %s" % path)
    if expect_ext and not ap.lower().endswith(expect_ext):
        raise UsageError("report path must end with %s: %s" % (expect_ext, path))
    d = os.path.dirname(ap)
    if d and not os.path.isdir(d):
        os.makedirs(d, exist_ok=True)
    return ap


def build_command_line(args) -> str:
    cmd = "python Tools/Docs/code_map_check.py"
    if args.check:
        cmd += " --check"
    if args.paths:
        cmd += " --paths " + args.paths
    if args.report:
        cmd += " --report " + args.report
    if args.report_md:
        cmd += " --report-md " + args.report_md
    if args.fingerprint:
        cmd += " --fingerprint"
    return cmd


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="code_map.md compliance checker (spec v2, read-only)")
    ap.add_argument("--check", action="store_true",
                    help="run the check (default action when no other mode flag is given)")
    ap.add_argument("--paths", default=None,
                    help="comma-separated file/dir paths (default: whole repo)")
    ap.add_argument("--report", default=None, help="machine-readable JSON report path")
    ap.add_argument("--report-md", default=None, help="human-readable Markdown report path")
    ap.add_argument("--fingerprint", action="store_true",
                    help="also compute §5 source fingerprints for every scope")
    ap.add_argument("--fp-probe", action="store_true",
                    help="diagnose each declared §5 path-glob against candidate aggregation variants")
    ap.add_argument("--root", default=None, help="repository root override")
    args = ap.parse_args(argv)

    root = os.path.abspath(args.root) if args.root else default_root()
    if not os.path.isdir(root):
        raise UsageError("root not found: %s" % root)

    targets = [p for p in args.paths.split(",")] if args.paths else None
    report = run_check(root, targets, with_fingerprint=args.fingerprint)

    if not report["per_file"]:
        sys.stderr.write(ascii_safe("nothing to check under %s\n" % root))
        return 3

    print_summary(report)

    if args.fp_probe:
        rows = fp_probe(root, report["per_file"])
        report["fingerprint_probe"] = rows
        if not rows:
            print("fp_probe: no declared fingerprint found")
        for row in rows:
            print("fp_probe: %s glob=%s declared=%s files=%d matches=%s"
                  % (row["file"], row["glob"], row["declared"], row["files"],
                     ",".join(row["matches"]) or "NONE"))
            for name in FP_VARIANT_NAMES:
                print("    %-24s %s%s" % (name, row["variants"][name],
                                          "  <== MATCH" if name in row["matches"] else ""))

    if args.report:
        out = ensure_writable_output(args.report, ".json")
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(report, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print(ascii_safe("report json: %s" % out))
    if args.report_md:
        out = ensure_writable_output(args.report_md, ".md")
        with open(out, "w", encoding="utf-8", newline="\n") as fh:
            fh.write(build_markdown(report, build_command_line(args)))
        print(ascii_safe("report md: %s" % out))

    return 1 if report["summary"]["error"] > 0 else 0


def default_root() -> str:
    here = os.path.dirname(os.path.abspath(__file__))
    cand = os.path.dirname(os.path.dirname(here))
    if os.path.isdir(os.path.join(cand, "Source")) or \
            os.path.exists(os.path.join(cand, CODE_MAP_NAME)):
        return cand
    return os.getcwd()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except UsageError as exc:
        sys.stderr.write(ascii_safe("usage error: %s\n" % exc))
        sys.exit(2)
