#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""仓库级 Markdown 链接/锚点检查（纯标准库）。

用法：
  python Tools/Docs/check_md_links.py                 # 检查 Docs/（默认）
  python Tools/Docs/check_md_links.py --all-repo      # 检查全仓库 md
  python Tools/Docs/check_md_links.py --report out.txt
退出码：存在失效目标或锚点时为 1（可作门禁）。
跳过：.git / bin / obj / node_modules / temp / .venv / external（子仓库内容）。
"""
from __future__ import annotations
import argparse
import io
import os
import re
import subprocess
import sys
from urllib.parse import unquote

SKIP_DIRS = {'.git', 'bin', 'obj', 'node_modules', 'temp', 'tmp', '.venv', 'dist', 'dist-dev', 'pub', '.pudding',
             # 以下与 code_map_check.py 的 LIMITS["EXCLUDE_DIR_NAMES"] 对齐（约定上的单一真源）。
             # 漏掉 `.work` 的后果是实测出来的：--all-repo 会把三份整仓库归档快照也算进去，
             # 失效目标从 134 虚增到 898（其中 764 条全在 .work/ 下，且锚点 29 条全部来自它）。
             '.work', '__pycache__', '.vs', '.idea'}
SKIP_PREFIX = ('external/',)
LINK_RE = re.compile(r'\]\(\s*([^)\s]+?)(\s+"[^"]*")?\s*\)')
HEADING_RE = re.compile(r'^(#{1,6})\s+(.*?)\s*#*\s*$')


def slug(text: str) -> str:
    text = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', text)
    text = re.sub(r'<[^>]*>', '', text)
    text = text.replace('`', '').replace('*', '').replace('\\', '')
    text = text.lower()
    text = re.sub(r'[^\w\- ]', '', text, flags=re.UNICODE)
    return text.replace(' ', '-')


def headings(path: str) -> set:
    out = set()
    in_fence = False
    for line in io.open(path, encoding='utf-8', errors='replace'):
        if re.match(r'^\s*(```|~~~)', line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        m = HEADING_RE.match(line.rstrip('\n'))
        if m:
            s = slug(m.group(2))
            if s:
                out.add(s)
    return out


def scan_lines(path: str):
    """产出「去掉围栏代码块与行内代码」后的行，避免把代码块里的示例链接当成真链接。"""
    in_fence = False
    for raw in io.open(path, encoding='utf-8', errors='replace'):
        line = raw.rstrip('\n')
        if re.match(r'^\s*(`{3,}|~{3,})', line):
            in_fence = not in_fence
            continue
        if in_fence:
            continue
        yield re.sub(r'`[^`]*`', '``', line)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('--all-repo', action='store_true')
    ap.add_argument('--report')
    args = ap.parse_args()

    root = subprocess.run(['git', 'rev-parse', '--show-toplevel'], capture_output=True, text=True).stdout.strip()
    os.chdir(root)
    scope = '' if args.all_repo else 'Docs'

    files = []
    for dirpath, dirnames, filenames in os.walk(scope or '.'):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        rel_dir = os.path.relpath(dirpath, root).replace(os.sep, '/')
        if any(rel_dir.startswith(p) for p in SKIP_PREFIX):
            dirnames[:] = []
            continue
        for fn in filenames:
            if fn.lower().endswith('.md'):
                files.append(os.path.join(dirpath, fn))

    cache, missing_target, missing_anchor = {}, [], []
    for path in files:
        rel = os.path.relpath(path, root).replace(os.sep, '/')
        base = os.path.dirname(path)
        matches = [m for line in scan_lines(path) for m in LINK_RE.finditer(line)]
        for m in matches:
            raw = m.group(1)
            # CommonMark 允许用尖括号包裹链接目标（用于含空格的路径）：`[t](<a b.md>)`。
            # 不剥掉尖括号就会把它当成文件名的一部分，于是 R 永远报「目标不存在」。
            # 实测：Docs/14_reports/2026-11-05-Agent组件化交付索引.md 一文件占 86 条这种假阳。
            if raw.startswith('<') and raw.endswith('>') and len(raw) >= 2:
                raw = raw[1:-1]
            if re.match(r'^(https?:|mailto:|file:|data:)', raw):
                continue
            target, anchor = raw, ''
            if '#' in raw:
                target, anchor = raw.split('#', 1)
            if target == '':
                if anchor:
                    if path not in cache:
                        cache[path] = headings(path)
                    if slug(unquote(anchor)) not in cache[path]:
                        missing_anchor.append((rel, raw))
                continue
            dec = unquote(target)
            dec = re.sub(r':\d+(-\d+)?$', '', dec)          # 允许 `file.cs:123` 形式
            abs_p = os.path.normpath(os.path.join(base, dec))
            if not os.path.exists(abs_p) and os.path.splitext(abs_p)[1] == '' and os.path.exists(abs_p + '.md'):
                abs_p += '.md'
            if not os.path.exists(abs_p):
                missing_target.append((rel, raw))
                continue
            if anchor and abs_p.lower().endswith('.md'):
                if abs_p not in cache:
                    cache[abs_p] = headings(abs_p)
                if slug(unquote(anchor)) not in cache[abs_p]:
                    missing_anchor.append((rel, raw))

    lines = ['扫描 md=%d  失效目标=%d  失效锚点=%d' % (len(files), len(missing_target), len(missing_anchor))]
    for label, rows in (('失效目标', missing_target), ('失效锚点', missing_anchor)):
        if rows:
            lines.append('--- %s ---' % label)
            for rel, tgt in rows:
                lines.append('  %s  ->  %s' % (rel, tgt))
    text = '\n'.join(lines)
    print(text)
    if args.report:
        io.open(args.report, 'w', encoding='utf-8', newline='\n').write(text + '\n')
    return 1 if (missing_target or missing_anchor) else 0


if __name__ == '__main__':
    sys.exit(main())
