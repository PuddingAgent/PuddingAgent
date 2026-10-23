"""code_map_fingerprint.py 命令行契约自测（只读，不跑全量哈希）。

**为什么需要它**：2026-10-23 我为了看用法执行了 `code_map_fingerprint.py --help`，
该版本没有 argparse、也不认识 `--help`，于是**静默按 dry-run 跑了起来**（打印
`MAX_LINE_LEN = 300 | cache entries = 254`）。它当时写下 0 份 code_map（因为 48 份
都已声明指纹）、只写了 temp 缓存，所以损失是「几秒 + 一次缓存覆写」；但同一路径下
只要有人顺手带上 `--apply`，一次「看一眼用法」就会变成写盘。**工具本身要对参数 fail-closed。**

自测覆盖的契约（每条都能取红）：
  1. `--help` / `-h` → exit 0，并打印合法参数表；
  2. 未知参数（`--bogus`）→ exit 2，且 **stderr 指出是哪个参数**；
  3. `--apply --bogus` → exit 2（破坏性开关不得因「顺带一个拼错的参数」而生效）；
  4. `--help` **不执行计划**（stdout 无 `[stage=`/`待写入`），且**不写缓存**。

用法（仓库根）：
  python Tools/Docs/code_map_fingerprint_selftest.py
取红验证（改坏实现后必须变红）：
  临时删掉 `main()` 首行 `parse_argv(sys.argv[1:])`，重跑本自测 → 应出现 3 条 FAIL。
"""
import io
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
TARGET = os.path.join(HERE, 'code_map_fingerprint.py')
CACHE = os.path.join(ROOT, 'temp', 'cm_fp_cache.json')

_FAILS = []
_PASSES = 0


def check(name, cond, detail=''):
    global _PASSES
    if cond:
        _PASSES += 1
        print('PASS  %s' % name)
    else:
        _FAILS.append(name)
        print('FAIL  %s%s' % (name, ('  <- ' + detail) if detail else ''))


def run(*args):
    env = dict(os.environ, PYTHONIOENCODING='utf-8')
    return subprocess.run([sys.executable, TARGET] + list(args), cwd=ROOT, env=env,
                          capture_output=True, text=True, encoding='utf-8', errors='replace')


def cache_stamp():
    try:
        st = os.stat(CACHE)
        return (st.st_size, st.st_mtime_ns)
    except OSError:
        return None


def main():
    before = cache_stamp()

    r = run('--help')
    check('contract: --help exits 0', r.returncode == 0, 'rc=%s' % r.returncode)
    check('contract: --help prints accepted flags',
          '--apply' in r.stdout and '合法参数' in r.stdout)
    check('contract: --help runs no plan (no stage line)',
          '[stage=' not in r.stdout and '待写入' not in r.stdout)

    r = run('-h')
    check('contract: -h exits 0', r.returncode == 0, 'rc=%s' % r.returncode)

    r = run('--bogus')
    check('contract: unknown flag exits 2', r.returncode == 2, 'rc=%s' % r.returncode)
    check('contract: unknown flag is named on stderr', '--bogus' in r.stderr)
    check('contract: unknown flag runs no plan',
          '[stage=' not in r.stdout and '待写入' not in r.stdout)

    r = run('--apply', '--bogus')
    check('contract: --apply + unknown flag still exits 2', r.returncode == 2,
          'rc=%s' % r.returncode)
    check('contract: --apply + unknown flag writes nothing', r.stdout.strip() == '')

    check('contract: help/invalid paths leave the hash cache untouched',
          cache_stamp() == before, 'before=%r after=%r' % (before, cache_stamp()))

    print('\n%d PASS / %d FAIL' % (_PASSES, len(_FAILS)))
    if _FAILS:
        print('FAILED: ' + ', '.join(_FAILS))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
