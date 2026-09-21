# 缺陷报告：`file_patch` 同文件多条目批量编辑**静默丢失**（2026-09-21）

- **严重度**：高（静默数据丢失 + **虚假成功回报**；影响所有 Agent 的文档/代码编辑）
- **状态**：**已 100% 复现**；机制在代码中已定位（operations 路径确证，unified-diff 路径同构待一并确认）
- **触发条件**：单次 `file_patch` 调用中，`patches` 数组包含**两条及以上指向同一文件**的条目
- **发现者**：`default.global_general-assistant.6a8`（RSI S3 施工过程中，同一现象**两次**命中）

---

## 1. 最小复现（可重放）

**目标文件** `TestScripts/temp/file-patch-clobber-repro.txt`（调用前）：

```
LINE-A-ORIGINAL
LINE-B-ORIGINAL
LINE-C-ORIGINAL
```

**调用**：`file_patch(patches=[ {path: <同文件>, operations:[A→A-PATCHED]}, {path: <同文件>, operations:[B→B-PATCHED]} ])`

**工具回报**（两条**都**报成功）：

```
TestScripts\temp\file-patch-clobber-repro.txt: patched (1 replacements)
- LINE-A-ORIGINAL
+ LINE-A-PATCHED-BY-ENTRY-1

TestScripts\temp\file-patch-clobber-repro.txt: patched (1 replacements)
- LINE-B-ORIGINAL
+ LINE-B-PATCHED-BY-ENTRY-2
```

**实际文件内容**（读回）：

```
LINE-A-ORIGINAL            ← ⛔ entry 1 的编辑消失，但回报为成功
LINE-B-PATCHED-BY-ENTRY-2
LINE-C-ORIGINAL
```

---

## 2. 期望 vs 实际

| | |
|---|---|
| 期望 | 两条编辑都落盘 |
| 实际 | **只有最后一条落盘**，前一条**静默丢失**；**无告警、无错误、无差异提示** |

---

## 3. 影响评估

1. 任何"一次调用改同一文件多处"的用法都会**丢改动**，而模型**以为自己改成功了** ⇒
   后续推理、验证、提交全部建立在**不存在的改动**之上。
2. 因为回报是成功，**没有任何下游信号会暴露它** ⇒ 属最难发现的一类缺陷（不是"报错"，是"撒谎"）。
3. **已有两例真实事故**：均发生于 2026-09-21 RSI S3 规格文档 `Docs/Features/S3-轨迹源-实施规格-2026-09-21.md`
   的 §1.7 插入（同一现象两次：§7 条目落地、§1.7 整块丢失，而两条都报 `patched (1 replacements)`）。
4. 该缺陷**不会被任何现有测试发现**（没有同文件多条目用例）。

---

## 4. 机制（代码定位）

- **实测复现路径**：**operations 批量路径** —— `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`
  中 `patches` 数组的处理（约 `L911`–`L1041`）：为每个条目计算"新内容"，再统一写盘。
  同路径的多个条目**各自基于同一份原始文本**计算 ⇒ 写盘时后写覆盖先写 ⇒ 前者编辑被丢弃。
- **同构嫌疑（待一并确认）**：unified-diff 路径 `UnifiedDiffPatchRunner.Apply`（约 `L952`–`L1045`）结构相同——
  `var original = File.ReadAllText(fullPath);` 在**写盘之前**对每个 patch 条目各读一次，
  最后 `File.Move(tempFiles[i], touchedFiles[i].FullPath, overwrite: true)` 逐个覆盖。
- **⚠️ 自我更正**：提交 `184e770` 的信息曾称"机制已定位到 `UnifiedDiffPatchRunner.Apply`"。
  本次复现证明 **operations 路径同样/首先复现**；两条路径是否同源，留待修复时确认。**以本报告为准。**

---

## 5. 建议修复方向（**尚未实施**，待评估）

1. **按路径合并**：解析/规范化之后，把同一文件的所有条目合并为一个顺序 operations 序列 ⇒ **一次计算一次写盘**。
2. **或 fail-closed**：写盘前检测同路径重复，直接**拒绝**并提示调用方拆分调用 ——
   **拒绝优于静默丢失**（后者已经害了两次）。
3. **必须补契约测试**：同文件两条条目 ⇒ 两条编辑都生效（**当前必红**）；
   以及"同文件两条条目 + 其中一条匹配失败" ⇒ **不得留下半成品**。
4. **回报必须可机械验证**：`patched (1 replacements)` 应附带**写入后的内容指纹或行数**，
   让"报告成功但没落盘"这类问题**无法悄悄通过**。

---

## 6. 与当前 RSI 工作的关系

**⛔ 不得在本 RSI 片内夹带修复**：这是**工具层平台缺陷**，影响面远超 RSI（所有 Agent 的编辑都走它）。
⇒ 单独立卡跟踪（见看板），RSI 片内只保留**规避措施**：
同一文件的多处编辑**拆成多次调用**，并在编辑后**读回复核**（本片已按此执行）。
