using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Samples.AgentConfigurationClient;

[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.MethodLevel)]

namespace AgentConfigurationClient.Tests;

[TestClass]
public sealed class ConfigurationInspectorTests
{
    private static readonly AgentRef Reference = new("workspace-one", "agent-one");

    [TestMethod]
    public async Task ValidConfiguration_WritesExactJsonLine_AndReturnsZero()
    {
        var api = new FakeConfigurationApi
        {
            Snapshot = Snapshot("Whale", "line one\nline two")
        };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error);

        var expected = "{\"workspaceId\":\"workspace-one\",\"agentId\":\"agent-one\",\"revision\":\"rev-1\"," +
            "\"displayName\":\"Whale\",\"description\":\"line one\\nline two\",\"templateId\":\"template-one\"}" +
            Environment.NewLine;
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(expected, output.ToString());
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.AreEqual(2, api.GetCalls, "Agents.GetAsync and Configuration.GetAsync each read the projection once.");
    }

    [TestMethod]
    public async Task NullMetadata_IsWrittenAsJsonNull()
    {
        var api = new FakeConfigurationApi { Snapshot = Snapshot(null, null) };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error);

        var expected = "{\"workspaceId\":\"workspace-one\",\"agentId\":\"agent-one\",\"revision\":\"rev-1\"," +
            "\"displayName\":null,\"description\":null,\"templateId\":\"template-one\"}" + Environment.NewLine;
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(expected, output.ToString());
    }

    [TestMethod]
    public async Task ChineseAndControlCharacters_RoundTripOnOneJsonLine()
    {
        const string displayName = "鲸鲸";
        const string description = "第一行\r\n第二行 \"引号\" \\路径 & <tag>";
        var api = new FakeConfigurationApi { Snapshot = Snapshot(displayName, description) };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error);

        Assert.AreEqual(0, exitCode);
        var text = output.ToString();
        Assert.IsTrue(text.EndsWith(Environment.NewLine, StringComparison.Ordinal), "stdout must end with exactly one line break.");
        var body = text[..^Environment.NewLine.Length];
        Assert.IsFalse(body.Contains('\r') || body.Contains('\n'), "Raw line breaks must be JSON-escaped, not literal.");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        CollectionAssert.AreEqual(
            new[] { "workspaceId", "agentId", "revision", "displayName", "description", "templateId" },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(displayName, root.GetProperty("displayName").GetString());
        Assert.AreEqual(description, root.GetProperty("description").GetString());
    }

    [TestMethod]
    public async Task PermissionDenied_PrintsOnlyCode_AndNoConfiguration()
    {
        var api = new FakeConfigurationApi
        {
            GetFailure = new ConfigurationApiException("permission.denied", "caller is not allowed to read this agent")
        };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, output.ToString(), "Denied reads must not emit any configuration.");
        Assert.AreEqual("permission.denied" + Environment.NewLine, error.ToString());
        Assert.IsFalse(error.ToString().Contains("not allowed", StringComparison.Ordinal),
            "Only the stable code may reach stderr.");
    }

    [TestMethod]
    public async Task MissingAgent_ReturnsOne_WithStableCode()
    {
        var api = new FakeConfigurationApi
        {
            GetFailure = new ConfigurationApiException("resource.not_found", "agent does not exist")
        };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error);

        Assert.AreEqual(1, exitCode);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual("resource.not_found" + Environment.NewLine, error.ToString());
    }

    [TestMethod]
    public async Task CanceledToken_Returns130_AndWritesNothing()
    {
        var api = new FakeConfigurationApi { Snapshot = Snapshot("Whale", null) };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId,
            output, error, cancellation.Token);

        Assert.AreEqual(130, exitCode);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(string.Empty, error.ToString());
    }

    [TestMethod]
    public async Task CancellationNotRequestedByCaller_IsNotConvertedToCanceledExit()
    {
        var api = new FakeConfigurationApi
        {
            GetFailure = new OperationCanceledException("timeout-like cancellation that the caller did not request")
        };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            ConfigurationInspector.RunAsync(api, Reference.WorkspaceId, Reference.AgentId, output, error, CancellationToken.None));
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    public async Task UpdateAndReadChanges_AreNeverCalled()
    {
        var success = new FakeConfigurationApi { Snapshot = Snapshot("Whale", "line one\nline two") };
        using var successOutput = new TrackingWriter();
        using var successError = new TrackingWriter();
        Assert.AreEqual(0, await ConfigurationInspector.RunAsync(success, Reference.WorkspaceId, Reference.AgentId,
            successOutput, successError));

        var denied = new FakeConfigurationApi { GetFailure = new ConfigurationApiException("permission.denied", "denied") };
        using var deniedOutput = new TrackingWriter();
        using var deniedError = new TrackingWriter();
        Assert.AreEqual(1, await ConfigurationInspector.RunAsync(denied, Reference.WorkspaceId, Reference.AgentId,
            deniedOutput, deniedError));

        foreach (var api in new[] { success, denied })
        {
            Assert.AreEqual(0, api.UpdateCalls, "UpdateAsync must never be called.");
            Assert.AreEqual(0, api.ReadChangesCalls, "ReadChangesAsync must never be called.");
        }
    }

    [TestMethod]
    public async Task InjectedApiAndWriters_AreNotDisposed()
    {
        var success = new FakeConfigurationApi { Snapshot = Snapshot("Whale", null) };
        using var output = new TrackingWriter();
        using var error = new TrackingWriter();
        Assert.AreEqual(0, await ConfigurationInspector.RunAsync(success, Reference.WorkspaceId, Reference.AgentId, output, error));

        var denied = new FakeConfigurationApi { GetFailure = new ConfigurationApiException("permission.denied", "denied") };
        Assert.AreEqual(1, await ConfigurationInspector.RunAsync(denied, Reference.WorkspaceId, Reference.AgentId, output, error));

        Assert.IsFalse(success.Disposed, "The injected IAgentConfigurationApi must not be disposed.");
        Assert.IsFalse(denied.Disposed, "The injected IAgentConfigurationApi must not be disposed.");
        Assert.IsFalse(output.Disposed, "The injected output writer must not be disposed.");
        Assert.IsFalse(error.Disposed, "The injected error writer must not be disposed.");
    }

    private static AgentConfigurationSnapshot Snapshot(string? displayName, string? description) =>
        new(Reference, "rev-1", displayName, description, "template-one", DateTimeOffset.UnixEpoch);

    private sealed class FakeConfigurationApi : IAgentConfigurationApi, IDisposable
    {
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int ReadChangesCalls { get; private set; }
        public bool Disposed { get; private set; }
        public AgentConfigurationSnapshot? Snapshot { get; init; }
        public Exception? GetFailure { get; init; }

        public Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default)
        {
            GetCalls++;
            ct.ThrowIfCancellationRequested();
            if (GetFailure is not null)
            {
                return Task.FromException<AgentConfigurationSnapshot>(GetFailure);
            }
            return Task.FromResult(Snapshot ?? throw new AssertFailedException("Snapshot is not configured."));
        }

        public Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch,
            ConfigurationUpdateOptions options, CancellationToken ct = default)
        {
            UpdateCalls++;
            throw new AssertFailedException("The inspector must not call UpdateAsync.");
        }

        public Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0, int limit = 100,
            CancellationToken ct = default)
        {
            ReadChangesCalls++;
            throw new AssertFailedException("The inspector must not call ReadChangesAsync.");
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingWriter : StringWriter
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

[TestClass]
public sealed class ProgramShellTests
{
    private static readonly string[] ValidArguments =
    [
        "--base-url", "http://127.0.0.1:5199",
        "--credential-file", "credential.json",
        "--workspace", "workspace-one",
        "--agent", "agent-one"
    ];

    [TestMethod]
    public void AllRequiredArguments_AreParsed()
    {
        Assert.IsTrue(Program.TryParseArguments(ValidArguments, out var options, out var error), error);
        Assert.AreEqual("http://127.0.0.1:5199", options.BaseUrl);
        Assert.AreEqual("credential.json", options.CredentialFile);
        Assert.AreEqual("workspace-one", options.WorkspaceId);
        Assert.AreEqual("agent-one", options.AgentId);
    }

    [TestMethod]
    public void UnknownArgument_IsRejected()
    {
        var arguments = ValidArguments.Append("--verbose").ToArray();
        Assert.IsFalse(Program.TryParseArguments(arguments, out _, out var error));
        StringAssert.Contains(error, "Unknown argument");
    }

    [TestMethod]
    public void DuplicateArgument_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--agent", "another-agent"]).ToArray();
        Assert.IsFalse(Program.TryParseArguments(arguments, out _, out var error));
        StringAssert.Contains(error, "Duplicate argument");
    }

    [TestMethod]
    public void MissingRequiredArgument_IsRejected()
    {
        Assert.IsFalse(Program.TryParseArguments(["--base-url", "https://example.com"], out _, out var error));
        Assert.AreEqual(Program.Usage, error);
    }

    [TestMethod]
    public void ArgumentWithoutValue_IsRejected()
    {
        var arguments = ValidArguments[..^2].Concat(["--agent"]).ToArray();
        Assert.IsFalse(Program.TryParseArguments(arguments, out _, out var error));
        StringAssert.Contains(error, "Missing value");
    }

    [TestMethod]
    public void EmptyArgumentValue_IsRejected()
    {
        var arguments = ValidArguments.ToArray();
        arguments[3] = string.Empty;
        Assert.IsFalse(Program.TryParseArguments(arguments, out _, out var error));
        StringAssert.Contains(error, "Missing value");
    }

    [TestMethod]
    public void EmptyArgumentList_IsRejected()
    {
        Assert.IsFalse(Program.TryParseArguments([], out _, out var error));
        Assert.AreEqual(Program.Usage, error);
    }

    [TestMethod]
    [DataRow("http://localhost:5199")]
    [DataRow("http://127.0.0.1:5199")]
    [DataRow("http://[::1]:5199")]
    [DataRow("https://127.0.0.1:5199")]
    [DataRow("https://example.com")]
    public void AcceptedBaseUrls_AreValidated(string value)
    {
        Assert.IsTrue(Program.TryValidateBaseUri(value, out var baseUri, out var error), error);
        Assert.IsNotNull(baseUri);
    }

    [TestMethod]
    [DataRow("http://example.com")]
    [DataRow("http://10.0.0.5:5199")]
    [DataRow("http://0.0.0.0:5199")]
    public void RemotePlainHttp_IsRejected(string value)
    {
        Assert.IsFalse(Program.TryValidateBaseUri(value, out _, out var error));
        StringAssert.Contains(error, "loopback");
    }

    [TestMethod]
    [DataRow("not-a-url")]
    [DataRow("/relative/path")]
    [DataRow("ftp://127.0.0.1:5199")]
    [DataRow("file:///C:/data")]
    public void MalformedOrNonHttpBaseUrl_IsRejected(string value)
    {
        Assert.IsFalse(Program.TryValidateBaseUri(value, out _, out var error));
        StringAssert.Contains(error, "absolute http or https");
    }

    [TestMethod]
    [DataRow("https://user:secret@example.com")]
    [DataRow("https://example.com?port=5199")]
    [DataRow("https://example.com#fragment")]
    public void UserinfoQueryAndFragment_AreRejected(string value)
    {
        Assert.IsFalse(Program.TryValidateBaseUri(value, out _, out var error));
        StringAssert.Contains(error, "userinfo");
        Assert.IsFalse(error.Contains("secret", StringComparison.Ordinal), "Rejected URLs must not echo credentials.");
    }

    [TestMethod]
    public void BaseUrl_TrailingSlashIsNormalized()
    {
        Assert.IsTrue(Program.TryValidateBaseUri("http://127.0.0.1:5199", out var root, out _));
        Assert.AreEqual("http://127.0.0.1:5199/", root.AbsoluteUri);
        Assert.IsTrue(Program.TryValidateBaseUri("https://example.com/gateway", out var nested, out _));
        Assert.AreEqual("https://example.com/gateway/", nested.AbsoluteUri);
    }

    [TestMethod]
    public void ValidCredentialFile_IsRead()
    {
        var path = WriteTemporaryFile("{\"accessToken\":\"management-jwt-value\"}");
        try
        {
            Assert.IsTrue(Program.TryReadAccessToken(path, out var token, out var error), error);
            Assert.AreEqual("management-jwt-value", token);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void MissingCredentialFile_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-ds01-missing-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.IsFalse(Program.TryReadAccessToken(path, out var token, out var error));
        Assert.AreEqual(string.Empty, token);
        StringAssert.Contains(error, "could not be read");
    }

    [TestMethod]
    [DataRow("{\"accessToken\":\"\"}")]
    [DataRow("{\"accessToken\":\"   \"}")]
    [DataRow("{\"accessToken\":42}")]
    [DataRow("{\"otherToken\":\"value\"}")]
    [DataRow("[]")]
    public void InvalidCredentialShape_IsRejected(string content)
    {
        var path = WriteTemporaryFile(content);
        try
        {
            Assert.IsFalse(Program.TryReadAccessToken(path, out var token, out var error));
            Assert.AreEqual(string.Empty, token);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void InvalidCredentialJson_DoesNotEchoFileContent()
    {
        var path = WriteTemporaryFile("{\"accessToken\": \"top-secret-value\"");
        try
        {
            Assert.IsFalse(Program.TryReadAccessToken(path, out _, out var error));
            StringAssert.Contains(error, "not valid JSON");
            Assert.IsFalse(error.Contains("top-secret-value", StringComparison.Ordinal),
                "Credential file content must never be echoed.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemporaryFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "pudding-ds01-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content);
        return path;
    }
}
