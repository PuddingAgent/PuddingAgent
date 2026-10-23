"""为缺失 §5 源指纹的 code_map.md 生成并（--apply）写入元数据行。

唯一指纹真源 = Tools/Docs/code_map_check.py（import 复用 glob_fingerprint / list_scope_files /
rel_posix），不另写算法。

**每个候选 glob 都做"文件数守恒"校验**：`glob_fingerprint` 把 `bases = [base_dir, root]` 两轮
结果并集，因此组件相对写法会在仓库根二次命中（实测 `**/*.cs` 在每个组件都得到同一个 3379 文件
摘要；`Tools/**` 之类同名根目录同样污染）⇒ 只接受「实测量 == 组件内应有量」的 glob。校验同时
是 gitignore 探测器：容器里若含被 gitignore 的运行时/构建产物，整组件 glob 会多算而被拒，于是
自动退到更细的粒度。

取值优先级：
  A. 组件相对细分：一级子目录 `<sub>/**` + 顶层未忽略散文件按扩展名 `*<ext>`（覆盖最全、粒度最细）
  C. 全量紧凑：仓库根相对 `<组件>/**` 单个 glob（校验通过 == 恰好等于该组件的干净全量）
  D. 兜底：顶层散文件合并为 `<组件>/*` 一个 glob，子目录按文件数降序塞进行宽预算，
     未纳入项用紧随其后的 `> 指纹覆盖:` 行明示（不假装全量）
被污染的候选自动升级为根相对写法（`<组件>/<sub>/**`）重试；整目录都被 gitignore 的直接跳过
（它们本来就不属源指纹输入集）；IDE/OS 垃圾（`*.lscache`、`*.user`、`$*`）不纳入指纹。

实测成本：/mnt/d（drvfs）上一次全量约 9 分钟，故把 glob 实测结果缓存到 temp/cm_fp_cache.json，
dry-run 与 --apply 两趟不重复哈希（`--fresh` 可强制重算）。

用法（仓库根）：
  python Tools/Docs/code_map_fingerprint.py            # dry-run：只打印将要插入的内容
  python Tools/Docs/code_map_fingerprint.py --help     # 只打印本说明：不哈希、不写盘、不留缓存
  python Tools/Docs/code_map_fingerprint.py --apply    # 真正写盘（保留原 EOL 与 BOM）

只接受 --apply / --fresh / -h(--help)；其余参数一律 exit 2 —— 本工具 dry-run 也会做
全量哈希并写缓存，把拼错的参数当合法参数执行，会让一次「看一眼用法」跑满约 9 分钟。

前置输入：先跑 `python Tools/Docs/code_map_check.py --check --report temp/cm_rep.json`
生成报告（含 entry_rows 与 declared_fingerprint），本工具只补 declared_fingerprint 为空的文件。
"""
import io
import json
import os
import subprocess
import sys
import time

DOCS_DIR = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(DOCS_DIR)).replace('\\', '/')
sys.path.insert(0, DOCS_DIR)
import code_map_check as C  # noqa: E402

BOM = b'\xef\xbb\xbf'
REPORT = os.path.join(ROOT, 'temp', 'cm_rep.json')
CACHE_PATH = os.path.join(ROOT, 'temp', 'cm_fp_cache.json')
APPLY = '--apply' in sys.argv
TODAY = time.strftime('%Y-%m-%d')
MAX = C.LIMITS['MAX_LINE_LEN']
EXDIRS = set(C.LIMITS['EXCLUDE_DIR_NAMES'])
JUNK_EXT = {'.lscache', '.user', '.dotSettings'}
_CACHE = {}

KNOWN_FLAGS = ('--apply', '--fresh')
HELP_FLAGS = ('-h', '--help')


def usage(stream=None):
    """打印模块说明与合法参数表（只读，不触碰仓库）。"""
    stream = stream or sys.stdout
    stream.write((__doc__ or '').rstrip() + '\n')
    stream.write('\n合法参数：--apply（真写盘）、--fresh（忽略缓存重算）、-h/--help（本说明）\n')


def parse_argv(argv):
    """校验命令行：未知参数 fail-closed，-h/--help 立刻退出。

    为什么不用 argparse：本工具的 --apply 是破坏性写盘开关，而 dry-run 本身也要做
    全量 glob 哈希（约 9 分钟）并写 temp 缓存。argparse 会把未知参数报成 "unrecognized
    arguments" 也算友好，但历史脚本一直靠 `'--apply' in sys.argv` 取标志位；这里保留
    该形态、只补一道显式的参数白名单，让 `--help` 与拼错的参数都不会顺手触发哈希。
    """
    unknown = [a for a in argv if a not in KNOWN_FLAGS + HELP_FLAGS]
    if unknown:
        sys.stderr.write('未知参数：%s\n' % ' '.join(unknown))
        usage(sys.stderr)
        raise SystemExit(2)
    if any(a in HELP_FLAGS for a in argv):
        usage()
        raise SystemExit(0)


def rel(p):
    return C.rel_posix(ROOT, p)


def is_junk(name):
    return name.startswith('$') or os.path.splitext(name)[1] in JUNK_EXT


def git_date(repo_rel):
    try:
        out = subprocess.run(['git', 'log', '-1', '--date=short', '--format=%ad', '--', repo_rel],
                             cwd=ROOT, capture_output=True, text=True).stdout.strip()
    except OSError:
        out = ''
    return out or TODAY


def ignored_set(paths):
    if not paths:
        return set()
    out = subprocess.run(['git', 'check-ignore', '--stdin'], cwd=ROOT, text=True,
                         input='\n'.join(paths), capture_output=True).stdout
    return set(l.strip() for l in out.splitlines() if l.strip())


def scan(base):
    """返回一级子目录（含文件清单）、顶层散文件、被 gitignore 的路径集合。"""
    dirs, files = [], []
    for name in sorted(os.listdir(base)):
        full = os.path.join(base, name)
        if name == C.CODE_MAP_NAME or name in EXDIRS:
            continue
        if os.path.isdir(full):
            kids = [f for f in C.list_scope_files(ROOT, full) if not is_junk(os.path.basename(f))]
            if kids:
                dirs.append((name, kids))
        elif os.path.isfile(full) and not C._path_is_excluded(rel(full)) and not is_junk(name):
            files.append(name)
    everything = [rel(f) for _n, kids in dirs for f in kids] + [rel(os.path.join(base, f)) for f in files]
    return {'dirs': dirs, 'files': files, 'ignored': ignored_set(everything)}


def scan_raw(base):
    """不做垃圾过滤的原始视图：C 阶段整组件 glob 会连垃圾一起算，总账必须用原始口径。"""
    dirs, files = [], []
    for name in sorted(os.listdir(base)):
        full = os.path.join(base, name)
        if name == C.CODE_MAP_NAME or name in EXDIRS:
            continue
        if os.path.isdir(full):
            kids = C.list_scope_files(ROOT, full)
            if kids:
                dirs.append((name, len(kids), [rel(f) for f in kids]))
        elif os.path.isfile(full) and not C._path_is_excluded(rel(full)):
            files.append(name)
    return dirs, files


def measured(base, g, expect):
    key = '%s|%s' % (rel(base), g)
    if key not in _CACHE:
        fp = C.glob_fingerprint(ROOT, g, base_dir=base)
        _CACHE[key] = None if (fp is None or fp.get('truncated')) else (fp['value'], fp['file_count'])
    hit = _CACHE[key]
    if hit and hit[1] == expect:
        return hit
    if os.environ.get('CM_FP_DEBUG'):
        print('    [probe] %s expect=%d got=%s' % (g, expect, hit[1] if hit else 'none'))
    return None


def pair(base, comp, g, expect):
    """返回 '<glob>=<value>' 或 None；裸写法被污染时改用组件相对根写法重试。"""
    hit = measured(base, g, expect)
    if hit:
        return '%s=%s' % (g, hit[0])
    alt = '%s/%s' % (comp, g)
    hit = measured(base, alt, expect)
    return ('%s=%s' % (alt, hit[0])) if hit else None


def ext_groups(files):
    buckets = {}
    for n in files:
        e = os.path.splitext(n)[1]
        buckets.setdefault(e or n, []).append(n)
    return [(('*' + e) if e.startswith('.') else e, len(v)) for e, v in sorted(buckets.items())]


def build_line(pairs, entry_rows, path):
    return '> 源指纹: %s · 条目数: %d · 最近整理: %s' % (', '.join(pairs), entry_rows,
                                                     git_date(path))


def insert_lines(path, block):
    """把指纹块插到 YAML front matter 之后（无 front matter 则 H1 之后），并保留原 EOL 与 BOM。

    历史坑（已修）：
      1. 用 `startswith('# ')` 认 H1 时，文件带 UTF-8 BOM 会认不出来，块被插到 BOM 前面，
         BOM 被挤到第二行 ⇒ 现在先剥 BOM 再定位，写回时原样补回。
      2. 下一行本来就是空行时再补一个空行会产生连续两个空行 ⇒ 现在只在下行非空时补。
    """
    raw = io.open(path, 'rb').read()
    bom = raw.startswith(BOM)
    text = (raw[len(BOM):] if bom else raw).decode('utf-8')
    eol = '\r\n' if '\r\n' in text else '\n'
    lines = text.split(eol)
    if lines and lines[0].strip() == '---':
        end = next((i for i, l in enumerate(lines[1:], start=1) if l.strip() == '---'), None)
        at = (end + 1) if end is not None else 0
    else:
        at = next((i for i, l in enumerate(lines) if l.startswith('# ')), None)
        at = (at + 1) if at is not None else 0
    body = list(block)
    if at < len(lines) and lines[at].strip() != '':
        body.append('')
    out = eol.join(lines[:at] + body + lines[at:])
    io.open(path, 'wb').write((BOM if bom else b'') + out.encode('utf-8'))
    return at + 1


def plan(base, path, entry_rows):
    info = scan(base)
    comp = rel(base)
    ignored = info['ignored']

    # 整目录被 gitignore 的候选直接剔除（不属源指纹输入集），其余必须通过守恒校验
    dirs, files = [], list(info['files'])
    for name, kids in info['dirs']:
        rels = [rel(f) for f in kids]
        if all(r in ignored for r in rels):
            continue
        dirs.append((name, len(rels)))

    def mk(pairs):
        return build_line(pairs, entry_rows, path)

    raw_dirs, raw_files = scan_raw(base)
    raw_dirs = [(n, c) for n, c, rels in raw_dirs if not all(r in ignored for r in rels)]
    total = sum(c for _n, c in raw_dirs) + len(raw_files)

    # ---- A：细分全量
    pairs, ok = [], True
    for name, cnt in dirs:
        p = pair(base, comp, name + '/**', cnt)
        if p is None:
            ok = False
            break
        pairs.append(p)
    if ok:
        for g, cnt in ext_groups(files):
            p = pair(base, comp, g, cnt)
            if p is None:
                ok = False
                break
            pairs.append(p)
    if ok and pairs and len(mk(pairs)) <= MAX:
        return mk(pairs), [mk(pairs)], 'A', ''

    # ---- C：根相对整组件单 glob（总账用原始口径，含垃圾文件）
    hit = measured(base, comp + '/**', total)
    if hit:
        line = mk(['%s/**=%s' % (comp, hit[0])])
        if len(line) <= MAX:
            return line, [line], 'C', ''

    # ---- D：顶层散文件合并 + 子目录按文件数降序塞进预算
    # 顶层优先（入口文件比 CI/契约配置更重要）；隐藏文件无法被 glob `*` 匹配（Python glob 默认跳过
    # dot 文件），因此单独告知未纳入。
    pairs, covered, dropped = [], [], []
    visible = [n for n in raw_files if not n.startswith('.')]
    hidden = [n for n in raw_files if n.startswith('.')]
    if visible:
        hit = measured(base, comp + '/*', len(visible))
        if hit:
            pairs.append('%s/*=%s' % (comp, hit[0]))
            covered.append('*')
        else:
            dropped.extend(g for g, _c in ext_groups(visible))
    dropped.extend(hidden)
    for name, cnt in sorted(dirs, key=lambda d: -d[1]):
        p = pair(base, comp, name + '/**', cnt)
        if p is None:
            dropped.append(name + '/**')
            continue
        trial = pairs + [p]
        if len(mk(trial)) > MAX:
            dropped.append(name + '/**')
            continue
        pairs, covered = trial, covered + [name + '/**']
    if not pairs:
        return None, [], 'D', ','.join(dropped)
    line = mk(pairs)
    note = ''
    if dropped:
        note = '> 指纹覆盖: %s；未纳入 %d 项: %s' % ('、'.join(covered), len(dropped), '、'.join(sorted(dropped)))
        if len(note) > MAX:
            note = '> 指纹覆盖: %s；未纳入 %d 项（宽组件按行宽预算裁剪，未纳入项不参与指纹校验）' % (
                '、'.join(covered), len(dropped))
    return line, ([line] + ([note] if note else [])), 'D', ','.join(dropped)


def load_cache():
    if '--fresh' in sys.argv or not os.path.exists(CACHE_PATH):
        return
    try:
        data = json.load(io.open(CACHE_PATH, encoding='utf-8'))
    except ValueError:
        return
    for k, v in data.items():
        _CACHE[k] = tuple(v) if v else None


def save_cache():
    tmp = {}
    for k, v in _CACHE.items():
        tmp[k] = list(v) if v else None
    io.open(CACHE_PATH, 'w', encoding='utf-8').write(json.dumps(tmp, indent=0, sort_keys=True))


def main():
    parse_argv(sys.argv[1:])
    load_cache()
    rep = json.load(io.open(REPORT, encoding='utf-8'))
    print('MAX_LINE_LEN =', MAX, '| cache entries =', len(_CACHE))
    todo, failed = [], []
    for pf in rep['per_file']:
        if pf.get('declared_fingerprint'):
            continue
        path = os.path.join(ROOT, pf['path'])
        line, block, stage, dropped = plan(os.path.dirname(path), pf['path'], pf['entry_rows'])
        if not line or len(line) > MAX:
            failed.append(pf['path'])
            continue
        todo.append((pf['path'], block, stage, dropped))
    save_cache()

    print('--- 待写入 %d 份，失败 %d 份' % (len(todo), len(failed)))
    for p, block, stage, dropped in todo:
        n = len([d for d in dropped.split(',') if d])
        print('[stage=%s len=%d%s] %s' % (stage, len(block[0]), (' dropped=%d' % n) if n else '', p))
        for b in block:
            print('    ' + b)
    for f in failed:
        print('FAILED:', f)

    if APPLY:
        for p, block, _s, _d in todo:
            at = insert_lines(os.path.join(ROOT, p), block)
            print('WROTE line %d (+%d) -> %s' % (at, len(block), p))


if __name__ == '__main__':
    main()
