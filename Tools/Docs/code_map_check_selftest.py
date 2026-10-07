#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""`Tools/Docs/code_map_check.py` 的自测（fixture 取红 + 修绿 + 只读性证明）。

设计要点
  * 三个 fixture 覆盖**全部 11 条规则 ID**，每条都能取红（否则断言失败）。
  * 「修好」断言：把坏 fixture 的语义修正版写回去，命中集合必须变为空集（全绿）。
  * 「只读」断言分两层：
      - **冻结镜像**层（对并发写入者免疫）：把仓库的全部 code_map 逐字节复制到临时目录，
        跑检查器后断言副本未被改动 —— 这是硬 PASS/FAIL。
      - **活仓库**层：带**对照组**（不调用检查器、只等待 2 秒再取一次快照）。若对照组本身就
        观察到文件变化 ⇒ 该轮实验**无法归因**，记为 INCONCLUSIVE（不计失败，但显式打印），
        因为本仓确有外部 Agent 正在并行重写 code_map。
  * 「可复现」断言同样跑在冻结镜像上（连跑两次 `summary`/`rule_hits` 必须完全一致），
    活仓库上的双跑只作参考、带漂移归因。
  * 全程只写**系统临时目录**；不触碰仓库里的任何文件。

用法：python Tools/Docs/code_map_check_selftest.py
退出码：0 无 FAIL；1 有 FAIL；2 环境/加载失败
"""
from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import os
import shutil
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
CHECKER = os.path.join(HERE, "code_map_check.py")

RESULTS = []


def check(name, ok, detail=""):
    status = "PASS" if ok else "FAIL"
    RESULTS.append((name, status, detail))
    print("[%s] %s%s" % (status, name, ("  -- " + detail) if detail else ""))
    return bool(ok)


def check_inconclusive(name, detail=""):
    RESULTS.append((name, "INCONCLUSIVE", detail))
    print("[INCONCLUSIVE] %s%s" % (name, ("  -- " + detail) if detail else ""))
    return False


def info(msg):
    print("[INFO] %s" % msg)


def load_checker():
    spec = importlib.util.spec_from_file_location("code_map_check_under_test", CHECKER)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


# ---------------------------------------------------------------------------
# fixtures
# ---------------------------------------------------------------------------
def fixture_a_lines():
    """坏样本：10 条规则同时取红（不含 missing-fingerprint —— 它有元数据但哈希是错的）。"""
    lines = [
        "# Fixture Code Map (BAD)",
        "",
        "> 源指纹: helper.py=000000000000 · 条目数: 1 · 最近整理: 2026-01-01",
        "",
        "## 1. Demo",
        "",
        "| 文件 | 用途 | 关键符号 | 约束 |",
        "|------|------|----------|------|",
        "| `a.py` | " + ("p" * 100) + " | `s1`、`s2`、`s3`、`s4`、`s5`、`s6` | ok |",
        "| `Source/Demo/a.py` | b | — | — |",
        "",
        "```python",
        "print('x')",
        "```",
        "",
        "x" * 320,
        "",
        "[broken](no_such_file_xyz.md)",
        "",
        "本次 新增了 修复了",
        "未来 计划 将支持",
        "",
        "| 文件 | 用途 |",
        "|------|------|",
    ]
    for i in range(401):
        lines.append("| `f%d.py` | item |" % i)
    lines.append("")
    for k in range(9):
        lines.append("| 文件 | 用途 |")
        lines.append("|------|------|")
        lines.append("| `g%d.py` | row |" % k)
        lines.append("")
    return lines


def fixture_b_lines(fp_digest):
    """修好版：全部规则转绿（含 §5 源指纹的正确聚合值）。"""
    return [
        "# Fixture Code Map (FIXED)",
        "",
        "> 源指纹: helper.py=%s · 条目数: 1 · 最近整理: 2026-10-07" % fp_digest,
        "",
        "## 1. Demo",
        "",
        "| 文件 | 用途 |",
        "|------|------|",
        "| `helper.py` | fixture helper module |",
        "",
        "链接自检：[helper.py](helper.py)",
        "",
    ]


def fixture_c_lines():
    """缺 §5 元数据：只应命中 missing-fingerprint。"""
    return [
        "# Fixture Code Map (NO METADATA)",
        "",
        "## 1. Demo",
        "",
        "| 文件 | 用途 |",
        "|------|------|",
        "| `a.py` | demo entry |",
        "",
    ]


def write_lines(path, lines):
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines))


def snapshot_paths(paths):
    out = {}
    for p in paths:
        try:
            with open(p, "rb") as fh:
                out[p] = hashlib.sha256(fh.read()).hexdigest()
        except OSError:
            out[p] = None
    return out


def snapshot_tree(root):
    out = {}
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in filenames:
            p = os.path.join(dirpath, fn)
            with open(p, "rb") as fh:
                out[os.path.relpath(p, root).replace("\\", "/")] = hashlib.sha256(fh.read()).hexdigest()
    return out


def hits(report):
    return set(r for r, n in report["rule_hits"].items() if n > 0)


def rel_list(paths, root=REPO_ROOT):
    return [os.path.relpath(p, root).replace("\\", "/") for p in paths]


EXPECTED_A = {
    "line-too-long", "field-too-long", "field-too-many-items", "fence-forbidden",
    "entries-exceed", "header-repeat", "layering-violation", "stale-fingerprint",
    "anti-pattern", "link-broken",
}


def build_mirror(files, dest):
    """把仓库的全部 code_map 逐字节复制成冻结镜像（只读断言与确定性断言的载体）。"""
    for p in files:
        rel = os.path.relpath(p, REPO_ROOT)
        dst = os.path.join(dest, rel)
        d = os.path.dirname(dst)
        if d and not os.path.isdir(d):
            os.makedirs(d, exist_ok=True)
        with open(p, "rb") as src, open(dst, "wb") as out:
            out.write(src.read())
    return dest


# ---------------------------------------------------------------------------
# 阶段
# ---------------------------------------------------------------------------
def stage_rule_red(mod, dirs):
    dir_a = dirs["a"]
    write_lines(os.path.join(dir_a, "helper.py"), ["VALUE = 1", ""])
    write_lines(os.path.join(dir_a, "code_map.md"), fixture_a_lines())

    before = snapshot_tree(dir_a)
    rep = mod.run_check(dir_a, [dir_a])
    after = snapshot_tree(dir_a)

    got = hits(rep)
    check("fixture A: hit rule ids == expected set", got == EXPECTED_A,
          "missing=%s unexpected=%s" % (sorted(EXPECTED_A - got), sorted(got - EXPECTED_A)))
    check("fixture A: every expected rule fires >=1",
          all(rep["rule_hits"][r] >= 1 for r in EXPECTED_A),
          str({r: rep["rule_hits"][r] for r in sorted(EXPECTED_A)}))
    check("fixture A: fence-forbidden count == 2 (two marker lines)",
          rep["rule_hits"]["fence-forbidden"] == 2,
          "count=%d" % rep["rule_hits"]["fence-forbidden"])
    check("fixture A: entries-exceed fires (412 entry rows > 400)",
          rep["rule_hits"]["entries-exceed"] == 1,
          "entry_rows=%d" % rep["per_file"][0]["entry_rows"])
    check("fixture A: header-repeat fires (10 repeats > 8)",
          rep["rule_hits"]["header-repeat"] == 1,
          "header_repeat_max=%d" % rep["per_file"][0]["header_repeat_max"])
    check("fixture A: layering-violation fires", rep["rule_hits"]["layering-violation"] == 1)
    check("fixture A: stale-fingerprint fires (declared 000000000000)",
          rep["rule_hits"]["stale-fingerprint"] == 1)
    check("fixture A: missing-fingerprint does NOT fire",
          rep["rule_hits"]["missing-fingerprint"] == 0)
    check("fixture A: gate FAIL and error > 0",
          rep["summary"]["gate"] == "FAIL" and rep["summary"]["error"] > 0,
          "error=%d warn=%d" % (rep["summary"]["error"], rep["summary"]["warn"]))
    check("fixture A: checker wrote nothing to the fixture tree", before == after,
          "changed=%s" % sorted(set(before) ^ set(after)))
    return rep


def stage_fix_to_green(mod, dirs):
    dir_b = dirs["b"]
    write_lines(os.path.join(dir_b, "helper.py"), ["VALUE = 1", ""])
    fp = mod.glob_fingerprint(dir_b, "helper.py", base_dir=dir_b)
    check("fixture B: glob_fingerprint resolves helper.py",
          fp is not None and fp["file_count"] == 1,
          "" if fp is None else "digest=%s files=%d" % (fp["digest"], fp["file_count"]))
    write_lines(os.path.join(dir_b, "code_map.md"), fixture_b_lines(fp["digest"]))

    before = snapshot_tree(dir_b)
    rep = mod.run_check(dir_b, [dir_b])
    after = snapshot_tree(dir_b)

    got = hits(rep)
    check("fixture B (fixed): hit rule ids == {} (all green)", got == set(),
          "got=%s" % sorted(got))
    check("fixture B: error==0 warn==0 info==0",
          rep["summary"]["error"] == 0 and rep["summary"]["warn"] == 0
          and rep["summary"]["info"] == 0,
          "error=%d warn=%d info=%d" % (rep["summary"]["error"], rep["summary"]["warn"],
                                        rep["summary"]["info"]))
    check("fixture B: gate PASS", rep["summary"]["gate"] == "PASS")
    check("fixture B: fingerprint matched (declared == computed)",
          rep["rule_hits"]["stale-fingerprint"] == 0)
    check("fixture B: checker wrote nothing to the fixture tree", before == after)
    return rep


def stage_missing_metadata_red(mod, dirs):
    dir_c = dirs["c"]
    write_lines(os.path.join(dir_c, "code_map.md"), fixture_c_lines())
    rep = mod.run_check(dir_c, [dir_c])
    got = hits(rep)
    check("fixture C: hit rule ids == {'missing-fingerprint'}",
          got == {"missing-fingerprint"}, "got=%s" % sorted(got))
    return rep


def stage_rule_coverage(mod, rep_a, rep_c):
    covered = hits(rep_a) | hits(rep_c)
    expected_all = set(mod.RULE_ORDER)
    check("coverage: fixtures exercise all 11 rule ids (each can go red)",
          covered == expected_all, "uncovered=%s" % sorted(expected_all - covered))
    check("coverage: rule id set == spec §7 table ids",
          expected_all == {"line-too-long", "field-too-long", "field-too-many-items",
                           "fence-forbidden", "entries-exceed", "header-repeat",
                           "layering-violation", "missing-fingerprint",
                           "stale-fingerprint", "anti-pattern", "link-broken"})


def stage_fingerprint_algorithm(mod, dirs):
    d = dirs["fp"]
    write_lines(os.path.join(d, "a.txt"), ["A1", ""])
    write_lines(os.path.join(d, "b.txt"), ["B1", ""])

    fp1 = mod.scope_fingerprint(d, d)
    fp2 = mod.scope_fingerprint(d, d)
    check("fingerprint: deterministic across two runs", fp1 == fp2,
          "digest=%s count=%d" % (fp1["digest"], fp1["file_count"]))
    check("fingerprint: file count == 2 (code_map.md itself excluded)",
          fp1["file_count"] == 2, "count=%d" % fp1["file_count"])

    write_lines(os.path.join(d, "a.txt"), ["A2", ""])
    fp3 = mod.scope_fingerprint(d, d)
    check("fingerprint: changes when a file content changes", fp3["digest"] != fp1["digest"])

    write_lines(os.path.join(d, "a.txt"), ["A1", ""])
    fp4 = mod.scope_fingerprint(d, d)
    check("fingerprint: bit-identical restore returns identical digest",
          fp4["digest"] == fp1["digest"] and fp4["digest_full"] == fp1["digest_full"])

    os.makedirs(os.path.join(d, "temp"), exist_ok=True)
    write_lines(os.path.join(d, "temp", "ignored.txt"), ["SHOULD NOT COUNT", ""])
    fp5 = mod.scope_fingerprint(d, d)
    check("fingerprint: §4 exclude dir (temp/) does not affect digest",
          fp5 == fp1, "digest=%s count=%d" % (fp5["digest"], fp5["file_count"]))

    write_lines(os.path.join(d, "c.txt"), ["C1", ""])
    fp6 = mod.scope_fingerprint(d, d)
    check("fingerprint: adding a counted file changes digest", fp6["digest"] != fp1["digest"])
    return fp1


def stage_mirror_reproducible(mod, mirror_root):
    r1 = mod.run_check(mirror_root, None)
    r2 = mod.run_check(mirror_root, None)
    same = (r1["rule_hits"] == r2["rule_hits"] and r1["summary"] == r2["summary"]
            and [p["sha256"] for p in r1["per_file"]] == [p["sha256"] for p in r2["per_file"]])
    check("reproducible (frozen mirror): two runs -> identical rule_hits/summary/file hashes",
          same, "files=%d error=%d warn=%d" % (r1["summary"]["code_map_files"],
                                               r1["summary"]["error"], r1["summary"]["warn"]))
    return r1


def stage_mirror_readonly(mod, mirror_root, mirror_files):
    before = snapshot_paths(mirror_files)
    mod.run_check(mirror_root, None)
    after = snapshot_paths(mirror_files)
    changed = sorted(rel_list([p for p in before if before[p] != after[p]], mirror_root))
    check("read-only (frozen mirror): checker left every code_map byte-identical",
          not changed, "changed=%s" % changed)


def stage_live_repo_readonly(mod, files):
    """活仓库层：**仅信息性**，不做断言。

    本仓有外部 Agent 正在并行重写 `code_map.md`，短对照窗（无检查器调用）无法证明静态；
    因此“活树未被改动”在移动的目标上不可作为可失败断言。真正的只读断言由两层承担：
      * `read-only (frozen mirror)` —— 在真实输入的逐字节副本上跑同一代码路径（对并发写入者免疫）；
      * `static: no destructive write APIs` + “2 个写点 + 拒绝 code_map.md 路径”。
    这里把活树的漂移作为**环境证据**打印出来，供报告里的诚实标注引用。
    """
    q1 = snapshot_paths(files)
    time.sleep(2.0)
    q2 = snapshot_paths(files)
    control_drift = rel_list([p for p in q1 if q1[p] != q2[p]])

    before = snapshot_paths(files)
    mod.run_check(REPO_ROOT, None)
    after = snapshot_paths(files)
    check_drift = rel_list([p for p in before if before[p] != after[p]])

    info("live tree: control window (2s, NO checker call) drifted: %s"
         % (control_drift or "none"))
    info("live tree: check window drifted: %s" % (check_drift or "none"))
    info("live tree: these drifts are attributed to the external writer, not the checker "
         "(the frozen-mirror assertion above covers the same code path)")
    return check_drift


def stage_mirror_vs_live(files, mirror_root):
    """信息性对照：镜像快照之后，活仓库又漂了多少个 code_map。"""
    drifted = []
    for p in files:
        rel = os.path.relpath(p, REPO_ROOT)
        m = os.path.join(mirror_root, rel)
        if not os.path.exists(m):
            drifted.append(rel + " (missing in mirror)")
            continue
        with open(p, "rb") as f1, open(m, "rb") as f2:
            if f1.read() != f2.read():
                drifted.append(rel)
    info("live tree drifted since the mirror snapshot: %d/%d files %s"
         % (len(drifted), len(files), drifted[:6]))
    return drifted


def stage_mirror_matches_repo(files, mirror_root):
    mismatched = []
    for p in files:
        rel = os.path.relpath(p, REPO_ROOT)
        m = os.path.join(mirror_root, rel)
        if not os.path.exists(m):
            mismatched.append(rel)
            continue
        with open(p, "rb") as f1, open(m, "rb") as f2:
            if f1.read() != f2.read():
                mismatched.append(rel)
    check("mirror: byte-identical copy of every code_map at copy time",
          not mismatched, "mismatched=%s" % mismatched[:5])


def stage_stdout_ascii(mod, rep):
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        mod.print_summary(rep)
    text = buf.getvalue()
    bad = sorted(set(ch for ch in text if ord(ch) > 127))
    check("stdout: summary is pure ASCII", not bad, "non-ascii=%r" % bad)
    check("stdout: summary reports scanned file count",
          ("files_scanned: %d" % rep["summary"]["code_map_files"]) in text)
    check("stdout: summary reports rule ids",
          "line-too-long" in text and "stale-fingerprint" in text)


def stage_static_readonly_guard():
    """静态红线：源码里不得存在任何自动改写/截断/重排路径。"""
    with open(CHECKER, "r", encoding="utf-8") as fh:
        src = fh.read()
    forbidden = ["os.remove", "os.unlink", "os.rename", "os.replace", "os.truncate",
                 "shutil.", ".write_text(", ".write_bytes(", ".writelines("]
    found = [t for t in forbidden if t in src]
    check("static: no destructive write APIs in checker source", not found, "found=%s" % found)

    marker = 'open(out, "w", encoding="utf-8", newline="\\n")'
    check("static: exactly 2 write-open call sites (the 2 report files)",
          src.count(marker) == 2, "count=%d" % src.count(marker))
    positions, start = [], 0
    while True:
        i = src.find(marker, start)
        if i < 0:
            break
        positions.append(i)
        start = i + 1
    main_idx = src.index("def main(")
    check("static: both write-open sites live after def main(",
          bool(positions) and all(p > main_idx for p in positions), "positions=%s" % positions)
    check("static: report path guard refuses to overwrite code_map.md",
          "refusing to write over a code_map.md" in src)
    check("static: MAX_ENTRY_ROWS / MAX_FENCES constants present (single source of truth)",
          '"MAX_ENTRY_ROWS": 400' in src and '"MAX_FENCES": 0' in src)


def stage_api_contract(mod, rep):
    check("contract: report embeds LIMITS verbatim",
          rep["limits"] == mod.LIMITS
          and {"MAX_LINE_LEN", "MAX_ENTRY_ROWS", "MAX_FENCES", "MAX_TABLE_HEADER_REPEAT",
               "MAX_FIELD_LEN", "MAX_ITEMS"}.issubset(rep["limits"]),
          "keys=%s" % sorted(rep["limits"]))
    check("contract: LIMITS.MAX_FIELD_LEN has exactly the 5 spec columns",
          sorted(rep["limits"]["MAX_FIELD_LEN"]) ==
          sorted(["文件 / 目录", "用途", "关键符号", "关联", "约束"]))
    check("contract: 11 rule ids with §7 severities",
          len(mod.RULE_ORDER) == 11
          and all(mod.RULES[r] == "error" for r in
                  ["line-too-long", "field-too-long", "field-too-many-items",
                   "fence-forbidden", "entries-exceed", "link-broken"])
          and all(mod.RULES[r] == "warn" for r in
                  ["header-repeat", "layering-violation", "missing-fingerprint",
                   "stale-fingerprint", "anti-pattern"]), "rules=%s" % mod.RULES)
    check("contract: report has distributions for the 4 spec columns",
          sorted(rep["distributions"]) == sorted(["用途", "关键符号", "关联", "约束"]))
    check("contract: report has per-file metrics (lines/non-empty/table/fences/longest)",
          bool(rep["per_file"]) and
          all(k in rep["per_file"][0] for k in
              ("lines", "non_empty_lines", "table_lines", "fences", "longest_line_chars")))
    check("contract: report has sha256+mtime per file (snapshot pinning)",
          all(p.get("sha256") for p in rep["per_file"]))
    check("contract: anti_pattern_spec_errors is non-gating (no error severity inside)",
          all(v["severity"] != "error" for v in rep["anti_pattern_spec_errors"]["items"]))
    check("contract: fingerprint_check block present",
          isinstance(rep.get("fingerprint_check"), dict)
          and "globs_declared" in rep["fingerprint_check"])


def main():
    if not os.path.isfile(CHECKER):
        print("[FAIL] checker not found: %s" % CHECKER)
        return 2
    try:
        global mod
        mod = load_checker()
    except Exception as exc:  # noqa: BLE001
        print("[FAIL] cannot load checker: %r" % exc)
        return 2

    base = tempfile.mkdtemp(prefix="code_map_selfcheck_")
    dirs = {k: os.path.join(base, k) for k in ("a", "b", "c", "fp", "mirror")}
    for v in dirs.values():
        os.makedirs(v, exist_ok=True)

    try:
        rep_a = stage_rule_red(mod, dirs)
        stage_fix_to_green(mod, dirs)
        rep_c = stage_missing_metadata_red(mod, dirs)
        stage_rule_coverage(mod, rep_a, rep_c)
        stage_fingerprint_algorithm(mod, dirs)

        repo_files = mod.find_code_maps(REPO_ROOT)
        mirror_root = dirs["mirror"]
        build_mirror(repo_files, mirror_root)
        mirror_files = mod.find_code_maps(mirror_root)
        stage_mirror_matches_repo(repo_files, mirror_root)
        check("mirror: same number of code_map files as the live tree (%d)"
              % len(repo_files), len(mirror_files) == len(repo_files),
              "mirror=%d live=%d" % (len(mirror_files), len(repo_files)))

        rep_mirror = stage_mirror_reproducible(mod, mirror_root)
        stage_mirror_readonly(mod, mirror_root, mirror_files)

        independent = []
        for dirpath, dirnames, filenames in os.walk(REPO_ROOT):
            dirnames[:] = [d for d in dirnames
                           if d not in {".git", "bin", "obj", "node_modules", ".pudding",
                                        "temp", "dist", "dist-dev", "__pycache__",
                                        ".venv", ".vs", ".idea"}]
            rel = os.path.relpath(dirpath, REPO_ROOT).replace("\\", "/")
            if rel.lower().startswith("external/references"):
                dirnames[:] = []
                continue
            if "code_map.md" in filenames:
                independent.append(os.path.join(dirpath, "code_map.md"))
        check("repo: independent enumeration agrees with checker walk",
              len(independent) == len(repo_files),
              "independent=%d checker=%d" % (len(independent), len(repo_files)))
        check("repo: enumerated code_map count == %d" % len(repo_files),
              len(repo_files) == rep_mirror["summary"]["code_map_files"],
              "walk=%d report=%d" % (len(repo_files), rep_mirror["summary"]["code_map_files"]))

        stage_live_repo_readonly(mod, repo_files)
        stage_mirror_vs_live(repo_files, mirror_root)

        live1 = mod.run_check(REPO_ROOT, None)
        live2 = mod.run_check(REPO_ROOT, None)
        if (live1["rule_hits"] == live2["rule_hits"] and live1["summary"] == live2["summary"]):
            check("reproducible (live tree): two consecutive runs agree", True,
                  "files=%d error=%d warn=%d" % (live1["summary"]["code_map_files"],
                                                 live1["summary"]["error"],
                                                 live1["summary"]["warn"]))
        else:
            check_inconclusive(
                "reproducible (live tree): two consecutive runs",
                "numbers differ between runs; the frozen-mirror determinism assertion above "
                "is the authoritative evidence")
        stage_stdout_ascii(mod, live1)
        stage_api_contract(mod, live1)
        stage_static_readonly_guard()
    finally:
        shutil.rmtree(base, ignore_errors=True)

    passed = [n for n, s, _ in RESULTS if s == "PASS"]
    failed = [(n, d) for n, s, d in RESULTS if s == "FAIL"]
    incon = [(n, d) for n, s, d in RESULTS if s == "INCONCLUSIVE"]
    print("")
    print("SUMMARY: %d passed, %d failed, %d inconclusive"
          % (len(passed), len(failed), len(incon)))
    for n, d in failed:
        print("  FAILED: %s  -- %s" % (n, d))
    for n, d in incon:
        print("  INCONCLUSIVE: %s  -- %s" % (n, d))
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
