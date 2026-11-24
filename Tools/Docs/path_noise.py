#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""仓库噪声目录 / 路径前缀的**唯一真源**。

为什么单独成模块（2026-11-22）：同一份「哪些目录不算内容」的语义曾在三个工具里各写一遍 ——
`code_map_check.LIMITS["EXCLUDE_DIR_NAMES"]`（13 项，含 `.work`）、
`check_md_links.SKIP_DIRS`（15 项，曾缺 `.work`）、
`front_matter.SKIP_DIR_NAMES`（11 项，曾缺 `.work`）。

漂移的后果是**实测过的**，不是理论风险：
- `check_md_links --all-repo` 因缺 `.work`，扫描 13928 个 md、报 898 条失效目标
  （其中 764 条在 `.work/` 归档快照里），真实活树只有 134 条；
- `front_matter --all-repo` 同样原因，300 秒跑不完 —— 跑不完的门禁等于没有门禁。

消费者用法（放在脚本头部，保证以任意方式 import 都能解析到本模块）:

    import os, sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import path_noise

    dirnames[:] = [d for d in dirnames if d not in path_noise.DIR_NAMES]
    if path_noise.is_noise_path(rel_posix):
        ...
"""

from __future__ import annotations

#: 按目录名匹配（不限层级）。判据：构建产物 / 依赖 / IDE 与工具产物 / 归档快照。
DIR_NAMES = frozenset({
    # 构建与依赖
    "bin", "obj", "node_modules", "dist", "dist-dev", "pub",
    # 运行期与工具产物
    "temp", "tmp", ".pudding", "__pycache__", ".venv",
    # 测试与覆盖率产物：MSTest 每跑一次就新生成 `Deploy_<host> <ts>_<pid>/`、`coverage.*`、`*.trx`，
    # 若收进指纹（`Source/**` / `Tests/**` 递归 glob 会覆盖到），每跑一次测试都会把源指纹判成陈旧。
    "TestResults",
    # IDE / VCS
    ".git", ".vs", ".idea",
    # 整仓库归档快照（.gitignore 内；内部链接与相对路径本就不可达）
    ".work",
})

#: 仓库相对路径前缀（统一写成不带尾斜杠的形式，匹配时按 startswith 判断）。
PATH_PREFIXES = (
    "external/references",
    "external/github.hyfree.GM",
)


def is_noise_path(rel_posix: str) -> bool:
    """判断仓库相对路径（正斜杠）是否落在噪声范围内。"""
    p = rel_posix.replace("\\", "/").lstrip("./")
    for pref in PATH_PREFIXES:
        if p == pref or p.startswith(pref + "/"):
            return True
    return False
