# Task 08 — 记忆系统设计方案

> **状态：** ✏️ 设计中
> **依赖：** Task 09 (Agent 生命周期)
>
> **⚠️ 定位变更：** 记忆系统已从代码文档记忆升级为**个人生活图谱**。存储内容涵盖用户约束（过敏/预算/偏好）、任务沉淀知识、长期情感链接三层。记忆目录为 `~/Documents/Pudding/Memory/`，核心设计见 [task18定位.md](../Tasks/task18定位.md)。

---

参考 **OpenClaw** 的设计并结合我们 **PuddingAssistant** 的特点，我们可以为 Agent 构建一个“三层记忆、文件为本、混合检索”的记忆系统。

在 PuddingAssistant 中，记忆不只是数据库里的 0 和 1，而是**透明的、可编辑的、像布丁一样易于塑形的 Markdown 文件**。

---

### 1. 三层架构：从瞬时到永恒

我们将记忆划分为三个物理层级：

| 记忆层级 | 存储形式 | 生命周期 | 核心作用 |
| --- | --- | --- | --- |
| **短期记忆 (STM)** | LLM Session | 活跃会话期间 | 维护对话连贯性，利用云端 Session Cache 节省 Token。 |
| **工作记忆 (WM)** | `memory/YYYY-MM-DD.md` | 任务/项目周期 | 记录当日决策、中间步骤、命令行输出。属于“热数据”。 |
| **长期记忆 (LTM)** | `MEMORY.md` | 永久 | 存储架构设计、个人偏好、已解决的疑难 Bug。属于“精华”。 |

---

### 2. 参考 OpenClaw 的“主动刷写”机制

记忆不能只靠被动存储。我们要引入 **“压缩前冲洗 (Pre-compaction Flush)”**：

* **机制：** 当 Agent 的 Context 快满（触发 Compaction）时，系统会向 Agent 发送一个**静默指令（Silent Ping）**。
* **指令内容：** “会话即将被压缩。请在丢失细节前，将当前任务的关键决策、待办事项或重要发现写入 `memory/YYYY-MM-DD.md`。”
* **视觉效果：** 在 Swarm 日志中，你会看到一个微小的图标 💾 闪烁，代表 Agent 正在“整理思绪”。

---

### 3. 混合检索系统：BM25 + Vector

既然使用 Markdown 存储，检索就是核心。我们采用 **Hybrid Search** 以兼顾“模糊意图”和“精确查找”：

* **向量检索 (Vector)：** 适合“我想找找关于之前讨论过的数据库优化建议”。
* **全文检索 (BM25/FTS5)：** 适合“找到那个具体的错误代码 `0x8004210B`”或“查看 `AuthService.cs` 的重构记录”。
* **技术栈：** 使用 **SQLite + sqlite-vec** 扩展。Markdown 文件作为 Single Source of Truth，SQLite 作为索引。

---

### 4. 记忆的“拟人化”管理 (Pudding Style)

为了让记忆系统更酷炫：

* **记忆气泡 (Memory Bubbles)：** 在 Agent 的 Detail 面板，可以看到几个漂浮的小气泡，文字是 `MEMORY.md` 中的关键词。
* **记忆转移：** 当 Leader 销毁一个 Worker 时，界面会显示一个**“知识导出”**的动画——一串字符流从即将消失的 Worker 飞向 Leader 或 `MEMORY.md`。
* **人机共创：** 用户可以直接打开 `MEMORY.md` 修改里面的内容。Agent 在下一次检索时会立即感知到你的修改，这种“修改记忆”的感觉非常像是在给 AI “洗脑”。

---

### 5. 跨 Agent 共享与隔离策略

* **私有记忆空间：** 每个 Agent 都有自己的 `agents/<AgentName>/memory/` 文件夹。
* **公共知识库：** 项目根目录下的 `docs/` 和 `MEMORY.md` 是全局可见的。
* **Leader 的特权：** 只有 Leader 有权向全局 `MEMORY.md` 写入。Worker 如果有重大发现，必须通过“发消息”告诉 Leader，由 Leader 审核后写入长期记忆。

---

### 🛠️ 关键代码：`MemoryHub` 逻辑

在 C# 中，我们可以通过文件监听器（FileSystemWatcher）实现 OpenClaw 风格的实时索引：

```csharp
public class MemoryHub {
    // 监听 Markdown 文件的变动
    private FileSystemWatcher _watcher;

    public void StartWatching(string workspacePath) {
        _watcher = new FileSystemWatcher(workspacePath, "*.md");
        _watcher.Changed += async (s, e) => {
            // 1. 读取变动内容
            // 2. 调用 Embedding 接口
            // 3. 更新 SQLite 向量表
            await ReIndexFile(e.FullPath);
        };
    }

    // Agent 调用此工具来搜索记忆
    public async Task<List<MemorySnippet>> Search(string query) {
        // 执行混合搜索：Vector + FTS5
        return await _sqliteDb.HybridQuery(query);
    }
}

```

### 🍮 总结：

这种基于 Markdown 的记忆系统不仅轻量，而且赋予了用户极大的**掌控力**。你随时可以打开文件看看 AI 到底记住了你什么。

**我们要不要开始设计 `MEMORY.md` 的自动格式化模板？**
比如它应该包含 `# 架构决策`、`# 编码风格`、`# 避雷指南` 等章节，这样 Leader 写入时会更有组织性。

**这套记忆系统一旦跑通，你的 Agent 就不再是“阅后即焚”的工具，而是一个会随着项目增长而“共同成长”的伙伴。**