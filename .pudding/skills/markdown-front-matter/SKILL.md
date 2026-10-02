# Markdown Front Matter 规范与校验

## 来源

2026-10-02 Docs 结构整治：`Docs/` 下 622 个 md 原本**没有一个**带 Front Matter。用户裁定「md 文件开头必须是标准 Markdown Front Matter」，
遂落地工具 `Tools/Docs/front_matter.py` 与规则 `Agents.md`「Markdown Front Matter 规则（强制）」，并全量补齐。本技能把该流程固化为可复用步骤。

## 目标

让 `Docs/**/*.md` 的每个文件都具备**合规的 12 字段 Front Matter**，并在新增/移动文档后自动、可复核地维持该门禁。

## 触发条件

- 新增、拆分、移动、改名任何 `Docs/` 下的 md；
- `python Tools/Docs/front_matter.py --check` 非零退出；
- 用户要求「补齐/校验文档头」「加 Front Matter」「文档元数据规范化」；
- 需要按 `status` / `categories` / `tags` 检索或统计文档时。

## 字段规范（12 项，顺序固定）

| 字段 | 含义 | 取值来源（工具自动推导） |
|------|------|--------------------------|
| `title` | 标题 | 首个 H1（无则用文件名） |
| `author` | 作者 | git 首次提交作者（无记录用 `git config user.name`） |
| `date` | 创建日期 | git 首次提交日期（`YYYY-MM-DD`） |
| `last_reviewed` | 复核/改动日期 | git 最后一次提交日期 |
| `status` | 文档状态 | `draft` / `proposed` / `active` / `deprecated` / `archived`（时间序日志与 `90_archive` 用 `archived`） |
| `description` | 概要 | 首个段落 → 引用块 → 表首行（≤200 字） |
| `categories` | 分类 | `[docs, <所属目录>]`，如 `[docs, features]` |
| `tags` | 标签 | 文件名与目录名切分（去日期、纯数字） |
| `related_docs` | 引用的其他文档 | 文内 markdown 链接中真实存在的 md（仓库相对路径） |
| `related_files` | 关联文件 | 文内反引号里真实存在的文件路径 |
| `slug` | 短标识 | 文件名 slug + 目录前缀（如 `features-adr-desktop-shell-...`），避免 README 同名冲突 |
| `draft` | 草稿标记 | 默认 `false` |

示例（`Docs/12_features/` 下的 ADR）：

```yaml
---
title: ADR：WinUI Shell、既有 Web UI 与独立 Core 进程
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: active
description: "日期：2026-09-29。状态：Accepted（用户已裁定）……"
categories: [docs, features]
tags: [adr, desktop, shell, webui, separate, core, features]
related_docs: [Docs/14_reports/Desktop-Shell-Recovery-2026-09-29.md]
related_files: [Source/PuddingDesktop/PuddingDesktop.csproj]
slug: features-adr-desktop-shell-webui-separate-core-2026-09-29
draft: false
---
```

## 步骤

1. **先看门禁现状**

```bash
python Tools/Docs/front_matter.py --check                    # 默认只扫 Docs/；--all-repo 扫全仓库
python Tools/Docs/front_matter.py --check --report temp/fm.json   # 输出 JSON 明细
```

2. **预览将被补齐的内容（务必先 dry-run）**

```bash
python Tools/Docs/front_matter.py --fix --dry-run
python Tools/Docs/front_matter.py --fix --dry-run --print-sample 3      # 打印前 3 个将生成的块
python Tools/Docs/front_matter.py --fix --dry-run --paths Docs/12_features
```

3. **小范围试点 → 全量补齐**

```bash
python Tools/Docs/front_matter.py --fix --paths Docs/12_features
python Tools/Docs/front_matter.py --fix                      # 幂等：只补缺失字段，不覆盖已有非空值
python Tools/Docs/front_matter.py --fix --force              # 仅在需要覆盖"取值非法"的字段时使用
```

4. **回读验证**

```bash
python Tools/Docs/front_matter.py --check                    # 必须退出码 0
head -20 Docs/12_features/<某个文件>.md                       # 抽查：Front Matter 后紧跟原 H1，正文未被改动
python Tools/Docs/check_md_links.py                          # Front Matter 里的路径不应引入失效链接
```

5. **与代码一起提交**：同一原子任务一个 commit（见 `Docs/10_conventions/Agents-Hygiene.md`）。

## 质量门禁

- [ ] `python Tools/Docs/front_matter.py --check` 退出码 0（新增文件也不例外）
- [ ] 抽查文件：Front Matter 之后原正文**逐字未变**（工具只在文件头插入/合并字段）
- [ ] `related_docs` / `related_files` 中的路径在仓库内真实存在
- [ ] 未覆盖他方已写的非空字段（只有在确认取值非法时才用 `--force`）
- [ ] 归档/时间序日志的 `status` 为 `archived`，其余为 `active`（或按语义显式设置）
- [ ] `python Tools/Docs/check_md_links.py` 未因新增/改动引入新的失效链接

## 常见坑

1. **不要手写整个块再让工具覆盖**：`--fix` 只补缺失；已有非空值一律保留，`--force` 才会改非法值。
2. **`related_docs` / `related_files` 允许为空列表**（文件确实没有引用）；`categories` / `tags` 不得为空——
   校验规则据此区分，别把空列表当错误去"编造"引用。
3. **被移动/改名的文件**：新路径在 git 里没有历史，工具用 `git log --follow` 兜底（较慢），
   再无记录才回落到文件 mtime；批量补齐大改动后会明显变慢，属正常。
4. **只有 `Docs/**` 由默认门槛覆盖**；`.pudding/skills/**` 等技能文件按各自格式（`manifest.json` + `SKILL.md`）维护，
   不加 Front Matter，避免与技能加载器冲突。
5. **不要在 Front Matter 里写日志**：日期型进展写 `Docs/00_changelog/`；Front Matter 只描述"这篇文档是什么"。
6. **YAML 转义**：`description` 含 `:` / `#` / 引号时由工具自动加引号并转义；手改后务必再跑 `--check`。

## 关联

- 规则：`Agents.md`「Markdown Front Matter 规则（强制）」「Docs 目录与文档规范（强制）」
- 工具：`Tools/Docs/front_matter.py`（校验/补齐）、`Tools/Docs/check_md_links.py`（链接/锚点门禁）
- 入口：`Docs/README.md`（目录地图与放什么→去哪）
- 实施记录：`Docs/00_changelog/2026Year/10/2026-10-02-Docs结构整治与FrontMatter规范落地.md`
