"""一次性数据迁移：修复 Agent SKILL 的垃圾 summary（"name: <skillId>"）。

背景（Docs/Reports/skill-spec-conformance-2026-09-21.md §6.2 / §7 / §8.4）：
  AgentSkillFileService.DeriveSummary 原本只跳过 '---' 行、不跳过 YAML frontmatter 的字段行，
  于是只要 SKILL.md 带 frontmatter，派生摘要就变成 "name: <skillId>"。
  该缺陷已在平台上修复（只影响未来的 Create/Update），但**存量 manifest 仍是垃圾值**。

为什么不做成平台自愈（报告 §8.2 的取舍）：
  summary 可能是调用方**显式提供**的（Create 取 request.Summary ?? DeriveSummary），
  而 manifest 没有记录"来源"⇒ 平台盲目重算会覆盖作者写的摘要。
  因此把"一次性知识"（如何认出旧缺陷值）留在迁移工具里，生产代码不保留 Legacy 逻辑。

判定谓词（**关键安全设计**）：
  只有当 manifest.summary **恰好等于旧缺陷代码会对同一 markdown 产生的值**时才替换；
  否则一律 SKIP 并打印原因 —— 绝不做"无差别覆盖作者摘要"。

写盘方式：外科式替换 manifest.json 里那一行 `"summary": "..."`（正则 + JSON 转义），
  不重新序列化整个文件 ⇒ 其余字段、顺序、缩进逐字节不变。

安全设计：
  * **默认 dry-run**，只有显式 --apply 才写盘；
  * 不做备份（技能目录不在 git，请先自行打包；本轮已有 temp/skills-backup-20260921-1910.zip）；
  * 改完 summary 会让 manifest.contentHash 过期 —— 由平台新增的"索引重建自愈"在下次
    rebuild_index 时自动修正（见报告 §8），本脚本不碰 contentHash。

用法：
  set "PYTHONUTF8=1" && python TestScripts/skill-spec/repair-skill-summaries.py [--apply] [--root <skills 目录>]
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

DEFAULT_ROOT = Path(r"D:\data\agents\default.global_general-assistant.6a8\skills")

SUMMARY_FIELD = re.compile(r'^(?P<indent>\s*)"summary":\s*"(?P<value>(?:[^"\\]|\\.)*)"(?P<comma>,?)\s*$', re.MULTILINE)

# System.Text.Json 默认 JavaScriptEncoder 会把下列字符转义成 \uXXXX，这里对齐以保持风格一致。
# 注意：**不能**把 '"' 放进这张表 —— json.dumps 已经把内层引号输出成 \"，
# 再替换会**连外层引号一起毁掉**，直接产出非法 JSON（本脚本第一版就踩了这个坑，见报告 §9.2）。
_HTML_ESCAPES = {"&": "\\u0026", "<": "\\u003C", ">": "\\u003E", "'": "\\u0027", "+": "\\u002B"}


def encode_json_string(value: str) -> str:
    """序列化成 System.Text.Json 风格的 JSON 字符串字面量（含外层引号）。"""
    dumped = json.dumps(value, ensure_ascii=True)
    for char, escaped in _HTML_ESCAPES.items():
        dumped = dumped.replace(char, escaped)
    return dumped


def _non_empty_lines(markdown: str) -> list[str]:
    return [line for line in markdown.replace("\r\n", "\n").split("\n") if line.strip()]


def legacy_derive_summary(markdown: str) -> str:
    """逐字复刻**修复前**的 DeriveSummary，用于认出旧缺陷值。"""
    for raw_line in _non_empty_lines(markdown):
        line = raw_line.strip()
        if line == "---":
            continue
        if line.startswith("#"):
            line = line.lstrip("#").strip()
        if not line:
            continue
        return line[:240]
    return ""


def current_derive_summary(markdown: str) -> str:
    """镜像**修复后**的 AgentSkillFileService.DeriveSummary（跳过 frontmatter、优先 description）。"""
    lines = _non_empty_lines(markdown)
    index = 0

    if index < len(lines) and lines[index].strip() == "---":
        index += 1
        description: str | None = None
        while index < len(lines) and lines[index].strip() != "---":
            front_matter = lines[index].strip()
            if description is None and front_matter.startswith("description:"):
                description = front_matter[len("description:"):].strip().strip("\"'")
            index += 1
        if index < len(lines):
            index += 1
        if description and description.strip():
            return description[:240]

    for raw_line in lines[index:]:
        line = raw_line.strip()
        if line == "---":
            continue
        if line.startswith("#"):
            line = line.lstrip("#").strip()
        if not line:
            continue
        return line[:240]
    return ""


def repair(root: Path, apply: bool) -> int:
    stats: Counter[str] = Counter()
    skipped_other: list[str] = []
    pending: list[tuple[Path, str]] = []

    for entry in sorted(root.iterdir()):
        manifest_path = entry / "manifest.json"
        skill_md = entry / "SKILL.md"
        if not entry.is_dir() or not manifest_path.is_file() or not skill_md.is_file():
            continue

        text = manifest_path.read_text(encoding="utf-8")
        try:
            manifest = json.loads(text.lstrip("\ufeff"))  # 少数 manifest 带 BOM，解析时剥掉、写回时原样保留
        except json.JSONDecodeError as exc:
            skipped_other.append(f"{entry.name}\tmanifest 无法解析: {exc}")
            continue

        stored = manifest.get("summary") or ""
        markdown = skill_md.read_text(encoding="utf-8-sig")

        if stored != legacy_derive_summary(markdown):
            # 不是旧缺陷形态 ⇒ 可能是作者显式写的摘要，**不动**。
            stats["skip-not-legacy-shape"] += 1
            continue

        replacement = current_derive_summary(markdown)
        if not replacement or replacement == stored:
            stats["skip-no-better-value"] += 1
            continue

        match = SUMMARY_FIELD.search(text)
        if match is None:
            skipped_other.append(f"{entry.name}\t找不到可替换的 summary 字段行")
            continue

        updated = text[:match.start()] + (
            f"{match.group('indent')}\"summary\": {encode_json_string(replacement)}{match.group('comma')}"
        ) + text[match.end():]

        # 写盘前自检：改写后的文本必须仍是合法 JSON，且 summary 落在预期值上。
        # （这条自检就是为第一版那个“外层引号被转义”的事故补的闸门。）
        try:
            verified = json.loads(updated.lstrip("\ufeff"))
        except json.JSONDecodeError as exc:
            skipped_other.append(f"{entry.name}\t改写后不是合法 JSON，已拒绝写盘: {exc}")
            continue
        if verified.get("summary") != replacement:
            skipped_other.append(f"{entry.name}\t改写后 summary 校验不一致，已拒绝写盘")
            continue

        pending.append((manifest_path, updated))
        stats["would-repair"] += 1
        print(f"{'APPLY' if apply else 'DRY  '} {entry.name}\n     old={stored!r}\n     new={replacement[:100]!r}")

    if apply:
        # 两阶段提交：全部候选都通过自检后，才统一落盘。
        for path, updated in pending:
            path.write_text(updated, encoding="utf-8", newline="")
        stats.pop("would-repair", None)
        stats["repaired"] = len(pending)

    mode = "APPLIED" if apply else "DRY-RUN"
    print(f"--- {mode} ---")
    print(f"root={root}")
    for key, count in sorted(stats.items()):
        print(f"  {key}\t{count}")
    for line in skipped_other:
        print(f"  SKIPPED {line}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=DEFAULT_ROOT)
    parser.add_argument("--apply", action="store_true", help="真正写盘（默认只做 dry-run）")
    args = parser.parse_args()
    return repair(args.root, args.apply)


if __name__ == "__main__":
    sys.exit(main())
