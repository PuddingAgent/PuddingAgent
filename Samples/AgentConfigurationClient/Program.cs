using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pudding.Api.Sdk;

namespace Pudding.Samples.AgentConfigurationClient;

/// <summary>
/// Process shell only: argument parsing, credential loading and HttpClient construction.
/// Configuration reading lives in <see cref="ConfigurationInspector"/>; this file contains no SDK logic.
/// </summary>
internal static class Program
{
    internal const int SuccessExitCode = 0;
    internal const int ConfigurationFailureExitCode = 1;
    internal const int UsageExitCode = 2;
    internal const int CanceledExitCode = 130;
    internal const string Usage =
        "Usage: AgentConfigurationClient --base-url <url> --credential-file <path> --workspace <id> --agent <id>";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] RequiredFlags = ["--base-url", "--credential-file", "--workspace", "--agent"];

    internal sealed record CommandLineOptions(string BaseUrl, string CredentialFile, string WorkspaceId, string AgentId);

    private static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var options, out var argumentError))
        {
            Console.Error.WriteLine(argumentError);
            return UsageExitCode;
        }
        if (!TryValidateBaseUri(options.BaseUrl, out var baseUri, out var baseUrlError))
        {
            Console.Error.WriteLine(baseUrlError);
            return UsageExitCode;
        }
        if (!TryReadAccessToken(options.CredentialFile, out var accessToken, out var credentialError))
        {
            Console.Error.WriteLine(credentialError);
            return UsageExitCode;
        }

        using var shutdown = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelKeyPress = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelKeyPress;
        try
        {
            // Default handler: certificate validation stays enabled; no custom TLS bypass is installed.
            using var httpClient = new HttpClient { BaseAddress = baseUri, Timeout = RequestTimeout };
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await ConfigurationInspector.RunAsync(new HttpAgentConfigurationApi(httpClient),
                options.WorkspaceId, options.AgentId, Console.Out, Console.Error, shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelKeyPress;
        }
    }

    internal static bool TryParseArguments(string[] args, out CommandLineOptions options, out string error)
    {
        options = null!;
        error = string.Empty;
        if (args is null || args.Length == 0)
        {
            error = Usage;
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (!RequiredFlags.Contains(flag, StringComparer.Ordinal))
            {
                error = $"Unknown argument: {flag}. {Usage}";
                return false;
            }
            if (values.ContainsKey(flag))
            {
                error = $"Duplicate argument: {flag}.";
                return false;
            }
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])
                || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Missing value for argument: {flag}.";
                return false;
            }
            values[flag] = args[index + 1];
            index++;
        }

        if (RequiredFlags.Any(flag => !values.ContainsKey(flag)))
        {
            error = Usage;
            return false;
        }

        options = new CommandLineOptions(values["--base-url"], values["--credential-file"],
            values["--workspace"], values["--agent"]);
        return true;
    }

    internal static bool TryValidateBaseUri(string value, out Uri baseUri, out string error)
    {
        baseUri = null!;
        error = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = "--base-url must be an absolute http or https URL.";
            return false;
        }
        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            error = "--base-url must not contain userinfo, a query string or a fragment.";
            return false;
        }
        if (parsed.Scheme == Uri.UriSchemeHttp && !IsLoopbackHost(parsed))
        {
            error = "Plain HTTP is only accepted for loopback hosts; use HTTPS for every other host.";
            return false;
        }

        // Relative routes (api/workspaces/...) resolve against a base that ends with '/'.
        baseUri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    internal static bool TryReadAccessToken(string path, out string accessToken, out string error)
    {
        accessToken = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "--credential-file must not be empty.";
            return false;
        }
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("accessToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                error = "--credential-file must contain a non-empty string property named \"accessToken\".";
                return false;
            }
            accessToken = tokenElement.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            // Never echo the file body or the would-be token.
            error = "--credential-file is not valid JSON.";
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "--credential-file could not be read.";
            return false;
        }
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }
}
