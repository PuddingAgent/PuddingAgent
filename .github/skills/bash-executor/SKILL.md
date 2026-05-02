---
name: bash-executor
description: 使用 bash 进入 Linux 环境执行命令、文本处理、运行脚本。支持 grep、sed、awk、find、git 等全部 Linux 命令。用 exit 退出 Linux 模式回到 Windows PowerShell。Triggers: bash, linux, grep, sed, awk, find, text processing, shell script, git.
argument-hint: "要执行的 bash 命令或脚本，例如 'grep -r TODO Source/' 或 '通过 bash 执行 sed 批量替换'"
---

# Bash Executor — Linux 命令执行

## 概述

通过 `bash` 命令进入 Linux 环境，支持完整的 Linux 命令行工具链（grep、sed、awk、find、vim、git 等），解决 Windows PowerShell 下命令不兼容、文本处理受限等问题。

**核心用法：先 `bash` 进入 Linux 模式，执行完命令后 `exit` 回到 Windows 模式。**

## 何时使用

**必须使用（Windows 下以下操作首选 bash）：**
- 文本搜索：`grep` 替代 `Select-String`，语法更简洁、性能更好
- 文本批量替换：`sed` 替代 PowerShell 字符串操作
- 结构化文本处理：`awk`
- 文件查找：`find` 替代 `Get-ChildItem -Recurse`
- 运行 bash 脚本（`.sh`）
- 任何需要 Linux 工具链的场景（`xargs`、`sort`、`uniq`、`wc`、`diff`、`patch` 等）

**跳过：**
- 编译 .NET 项目（推荐在 Windows PowerShell 下用 `dotnet build`）
- 运行 PowerShell 专属命令
- 安装 NuGet/NPM 包

## 使用方法

### 进入/退出

```bash
# 进入 Linux 环境
bash

# 此时在 Linux 模式下，可以执行任意 Linux 命令
ls -la
grep -r "ILogger" Source/ --include="*.cs"

# 退出 Linux 环境，回到 Windows PowerShell
exit
```

### 单条命令（不进入交互模式）

```
# 在 PowerShell 中直接运行单条 bash 命令（不用先 bash 再 exit）
# 使用 run_in_terminal 工具，command 参数以 bash -c 开头
bash -c "grep -r 'TODO' Source/Pudding.Agent/ --include='*.cs'"
```

## 典型场景

### 1. 代码搜索（grep）

```bash
# 递归搜索所有 .cs 文件中的关键字（不区分大小写）
grep -rn "ILogger" Source/ --include="*.cs"

# 搜索并排除 Tests 目录
grep -rn "catch" Source/ --include="*.cs" --exclude-dir=Tests | head -20

# 正则搜索（多个关键字）
grep -rn "password|secret|key" Source/ --include="*.json"
```

### 2. 文件查找（find + file_search 配合）

```bash
# 查找最近 7 天修改的 .cs 文件
find Source/ -name "*.cs" -mtime -7

# 查找超过 1000 行的 .cs 文件
find Source/ -name "*.cs" -exec wc -l {} \; | awk '$1 > 1000'

# 统计各模块 .cs 文件数量
find Source/ -name "*.cs" | cut -d/ -f2 | sort | uniq -c | sort -rn
```

### 3. 文本批量替换（sed）

```bash
# 将所有 .cs 文件中的 Debug.WriteLine 替换为 ILogger.Debug（先预览）
grep -rn "Debug.WriteLine" Source/ --include="*.cs"

# 批量替换（sed -i 原地修改，建议先 git stash 备份）
sed -i 's/Debug\.WriteLine/ILogger.Debug/g' $(grep -rl "Debug.WriteLine" Source/)
```

### 4. 统计与聚合

```bash
# 统计 catch 块数量
grep -r "catch" Source/ --include="*.cs" | wc -l

# 统计各模块代码行数
find Source/ -name "*.cs" | xargs wc -l | sort -rn | head -20
```

## 工作流

```
1. 评估任务是否适合 bash → 是 → 继续
2. bash 进入 Linux 环境
3. 执行命令（grep/sed/awk/find 等）
4. 验证结果
5. exit 退出，回到 Windows PowerShell
```

## 注意事项

- **不修改文件的操作（grep/find/wc 等）**：直接在 Linux 模式下执行，完事 exit
- **可能修改文件的操作（sed -i 等）**：执行前确保已 git stash 或提交，避免不可逆错误
- **多行 PowerShell 命令**仍需写临时 .ps1 文件执行，不要混用 bash 和 PowerShell 语法
- **编译/构建**仍建议在 Windows PowerShell 下用 `dotnet build`
