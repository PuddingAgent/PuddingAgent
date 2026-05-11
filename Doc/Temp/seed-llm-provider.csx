// Temp script to insert LLM Provider and Model into SQLite via Docker volume
using Microsoft.Data.Sqlite;

var dbPath = @"\\wsl$\docker-desktop-data\data\docker\volumes\puddingagent_agent_data\_data\pudding_platform.db";

// Try WSL path
if (!File.Exists(dbPath))
{
    dbPath = @"\\wsl.localhost\docker-desktop-data\data\docker\volumes\puddingagent_agent_data\_data\pudding_platform.db";
}

if (!File.Exists(dbPath))
{
    Console.WriteLine("DB not found at WSL path. Trying docker cp...");
    return;
}

Console.WriteLine($"Found DB at: {dbPath}");

using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();

// Check if mimo already exists
using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM LlmProviders WHERE ProviderId = 'mimo'", conn);
var count = (long)checkCmd.ExecuteScalar()!;

if (count > 0)
{
    Console.WriteLine("Mimo provider already exists. Updating...");
    using var updateCmd = new SqliteCommand(
        "UPDATE LlmProviders SET BaseUrl = @url, ApiKey = @key, IsEnabled = 1, UpdatedAt = @now WHERE ProviderId = 'mimo'", conn);
    updateCmd.Parameters.AddWithValue("@url", "https://token-plan-cn.xiaomimimo.com/v1");
    updateCmd.Parameters.AddWithValue("@key", "tp-cm2d6b219te46815h95bwm4zyogj0qfvw8r755fk2b111qjy");
    updateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
    updateCmd.ExecuteNonQuery();
    Console.WriteLine("Provider updated.");
}
else
{
    Console.WriteLine("Inserting Mimo provider...");
    using var insertCmd = new SqliteCommand(
        @"INSERT INTO LlmProviders (ProviderId, Name, Protocol, BaseUrl, ApiKey, Description, IsEnabled, CreatedAt, UpdatedAt)
          VALUES ('mimo', 'Mimo V2.5', 'openai', @url, @key, 'Mimo V2.5 Pro, 1M context', 1, @now, @now)", conn);
    insertCmd.Parameters.AddWithValue("@url", "https://token-plan-cn.xiaomimimo.com/v1");
    insertCmd.Parameters.AddWithValue("@key", "tp-cm2d6b219te46815h95bwm4zyogj0qfvw8r755fk2b111qjy");
    insertCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
    insertCmd.ExecuteNonQuery();
    Console.WriteLine("Provider inserted.");
}

// Also insert a default model
using var modelCheckCmd = new SqliteCommand("SELECT COUNT(*) FROM LlmModels WHERE ModelId = 'mimo-v2.5-pro'", conn);
var modelCount = (long)modelCheckCmd.ExecuteScalar()!;
if (modelCount == 0)
{
    Console.WriteLine("Inserting Mimo model...");
    using var insertModelCmd = new SqliteCommand(
        @"INSERT INTO LlmModels (ProviderId, ModelId, Name, Description, MaxContextTokens, MaxOutputTokens, IsDefault, SortOrder, CreatedAt, UpdatedAt)
          VALUES (1, 'mimo-v2.5-pro', 'Mimo V2.5 Pro', '1M context, 128K output', 1048576, 131072, 1, 10, @now, @now)", conn);
    insertModelCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
    insertModelCmd.ExecuteNonQuery();
    Console.WriteLine("Model inserted.");
}
else
{
    Console.WriteLine("Model already exists.");
}

Console.WriteLine("Done!");
