// ═══════════════════════════════════════════════════════════════
// Memory Library Admin Page Types (ADR-030)
// ═══════════════════════════════════════════════════════════════

export interface MemoryLibraryOverviewDto {
  workspaceId: string;
  libraryCount: number;
  bookCount: number;
  treeNodeCount: number;
  agentId?: string;
}

export interface MemoryLibraryTreeNodeDto {
  id: string;
  parentId: string | null;
  type: string;
  title: string;
  summary?: string;
  status: string;
  bookId?: string;
  children: MemoryLibraryTreeNodeDto[];
}

export interface MemoryChapterSectionDto {
  chapterId: string;
  bookId: string;
  title: string;
  content: string;
  contentType: string;
  importance: number;
  createdAt: number;
  updatedAt: number;
}

export interface MemoryBookPageDto {
  workspaceId: string;
  libraryId: string;
  bookId: string;
  title: string;
  summary?: string;
  status: string;
  chapters: MemoryChapterSectionDto[];
}

export interface MemorySearchResultDto {
  bookId: string;
  chapterId: string;
  bookTitle: string;
  snippet: string;
  score: number;
}

export interface LibraryRecord {
  libraryId: string;
  workspaceId: string;
  agentId?: string;
  name: string;
  description?: string;
  createdAt: number;
  updatedAt: number;
}
