using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services.Eventing;

/// <summary>PA-07A S5 idempotent SQLite schema bootstrap for subscriptions and their checkpoint/deliveries.</summary>
public static class EventSubscriptionSchemaBootstrapper
{
    private static readonly string[] Ddl =
    [
        """
        CREATE TABLE IF NOT EXISTS event_subscriptions (
            subscription_id   TEXT    NOT NULL PRIMARY KEY,
            plugin_id         TEXT    NOT NULL,
            generation        INTEGER NOT NULL,
            workspace_id      TEXT    NOT NULL,
            kinds             TEXT    NOT NULL,
            cursor_sequence   INTEGER NOT NULL DEFAULT 0,
            scanned_through_sequence INTEGER NOT NULL DEFAULT 0,
            revision          INTEGER NOT NULL DEFAULT 0,
            created_at        TEXT    NOT NULL,
            cancelled         INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS event_deliveries (
            delivery_id       TEXT    NOT NULL PRIMARY KEY,
            subscription_id   TEXT    NOT NULL,
            event_id          TEXT    NOT NULL,
            event_sequence    INTEGER NOT NULL,
            envelope_json     TEXT    NOT NULL,
            attempt           INTEGER NOT NULL DEFAULT 0,
            claimed_at        TEXT    NOT NULL,
            failure_code      TEXT,
            dead_lettered     INTEGER NOT NULL DEFAULT 0,
            lease_owner_id    TEXT,
            lease_until_unix_ms INTEGER
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS event_outbox (
            event_id          TEXT    NOT NULL PRIMARY KEY,
            stream_id         TEXT    NOT NULL,
            sequence          INTEGER NOT NULL,
            kind              TEXT    NOT NULL,
            schema_version    INTEGER NOT NULL,
            occurred_at       TEXT    NOT NULL,
            occurred_at_ms    INTEGER NOT NULL,
            workspace_id      TEXT    NOT NULL,
            payload_ref       TEXT    NOT NULL,
            payload_hash      TEXT    NOT NULL,
            producer          TEXT    NOT NULL,
            causation_id      TEXT,
            correlation_id    TEXT,
            business_ref      TEXT,
            published         INTEGER NOT NULL DEFAULT 0
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_event_outbox_stream_sequence ON event_outbox(stream_id, sequence);",
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_event_deliveries_sub_event ON event_deliveries(subscription_id, event_id);",
        "CREATE INDEX IF NOT EXISTS IX_event_deliveries_sub ON event_deliveries(subscription_id, event_sequence);",
        "CREATE INDEX IF NOT EXISTS IX_event_subscriptions_ws ON event_subscriptions(workspace_id);",
    ];

    public static async Task EnsureCreatedAsync(PlatformDbContext db, ILogger? logger, CancellationToken ct = default)
    {
        foreach (var ddl in Ddl)
            await db.Database.ExecuteSqlRawAsync(ddl, ct).ConfigureAwait(false);
        logger?.LogInformation("PA-07A subscription schema bootstrap completed ({Count} statements).", Ddl.Length);
    }
}
