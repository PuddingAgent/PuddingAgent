PRAGMA foreign_keys=ON;

-- 2.1 Libraries 表
CREATE TABLE IF NOT EXISTS Libraries (
    LibraryId     TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NULL,
    Name          TEXT NOT NULL,
    Description   TEXT,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Libraries_Workspace
    ON Libraries(WorkspaceId);
CREATE INDEX IF NOT EXISTS IX_Libraries_Workspace_Agent
    ON Libraries(WorkspaceId, AgentId);

-- ADR-030 revision: Library is workspace + agent scoped. NULL AgentId means legacy workspace-only library.

-- 2.2 Books 表
CREATE TABLE IF NOT EXISTS Books (
    BookId        TEXT PRIMARY KEY,
    LibraryId     TEXT NOT NULL REFERENCES Libraries(LibraryId),
    Title         TEXT NOT NULL,
    Summary       TEXT NOT NULL DEFAULT '',
    Status        TEXT NOT NULL DEFAULT 'active',
    Version       INTEGER NOT NULL DEFAULT 1,
    AccessCount   INTEGER NOT NULL DEFAULT 0,
    LastAccessedAt INTEGER,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Books_Library_UpdatedAt
    ON Books(LibraryId, UpdatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_Books_Status
    ON Books(Status, UpdatedAt DESC);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Books_Library_Title_Active
    ON Books(LibraryId, Title)
    WHERE Status = 'active';

-- 2.3 BookIndexes 表
CREATE TABLE IF NOT EXISTS BookIndexes (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    BookId        TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    TagPath       TEXT NOT NULL,
    Weight        INTEGER NOT NULL DEFAULT 1,
    CreatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_BookIndexes_TagPath
    ON BookIndexes(TagPath);
CREATE INDEX IF NOT EXISTS IX_BookIndexes_BookId
    ON BookIndexes(BookId);

-- 2.4 Chapters 表
CREATE TABLE IF NOT EXISTS Chapters (
    ChapterId     TEXT PRIMARY KEY,
    BookId        TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    Title         TEXT NOT NULL,
    ChapterOrder  INTEGER NOT NULL DEFAULT 0,
    Content       TEXT NOT NULL,
    ContentType   TEXT NOT NULL DEFAULT 'markdown',
    Importance    REAL NOT NULL DEFAULT 0.5,
    Status        TEXT NOT NULL DEFAULT 'active',
    SupersededByChapterId TEXT,
    SupersededAt  INTEGER,
    SourceSessionId TEXT,
    WordCount     INTEGER NOT NULL DEFAULT 0,
    AgentInstanceId TEXT,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Chapters_Book_Order
    ON Chapters(BookId, ChapterOrder);

CREATE INDEX IF NOT EXISTS IX_Chapters_Book_Status_Order
    ON Chapters(BookId, Status, ChapterOrder);

CREATE INDEX IF NOT EXISTS IX_Chapters_SupersededBy
    ON Chapters(SupersededByChapterId);

-- Phase 4: 嵌入向量列（兼容已有数据库）
ALTER TABLE Books ADD COLUMN Embedding BLOB;
ALTER TABLE Chapters ADD COLUMN Embedding BLOB;

-- ADR-028 Phase 2: Chapter 来源引用列
ALTER TABLE Chapters ADD COLUMN SourceReference TEXT;
ALTER TABLE Chapters ADD COLUMN ReferenceType TEXT;

-- R2: Chapter 取代语义
ALTER TABLE Chapters ADD COLUMN Status TEXT NOT NULL DEFAULT 'active';
ALTER TABLE Chapters ADD COLUMN SupersededByChapterId TEXT;
ALTER TABLE Chapters ADD COLUMN SupersededAt INTEGER;

-- ADR-042: Agent 记忆隔离
ALTER TABLE Chapters ADD COLUMN AgentInstanceId TEXT;

-- jieba 分词列（供 FTS5 索引使用）
ALTER TABLE Chapters ADD COLUMN TitleTokens TEXT NOT NULL DEFAULT '';
ALTER TABLE Chapters ADD COLUMN ContentTokens TEXT NOT NULL DEFAULT '';

-- 2.5 Pointers 表 (ADR-029: 定义在前，ALTER 紧跟其后)
CREATE TABLE IF NOT EXISTS Pointers (
    PointerId     TEXT PRIMARY KEY,
    ChapterId     TEXT NOT NULL REFERENCES Chapters(ChapterId) ON DELETE CASCADE,
    TargetType    TEXT NOT NULL,
    TargetId      TEXT NOT NULL,
    TargetLabel   TEXT,
    Description   TEXT,
    Relevance     INTEGER NOT NULL DEFAULT 5,
    CreatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Pointers_ChapterId
    ON Pointers(ChapterId);
CREATE INDEX IF NOT EXISTS IX_Pointers_TargetType_TargetId
    ON Pointers(TargetType, TargetId);

-- ADR-028 Phase 3: Pointer 泛化列
ALTER TABLE Pointers ADD COLUMN WorkspaceId TEXT;
ALTER TABLE Pointers ADD COLUMN SourceType TEXT;
ALTER TABLE Pointers ADD COLUMN SourceId TEXT;
CREATE INDEX IF NOT EXISTS IX_Pointers_WorkspaceId ON Pointers(WorkspaceId);
CREATE INDEX IF NOT EXISTS IX_Pointers_SourceType_SourceId ON Pointers(SourceType, SourceId);
CREATE TABLE IF NOT EXISTS SourceReferences (
    SourceReferenceId TEXT PRIMARY KEY,
    WorkspaceId       TEXT NOT NULL,
    OwnerType         TEXT NOT NULL,
    OwnerId           TEXT NOT NULL,
    TargetType        TEXT NOT NULL,
    TargetId          TEXT NOT NULL,
    TargetRange       TEXT,
    Label             TEXT,
    Description       TEXT,
    CreatedAt         INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SourceReferences_Owner ON SourceReferences(OwnerType, OwnerId);
CREATE INDEX IF NOT EXISTS IX_SourceReferences_Workspace ON SourceReferences(WorkspaceId, TargetType, TargetId);

-- ADR-028 Phase 3: MemoryTreeNodes 表
CREATE TABLE IF NOT EXISTS MemoryTreeNodes (
    NodeId       TEXT PRIMARY KEY,
    WorkspaceId  TEXT NOT NULL,
    LibraryId    TEXT NOT NULL,
    ParentNodeId TEXT,
    Path         TEXT NOT NULL,
    Name         TEXT NOT NULL,
    Summary      TEXT,
    NodeType     TEXT NOT NULL DEFAULT 'category',
    Status       TEXT NOT NULL DEFAULT 'active',
    SortOrder    INTEGER NOT NULL DEFAULT 0,
    CreatedAt    INTEGER NOT NULL,
    UpdatedAt    INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_WS_Lib ON MemoryTreeNodes(WorkspaceId, LibraryId);
CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_Parent ON MemoryTreeNodes(ParentNodeId);
CREATE INDEX IF NOT EXISTS IX_MemoryTreeNodes_Path ON MemoryTreeNodes(Path);

-- ADR-028 Phase 3: BookTreeMounts 表
CREATE TABLE IF NOT EXISTS BookTreeMounts (
    Id        TEXT PRIMARY KEY,
    BookId    TEXT NOT NULL,
    NodeId    TEXT NOT NULL,
    Weight    INTEGER NOT NULL DEFAULT 1,
    CreatedAt INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_BookTreeMounts_BookId ON BookTreeMounts(BookId);
CREATE INDEX IF NOT EXISTS IX_BookTreeMounts_NodeId ON BookTreeMounts(NodeId);

-- 2.6 Branches 表
CREATE TABLE IF NOT EXISTS Branches (
    BranchId      TEXT PRIMARY KEY,
    BookId        TEXT NOT NULL REFERENCES Books(BookId) ON DELETE CASCADE,
    BranchName    TEXT NOT NULL,
    Description   TEXT,
    CreatedBy     TEXT,
    MergedInto    TEXT REFERENCES Branches(BranchId),
    IsDefault     INTEGER NOT NULL DEFAULT 0,
    CreatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Branches_BookId
    ON Branches(BookId);

-- 3.0 Fact-first memory foundation
CREATE TABLE IF NOT EXISTS MemorySpaces (
    MemorySpaceId TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    Name          TEXT NOT NULL,
    Description   TEXT,
    Status        TEXT NOT NULL DEFAULT 'active',
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemorySpaces_Workspace_Agent_Status
    ON MemorySpaces(WorkspaceId, AgentId, Status);

CREATE TABLE IF NOT EXISTS MemoryFacts (
    FactId                TEXT PRIMARY KEY,
    WorkspaceId           TEXT NOT NULL,
    AgentId               TEXT NOT NULL,
    MemorySpaceId         TEXT NOT NULL,
    Statement             TEXT NOT NULL,
    StructuredPayloadJson TEXT,
    FactType              TEXT NOT NULL,
    Confidence            REAL NOT NULL,
    Status                TEXT NOT NULL DEFAULT 'pending',
    SupersededByFactId    TEXT,
    CreatedByType         TEXT NOT NULL,
    CreatedById           TEXT,
    CreatedAt             INTEGER NOT NULL,
    UpdatedAt             INTEGER NOT NULL,
    AcceptedAt            INTEGER,
    RejectedAt            INTEGER,
    ArchivedAt            INTEGER
);
CREATE INDEX IF NOT EXISTS IX_MemoryFacts_Space_Status
    ON MemoryFacts(WorkspaceId, AgentId, MemorySpaceId, Status);
CREATE INDEX IF NOT EXISTS IX_MemoryFacts_Type_Status
    ON MemoryFacts(WorkspaceId, AgentId, FactType, Status);
CREATE INDEX IF NOT EXISTS IX_MemoryFacts_SupersededBy
    ON MemoryFacts(WorkspaceId, AgentId, SupersededByFactId);

CREATE TABLE IF NOT EXISTS MemoryFactEvidence (
    EvidenceId    TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    SourceType    TEXT NOT NULL,
    SourceId      TEXT NOT NULL,
    SourceRange   TEXT,
    QuoteSummary  TEXT,
    EvidenceHash  TEXT,
    Confidence    REAL NOT NULL,
    CreatedAt     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Fact
    ON MemoryFactEvidence(WorkspaceId, AgentId, FactId);
CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Source
    ON MemoryFactEvidence(WorkspaceId, AgentId, SourceType, SourceId);
CREATE INDEX IF NOT EXISTS IX_MemoryFactEvidence_Hash
    ON MemoryFactEvidence(WorkspaceId, AgentId, EvidenceHash);

CREATE TABLE IF NOT EXISTS MemoryFactContexts (
    ContextId     TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    ContextJson   TEXT NOT NULL,
    ContextHash   TEXT NOT NULL,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactContexts_Fact
    ON MemoryFactContexts(WorkspaceId, AgentId, FactId);
CREATE INDEX IF NOT EXISTS IX_MemoryFactContexts_Hash
    ON MemoryFactContexts(WorkspaceId, AgentId, ContextHash);

CREATE TABLE IF NOT EXISTS MemoryFactFreshness (
    FreshnessId      TEXT PRIMARY KEY,
    WorkspaceId      TEXT NOT NULL,
    AgentId          TEXT NOT NULL,
    MemorySpaceId    TEXT NOT NULL,
    FactId           TEXT NOT NULL,
    ObservedAt       INTEGER,
    LastVerifiedAt   INTEGER,
    ValidFrom        INTEGER,
    ValidTo          INTEGER,
    HalfLifeSeconds  INTEGER,
    DecayKind        TEXT NOT NULL DEFAULT 'stable',
    StaleThreshold   REAL NOT NULL DEFAULT 0.5,
    ExpiredThreshold REAL NOT NULL DEFAULT 0.1,
    RefreshHint      TEXT,
    FreshnessReason  TEXT,
    CreatedAt        INTEGER NOT NULL,
    UpdatedAt        INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactFreshness_Fact
    ON MemoryFactFreshness(WorkspaceId, AgentId, FactId);

CREATE TABLE IF NOT EXISTS MemoryFactEntityMentions (
    MentionId      TEXT PRIMARY KEY,
    WorkspaceId    TEXT NOT NULL,
    AgentId        TEXT NOT NULL,
    MemorySpaceId  TEXT NOT NULL,
    FactId         TEXT NOT NULL,
    EntityKey      TEXT NOT NULL,
    EntityType     TEXT NOT NULL,
    DisplayName    TEXT NOT NULL,
    Role           TEXT NOT NULL,
    AliasesJson    TEXT,
    PropertiesJson TEXT,
    Confidence     REAL NOT NULL,
    CreatedAt      INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactEntityMentions_Entity
    ON MemoryFactEntityMentions(WorkspaceId, AgentId, EntityKey);
CREATE INDEX IF NOT EXISTS IX_MemoryFactEntityMentions_Fact
    ON MemoryFactEntityMentions(WorkspaceId, AgentId, FactId);

CREATE TABLE IF NOT EXISTS MemoryFactAssociations (
    AssociationId   TEXT PRIMARY KEY,
    WorkspaceId     TEXT NOT NULL,
    AgentId         TEXT NOT NULL,
    MemorySpaceId   TEXT NOT NULL,
    FactId          TEXT NOT NULL,
    SourceKind      TEXT NOT NULL,
    SourceKey       TEXT NOT NULL,
    TargetKind      TEXT NOT NULL,
    TargetKey       TEXT NOT NULL,
    AssociationType TEXT NOT NULL,
    Weight          REAL NOT NULL,
    Confidence      REAL NOT NULL,
    ContextJson     TEXT,
    EvidenceIdsJson TEXT,
    ObservedAt      INTEGER,
    HalfLifeSeconds INTEGER,
    Reason          TEXT,
    CreatedAt       INTEGER NOT NULL,
    UpdatedAt       INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Source
    ON MemoryFactAssociations(WorkspaceId, AgentId, SourceKind, SourceKey);
CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Target
    ON MemoryFactAssociations(WorkspaceId, AgentId, TargetKind, TargetKey);
CREATE INDEX IF NOT EXISTS IX_MemoryFactAssociations_Fact
    ON MemoryFactAssociations(WorkspaceId, AgentId, FactId);

CREATE TABLE IF NOT EXISTS MemoryFactRevisions (
    RevisionId    TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    AgentId       TEXT NOT NULL,
    MemorySpaceId TEXT NOT NULL,
    FactId        TEXT NOT NULL,
    RevisionType  TEXT NOT NULL,
    BeforeJson    TEXT,
    AfterJson     TEXT,
    ActorType     TEXT NOT NULL,
    ActorId       TEXT,
    Reason        TEXT,
    CreatedAt     INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MemoryFactRevisions_Fact_CreatedAt
    ON MemoryFactRevisions(WorkspaceId, AgentId, FactId, CreatedAt);

-- 2.7 全文索引 (FTS5)
CREATE VIRTUAL TABLE IF NOT EXISTS Books_fts USING fts5(
    Title, Summary, BookId UNINDEXED,
    content=Books, content_rowid=rowid
);

CREATE TRIGGER IF NOT EXISTS trg_Books_ai AFTER INSERT ON Books BEGIN
    INSERT INTO Books_fts(rowid, Title, Summary, BookId)
    VALUES (new.rowid, new.Title, new.Summary, new.BookId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Books_ad AFTER DELETE ON Books BEGIN
    INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId)
    VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Books_au AFTER UPDATE ON Books BEGIN
    INSERT INTO Books_fts(Books_fts, rowid, Title, Summary, BookId)
    VALUES ('delete', old.rowid, old.Title, old.Summary, old.BookId);
    INSERT INTO Books_fts(rowid, Title, Summary, BookId)
    VALUES (new.rowid, new.Title, new.Summary, new.BookId);
END;

CREATE VIRTUAL TABLE IF NOT EXISTS Chapters_fts USING fts5(
    TitleTokens, ContentTokens, ChapterId UNINDEXED,
    content=Chapters, content_rowid=rowid
);

CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN
    INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId)
    VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN
    INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId)
    VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN
    INSERT INTO Chapters_fts(Chapters_fts, rowid, TitleTokens, ContentTokens, ChapterId)
    VALUES ('delete', old.rowid, old.TitleTokens, old.ContentTokens, old.ChapterId);
    INSERT INTO Chapters_fts(rowid, TitleTokens, ContentTokens, ChapterId)
    VALUES (new.rowid, new.TitleTokens, new.ContentTokens, new.ChapterId);
END;
