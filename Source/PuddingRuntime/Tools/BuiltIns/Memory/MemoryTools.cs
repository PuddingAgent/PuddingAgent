// ═══════════════════════════════════════════════════════════════
// MemoryTools — 拆分索引（已废弃此文件）
// ═══════════════════════════════════════════════════════════════
//
// 原 MemoryTools.cs 已拆分为以下独立文件：
//
//   SaveMemoryTool.cs    → save_memory 工具（写入/更新记忆）
//   GrepMemoryTool.cs    → grep_memory 工具（全文检索记忆）
//   ManageMemoryTool.cs  → manage_memory 工具（编排器，委托给 Handlers）
//
//   Handlers/
//     BookHandler.cs       → Book CRUD 操作
//     ChapterHandler.cs    → Chapter CRUD 操作
//     DedupHandler.cs      → 去重报告 + 合并
//     GraphHandler.cs      → 知识图谱关联
//     ReferenceHandler.cs  → Pointer 引用
//
//   MemoryToolArgs.cs     → 3 个 Args record 集中管理
//   MemoryToolHelper.cs   → 共享辅助方法
//
// 此文件保留为空文件，避免编译器引用断裂。
// 如需新增工具类，请创建独立 .cs 文件。
// ═══════════════════════════════════════════════════════════════
