using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools
{
    /// <summary>Converts a JSON string or an array of strings into a string[] tool parameter.</summary>
    internal sealed class StringOrStringArrayConverter : JsonConverter<string[]>
    {
        public override string[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return [reader.GetString()!];
                case JsonTokenType.StartArray:
                {
                    var values = new List<string>();
                    while (reader.Read())
                    {
                        if (reader.TokenType == JsonTokenType.EndArray)
                            break;
                        if (reader.TokenType != JsonTokenType.String)
                            throw new JsonException(
                                $"Expected a JSON string inside the 'files' array, got '{reader.TokenType}'.");
                        values.Add(reader.GetString()!);
                    }

                    return values.ToArray();
                }
                default:
                    throw new JsonException(
                        $"Cannot convert JSON token '{reader.TokenType}' to a string array for 'files'.");
            }
        }

        public override void Write(Utf8JsonWriter writer, string[]? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }

    public sealed record GitCommitArgs
    {
        [ToolParam("Git repository path (defaults to current working directory)")]
        public string? Path { get; init; }

        [ToolParam("Commit message")]
        public required string Message { get; init; }

        [ToolParam("Optional file paths to stage before committing")]
        [JsonConverter(typeof(StringOrStringArrayConverter))]
        public string[]? Files { get; init; }

        [ToolParam("Allow an empty commit when there are no staged changes (default false)")]
        public bool AllowEmpty { get; init; } = false;
    }

    [Tool(
        id: GitConstants.CommitId,
        name: "Git Commit",
        description: "使用给定消息创建提交（commit），可先暂存文件。何时用：把已完成并验证过的改动固化为历史节点；Files 缺省时只提交已暂存内容。怎么用/坑：Message 必填，建议写清变更内容；Files 可选，传入会先 stage 再提交；未配置 user.name/user.email 会失败；无任何暂存改动时默认报错，AllowEmpty 可提交空提交（慎用）。Create a commit with the given message, optionally staging files first; use to persist verified changes as a history node; Message is required, Files are staged first when provided, user.name/user.email must be configured, and AllowEmpty should be used sparingly",
        category: ToolCategory.FileSystem,
        permission: ToolPermissionLevel.Low,
        safety: ToolSafetyFlags.ConcurrencySafe,
        SortOrder = 67)]
    public sealed class GitCommitTool : PuddingToolBase<GitCommitArgs>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            GitCommitArgs args, ToolExecutionContext context, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args.Message))
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Message is required."));
                }

                var repoPath = args.Path
                    ?? HostFileToolPaths.ResolveWorkspaceRoot(context.WorkingDirectory);

                using var repo = new Repository(repoPath);

                if (args.Files is { Length: > 0 })
                {
                    Commands.Stage(repo, args.Files);
                }

                var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
                if (signature is null)
                {
                    return Task.FromResult(ToolExecutionResult.Fail(
                        "Unable to build a commit signature. Configure user.name and user.email in Git config."));
                }

                var options = new CommitOptions
                {
                    AllowEmptyCommit = args.AllowEmpty
                };

                var commit = repo.Commit(args.Message.Trim(), signature, signature, options);

                return Task.FromResult(ToolExecutionResult.Ok(JsonSerializer.Serialize(new
                {
                    path = repoPath,
                    sha = commit.Sha,
                    message = commit.MessageShort
                })));
            }
            catch (LibGit2SharpException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ToolExecutionResult.Fail(ex.Message));
            }
        }
    }
}
