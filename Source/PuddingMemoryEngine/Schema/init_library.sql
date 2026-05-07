PRAGMA foreign_keys=ON;

-- 2.1 Libraries 表
CREATE TABLE IF NOT EXISTS Libraries (
    LibraryId     TEXT PRIMARY KEY,
    WorkspaceId   TEXT NOT NULL,
    Name          TEXT NOT NULL,
    Description   TEXT,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Libraries_Workspace
    ON Libraries(WorkspaceId);

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
    SourceSessionId TEXT,
    WordCount     INTEGER NOT NULL DEFAULT 0,
    CreatedAt     INTEGER NOT NULL,
    UpdatedAt     INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_Chapters_Book_Order
    ON Chapters(BookId, ChapterOrder);

-- Phase 4: 嵌入向量列（兼容已有数据库）
ALTER TABLE Books ADD COLUMN Embedding BLOB;
ALTER TABLE Chapters ADD COLUMN Embedding BLOB;

-- 2.5 Pointers 表
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
    Title, Content, ChapterId UNINDEXED,
    content=Chapters, content_rowid=rowid
);

CREATE TRIGGER IF NOT EXISTS trg_Chapters_ai AFTER INSERT ON Chapters BEGIN
    INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId)
    VALUES (new.rowid, new.Title, new.Content, new.ChapterId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Chapters_ad AFTER DELETE ON Chapters BEGIN
    INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId)
    VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId);
END;

CREATE TRIGGER IF NOT EXISTS trg_Chapters_au AFTER UPDATE ON Chapters BEGIN
    INSERT INTO Chapters_fts(Chapters_fts, rowid, Title, Content, ChapterId)
    VALUES ('delete', old.rowid, old.Title, old.Content, old.ChapterId);
    INSERT INTO Chapters_fts(rowid, Title, Content, ChapterId)
    VALUES (new.rowid, new.Title, new.Content, new.ChapterId);
END;
