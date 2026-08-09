# Code Map 增量更新

## 来源

6a8 ↔ 蜜糖 重复工作模式审计（2026-08-08），基于 `29a92b2`（全量 18 L2 + 1 L1 code_map 扩展）的经验抽象。

## 目标

当工作区新增一个子项目（含 `.csproj`）时，自动发现并为其生成标准 `code_map.md`（L2），同时更新根 `code_map.md`（L1）的索引表格，保持索引体系完整。

## 前提条件

- 项目根目录可通过 `.git` + 根 `code_map.md` 定位
- L1 索引的唯一位置是 `{PROJECT_ROOT}/code_map.md`；不要再读取旧路径 `{PROJECT_ROOT}/Source/code_map.md`
- 新增项目位于 `Source/{ProjectName}/`，含 `.csproj`
- `list_dir`、`file_search`、`file_read`、`file_write` 工具可用

## 步骤

### 步骤 1：定位项目根目录

```
从当前工作目录或已知路径出发，向上查找同时满足以下两个条件的目录：
  - 包含 .git（Git 仓库）
  - 包含 code_map.md（根 L1 索引）

该目录即为项目根 PROJECT_ROOT。
```

### 步骤 2：全量发现现有 code_map

```
file_search(pattern="code_map.md", directory="{PROJECT_ROOT}")
  → 获取所有 L1 和 L2 code_map.md 文件路径列表
  → 提取已覆盖的 Source/{Project}/ 目录集合 covered_dirs
```

### 步骤 3：发现无 code_map 的新项目

```
list_dir("{PROJECT_ROOT}/Source/")
  → 列出所有子目录
  → 筛选：包含 .csproj 的子目录
  → 从筛选中排除：已在 covered_dirs 中的项目
  → 得到 new_projects 列表
```

### 步骤 4：为每个新项目生成 L2 code_map.md

对每个新项目：
```
1. list_dir("Source/{Project}/") → 获取所有 .cs 文件
2. file_read 每个 .cs 文件的开头 50 行 → 推断类角色
3. 按以下表格格式生成 code_map.md：

# {ProjectName} CodeMAP

> 自动生成 | {日期}

| 文件 | 角色 | 说明 |
|------|------|------|
| `Models/Xxx.cs` | 领域模型 | ... |
| `Services/Xxx.cs` | 业务服务 | ... |
| ... | ... | ... |

4. file_write 到 Source/{Project}/code_map.md
```

### 步骤 5：更新 L1 根 code_map.md

```
1. file_read("{PROJECT_ROOT}/code_map.md") → 读取根索引
2. 在子项目表格末尾追加新行，格式：
   | `{ProjectName}` | [code_map](Source/{ProjectName}/code_map.md) |
3. file_write 回根 code_map.md
```

### 步骤 6：可选提交

```
git add Source/{new_projects...}/code_map.md {PROJECT_ROOT}/code_map.md
git commit -m "docs: code_map 增量更新 — 新增 {N} 个 L2 索引"
```

## 质量门禁

- [ ] 新生成的 L2 code_map.md 格式与已有的一致（参考 `29a92b2` 中的范例）
- [ ] L1 根索引表格链接路径正确（`Source/{Project}/code_map.md`）
- [ ] 没有覆盖或损坏已有的 L2 文件
- [ ] Git 提交信息包含新增项目名称

## 历史参考

- 全量扩展 commit: `29a92b2`（新增 12 个 L2，更新 1 个 L1）
- SKILL 创建会话: `acd7ee428f284f9a845663b1a4d971db`
