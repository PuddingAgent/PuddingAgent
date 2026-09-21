"""EF 迁移漂移审计（只读，不编译、不连库）。

回答任务卡验收标准 1：给出「漂移条目数 + 阻塞点」的**实测**证据，而不是印象定性。

方法（全静态）：
  1. 从 Migrations/*ModelSnapshot.cs 提取快照知道的实体集合 S；
  2. 从 Data/*DbContext.cs 提取模型声明的实体集合 C（DbSet<T> 与 Entity<T> 两种写法）；
  3. 从 *SchemaBootstrapper.cs 提取幂等建表语句里的表名集合 B（绕行面）；
  4. 统计迁移文件数、以及**缺少 .Designer.cs 的迁移数**（EF 生成的迁移必带 Designer）；
  5. 比较快照与最新源码的修改时间。

输出为三个集合差（漂移条目）：C-S（模型有、快照无）、S-C（快照有、模型无）、B 的覆盖情况。
用法：python TestScripts/ef-migration-audit.py
"""
from __future__ import annotations

import re
from pathlib import Path

REPO = Path(r"E:\github\AgentNetworkPlan\PuddingAgent")
PROJECTS = {
    "PuddingPlatform": REPO / "Source" / "PuddingPlatform",
}

ENTITY_IN_SNAPSHOT = re.compile(r'modelBuilder\.Entity\("([^"]+)"')
DBSET = re.compile(r"DbSet<([A-Za-z0-9_]+)>")
ENTITY_CALL = re.compile(r"\.Entity<([A-Za-z0-9_]+)>")
CREATE_TABLE = re.compile(r"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?[\"'\[\]]?([A-Za-z0-9_]+)", re.IGNORECASE)
MAX_LIST = 40


def simple(name: str) -> str:
    return name.split(".")[-1]


def read(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def report_section(title: str) -> None:
    print()
    print(f"=== {title} ===")


def main() -> int:
    for project, root in PROJECTS.items():
        migrations = root / "Migrations"
        data = root / "Data"
        if not migrations.is_dir():
            print(f"SKIP {project}: 没有 Migrations 目录")
            continue

        snapshot_files = sorted(migrations.glob("*ModelSnapshot.cs"))
        if not snapshot_files:
            print(f"SKIP {project}: 没有 ModelSnapshot")
            continue
        snapshot = snapshot_files[0]
        snapshot_text = read(snapshot)

        snapshot_entities = {simple(m) for m in ENTITY_IN_SNAPSHOT.findall(snapshot_text)}

        context_files = sorted(data.glob("*DbContext.cs")) + sorted(data.glob("*DbContextFactory.cs"))
        context_text = "\n".join(read(f) for f in context_files)
        context_entities = {m for m in DBSET.findall(context_text)}
        context_entities |= {m for m in ENTITY_CALL.findall(context_text)}

        bootstrapper_files = sorted(root.rglob("*SchemaBootstrapper.cs"))
        bootstrap_tables: dict[str, set[str]] = {}
        for file in bootstrapper_files:
            tables = {t for t in CREATE_TABLE.findall(read(file))}
            if tables:
                bootstrap_tables[file.name] = tables
        all_bootstrap_tables = set().union(*bootstrap_tables.values()) if bootstrap_tables else set()

        migration_cs = sorted(migrations.glob("2*.cs"))
        base_names = [f.name[:-3] for f in migration_cs if not f.name.endswith(".Designer.cs")]
        designer_names = {f.name[:-len(".Designer.cs")] for f in migration_cs if f.name.endswith(".Designer.cs")}
        without_designer = [n for n in base_names if n not in designer_names]

        report_section(f"{project} 快照 vs 模型")
        print(f"snapshot      = {snapshot.relative_to(REPO)}")
        print(f"snapshot 实体数 = {len(snapshot_entities)}")
        print(f"模型实体数      = {len(context_entities)}")
        print(f"来源文件        = {', '.join(f.name for f in context_files)}")

        model_only = sorted(context_entities - snapshot_entities)
        snapshot_only = sorted(snapshot_entities - context_entities)
        print(f"C-S（模型有、快照无）= {len(model_only)}")
        for name in model_only[:MAX_LIST]:
            print(f"    - {name}")
        if len(model_only) > MAX_LIST:
            print(f"    ... 其余 {len(model_only) - MAX_LIST} 个省略")
        print(f"S-C（快照有、模型无）= {len(snapshot_only)}")
        for name in snapshot_only[:MAX_LIST]:
            print(f"    - {name}")

        report_section(f"{project} 迁移链完整性")
        print(f"迁移数（含 Designer 计数）= {len(migration_cs)}，其中基迁移 = {len(base_names)}")
        print(f"缺 .Designer.cs 的迁移 = {len(without_designer)}")
        for name in without_designer:
            print(f"    - {name}")

        report_section(f"{project} bootstrapper 绕行面")
        print(f"bootstrapper 类数 = {len(bootstrapper_files)}（其中含 CREATE TABLE 的 = {len(bootstrap_tables)}）")
        print(f"bootstrapper 建表总数（去重）= {len(all_bootstrap_tables)}")
        covered = sorted(t for t in all_bootstrap_tables if t in {e.lower() for e in snapshot_entities}
                         or t in {e.lower() + "s" for e in snapshot_entities})
        print(f"其中表名与快照实体名可直接对应的 = {len(covered)}")
        for name in sorted(bootstrap_tables):
            print(f"    - {name}: {len(bootstrap_tables[name])} 张表")

        report_section(f"{project} 时间线")
        print(f"快照最后修改     = {snapshot.stat().st_mtime}")
        if migration_cs:
            newest = max(migration_cs, key=lambda f: f.stat().st_mtime)
            print(f"最新迁移文件     = {newest.name} @ {newest.stat().st_mtime}")
        if bootstrap_tables:
            newest_boot = max(bootstrapper_files, key=lambda f: f.stat().st_mtime)
            print(f"最新 bootstrapper = {newest_boot.name} @ {newest_boot.stat().st_mtime}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
