PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS Sessions (
    SessionId       TEXT PRIMARY KEY,
    ParentSessionId TEXT,
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
    ParentSessionId TEXT,
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

CREATE TABLE IF NOT EXISTS LegacyMemoryFacts (
    FactId           TEXT PRIMARY KEY,
    WorkspaceId      TEXT NOT NULL,
    Statement        TEXT NOT NULL,
    Confidence       REAL NOT NULL DEFAULT 0.8,
    Category         TEXT NOT NULL DEFAULT 'general',
    SourceSessionId  TEXT,
    SourceMessageId  TEXT,
    Tags             TEXT,
    Embedding        BLOB,
    AgentInstanceId  TEXT,
    Status           TEXT NOT NULL DEFAULT 'active',
    MergedInto       TEXT,
    AccessCount      INTEGER NOT NULL DEFAULT 0,
    CreatedAt        INTEGER NOT NULL,
    UpdatedAt        INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_LegacyMemoryFacts_Workspace_Category
    ON LegacyMemoryFacts(WorkspaceId, Category);
CREATE INDEX IF NOT EXISTS IX_LegacyMemoryFacts_SourceSession
    ON LegacyMemoryFacts(SourceSessionId);

CREATE TABLE IF NOT EXISTS MemoryPreferences (
    PreferenceId     TEXT PRIMARY KEY,
    WorkspaceId      TEXT NOT NULL,
    Category         TEXT NOT NULL,
    Key              TEXT NOT NULL,
    Value            TEXT NOT NULL,
    SourceSessionId  TEXT,
    SourceMessageId  TEXT,
    AgentInstanceId  TEXT,
    CreatedAt        INTEGER NOT NULL,
    UpdatedAt        INTEGER NOT NULL,
    UNIQUE(WorkspaceId, Category, Key)
);

CREATE INDEX IF NOT EXISTS IX_MemoryPreferences_Workspace
    ON MemoryPreferences(WorkspaceId, Category);

-- ADR-042: Agent 记忆隔离
ALTER TABLE MemoryPreferences ADD COLUMN AgentInstanceId TEXT;

-- ADR-042: Agent 记忆隔离 (LegacyMemoryFacts)
ALTER TABLE LegacyMemoryFacts ADD COLUMN AgentInstanceId TEXT;

CREATE TABLE IF NOT EXISTS SubconsciousJobLogs (
    JobId             TEXT PRIMARY KEY,
    SessionId         TEXT NOT NULL,
    Status            TEXT NOT NULL DEFAULT 'pending',
    FactsExtracted    INTEGER NOT NULL DEFAULT 0,
    FactsMerged       INTEGER NOT NULL DEFAULT 0,
    FactsDiscarded    INTEGER NOT NULL DEFAULT 0,
    ChaptersCreated   INTEGER NOT NULL DEFAULT 0,
    LlmTokensUsed     INTEGER NOT NULL DEFAULT 0,
    LlmModelId        TEXT,
    ElapsedMs         INTEGER NOT NULL DEFAULT 0,
    ErrorMessage      TEXT,
    StartedAt         INTEGER,
    CompletedAt       INTEGER,
    CreatedAt         INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_SubconsciousJobLogs_Session
    ON SubconsciousJobLogs(SessionId);

CREATE TABLE IF NOT EXISTS SubconsciousJobs (
    JobId               TEXT PRIMARY KEY,
    JobType             TEXT NOT NULL,
    IdempotencyKey      TEXT NOT NULL UNIQUE,
    Status              TEXT NOT NULL DEFAULT 'pending',
    WorkspaceId         TEXT NOT NULL,
    SessionId           TEXT NOT NULL,
    AgentId             TEXT NOT NULL,
    AgentTemplateId     TEXT NOT NULL,
    SourceHookName      TEXT,
    SourceEventId       TEXT,
    SourceCompactionId  TEXT,
    PayloadJson         TEXT NOT NULL DEFAULT '{}',
    ResultJson          TEXT,
    RetryCount          INTEGER NOT NULL DEFAULT 0,
    LeaseOwner          TEXT,
    LeaseUntil          INTEGER,
    AvailableAt         INTEGER NOT NULL,
    StartedAt           INTEGER,
    CompletedAt         INTEGER,
    ErrorMessage        TEXT,
    CreatedAt           INTEGER NOT NULL,
    UpdatedAt           INTEGER NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_SubconsciousJobs_IdempotencyKey
    ON SubconsciousJobs(IdempotencyKey);

CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_Status_AvailableAt
    ON SubconsciousJobs(Status, AvailableAt);

CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_LeaseUntil
    ON SubconsciousJobs(LeaseUntil);

CREATE INDEX IF NOT EXISTS IX_SubconsciousJobs_Workspace_Session
    ON SubconsciousJobs(WorkspaceId, SessionId);
