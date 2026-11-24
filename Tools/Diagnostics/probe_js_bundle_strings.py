# -*- coding: utf-8 -*-
"""在前端打包产物里核对「某段文案/标识符真的进了产物」。

为什么需要它
------------
压缩器会把非 ASCII 字符写成转义，而转义有**三种形态**同时在同一个产物里出现：

1. 原字            从未使用
2. 四位反斜杠-u    反斜杠 u 4E00 .. uFFFF    （常见，带不带花括号各一家）
3. 花括号反斜杠-u  反斜杠 u{XXXXX}           （esbuild 对部分码位用这种）

实测（2026-11-25，PuddingAgent 部署产物）：同一个 chunk 内
`全部未计价` 写成四位形态、`次没有价格` 写成花括号形态。
因此「只搜原字」「只搜四位小写转义」都会得到 0 —— 那是**搜索口径错**，不是产物没这段。

本脚本先归一化再搜索，并用自检（--selftest）证明三种形态都能命中。
「搜不到」只有在自检通过之后才允许被当成结论。

用法
----
    py -3 Tools/Diagnostics/probe_js_bundle_strings.py --root <产物目录> --needle "文案" [--needle ...]
    py -3 Tools/Diagnostics/probe_js_bundle_strings.py --root <产物目录> --needles-file <utf8 文件>
    py -3 Tools/Diagnostics/probe_js_bundle_strings.py --selftest

退出码：0 = 所有 needle 命中（或自检通过）；1 = 有 needle 未命中（或自检失败）。
"""
from __future__ import annotations

import argparse
import os
import re
import sys

ESC_BRACE = re.compile("\\\\" + "u\\{([0-9a-fA-F]{1,6})\\}")
ESC_FOUR = re.compile("\\\\" + "u([0-9a-fA-F]{4})")


def normalize(text: str) -> str:
    """把花括号形与四位形的反斜杠-u 转义都还原成真实字符。

    先处理花括号形：四位形的正则匹配不到 `u{...}`，但反过来花括号形会被
    四位形误吞（`u{6B21}` 里 `u` 后是 `{`，其实不会命中）——顺序仍固定为
    花括号优先，语义更直观，且与自检断言一致。
    """
    text = ESC_BRACE.sub(lambda m: _chr(m.group(1)), text)
    return ESC_FOUR.sub(lambda m: _chr(m.group(1)), text)


def _chr(hex_digits: str) -> str:
    try:
        return chr(int(hex_digits, 16))
    except ValueError:
        return ""


def iter_js_files(root: str):
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in filenames:
            if name.endswith(".js"):
                yield os.path.join(dirpath, name)


def probe(root: str, needles: list[str]) -> dict[str, list[str]]:
    """返回 needle -> 命中它的文件名列表（去重、保序）。"""
    hits: dict[str, list[str]] = {n: [] for n in needles}
    if not os.path.isdir(root):
        raise SystemExit("root 不是目录: %s" % root)
    for path in iter_js_files(root):
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            decoded = normalize(handle.read())
        base = os.path.basename(path)
        for needle in needles:
            if needle in decoded and base not in hits[needle]:
                hits[needle].append(base)
    return hits


SELFTEST_RAW = "从未使用"
SELFTEST_FOUR = "".join("\\u%04x" % ord(c) for c in "全部未计价")
SELFTEST_BRACE = "".join("\\u{%x}" % ord(c) for c in "次没有价格")
SELFTEST_ABSENT = "__SELFTEST_ABSENT_TOKEN__"


def selftest() -> int:
    """证明三种形态都能被 normalize 还原，且阴性对照不会被误报。

    只覆盖原字/四位的解码器会在这里取红——这就是本脚本自身的变异判据。
    """
    failures: list[str] = []

    cases = [
        ("raw_form", SELFTEST_RAW),
        ("four_escape", SELFTEST_FOUR),
        ("brace_escape", SELFTEST_BRACE),
    ]
    expected = {
        "raw_form": SELFTEST_RAW,
        "four_escape": "全部未计价",
        "brace_escape": "次没有价格",
    }

    for label, payload in cases:
        got = normalize(payload)
        if got != expected[label]:
            failures.append("%s: normalize 得到 %r，期望 %r" % (label, got, expected[label]))
        elif expected[label] not in got:
            failures.append("%s: 还原后仍搜不到 %r" % (label, expected[label]))

    # 阴性对照：不存在的内容不得被解码器「造」出来。
    if normalize(SELFTEST_ABSENT).count(expected["brace_escape"]) != 0:
        failures.append("negative: 阴性样本被误报")

    # 混合样本：一段文本里同时出现三种形态，必须三条都命中。
    mixed = "%s|%s|%s" % (SELFTEST_RAW, SELFTEST_FOUR, SELFTEST_BRACE)
    decoded_mixed = normalize(mixed)
    for label in expected:
        if expected[label] not in decoded_mixed:
            failures.append("mixed: 缺少 %s" % label)

    print("SELFTEST cases=%d" % len(cases))
    if failures:
        for item in failures:
            print("FAIL %s" % item)
        print("VERDICT selftest=FAILED")
        return 1
    print("VERDICT selftest=PASSED")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="在打包产物里核对文案/标识符是否真的进了前端产物")
    parser.add_argument("--root", help="产物目录（递归扫描 .js）")
    parser.add_argument("--needle", action="append", default=[], help="要查找的文本，可重复")
    parser.add_argument("--needles-file", help="UTF-8 文件，每行一个 needle（便于传中文避免 shell 编码问题）")
    parser.add_argument("--selftest", action="store_true", help="只跑解码器自检")
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()

    needles = list(args.needle)
    if args.needles_file:
        with open(args.needles_file, "r", encoding="utf-8") as handle:
            needles.extend([line.strip() for line in handle if line.strip()])

    if not args.root or not needles:
        parser.error("需要 --root 与至少一个 --needle/--needles-file")

    hits = probe(args.root, needles)
    width = max(len(n) for n in needles)
    print("ROOT=%s" % args.root)
    print("%-*s  %-5s  %s" % (width, "NEEDLE", "COUNT", "FILES"))
    missing = []
    for needle in needles:
        files = hits[needle]
        if not files:
            missing.append(needle)
        print("%-*s  %-5d  %s" % (width, needle, len(files), ",".join(files) or "-"))
    print("-" * 60)
    if missing:
        print("VERDICT missing=%d" % len(missing))
        return 1
    print("VERDICT all_present=%d" % len(needles))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
