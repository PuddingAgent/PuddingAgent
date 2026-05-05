PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS Sessions (
    SessionId       TEXT PRIMARY KEY,
    WorkspaceId     TEXT NOT NULL,
    AgentId         TEXT NOT NULL,
    Title           TEXT,
    Mode            TEXT NOT NULL DEFAULT 'chat',
    Status          TEXT NOT NULL DEFAULT 'active',
    Tags            TEXT,
    CreatedAt       INTEGER NOT NULL,
    LastActivityAt  INTEGER NOT NULL,
    MessageCount    INTEGER NOT NULL DEFAULT 0,
    TokenTotal      INTEGER NOT NULL DEFAULT 0,
    Metadata        TEXT
);

CREATE INDEX IF NOT EXISTS IX_Sessions_Workspace_LastActivity
    ON Sessions(WorkspaceId, LastActivityAt DESC);
CREATE INDEX IF NOT EXISTS IX_Sessions_Status
    ON Sessions(Status, LastActivityAt DESC);

CREATE TABLE IF NOT EXISTS Messages (
    MessageId       TEXT PRIMARY KEY,
    SessionId       TEXT NOT NULL REFERENCES Sessions(SessionId) ON DELETE CASCADE,
    ParentId        TEXT REFERENCES Messages(MessageId),
    BranchType      TEXT NOT NULL DEFAULT 'MAIN',
    Sequence        INTEGER NOT NULL,
    Role            TEXT NOT NULL,
    ContentType     TEXT NOT NULL DEFAULT 'text',
    Content         TEXT,
    ToolCallsJson   TEXT,
    ToolResultJson  TEXT,
    ThinkingJson    TEXT,
    AttachmentsJson TEXT,
    UsageJson       TEXT,
    ModelId         TEXT,
    AgentId         TEXT,
    Source          TEXT,
    CompactedBy     TEXT REFERENCES Messages(MessageId),
    CreatedAt       INTEGER NOT NULL,
    Metadata        TEXT
);

CREATE INDEX IF NOT EXISTS IX_Messages_Session_Seq
    ON Messages(SessionId, Sequence);
CREATE INDEX IF NOT EXISTS IX_Messages_Parent
    ON Messages(ParentId);
CREATE INDEX IF NOT EXISTS IX_Messages_Session_Branch_Seq
    ON Messages(SessionId, BranchType, Sequence);
CREATE INDEX IF NOT EXISTS IX_Messages_CompactedBy
    ON Messages(CompactedBy)
    WHERE CompactedBy IS NOT NULL;

CREATE TABLE IF NOT EXISTS Memories (
    MemoryId        TEXT PRIMARY KEY,
    Scope           TEXT NOT NULL DEFAULT 'session',
    SessionId       TEXT,
    WorkspaceId     TEXT,
    AgentId         TEXT,
    Tag             TEXT NOT NULL DEFAULT 'general',
    Content         TEXT NOT NULL,
    Importance      REAL NOT NULL DEFAULT 0.5,
    Confidence      REAL NOT NULL DEFAULT 0.8,
    SourceMessageId TEXT,
    AccessCount     INTEGER NOT NULL DEFAULT 0,
    LastAccessedAt  INTEGER,
    CreatedAt       INTEGER NOT NULL,
    ExpiresAt       INTEGER,
    SupersededBy    TEXT,
    Metadata        TEXT
);

CREATE INDEX IF NOT EXISTS IX_Memories_Scope_Workspace_Agent_CreatedAt
    ON Memories(Scope, WorkspaceId, AgentId, CreatedAt DESC);
CREATE INDEX IF NOT EXISTS IX_Memories_Workspace_Tag
    ON Memories(WorkspaceId, Tag);
CREATE INDEX IF NOT EXISTS IX_Memories_Workspace_Active
    ON Memories(WorkspaceId)
    WHERE SupersededBy IS NULL;

CREATE TABLE IF NOT EXISTS AgentMemories (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    MemoryId        TEXT NOT NULL,
    AgentInstanceId TEXT NOT NULL,
    MemoryType      TEXT NOT NULL DEFAULT 'long_term',
    Content         TEXT NOT NULL,
    DateKey         TEXT,
    ImportanceScore INTEGER NOT NULL DEFAULT 50,
    AccessedAt      INTEGER NOT NULL,
    CreatedAt       INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_AgentMemories_MemoryId
    ON AgentMemories(MemoryId);

CREATE INDEX IF NOT EXISTS IX_AgentMemories_AgentInstance_MemoryType_CreatedAt
    ON AgentMemories(AgentInstanceId, MemoryType, CreatedAt DESC);

CREATE VIRTUAL TABLE IF NOT EXISTS Messages_fts USING fts5(
    Content,
    content='Messages',
    content_rowid='rowid',
    tokenize='trigram'
);

CREATE TRIGGER IF NOT EXISTS trg_Messages_ai AFTER INSERT ON Messages BEGIN
  INSERT INTO Messages_fts(rowid, Content) VALUES (new.rowid, new.Content);
END;

CREATE TRIGGER IF NOT EXISTS trg_Messages_ad AFTER DELETE ON Messages BEGIN
  INSERT INTO Messages_fts(Messages_fts, rowid, Content) VALUES('delete', old.rowid, old.Content);
END;

CREATE TRIGGER IF NOT EXISTS trg_Messages_au AFTER UPDATE ON Messages BEGIN
  INSERT INTO Messages_fts(Messages_fts, rowid, Content) VALUES('delete', old.rowid, old.Content);
  INSERT INTO Messages_fts(rowid, Content) VALUES (new.rowid, new.Content);
END;
