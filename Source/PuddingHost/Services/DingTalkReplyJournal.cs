using Microsoft.Data.Sqlite;
using PuddingDingTalk;

namespace PuddingAgent.Services;

public sealed class DingTalkReplyJournal : IGroupReplyJournal
{
    private readonly string connectionString;

    public DingTalkReplyJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "receipts.db") }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS receipts (
                corp TEXT, room TEXT, id TEXT, sender TEXT, pudding_group TEXT, sent_at TEXT,
                received_at TEXT, status TEXT, PRIMARY KEY(corp,room,id))
            """;
        cmd.ExecuteNonQuery();
    }

    public bool TryClaim(GroupSource s, string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO receipts VALUES($corp,$room,$id,$user,$grp,$sent,$received,'unknown') ON CONFLICT(corp,room,id) DO NOTHING";
        AddIdentity(cmd, s, id);
        cmd.Parameters.AddWithValue("$user", s.UserId);
        cmd.Parameters.AddWithValue("$grp", s.PuddingGroup);
        cmd.Parameters.AddWithValue("$sent", s.SentAt.ToString("O"));
        cmd.Parameters.AddWithValue("$received", s.ReceivedAt.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public void Finish(GroupSource s, string id, string status)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE receipts SET status=$status WHERE corp=$corp AND room=$room AND id=$id";
        AddIdentity(cmd, s, id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static void AddIdentity(SqliteCommand cmd, GroupSource s, string id)
    {
        cmd.Parameters.AddWithValue("$corp", s.CorporationId);
        cmd.Parameters.AddWithValue("$room", s.ConversationId);
        cmd.Parameters.AddWithValue("$id", id);
    }
}
