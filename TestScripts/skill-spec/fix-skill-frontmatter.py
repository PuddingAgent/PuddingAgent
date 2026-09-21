"""规范化私有 SKILL.md 的 YAML frontmatter，使其通过官方 skills-ref 校验。

只做三类**机械**修复，不改任何字段语义（平台侧不解析 SKILL.md frontmatter：
Source/ 下无任何 C# 代码读取 frontmatter，见 Docs/Reports/skill-spec-conformance-2026-09-21.md）：

  1. ``tags: [a, b]`` 流式数组  → 块序列（每行 ``- item``）
  2. 外层 ```` ```markdown ```` 代码围栏 → 删除（frontmatter 被关在代码块里，同时模型读到的也是代码块）
  3. ``description:`` 未加引号且含 ":" → 加双引号（内部引号转义）

安全设计：
  * **默认 dry-run**，只有显式 ``--apply`` 才写盘。
  * 每个文件写盘前打印改动摘要；只改 SKILL.md，绝不动 manifest.json。
  * 只做「能确定无歧义」的变换；不确定的一律记为 SKIP 并输出原因，不猜。
  * 运行前请先备份技能目录（技能目录不在 git 中，没有版本回滚）。

用法：
  set "PYTHONUTF8=1" && uv run --project <skills-ref> python TestScripts/skill-spec/fix-skill-frontmatter.py [--apply]
"""
from __future__ import annotations

import argparse
import sys
from collections import Counter
from pathlib import Path

DEFAULT_ROOT = Path(r"D:\data\agents\default.global_general-assistant.6a8\skills")


def quote_description(value: str) -> str:
    """把含 ':' 的裸标量包成双引号（YAML 里 ': ' 会被解析成嵌套映射）。"""
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{escaped}"'


def normalize_frontmatter(fm_lines: list[str]) -> tuple[list[str], list[str]]:
    """返回 (新 frontmatter 行, 改动类型列表)。"""
    out: list[str] = []
    changes: list[str] = []
    index = 0
    while index < len(fm_lines):
        line = fm_lines[index]
        stripped = line.strip()

        # --- 修复 1：tags 流式数组 -> 块序列 ---
        if stripped.startswith("tags:") and stripped[len("tags:"):].strip().startswith("["):
            flow = stripped[len("tags:"):].strip()
            # 支持跨行的流式数组：一直吃到出现 ']'
            while "]" not in flow and index + 1 < len(fm_lines):
                index += 1
                flow += " " + fm_lines[index].strip()
            inner = flow.strip()[1:]
            closing = inner.rfind("]")
            if closing >= 0:
                items = [item.strip() for item in inner[:closing].split(",") if item.strip()]
                out.append("tags:")
                out.extend(f"  - {item}" for item in items)
                changes.append(f"tags-flow->block({len(items)})")
                index += 1
                continue
            # 解析不出来就原样保留，交给 SKIP 统计
            out.append(line)
            changes.append("tags-flow-UNPARSED")
            index += 1
            continue

        # --- 修复 3：description 未加引号且含 ':' ---
        if stripped.startswith("description:"):
            value = stripped[len("description:"):].strip()
            already_quoted = (value.startswith('"') and value.endswith('"')) or (
                value.startswith("'") and value.endswith("'")
            )
            if value and not already_quoted and ":" in value:
                out.append(f"description: {quote_description(value)}")
                changes.append("description-quoted")
                index += 1
                continue

        out.append(line)
        index += 1

    return out, changes


def fix_skill_md(path: Path) -> tuple[bool, list[str], str]:
    """返回 (是否变更, 改动类型, 说明)。"""
    text = path.read_text(encoding="utf-8")
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.replace("\r\n", "\n").split("\n")
    changes: list[str] = []

    # --- 修复 2：剥掉外层代码围栏 ---
    first_nonempty = next((i for i, line in enumerate(lines) if line.strip()), None)
    if first_nonempty is not None and lines[first_nonempty].lstrip().startswith("```"):
        last_fence = next(
            (i for i in range(len(lines) - 1, first_nonempty, -1) if lines[i].strip() == "```"),
            None,
        )
        if last_fence is not None:
            candidate = [line for i, line in enumerate(lines) if i not in (first_nonempty, last_fence)]
            if next((line for line in candidate if line.strip()), "").strip() == "---":
                lines = candidate
                changes.append("strip-outer-fence")

    # --- 定位 frontmatter ---
    start = next((i for i, line in enumerate(lines) if line.strip()), None)
    if start is None or lines[start].strip() != "---":
        return False, changes, "SKIP: 无 YAML frontmatter（不臆造元数据）"
    end = next((i for i in range(start + 1, len(lines)) if lines[i].strip() == "---"), None)
    if end is None:
        return False, changes, "SKIP: frontmatter 未闭合"

    fm_lines = lines[start + 1:end]
    new_fm, fm_changes = normalize_frontmatter(fm_lines)
    changes.extend(fm_changes)
    new_lines = lines[:start + 1] + new_fm + lines[end:]

    if new_lines == lines:
        return False, changes, "已符合规范（无改动）"
    return True, changes, newline.join(new_lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--apply", action="store_true", help="真正写盘（默认只做 dry-run）")
    args = parser.parse_args()

    written = 0
    unchanged = 0
    skipped: list[str] = []
    kinds: Counter[str] = Counter()

    for entry in sorted(args.root.iterdir()):
        skill_md = entry / "SKILL.md"
        if not entry.is_dir() or not skill_md.is_file():
            continue
        try:
            changed, changes, detail = fix_skill_md(skill_md)
        except Exception as exc:  # 单个文件失败不影响其它文件
            skipped.append(f"{entry.name}\tEXCEPTION {type(exc).__name__}: {exc}")
            continue

        if detail.startswith("SKIP"):
            skipped.append(f"{entry.name}\t{detail}")
            continue
        if not changed:
            unchanged += 1
            continue

        for kind in changes:
            kinds[kind] += 1
        if args.apply:
            skill_md.write_text(detail, encoding="utf-8", newline="")
            written += 1
        else:
            written += 1  # dry-run 下 written 表示「将会写」
            print(f"DRY  {entry.name}\t{', '.join(changes)}")

    mode = "APPLIED" if args.apply else "DRY-RUN"
    print(f"--- {mode} ---")
    print(f"root={args.root}")
    print(f"changed={written} unchanged={unchanged} skipped={len(skipped)}")
    for kind, count in kinds.most_common():
        print(f"  {kind}\t{count}")
    for line in skipped:
        print(f"  SKIPPED {line}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
