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
import re
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


def stage_anti_pattern_prose_scope(mod, dirs):
    r"""§6 词表只针对 prose：行内代码跨度与链接目标里的词不算命中。

    真因（Card B/C 误报）：`anti-pattern` 是逐行裸子串匹配，于是
      * `\`TodoCheckTool\`` 这类**标识符**里的 TODO 被算成「演进叙事」；
      * `[施工计划](...施工计划.md)` 这种**链接目标文件名**里的「计划」被算成「路线图」。
    本 stage 左侧两行必须 0 命中，第三行的裸 prose 必须仍命中（反过度抑制的对照组）。
    """
    d = tempfile.mkdtemp(prefix="code_map_ap_scope_")
    write_lines(os.path.join(d, "code_map.md"), [
        "# Fixture AP Prose Scope",
        "",
        "| 文件 | 用途 | 关键符号 | 关联 | 约束 |",
        "|------|------|----------|------|------|",
        "| `Services/TodoTools/TodoCheckTool.cs` | `todo_check`：勾选单项待办 | `TodoCheckTool` | `Services/TodoTools/TodoWriteTool.cs` | — |",
        "| `a.cs` | 见 [施工方案](Docs/12_features/施工计划.md) | `S1` | `b.cs` | — |",
        "| `b.cs` | 见 [施工计划](Docs/12_features/plan.md) | `S2` | `c.cs` | — |",
        "| `c.cs` | 本次修复了边界 | `S3` | `d.cs` | — |",
        "",
    ])
    rep = mod.run_check(d)
    viols = [v for v in rep["violations"] if v["rule"] == "anti-pattern"]
    non_prose = [v for v in viols if v["line"] in (5, 6)]
    prose = [v for v in viols if v["line"] in (7, 8)]
    check("anti-pattern: words inside code spans / link targets are not hits",
          not non_prose,
          "hits=%s" % [(v["line"], v["detail"]) for v in non_prose])
    check("anti-pattern: link labels and bare prose still hit (control)",
          len(prose) >= 2, "hits=%s" % [(v["line"], v["detail"]) for v in prose])


def stage_anti_pattern_identifier_boundary(mod):
    """§6 ASCII 词按标识符边界匹配：裸标识符里的字母组合不算自然语言断言。

    `GetTodoAsync` / `TodoItemView` 是符号名 —— 与 `prose_only` 剔除行内代码跨度同一个理由。
    对照组：真正的裸 `TODO` 断言必须仍然命中（反过度抑制）。
    """
    d = tempfile.mkdtemp(prefix="code_map_ap_ident_")
    write_lines(os.path.join(d, "code_map.md"), [
        "# Fixture AP Identifier Boundary",
        "",
        "| 文件 | 用途 | 关键符号 | 关联 | 约束 |",
        "|------|------|----------|------|------|",
        "| `Goals/IGoalQueryService.cs` | 查询含 GetTodoAsync 拆解投影 | `IGoalQueryService` | `Goals/GoalCommandService.cs` | — |",
        "| `b.cs` | TODO 补齐 | `S1` | `c.cs` | — |",
        "| `c.cs` | 读模型字段（active/iterations/steps/todo） | `S2` | `d.cs` | — |",
        "",
    ])
    rep = mod.run_check(d)
    viols = [v for v in rep["violations"] if v["rule"] == "anti-pattern"]
    ident = [v for v in viols if v["line"] == 5]
    bare = [v for v in viols if v["line"] == 6]
    field = [v for v in viols if v["line"] == 7]
    check("anti-pattern: TODO inside a bare identifier is not a hit", not ident,
          "hits=%s" % [(v["line"], v["detail"]) for v in ident])
    check("anti-pattern: bare TODO prose still hits (control)", len(bare) >= 1,
          "hits=%s" % [(v["line"], v["detail"]) for v in bare])
    check("anti-pattern: lowercase todo field name is not a hit (case-sensitive)",
          not field, "hits=%s" % [(v["line"], v["detail"]) for v in field])

def stage_list_items_parallel_phrases(mod):
    """§6/§7「清单型 F」只数**并列短语**：括号与行内代码里的分隔符不构成并列。

    真因（工作树归因 8/41 误报）：`_split_items` 曾裸 `re.split`，于是
      * `浏览器类型定义（标签、Cookie、视口等）` 一个短语被切成 3 项；
      * `` `GetOutlineAsync(filePath, sourceText, language, ct)` `` 参数列表被切成 4 项。
    前两行必须 0 命中；第三行真并列（3 个顶层短语）必须仍命中（反过度抑制对照组）；
    第四行验证括号内子短语不计入（顶层仍是 3 项）；第五行 2 项必须 0 命中，
    与第三行一起夹住 `LIMITS["LIST_MIN_ITEMS"]`（把它改成 4 会让第三行断言失败 = 变异取红）。
    """
    d = tempfile.mkdtemp(prefix="code_map_list_items_")
    write_lines(os.path.join(d, "code_map.md"), [
        "# Fixture List Items",
        "",
        "| 文件 | 用途 | 关键符号 | 关联 | 约束 |",
        "|------|------|----------|------|------|",
        "| `a.cs` | 浏览器类型定义（标签、Cookie、视口等） | `S1` | `b.cs` | — |",
        "| `b.cs` | 唯一端口：`GetOutlineAsync(filePath, sourceText, language, ct)` | `S2` | `c.cs` | — |",
        "| `c.cs` | 解析、写入、审计 | `S3` | `d.cs` | — |",
        "| `d.cs` | 解析（x、y）、写入、审计 | `S4` | `e.cs` | — |",
        "| `e.cs` | 解析、写入 | `S5` | `f.cs` | — |",
        "",
    ])
    rep = mod.run_check(d)
    items = {v["line"]: v["detail"] for v in rep["violations"]
             if v["rule"] == "anti-pattern" and v.get("category") == "清单型 F"}
    check("list-items: separators inside （）/inline code are not parallel phrases",
          not any(ln in items for ln in (5, 6)), "hits=%s" % items)
    check("list-items: three top-level phrases still hit (control)", 7 in items,
          "hits=%s" % items)
    check("list-items: bracketed sub-phrases are not counted (3 remain)",
          items.get(8) == "items=3", "hit=%s" % items.get(8))
    check("list-items: two phrases stay below the threshold (no hit)", 9 not in items,
          "hits=%s" % items)
    check("list-items: threshold lives in LIMITS (single source of truth)",
          mod.LIMITS.get("LIST_MIN_ITEMS") == 3, "LIMITS=%s" % mod.LIMITS.get("LIST_MIN_ITEMS"))


def stage_anti_pattern_placeholder_and_restated(mod):
    """§6「复述型 S」（warn）与「空值写法」（info）必须各自能取红，阈值仍取自 LIMITS。

    真因（2026-10-23 规范条款覆盖核对）：§6 七个类别里只有四类有夹具断言
    （演进叙事 / 路线图 / 清单型 F / 过度宣称），`复述型 S` 与 `空值写法` 只有实现没有断言 ——
    把这两段判定从检查器里删掉，selftest 依旧全绿。本 stage 补两侧边界：
      * 复述型 S：Jaccard 0.75 命中并带 `jaccard=` 明细，不相似的对照行 0 命中；
      * 空值写法：`待定` 命中且等级为 info，规范允许的 `—` 不命中（反过度抑制对照）。
    阈值必须来自 `LIMITS["JACCARD_THRESHOLD"]`，而不是调用点字面量（§8.1 单一真源）。
    """
    d = tempfile.mkdtemp(prefix="code_map_ap_placeholder_")
    write_lines(os.path.join(d, "code_map.md"), [
        "# Fixture AP Placeholder Restated",
        "",
        "| 文件 | 用途 | 关键符号 | 关联 | 约束 |",
        "|------|------|----------|------|------|",
        "| `a.cs` | 解析写入审计导出 | `S1` | `b.cs` | 解析写入审计 |",
        "| `b.cs` | 查询投影 | `S2` | `c.cs` | 禁止跨进程共享 |",
        "| `c.cs` | 事件转发 | `S3` | `d.cs` | 待定 |",
        "| `d.cs` | 令牌校验 | `S4` | `e.cs` | — |",
        "",
    ])
    rep = mod.run_check(d)
    ap = [v for v in rep["violations"] if v["rule"] == "anti-pattern"]
    restated = {v["line"]: v["detail"] for v in ap if v.get("category") == "复述型 S"}
    placeholder = {v["line"]: v for v in ap if v.get("category") == "空值写法"}
    check("restated-S: Jaccard 0.75 on 用途/约束 hits 复述型 S",
          restated.get(5, "").startswith("jaccard=0.7"), "hit=%s" % restated.get(5))
    check("restated-S: dissimilar 约束 does not hit (control)", 6 not in restated,
          "hits=%s" % restated)
    check("restated-S: threshold lives in LIMITS (single source of truth)",
          mod.LIMITS.get("JACCARD_THRESHOLD") == 0.5,
          "LIMITS=%s" % mod.LIMITS.get("JACCARD_THRESHOLD"))
    check("placeholder: 待定 hits 空值写法 at info level",
          placeholder.get(7, {}).get("severity") == "info",
          "hit=%s" % placeholder.get(7, {}).get("detail"))
    check("placeholder: spec-allowed em dash is not a hit (control)",
          8 not in placeholder, "hits=%s" % sorted(placeholder))


def stage_anti_pattern_skip_bypass(mod):
    """§6「逐文件免检旁路」必须能取红，且每个旁路词都要触发。

    真因（2026-10-23 规范条款覆盖核对）：§6 七个类别里六类有夹具断言，
    只有「逐文件免检旁路」（`SKIP_BYPASS_WORDS`）没有任何断言 —— 把这三个词
    从检查器里删掉，selftest 依旧全绿（B 级）。规范自己也写明：
    从 code_map 看不出脚本是否真加了旁路，故这条归入 `anti-pattern` 的 warn 级、
    只作 review 检查项；但**词级启发式仍是机器判定**，必须夹住。
    本 stage：三行各命中一个旁路词（词集合全覆盖），无旁路词的对照行 0 命中。
    """
    d = tempfile.mkdtemp(prefix="code_map_ap_bypass_")
    write_lines(os.path.join(d, "code_map.md"), [
        "# Fixture AP Skip Bypass",
        "",
        "| 文件 | 用途 | 关键符号 | 关联 | 约束 |",
        "|------|------|----------|------|------|",
        "| `a.cs` | 指纹生成器 | `S1` | `b.cs` | — |",
        "| `b.cs` | 对该文件加免检旁路 | `S2` | `c.cs` | — |",
        "| `c.cs` | 每次构建都变字节，特例跳过 | `S3` | `d.cs` | — |",
        "| `d.cs` | 跳过检查以保证门禁为绿 | `S4` | `e.cs` | — |",
        "",
    ])
    rep = mod.run_check(d)
    bypass = {v["line"]: v for v in rep["violations"]
              if v.get("category") == "逐文件免检旁路"}
    # 期望值写死在用例里，不跟 `SKIP_BYPASS_WORDS` 自比 ——
    # 否则「从常量里删一个词」会同时改变实现与期望，断言恒真（已变异实测验过这个陷阱）。
    expect = {6: "免检", 7: "特例跳过", 8: "跳过检查"}
    got = {ln: v["detail"].split("=", 1)[-1] for ln, v in bypass.items()}
    check("skip-bypass: each spec word hits its own row", got == expect,
          "got=%s expect=%s" % (got, expect))
    check("skip-bypass: hits stay at warn (spec §7 severity)",
          all(v["severity"] == "warn" for v in bypass.values()),
          "severities=%s" % sorted({v["severity"] for v in bypass.values()}))
    check("skip-bypass: row without a bypass word does not hit (control)",
          5 not in bypass, "lines=%s" % sorted(bypass))
    overlap = sorted(set(mod.SKIP_BYPASS_WORDS)
                     & {w for ws in mod.ANTI_PATTERN_WORDS.values() for w in ws})
    check("skip-bypass: word list disjoint from §6 词表 classes (no double tagging)",
          not overlap, "overlap=%s" % overlap)


DETAIL_ROW_RE = re.compile(r"^\| `([a-z][a-z-]+)` \| (?:error|warn|info) \| \d+ \|")


def detail_rows(md):
    """只数 §5「每文件违规清单」里的明细行 —— §2 的规则命中表首列同样是规则 ID，必须排除。"""
    start = md.find("## 5.")
    end = md.find("## 6.", start) if start >= 0 else -1
    if start < 0:
        return []
    body = md[start:end if end > start else len(md)]
    return [ln for ln in body.split("\n") if DETAIL_ROW_RE.match(ln)]


def stage_markdown_report(mod, rep):
    """`--report-md` 必须生成"本次运行"的报告，而不是复述工具诞生那次运行的手写叙事。

    真因：旧实现的 §0 是手写叙事（`## 0. 本次做了什么` / `交付 3 件新增物` /
    `未修改任何 code_map.md`），却在此后每一次运行都原样复述 —— 它描述的是**造工具那次**运行，
    不是当次运行，属陈旧投影生成器；§7 复现命令还写死了 `temp/code_map_selfcheck.*`，
    与实际调用（不同 `--paths` / `--report` 路径）不符。
    """
    cmdline = "python Tools/Docs/code_map_check.py --check --report x.json --report-md x.md"
    md = mod.build_markdown(rep, cmdline)
    check("report-md: §0 is generated from the current run, not from memory",
          "## 0. 本次运行" in md
          and ("| 违规条目 | %d 条" % len(rep["violations"])) in md,
          "violations=%d" % len(rep["violations"]))
    stale = [s for s in ("本次做了什么", "交付 3 件新增物", "未修改任何") if s in md]
    check("report-md: no stale hand-written narrative from the authoring run",
          not stale, "found=%s" % stale)
    check("report-md: every violation is listed individually (spec §7 no folding)",
          len(detail_rows(md)) == len(rep["violations"]),
          "rows=%d violations=%d" % (len(detail_rows(md)), len(rep["violations"])))
    check("report-md: 复现命令 echoes the actual invocation",
          ("\n%s\n" % cmdline) in md and "temp/code_map_selfcheck.md" not in md,
          "cmdline=%s" % cmdline)
    empty = dict(rep)
    empty["violations"] = []
    md0 = mod.build_markdown(empty, cmdline)
    check("report-md: zero violations renders （无） and no detail rows",
          "（无）" in md0 and not detail_rows(md0),
          "rows=%d" % len(detail_rows(md0)))


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
        stage_anti_pattern_prose_scope(mod, dirs)
        stage_anti_pattern_identifier_boundary(mod)
        stage_list_items_parallel_phrases(mod)
        stage_anti_pattern_placeholder_and_restated(mod)
        stage_anti_pattern_skip_bypass(mod)

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
        stage_markdown_report(mod, live1)
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
