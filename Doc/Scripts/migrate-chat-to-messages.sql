-- 回填旧 platform.ChatMessages 到新 memory.Messages
-- 注意：该脚本由 migrate-chat-to-messages.ps1 调用，并替换 __MEMORY_DB_PATH__。

PRAGMA foreign_keys = ON;
BEGIN TRANSACTION;

ATTACH DATABASE '__MEMORY_DB_PATH__' AS memorydb;

-- 1) 创建/更新 Session（若已存在则保持不覆盖，后续统一更新统计值）
INSERT OR IGNORE INTO memorydb.Sessions
(
    SessionId,
    WorkspaceId,
    AgentId,
    Title,
    Mode,
    Status,
    CreatedAt,
    LastActivityAt,
    MessageCount,
    TokenTotal
)
SELECT
    cm.SessionId,
    '' AS WorkspaceId,
    '' AS AgentId,
    NULL AS Title,
    'chat' AS Mode,
    'active' AS Status,
    MIN(CASE WHEN cm.CreatedAt < 1000000000000 THEN cm.CreatedAt * 1000 ELSE cm.CreatedAt END) AS CreatedAt,
    MAX(CASE WHEN cm.CreatedAt < 1000000000000 THEN cm.CreatedAt * 1000 ELSE cm.CreatedAt END) AS LastActivityAt,
    COUNT(*) AS MessageCount,
    0 AS TokenTotal
FROM main.ChatMessages cm
WHERE COALESCE(cm.SessionId, '') <> ''
GROUP BY cm.SessionId;

-- 2) 回填消息（MessageId 随机 32hex；Role 映射 agent -> assistant）
INSERT OR IGNORE INTO memorydb.Messages
(
    MessageId,
    SessionId,
    ParentId,
    BranchType,
    Sequence,
    Role,
    ContentType,
    Content,
    ThinkingJson,
    UsageJson,
    CreatedAt
)
SELECT
    lower(hex(randomblob(16))) AS MessageId,
    cm.SessionId,
    NULL AS ParentId,
    'MAIN' AS BranchType,
    row_number() OVER (
        PARTITION BY cm.SessionId
        ORDER BY cm.CreatedAt, cm.Id
    ) AS Sequence,
    CASE
        WHEN lower(cm.Role) = 'agent' THEN 'assistant'
        WHEN lower(cm.Role) = 'assistant' THEN 'assistant'
        WHEN lower(cm.Role) = 'system' THEN 'system'
        WHEN lower(cm.Role) = 'tool' THEN 'tool'
        ELSE 'user'
    END AS Role,
    'text' AS ContentType,
    cm.Content,
    cm.ThinkingJson,
    cm.UsageJson,
    CASE WHEN cm.CreatedAt < 1000000000000 THEN cm.CreatedAt * 1000 ELSE cm.CreatedAt END AS CreatedAt
FROM main.ChatMessages cm
WHERE COALESCE(cm.SessionId, '') <> '';

-- 3) 二次校准 Session 统计，确保与 Messages 一致
UPDATE memorydb.Sessions
SET
    LastActivityAt = COALESCE(
        (
            SELECT MAX(m.CreatedAt)
            FROM memorydb.Messages m
            WHERE m.SessionId = memorydb.Sessions.SessionId
        ),
        LastActivityAt
    ),
    MessageCount = COALESCE(
        (
            SELECT COUNT(*)
            FROM memorydb.Messages m
            WHERE m.SessionId = memorydb.Sessions.SessionId
        ),
        MessageCount
    )
WHERE SessionId IN (
    SELECT DISTINCT cm.SessionId
    FROM main.ChatMessages cm
    WHERE COALESCE(cm.SessionId, '') <> ''
);

DETACH DATABASE memorydb;
COMMIT;
