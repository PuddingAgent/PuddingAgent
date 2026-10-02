#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Markdown Front Matter 校验与补齐工具（纯标准库，无第三方依赖）。

用法（在仓库根执行）：
  python Tools/Docs/front_matter.py --check                 # 只检查，不合规即非零退出
  python Tools/Docs/front_matter.py --check --report out.json
  python Tools/Docs/front_matter.py --fix --dry-run         # 预览将做的修改
  python Tools/Docs/front_matter.py --fix                   # 补齐缺失字段（不覆盖已有非空值）
  python Tools/Docs/front_matter.py --fix --paths Docs/12_features --limit 20
  python Tools/Docs/front_matter.py --fix --force           # 覆盖取值非法的字段

字段（标准 Front Matter）：
  title, author, date, last_reviewed, status, description, categories, tags,
  related_docs, related_files, slug, draft

取值来源（可追溯，不编造）：git 历史（author/date/last_reviewed）、首个 H1（title）、
首个段落（description）、目录（categories）、文件名与目录（tags/slug）、文内链接（related_docs）、
文内反引号路径（related_files）。
"""
from __future__ import annotations

import argparse
import io
import json
import os
import re
import subprocess
import sys

REQUIRED = ['title', 'author', 'date', 'last_reviewed', 'status', 'description',
            'categories', 'tags', 'related_docs', 'related_files', 'slug', 'draft']
LIST_FIELDS = {'categories', 'tags', 'related_docs', 'related_files'}
VALID_STATUS = {'draft', 'active', 'proposed', 'deprecated', 'archived'}
DATE_RE = re.compile(r'^\d{4}-\d{2}-\d{2}$')
SKIP_DIR_NAMES = {'.git', '.pudding', 'bin', 'obj', 'node_modules', 'temp', 'tmp', '.venv', 'dist', 'dist-dev', 'pub'}
SKIP_PATH_PREFIX = ('external/references/', 'external/github.hyfree.GM/')
FM_DELIM = '---'
CATEGORY_BY_DIR = {
    '00_changelog': 'changelog', '01_message_channels': 'message-channels', '02_agent_runtime': 'agent-runtime',
    '03_multi_agent': 'multi-agent', '04_tools_and_skills': 'tools-and-skills',
    '05_providers_and_models': 'providers-and-models', '06_config': 'config', '07_architecture': 'architecture',
    '08_how_debuge': 'how-debug', '09_audit': 'audit', '10_conventions': 'conventions', '11_design': 'design',
    '12_features': 'features', '13_runbooks': 'runbooks', '14_reports': 'reports', '15_tasks': 'tasks',
    '16_qa': 'qa', '17_memory': 'memory', '18_superpowers': 'superpowers', '19_references': 'references',
    '20_resources': 'resources', '90_archive': 'archive',
}


# ---------------------------------------------------------------- 基础工具
def repo_root() -> str:
    out = subprocess.run(['git', 'rev-parse', '--show-toplevel'], capture_output=True, text=True)
    return out.stdout.strip().replace('/', os.sep)


def read_text(path: str) -> str:
    with io.open(path, encoding='utf-8', errors='replace') as fh:
        return fh.read()


def write_text(path: str, text: str) -> None:
    with io.open(path, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(text)


# ---------------------------------------------------------------- git 元数据（一次遍历全部历史）
def git_meta() -> dict:
    """{repo_rel_path: {'first_author':..,'first_date':..,'last_date':..}}"""
    out = subprocess.run(['git', 'log', '--no-merges', '--pretty=format:C|%an|%ad', '--date=short', '--name-only'],
                         capture_output=True)
    text = out.stdout.decode('utf-8', 'replace')
    meta: dict = {}
    author = date = None
    for line in text.splitlines():
        if line.startswith('C|'):
            _, author, date = line.split('|', 2)
            continue
        p = line.strip()
        if not p:
            continue
        rec = meta.setdefault(p, {})
        if 'last_date' not in rec:            # 日志为新→旧，首次即最新
            rec['last_date'] = date
            rec['last_author'] = author
        rec['first_date'] = date              # 持续覆盖到最早
        rec['first_author'] = author
    return meta


# ---------------------------------------------------------------- Front Matter 解析/生成
def split_front_matter(text: str):
    """返回 (fm_dict, fm_lines, body_start_line)；无 Front Matter 时 fm_dict=None。"""
    lines = text.split('\n')
    if not lines or lines[0].strip() != FM_DELIM:
        return None, [], 0
    for i in range(1, min(len(lines), 200)):
        if lines[i].strip() == FM_DELIM:
            return parse_fm(lines[1:i]), lines[1:i], i + 1
    return None, [], 0


def parse_fm(lines):
    """极简 YAML 子集解析：key: value / key: [a, b] / 多行 - 列表。"""
    fm, cur_list = {}, None
    for raw in lines:
        line = raw.rstrip()
        if not line.strip() or line.lstrip().startswith('#'):
            continue
        m = re.match(r'^([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(.*)$', line)
        if m:
            key, val = m.group(1), m.group(2).strip()
            cur_list = None
            if val == '':
                fm[key] = []
                cur_list = key
            elif val.startswith('[') and val.endswith(']'):
                fm[key] = [unquote(x.strip()) for x in val[1:-1].split(',') if x.strip()]
            else:
                fm[key] = unquote(val)
            continue
        m2 = re.match(r'^\s*-\s+(.*)$', line)
        if m2 and cur_list:
            fm[cur_list].append(unquote(m2.group(1).strip()))
    return fm


def unquote(v: str) -> str:
    v = v.strip()
    if len(v) >= 2 and v[0] == v[-1] and v[0] in '"\'':
        body = v[1:-1]
        return body.replace('\\"', '"').replace("\\'", "'") if v[0] == '"' else body
    return v


def quote(v) -> str:
    if isinstance(v, bool):
        return 'true' if v else 'false'
    s = str(v)
    if s == '':
        return '""'
    if re.search(r'[:#\[\]{},&*!|>%@`\'"]', s) or s != s.strip():
        return '"' + s.replace('\\', '\\\\').replace('"', '\\"') + '"'
    return s


def render_fm(fm: dict) -> str:
    out = [FM_DELIM]
    for k in REQUIRED:
        v = fm.get(k)
        if k in LIST_FIELDS:
            items = v if isinstance(v, list) else ([v] if v else [])
            out.append('%s: [%s]' % (k, ', '.join(quote(x) for x in items)))
        elif k == 'draft':
            out.append('draft: %s' % ('true' if (v is True or str(v).lower() == 'true') else 'false'))
        else:
            out.append('%s: %s' % (k, quote(v if v is not None else '')))
    out.append(FM_DELIM)
    return '\n'.join(out)


# ---------------------------------------------------------------- 取值推导
def first_heading(body: str, fallback: str) -> str:
    for line in body.split('\n'):
        if line.startswith('# '):
            return line[2:].strip().strip('#').strip()
    for line in body.split('\n'):
        if line.startswith('## '):
            return line[3:].strip().strip('#').strip()
    return fallback


def first_paragraph(body: str, limit: int = 200) -> str:
    lines = body.split('\n')

    def clean(s: str) -> str:
        s = re.sub(r'!\[[^\]]*\]\([^)]*\)', '', s)
        s = re.sub(r'\[([^\]]*)\]\([^)]*\)', r'\1', s)
        s = s.replace('`', '').replace('**', '').replace('*', '')
        return re.sub(r'\s+', ' ', s).strip()

    # 1) 普通段落（跳过标题/分隔/引用/表格）
    for i, line in enumerate(lines):
        s = line.strip()
        if not s or s.startswith(('#', '---', '>', '|')):
            continue
        buf = []
        for l2 in lines[i:]:
            s2 = l2.strip()
            if not s2:
                break
            buf.append(re.sub(r'^\s*[-*+]\s+', '', re.sub(r'^\s*\d+[.)]\s+', '', s2)))
        text = clean(' '.join(buf))
        if text:
            return text[:limit]
        break
    # 2) 引用块
    quote = [l.strip().lstrip('>').strip() for l in lines if l.strip().startswith('>')]
    text = clean(' '.join(q for q in quote if q))
    if text:
        return text[:limit]
    # 3) 表格首个数据行（跳过 |---| 分隔行）
    for l in lines:
        s = l.strip()
        if s.startswith('|') and not re.match(r'^\|[\s:\-|]+\|$', s):
            text = clean(' '.join(c.strip() for c in s.strip('|').split('|') if c.strip()))
            if text:
                return text[:limit]
    return ''


def categories_for(rel: str) -> list:
    parts = rel.split('/')
    if parts[0] == 'Docs' and len(parts) > 2:
        top = parts[1]
        cat = CATEGORY_BY_DIR.get(top, top.lstrip('0123456789_').replace('_', '-') or 'docs')
        return ['docs', cat]
    return ['docs']


def tags_for(rel: str, title: str) -> list:
    stem = os.path.splitext(os.path.basename(rel))[0]
    stem = re.sub(r'^\d+[-_]', '', stem)
    stem = re.sub(r'\d{4}[-_]?\d{2}[-_]?\d{2}', '', stem)
    stem = re.sub(r'^\d+[-_.]', '', stem)
    raw = [t for t in re.split(r'[-_\s.]+', stem) if len(t) > 1]
    ascii_tags = [t.lower() for t in raw if re.match(r'^[A-Za-z][A-Za-z0-9+]*$', t)]
    cjk = re.findall(r'[\u4e00-\u9fff]{2,6}', stem)
    tags = []
    for t in ascii_tags + cjk:
        if t not in tags:
            tags.append(t)
    d = os.path.dirname(rel)
    if d and d != 'Docs':
        last = os.path.basename(d)
        last = re.sub(r'^\d+_', '', last)
        if last and not last.isdigit() and re.match(r'^[a-z0-9._-]+$', last) and last not in tags:
            tags.append(last)
    return tags[:8]


def link_targets(body: str):
    for m in re.finditer(r'\]\(\s*([^)\s]+?)(?:\s+"[^"]*")?\s*\)', body):
        yield m.group(1)


def backtick_paths(body: str):
    for m in re.finditer(r'`([^`\n]+)`', body):
        t = m.group(1).strip()
        if re.match(r'^(Source|Tests|TestScripts|Tools|Docs|src|external|Directory\.Build|Agents|README|code_map)', t):
            yield t


def norm_repo_path(root: str, base_dir: str, target: str):
    t = target.split('#')[0].strip()
    if not t or re.match(r'^(https?:|mailto:|file:|/|[A-Za-z]:)', t):
        return None
    from urllib.parse import unquote
    t = unquote(t)
    abs_p = os.path.normpath(os.path.join(root, base_dir, t.replace('/', os.sep)))
    if not os.path.exists(abs_p):
        return None
    return os.path.relpath(abs_p, root).replace(os.sep, '/')


def related_docs_for(root: str, rel: str, body: str) -> list:
    base = os.path.dirname(rel)
    seen, out = set(), []
    for t in link_targets(body):
        p = norm_repo_path(root, base, t)
        if p and p.lower().endswith('.md') and p != rel and p not in seen:
            seen.add(p)
            out.append(p)
        if len(out) >= 20:
            break
    return out


def related_files_for(root: str, rel: str, body: str) -> list:
    base = os.path.dirname(rel)
    seen, out = set(), []
    for t in backtick_paths(body):
        t = re.sub(r':\d+(-\d+)?$', '', t)
        p = os.path.normpath(os.path.join(root, t.replace('/', os.sep)))
        if os.path.isfile(p):
            rp = os.path.relpath(p, root).replace(os.sep, '/')
            if rp != rel and rp not in seen:
                seen.add(rp)
                out.append(rp)
        if len(out) >= 20:
            break
    return out


def slug_for(rel: str) -> str:
    stem = os.path.splitext(os.path.basename(rel))[0]
    stem = stem.strip().lower()
    stem = re.sub(r'[\\/:*?"<>|#%&{}$!\'@+`=（）「」【】：、，。？；—－]', '-', stem)
    stem = re.sub(r'[\s_]+', '-', stem)
    stem = re.sub(r'-{2,}', '-', stem).strip('-')
    # 加一层目录前缀，避免 README.md 这类同名文件 slug 冲突
    parts = rel.split('/')
    if parts[0] == 'Docs' and len(parts) > 2:
        prefix = re.sub(r'^\d+_', '', parts[1]).replace('_', '-')
        stem = '%s-%s' % (prefix, stem)
    elif parts[0] == 'Docs':
        stem = 'docs-%s' % stem
    return stem or 'doc'


def status_for(rel: str) -> str:
    # 只有真正的时间序日志与归档才是 archived；目录级 README（规则入口）保持 active
    if re.match(r'^Docs/00_changelog/20\d\dYear/', rel) or rel.startswith('Docs/90_archive/'):
        return 'archived'
    return 'active'


def git_follow_meta(root: str, rel: str):
    """文件被移动/改名后当前路径无历史：用 --follow 兜底取最早/最新提交。"""
    out = subprocess.run(['git', 'log', '--follow', '--no-merges', '--date=short',
                          '--format=%an|%ad', '--', rel], capture_output=True)
    lines = [l for l in out.stdout.decode('utf-8', 'replace').splitlines() if '|' in l]
    if not lines:
        return None
    newest = lines[0].split('|')
    oldest = lines[-1].split('|')
    return {'first_author': oldest[0], 'first_date': oldest[1],
            'last_author': newest[0], 'last_date': newest[1]}


def mtime_date(path: str) -> str:
    import datetime
    return datetime.date.fromtimestamp(os.path.getmtime(path)).isoformat()


# ---------------------------------------------------------------- 扫描
def iter_md(root: str, only_paths):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_NAMES]
        rel_dir = os.path.relpath(dirpath, root).replace(os.sep, '/')
        if rel_dir.startswith(SKIP_PATH_PREFIX):
            dirnames[:] = []
            continue
        for fn in filenames:
            if not fn.lower().endswith('.md'):
                continue
            rel = os.path.relpath(os.path.join(dirpath, fn), root).replace(os.sep, '/')
            if only_paths and not any(rel.startswith(p) for p in only_paths):
                continue
            yield rel


def build_expected(root: str, rel: str, meta: dict, default_author: str) -> dict:
    abs_p = os.path.join(root, rel)
    text = read_text(abs_p)
    fm, _, body_start = split_front_matter(text)
    body = '\n'.join(text.split('\n')[body_start:])
    m = meta.get(rel) or git_follow_meta(root, rel) or {}
    fallback_date = mtime_date(abs_p)
    title = first_heading(body, os.path.splitext(os.path.basename(rel))[0])
    return {
        'title': title,
        'author': m.get('first_author') or default_author,
        'date': m.get('first_date') or fallback_date,
        'last_reviewed': m.get('last_date') or m.get('first_date') or fallback_date,
        'status': status_for(rel),
        'description': first_paragraph(body),
        'categories': categories_for(rel),
        'tags': tags_for(rel, title),
        'related_docs': related_docs_for(root, rel, body),
        'related_files': related_files_for(root, rel, body),
        'slug': slug_for(rel),
        'draft': False,
    }


def validate(fm: dict) -> list:
    """返回不合规原因列表。
    列表字段：related_docs / related_files 允许为空（文件确实没有引用），但字段必须存在且为列表；
    categories / tags 不得为空。"""
    problems = []
    if fm is None:
        return ['缺少 Front Matter']
    for k in REQUIRED:
        if k not in fm:
            problems.append('缺少字段 %s' % k)
        elif k in LIST_FIELDS:
            if not isinstance(fm[k], list):
                problems.append('字段 %s 不是列表' % k)
            elif not fm[k] and k in ('categories', 'tags'):
                problems.append('字段 %s 为空列表' % k)
        elif str(fm[k]).strip() == '':
            problems.append('字段 %s 为空' % k)
    for k in ('date', 'last_reviewed'):
        if k in fm and not DATE_RE.match(str(fm[k])):
            problems.append('字段 %s 日期格式非法：%s' % (k, fm[k]))
    if 'status' in fm and str(fm['status']) not in VALID_STATUS:
        problems.append('字段 status 取值非法：%s' % fm['status'])
    if 'draft' in fm and str(fm['draft']).lower() not in ('true', 'false'):
        problems.append('字段 draft 非布尔：%s' % fm['draft'])
    return problems


def main() -> int:
    ap = argparse.ArgumentParser(description='Markdown Front Matter 校验/补齐')
    ap.add_argument('--check', action='store_true', help='只检查（不合规则退出码 1）')
    ap.add_argument('--fix', action='store_true', help='补齐缺失字段')
    ap.add_argument('--dry-run', action='store_true', help='配合 --fix：只预览不写盘')
    ap.add_argument('--force', action='store_true', help='配合 --fix：覆盖取值非法的字段')
    ap.add_argument('--paths', nargs='*', default=None, help='只处理这些前缀（仓库相对）')
    ap.add_argument('--limit', type=int, default=0, help='最多处理 N 个文件（调试用）')
    ap.add_argument('--report', default=None, help='把明细写入 JSON')
    ap.add_argument('--default-author', default='', help='git 无记录时的作者兜底')
    ap.add_argument('--all-repo', action='store_true', help='不限 Docs/（默认只处理 Docs/ 下的 md）')
    ap.add_argument('--print-sample', type=int, default=0, help='打印前 N 个将生成的 Front Matter')
    args = ap.parse_args()

    root = repo_root()
    os.chdir(root)
    if args.paths is None and not args.all_repo:
        args.paths = ['Docs/']
    default_author = args.default_author or subprocess.run(
        ['git', 'config', 'user.name'], capture_output=True, text=True).stdout.strip() or 'unknown'
    meta = git_meta()

    checked = fixed = printed = 0
    details = []
    files = list(iter_md(root, args.paths))
    if args.limit:
        files = files[:args.limit]

    for rel in files:
        checked += 1
        text = read_text(os.path.join(root, rel))
        fm, _, _ = split_front_matter(text)
        problems = validate(fm)
        if not problems:
            continue
        exp = build_expected(root, rel, meta, default_author)
        if args.print_sample and printed < args.print_sample:
            print('=== %s ===' % rel)
            print(render_fm(exp))
            print()
            printed += 1
            continue
        if args.fix:
            if fm is None:
                new_text = render_fm(exp) + '\n\n' + text
            else:
                merged = dict(fm)
                for k in REQUIRED:
                    cur = merged.get(k)
                    empty = (cur is None) or (isinstance(cur, list) and not cur) or (not isinstance(cur, list) and str(cur).strip() == '')
                    invalid = args.force and any(p.startswith('字段 %s ' % k) for p in problems)
                    if empty or invalid:
                        merged[k] = exp[k]
                # 保持字段顺序
                ordered = {k: merged.get(k, exp[k]) for k in REQUIRED}
                if ordered == {k: fm.get(k) for k in REQUIRED}:
                    details.append({'file': rel, 'problems': problems, 'action': 'skip-no-change'})
                    continue
                body = '\n'.join(text.split('\n')[split_front_matter(text)[2]:])
                new_text = render_fm(ordered) + '\n\n' + body
            fixed += 1
            if not args.dry_run:
                write_text(os.path.join(root, rel), new_text)
            details.append({'file': rel, 'problems': problems, 'action': 'fixed' if not args.dry_run else 'would-fix'})
        else:
            details.append({'file': rel, 'problems': problems, 'action': 'reported'})

    print('%s：扫描 %d 个 md；不合规 %d 个；%s %d 个' % (
        'CHECK' if not args.fix else ('DRY-RUN' if args.dry_run else 'FIX'),
        checked, len(details), '将补齐' if (args.fix and args.dry_run) else ('已补齐' if args.fix else '报告'), fixed))
    if args.report:
        write_text(os.path.join(root, args.report), json.dumps(
            {'checked': checked, 'noncompliant': len(details), 'fixed': fixed, 'details': details},
            ensure_ascii=False, indent=1))
        print('明细：%s' % args.report)
    for d in details[:15]:
        print('  - %s  [%s]' % (d['file'], '；'.join(d['problems'][:3])))
    if not args.fix and details:
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
