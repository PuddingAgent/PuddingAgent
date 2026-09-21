# 技能与开放标准（Agent Skills spec）符合性检查 — 2026-09-21

- 检查对象：`D:\data\agents\default.global_general-assistant.6a8\skills`（144 个技能目录）
- 检查工具：**官方校验器** `agentskills/agentskills` 仓库的 `skills-ref`（Python 包，Apache-2.0）
- 方式：只读批量校验（`skills_ref.validate`），**未修改任何技能文件**
- 触发：RSI 设计文档 §9「外部调研整合」的 T0 切片

## 0. 结论

```
TOTAL=144  PASS=0  FAIL=144
```

**144 个技能没有一个通过官方格式校验。** 缺陷恰好分成三类，且 104 + 26 + 14 = 144（每技能归入唯一主类）：

| 缺陷类别 | 技能数 | 占比 | 根因 | 修法 |
|----------|--------|------|------|------|
| `TAGS_FLOW_STYLE` | **104** | 72.2% | `tags: [a, b]` 用 **YAML 流式数组**，被官方严格 YAML 判为 `ugly disallowed JSONesque flow mapping` | 改成块序列（每行 `- tag`），或整体移入 `metadata` |
| `NO_FRONTMATTER` | **26** | 18.1% | `SKILL.md` **不是**以 `---` 开头，而是以包裹用的 ```` ```markdown ```` 代码围栏开头 —— frontmatter 被关在代码块里 | 剥掉外层代码围栏 |
| `UNQUOTED_COLON_DESC` | **14** | 9.7% | `description` 未加引号且中间含 `: `，YAML 把它解析成嵌套映射（`mapping values are not allowed here`） | 给 description 加引号，或去掉冒号 |

> `NO_FRONTMATTER` 的证据（直接读原文件）：`agent-repo-health-check/SKILL.md` 首行是 ```` ```markdown ````，
> `---` 在其后；同一现象也被 `grep '^name:'` 独立佐证 —— 命中行号分两群，**line 2**（正常 frontmatter）
> 与 **line 3**（被代码围栏顶下去）。

## 1. ⚠️ 自我修正：我上一轮的判断不准确

上一轮我写「我们的技能格式与开放标准**完全不同**（元数据在 `manifest.json`、没有 frontmatter）」。**这是错的**：

- 实测 **118/144（82%）的 `SKILL.md` 本来就有 YAML frontmatter**，`name` 也符合「与父目录同名」；
- 真正的差距是两件事：
  1. **frontmatter 的机器有效性**（144/144 不通过 —— 流式 `tags`、代码围栏包裹、未引号冒号）；
  2. **description 的内容质量**：官方要求 `description` 同时写「做什么」和**「何时用」**并含助识别关键词；
     我们的 description 只写「做什么」（例：`agent-repo-health-check` 的描述完全不提 when to use）。

## 2. 工具链陷阱（可复用）

1. **`skills-ref` 在 zh-CN Windows 上直接崩**：`validator.py:172` 的 `skill_md.read_text()` **未指定 encoding**，
   而中文 Windows 的默认编码是 **GBK**，我们全部 `SKILL.md` 是 UTF-8 ⇒
   `UnicodeDecodeError: 'gbk' codec can't decode byte 0xae in position 352`。
   **解法**：设环境变量 `PYTHONUTF8=1`（强制 Python UTF-8 模式），**无需改它源码**。
2. **cmd 陷阱**：`set PYTHONUTF8=1 && …` 会把 `&&` 前的**空格**吃进变量值 ⇒
   `Fatal Python error: preconfig_init_utf8_mode: invalid PYTHONUTF8 environment variable value`。
   必须写 `set "PYTHONUTF8=1" && …`。
3. **官方 README 自陈**："This library is intended for **demonstration purposes only. It is not meant to be used in production.**"
   ⇒ 纠正调研中的表述：**许可**确实是 Apache-2.0（可用），但它**不能被当作生产依赖**；
   定位应为**参考校验器 / lint 门禁候选**。

复现命令（cmd）：

```cmd
set "PYTHONUTF8=1" && uv run --project <repo>\temp\agentskills-spec\skills-ref python <repo>\temp\validate-skills.py
```

（`temp/agentskills-spec` 为 `git clone --depth 1` 的官方仓库；`temp/validate-skills.py` 为批量校验+分类脚本。
两者都在不参与提交的 `temp/` 下，属一次性产物；若要把它接成门禁，应提升为正式脚本。）

## 3. 意义

1. **这类缺陷只能靠外部权威门禁发现。** 我们此前自研审计（`report-skill-portfolio.ps1` 的 content-audit / near-duplicate）
   看不到任何一条 —— 它检查的是「内容有没有用」，而官方校验器检查的是「**元数据机器可读吗**」。
   两者互补，不能互相替代。
2. **与 SkillsBench「自生成技能收益 ≈0」互相印证**：若自动生成的技能**连格式层都不合规**，
   就不能指望它的**路由元数据层**可靠 —— 而路由元数据正是渐进披露三层里的第一层（决定技能能不能被发现）。
3. **可直接复用的产物**：`skills-ref to-prompt` 能生成官方推荐的 L1 投影格式：
   ```xml
   <available_skills>
     <skill><name>…</name><description>…</description><location>/…/SKILL.md</location></skill>
   </available_skills>
   ```
   这正是我们设计的**元数据层**所需形态 ⇒ **直接采用，不必自研**（`<location>` 就是「模型按需去读正文」的入口）。

## 4. 下一步

1. 把 3 类缺陷修好（104 条改 `tags` 写法即可，机械且低风险；26 条剥围栏；14 条加引号），修完复跑校验，
   目标 **PASS 从 0 → 144**。⚠️ 属「改技能文件」，需作为独立切片并逐个可回滚（对应 C4）。
2. 之后把该校验接成**门禁**（新技能入库前必须 PASS），否则新生成的技能会继续复制同样的缺陷。
3. 同步把「description 必须写 when to use」列为**生成器侧契约** —— 这是自动化流水线的源头修复。

---

## 5. 修复执行记录（2026-09-21，同日闭环）

### 5.1 安全性前置

- 技能目录**不在 git 中**（无版本回滚）⇒ 先建回滚点：
  `temp/skills-backup-20260921-1910.zip`（495,328 bytes，全量打包）。
- 修复器默认 **dry-run**，只有显式 `--apply` 才写盘；只改 `SKILL.md`，**绝不动 `manifest.json`**。
- 修复器已提交：`TestScripts/skill-spec/fix-skill-frontmatter.py`（可复现、可重跑）。
- 语义安全性依据：`Source/` 下**没有任何 C# 代码读取 SKILL.md 的 frontmatter**（全局搜 `frontmatter` 零命中）；
  平台读的是 `manifest.json`，SKILL.md 只是「喂给模型的正文」⇒ 这几类修复**不改变平台行为**。

### 5.2 dry-run 结果（与官方分类对账）

| 修复类型 | 技能数 |
|---|---|
| `tags-flow->block`（2/3/4/5/6/8/9 项） | **139**（102+12+10+8+5+1+1） |
| `strip-outer-fence`（剥外层代码围栏） | **21** |
| `description-quoted` | **15** |
| SKIP（真正没有 frontmatter，**不臆造元数据**） | **5** |

对账：`21 + 5 = 26` 正好等于官方的 `NO_FRONTMATTER` 计数
⇒ 26 条里 **21 条是「frontmatter 被代码围栏包住」，只有 5 条是真的没有 frontmatter**。

### 5.3 apply 后的官方复验

**YAML 语法层修复成功**：原来 111 条 `Invalid YAML…` 全部消失 ——
错误类别从「**语法错**」推进到「**字段错**」，这是修复生效的客观证据。

```
TOTAL=144  PASS=0  FAIL=144
139  OTHER         → Unexpected fields in frontmatter: tags, version.
                     Only ['allowed-tools','compatibility','description','license','metadata','name'] are allowed
  5  NO_FRONTMATTER → atomic-delegation-discipline / code-map-incremental-update / image-prompt-builder /
                     multi-engine-topic-research / smart-committee-workflow
```

另发现**新一类**缺陷：`name` 超长（规范要求 **≤64 字符**）。已知超限示例：

- `goal-driven-heartbeat-orchestration-loop-for-long-running-engineering`（69）
- `milestone-handoff-with-collaborator-notification-and-memory-persistence`（71）
- `parallel-full-stack-feature-implementation-via-async-sub-agent-delegation`（73）
- `persist-runtime-discovered-constraints-and-behavior-switches-to-memory`（70）
- `prove-existing-semantics-unchanged-via-git-log-s-plus-baseline-range`（68）
- `recover-from-file-patch-anchor-mismatch-by-re-reading-the-target-text`（69）

### 5.4 ⭐ 结论：缺陷是**分层暴露**的

修好 YAML 语法层之后，校验器**才**能开始检查 schema 层；schema 层过了才会检查 `name` 约束。

⇒ 这本身就是「**为什么必须做成门禁**」的最好论证：**一次性清理永远追不上** ——
每修一层就露出下一层，只有把校验接成**入库门禁**，新生成的技能才不会继续复制同样的缺陷。
（外部调研里「138K SKILL.md 有 89.3% 违反 spec」正是这种持续劣化的结果。）

### 5.5 剩余两层需要**决策**，不是纯机械修复

1. **`tags` / `version` 不是规范字段**：规范只允许
   `name / description / license / compatibility / metadata / allowed-tools`，
   额外信息必须放进 `metadata`（string → string）。
   但 `tags` 承载 provenance（`auto-generated`、`dedup-reviewed:1.0.1`、`source-session:…`），
   直接删会丢诊断线索 ⇒ 倾向搬进 `metadata`（逗号连接的字符串）。
   **这是内容取舍，需要明确决策**，不顺手改。
2. **`name` ≤64 字符**：违规技能的**目录名 = skillId**，改名会破坏身份
   （`manifest.json`、SkillHub 记录、记忆/文档里的引用都会失效）。
   ⇒ 需要在「**改名**」与「**显式豁免（grandfather 名单）**」之间做治理决策。

（本轮到此停止：不在没有决策的情况下继续批量改内容。）

---

## 6. 副作用自查与新增发现（同一轮，改完文件后核实）

批量改了 139 个 `SKILL.md` 之后，我主动核实平台侧是否仍自洽，结果发现两件事。

### 6.1 `contentHash` 已失效（我造成的，**严重度低**，已记录）

证据（源码）：

```csharp
// Source/PuddingRuntime/Services/Skills/AgentSkillFileService.cs L517-L532
private static string ComputeContentHash(AgentSkillManifest manifest, string markdown)
{
    var canonical = JsonSerializer.Serialize(new
    {
        manifest.SkillId, manifest.Name, manifest.Version, manifest.Description,
        manifest.Summary, manifest.Tags, manifest.Enabled,
        SkillMarkdown = markdown,          // ← 包含 SKILL.md 正文
    }, JsonOptions);
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
```

`ContentHash` 只在 `CreateAsync` / `UpdateAsync` / `SetEnabledAsync` 里刷新（L107 / L207 / L246）。
我是**直接写盘**改的 `SKILL.md`，没有走这三个入口 ⇒ 139 个 manifest 的 `contentHash` 是**旧值**。
（`agent_skill rebuild_index` 也**不会**重算它：索引里的 `contentHash` 仍与改前一致 —— 已实测。）

**严重度评估：低。** 理由：该 hash 的用途是**变更检测**（本地 vs Hub 版本比对、publish/check-updates）。
当前状态是「磁盘内容已变、记录未变」⇒ 未来的变更检测会判定为**已变更**（与事实相符），
方向是安全的；反过来（内容变了却判定未变）才会造成静默错误。

**修复选项**（留作后续决策，不擅自手改 manifest）：
（a）走受支持入口逐个刷新（`agent_skill update` / `set_enabled`）—— 139 次调用，成本高；
（b）写脚本按 `ComputeContentHash` 复刻算法批量重算 —— 需复刻 `JsonOptions` 与字段顺序，**有算错风险**；
（c）随下一次技能治理（schema 层修复，本来就要改 `SKILL.md`）走受支持入口一并刷新 ⇒ **推荐**。

### 6.2 ⭐ 新发现：`DeriveSummary` 把 frontmatter 的 `name:` 行当成了摘要（平台缺陷）

`agent_skill get` / `get_index` 返回的 `summary` 全量是这样的：

```
"summary": "name: adjudicate-parallel-subagent-work-in-progress"
"summary": "name: agent-health-status-check"
"summary": "name: agent-repo-health-check"
...
```

根因（源码 L500-L515）：

```csharp
private static string DeriveSummary(string markdown)
{
    foreach (var rawLine in markdown.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line is "---")   // ← 只跳过 '---' 这一行
            continue;                            //    没有跳过 frontmatter 的**字段行**
        if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
        ...
        return line.Length <= 240 ? line : line[..240];
    }
    return string.Empty;
}
```

⇒ 只要 `SKILL.md` 有 YAML frontmatter，第一个非 `---` 的非空行就是 `name: <skillId>`，
于是**摘要变成了 `name: <skillId>` 这种垃圾值**（对全部带 frontmatter 的技能成立，实测 139/144）。
只有那 5 个**真正没有 frontmatter** 的手写技能摘要是正常的（它们的首行是 `# 标题`）。

**为什么这值得修**：`summary` 是**元数据层**的一部分（渐进披露第一层的候选内容，
也是 `ImproveSkillsAsync`/去重判断的输入）。摘要层被垃圾值污染，等于第一层元数据质量被破坏 ——
这与外部调研「26.4% 技能无 routing description / 路由元数据弱则检索失败」是同一类问题。

**建议修法**（下一轮作为独立原子任务，需带单测）：
`DeriveSummary` 应**先跳过 YAML frontmatter 块**（开头 `---` 到下一个 `---`），
再从正文里取首个有效行；且**优先取** frontmatter 的 `description`（若存在）而不是正文首行。

### 6.3 顺带确认（正面证据）：既有「取代」机制真的在工作

`agent-health-status-check` 的索引条目：

```
"enabled": false,
"tags": ["auto-generated", "self-evolution", "superseded", "superseded-by:agent-status-health-check"]
```

⇒ 「**取代而非新增**」在本仓库是**已在运行**的真实机制（不是设计愿景），
这为 RSI 的 `replaced_by` / `superseded` 语义提供了现成范本与落点。

### 6.4 本轮状态

- 平台侧一致性核查：`agent_skill rebuild_index` 已执行（144 条索引重建成功，属受支持入口，无副作用）。
- 未修改任何生产代码；`manifest.json` 一律未动。
- 回滚点仍在：`temp/skills-backup-20260921-1910.zip`。

---

## 7. `DeriveSummary` 缺陷已修复（2026-09-21 同日闭环）

§6.2 发现的平台缺陷已修复并带契约测试。

### 7.1 改动

`Source/PuddingRuntime/Services/Skills/AgentSkillFileService.cs`

1. **`DeriveSummary` 先跳过 YAML frontmatter 块**（开头 `---` 到下一个 `---`），
   不再把 `name: <skillId>` 当成摘要 —— 这是 139/144 个技能摘要退化的直接原因。
2. **frontmatter 里若有 `description`，优先采用它**：按 Agent Skills 规范，
   `description` 本就该写明「做什么 / 何时用」，是摘要的最佳来源；
   同时自动去掉 `description` 值外层可能存在的引号。
3. 抽出 `ClampSummary`（240 字符截断）供两条路径复用，保持原有截断语义不变。

### 7.2 契约测试（`Source/PuddingRuntimeTests/Services/AgentSkillFileServiceTests.cs`）

新增 5 条，覆盖「新行为 + 向后兼容 + 边界」：

| 测试 | 断言 |
|------|------|
| `CreateAsync_DerivesSummaryFromFrontmatterDescription_NotTheNameLine` | 有 `description` ⇒ 摘要 = description（**不再**是 `name:` 行），且外层引号被剥离 |
| `CreateAsync_SkipsFrontmatterBlock_WhenNoDescriptionIsGiven` | 无 `description` ⇒ 跳过 frontmatter，取正文首行标题 |
| `CreateAsync_DerivesSummaryFromFirstHeading_WhenNoFrontmatter` | 无 frontmatter ⇒ 行为与修复前一致（**向后兼容**） |
| `CreateAsync_YieldsEmptySummary_WhenOnlyFrontmatterIsPresent` | 只有 frontmatter ⇒ 空摘要（不返回 `name:` 垃圾值） |
| `CreateAsync_TruncatesDerivedSummaryTo240Characters` | 240 字符截断语义保持不变 |

### 7.3 实测结果

```cmd
dotnet test Source\PuddingRuntimeTests\PuddingRuntimeTests.csproj --filter "FullyQualifiedName~AgentSkillFileServiceTests"
```

```
已通过! - 失败: 0，通过: 14，已跳过: 0，总计: 14，持续时间: 549 ms
```

（该类原有 9 条测试全绿 + 新增 5 条全绿 ⇒ 修复**未破坏**既有语义。）

### 7.4 仍未解决的存量问题（下一轮）

修复只影响**未来**的 `CreateAsync` / `UpdateAsync`（显式传空 Summary 时）；
**已有 139 个技能的 manifest 里仍是旧的垃圾摘要**，且 §6.1 的 `contentHash` 同样过期。
已确认的路径事实：

- `RebuildIndexCoreAsync` 只读 manifest 的 `Summary`（`L378`），**不会**从磁盘重算 ⇒ `rebuild_index` 修不了；
- `UpdateAsync` 在 `request.Summary == null` 时**保留**旧值（`L198-202`）⇒ 只有显式传空串才会触发 `DeriveSummary`。

⇒ 正确的修复顺序是：**让 `UpdateAsync(Summary="")` 走一遍**（同时刷新 `summary` 与 `contentHash`，因为
`L207` 会一并重算 hash）。这需要 139 次调用或一个走**真实服务代码**的批量入口（不要复刻哈希算法）。
倾向后者：写一个调用 `AgentSkillFileService` 本体的维护脚本/测试宿主，
以受支持入口批量刷新，避免手工复刻 `ComputeContentHash` 造成漂移。

---

## 8. 机制性修复：索引重建自愈派生字段（2026-09-21，同日）

§6.1（`contentHash` 失效）与 §7.4（存量摘要无法刷新）的共同根因是**机制问题**，不是一次性事故：

> manifest 里的 `ContentHash` 是 `(manifest, SKILL.md 内容)` 的**派生值**，
> 但只有走 `CreateAsync` / `UpdateAsync` / `SetEnabledAsync` 才会重算。
> 而 **SKILL.md 被直接改盘是常态**（Agent 用 `file_write` 改技能正文、批量修复工具直接写盘），
> `RebuildIndexCoreAsync` 又**只读 manifest、根本不读 SKILL.md** ⇒ 派生字段可以无限期漂移，
> 而**索引层会照抄这些陈旧值** ⇒ 索引开始"说谎"。

### 8.1 改动：`RebuildIndexCoreAsync` 增加 `HealDerivedMetadataAsync`

`Source/PuddingRuntime/Services/Skills/AgentSkillFileService.cs`

- 索引重建时读取 `SKILL.md`，重算 `ContentHash`；
- **只在重算结果与现值不同时才回写 manifest**（原子写）⇒ 正常情况下**零写入**（幂等）；
- 不动 `UpdatedAt`（技能内容本身并没有被"更新"）。

### 8.2 ⚠️ 设计被测试当场纠正（重要教训）

**第一版设计错了**：我顺手也重算了 `Summary`。跑测试立刻出现 **2 条既有契约测试失败**：

```
失败 CreateAsync_Writes_Manifest_SkillMarkdown_And_Index
  预期 "Follow local coding standards before editing."（调用方显式给的 Summary）
  实际 "Coding Rules"（从 markdown 首行派生的）
失败 UpdateAsync_Updates_Manifest_Markdown_And_ContentHash
  预期 "Updated summary"  实际 "Updated"
```

根因：**`Summary` 不一定是派生值** —— `CreateAsync` 取 `request.Summary ?? DeriveSummary(markdown)`，
`UpdateAsync` 也允许调用方显式提供，而 **manifest 没有记录"来源"**。
盲目重算会**覆盖作者写的摘要**。

⇒ 修正后的设计边界：

| 字段 | 是否自愈 | 理由 |
|------|----------|------|
| `ContentHash` | ✅ 自愈 | 定义上就是 `(manifest, SKILL.md)` 的纯派生值，重算恒正确，不承载作者意图 |
| `Summary` | ❌ 不自愈 | 可能是调用方显式提供；manifest 无来源标记 ⇒ 重算会覆盖作者意图 |

**这正是"只写不跑是自欺"的现场验证**：一个看起来顺手的"顺手也修一下"，
如果没有测试，就会静默地把所有显式摘要改写成派生值。

### 8.3 契约测试（`AgentSkillFileServiceTests.cs`，该类现 16 条）

| 测试 | 断言 |
|------|------|
| `RebuildIndexAsync_HealsContentHash_ButPreservesAuthoredSummary` | 改盘后重建：hash 变化 ✅、**显式 Summary 保持不变** ✅、再重建一次不再写盘（**自愈收敛为不动点**）✅ |
| `RebuildIndexAsync_LeavesManifestUntouched_WhenDerivedFieldsAreConsistent` | 派生字段一致时 manifest **逐字节不变**（幂等，不得变成无意义写入源） |

实测：`失败 0，通过 16，总计 16，457 ms`（含 2 条既有测试的回归保护）。

### 8.4 存量数据的两条修复路径（明确区分）

| 目标 | 路径 | 前置 |
|------|------|------|
| 139 个过期 `contentHash` | **自动**：重启加载新二进制后调一次 `agent_skill rebuild_index` 即全部自愈 | 需要一次重启（批次由 6a8 决定） |
| 139 个垃圾 `Summary`（`name: <skillId>`） | **需一次性数据迁移**：平台侧故意不自动重算（见 §8.2）⇒ 走受支持入口 `UpdateAsync(Summary="")` 逐个刷新，或写一次性迁移脚本（须带"当前值等于旧缺陷形态"的判定谓词，不得无差别覆盖） | 无（脚本方式不需要重启） |

> 设计取舍已记录：把**永久机制**（hash 自愈）放进平台，把**一次性知识**（旧缺陷形态判定）留在迁移工具里，
> 避免在生产代码中长期保留 `LegacyDeriveSummary` 之类的兼容逻辑。

---

## 9. 一次性数据迁移：修复存量 141 个垃圾 `Summary`（2026-09-21）

§8.4 的第 2 条路径（存量垃圾摘要）本轮执行完毕。**含一次我自己造成的生产事故与回滚，如实记录。**

### 9.1 工具与判定谓词

新增 `TestScripts/skill-spec/repair-skill-summaries.py`（独立于 SKILL.md 格式修复器）：

- **默认 dry-run**，只有 `--apply` 才写盘；
- **判定谓词（安全核心）**：只有当 `manifest.summary` **恰好等于旧缺陷代码会对同一 markdown 产生的值**
  （`legacy_derive_summary`，逐字复刻修复前的 `DeriveSummary`）时才替换；否则 SKIP 并打印原因
  ⇒ **绝不会无差别覆盖作者写的摘要**；
- **外科式写入**：只替换 `manifest.json` 里 `"summary": "..."` 那一行的值（正则 + JSON 转义），
  不重新序列化整个文件 ⇒ 其余字段、顺序、缩进**逐字节不变**；
- 不碰 `contentHash`（由 §8 的平台自愈机制在下次 `rebuild_index` 时修正）；
- 兼容 UTF-8 BOM manifest（解析剥 BOM、写回原样保留）与 BOM 的 SKILL.md。

### 9.2 ⚠️ 事故：第一版脚本把 JSON 外层引号也转义了（141 个 manifest 变非法 JSON）

第一版 `_HTML_ESCAPES` 里我顺手把 `'"' → '\u0022'` 也加进了"对齐 System.Text.Json 风格"的转义表。
但 `json.dumps` **已经把内层引号输出成 `\"`**，而且**外层定界引号也必须保持字面量** ——
我的后处理把它们一并替换，产出：

```json
"summary": \u0022Quickly obtain an overview ...\u0022,
```

⇒ **非法 JSON**，直接破坏 141 个 manifest。**平台会因此读不到这些技能。**

**回滚**：因为有"改盘前先打包"的习惯，我打了一个只相差 18 秒的备份
（`temp/skills-backup-20260921-1935.zip`，19:35:15 打包、19:35:33 写盘），
用 `Expand-Archive` + 逐目录回写 `manifest.json` ⇒ `restored=144`，并核实样例回到 `"summary": "name: agent-repo-health-check"`。

**修正**（本次一并提交）：
1. **从转义表里删掉 `'"'`**（并写死注释说明为什么不能加）；
2. **新增写盘前自检**：改写后的文本必须 `json.loads` 成功，且 `summary` 落在预期值上，否则拒绝写盘；
3. **两阶段提交**：所有候选都通过自检后才统一落盘（不再边算边写）。

### 9.3 迁移结果与独立验证

```cmd
set "PYTHONUTF8=1" && python TestScripts\skill-spec\repair-skill-summaries.py --apply
```

```
--- APPLIED ---
  repaired              141
  skip-no-better-value    1
  skip-not-legacy-shape   2
```

**独立验证（用平台自己的解析器，而不是再信一遍我的脚本）**：
调用 `agent_skill action=rebuild_index` ⇒ **`count: 144`** —— 141 个被改写的 manifest
全部可被平台的 `AtomicFileWriter.ReadJsonAsync` 读回，索引也已带上真实摘要（抽样：`agent-repo-health-check`
的 `summary` 已是其 `description` 全文）。若仍有非法 JSON，该调用会失败或计数不足。

剩余 3 个未修且**故意不修**：2 个摘要不是旧缺陷形态（疑似作者手写）、1 个没有更好的可派生值。

### 9.4 教训（已固化为习惯）

1. **文本级外科替换必须回验"能否解析"** —— 人眼看 diff 看不出 `\u0022` 与 `"` 的区别，解析器一秒就能。
2. **备份要紧贴改动**：这次能 30 秒内无损回滚，唯一原因是备份就在写盘前 18 秒打的。
3. **不要在"对齐序列化器风格"的转义表里放引号** —— 序列化器负责定界，后处理只该管内容字符。
4. `141` 与 dry-run 的 `139` 差 2：BOM 修好后多修了 2 个（1 个 manifest 带 BOM、1 个 SKILL.md 带 BOM）
   ⇒ 说明**迁移工具的输入解码方式本身也会影响判定谓词命中数**，必须在报告里对账而不是含糊过去。

### 9.5 遗留

- 141 个 manifest 的 `contentHash` 仍是旧值：重启加载新二进制后调一次 `rebuild_index` 即由 §8 自愈机制修正；
- 若将来出现同类迁移，**先跑 dry-run 并抽样人工核对 new 值语义**（本轮 dry-run 已做，且正是它暴露了 139→141 的差异）。
