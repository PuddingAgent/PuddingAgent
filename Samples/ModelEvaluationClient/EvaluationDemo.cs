using System.Text.Json;
using Pudding.Api.Contracts;
using Pudding.Api.Sdk;

namespace Pudding.Samples.ModelEvaluationClient;

/// <summary>
/// Offline, injectable consumer of the model evaluation API. It accepts exactly one of two positional
/// commands, makes exactly one Classify or one Review call, and prints the original receipt as one
/// camelCase JSON line. It never reads files or stdin, trims or repairs its arguments, generates a
/// CallId, retries a request or claims durable delivery.
/// </summary>
public static class EvaluationDemo
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string Usage = string.Join(Environment.NewLine,
        "Usage: EvaluationDemo classify <requestJson>",
        "       EvaluationDemo review <requestJson>");

    private enum CommandKind { Classify, Review }

    public static async Task<int> RunAsync(string[] args, IModelEvaluationApi api, TextWriter output,
        TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Every argument or JSON binding error is decided before any API call: fixed usage on stderr, exit 2.
        if (!TryParse(args, out var kind, out var requestJson))
        {
            await WriteUsageAsync(error).ConfigureAwait(false);
            return 2;
        }

        ClassificationRequest? classification = null;
        AcceptanceRequest? acceptance = null;
        if (kind == CommandKind.Classify)
        {
            classification = DeserializeOrNull<ClassificationRequest>(requestJson);
            if (classification?.Input is null || classification.Options is null)
            {
                await WriteUsageAsync(error).ConfigureAwait(false);
                return 2;
            }
        }
        else
        {
            acceptance = DeserializeOrNull<AcceptanceRequest>(requestJson);
            if (acceptance?.Input is null || acceptance.Options is null)
            {
                await WriteUsageAsync(error).ConfigureAwait(false);
                return 2;
            }
        }

        // Exactly one boundary call through the SDK; the supplied options travel verbatim.
        var client = new ModelEvaluationsClient(api);
        try
        {
            var receiptJson = kind == CommandKind.Classify
                ? await ClassifyOnceAsync(client, classification!, ct).ConfigureAwait(false)
                : await ReviewOnceAsync(client, acceptance!, ct).ConfigureAwait(false);
            await output.WriteLineAsync(receiptJson).ConfigureAwait(false);
            return 0;
        }
        catch (ModelEvaluationException exception)
        {
            // Only the stable failure code: never the message, stack trace or request content.
            await error.WriteLineAsync($"code={exception.Code}").ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-requested cancellation is not a transport failure and owns no follow-up call.
            return 130;
        }
    }

    private static bool TryParse(string[] args, out CommandKind kind, out string requestJson)
    {
        kind = default;
        requestJson = string.Empty;
        if (args.Length != 2) return false;
        // Command names are compared verbatim; surrounding spaces make them unknown commands.
        if (args[0] == "classify") kind = CommandKind.Classify;
        else if (args[0] == "review") kind = CommandKind.Review;
        else return false;
        // The JSON text is handed over verbatim; a blank argument is a usage error, never a null request.
        if (string.IsNullOrWhiteSpace(args[1])) return false;
        requestJson = args[1];
        return true;
    }

    private static T? DeserializeOrNull<T>(string json) where T : class
    {
        try
        {
            // Frozen contract types carry [JsonUnmappedMemberHandling(Disallow)]; any binding failure,
            // including unknown members, is a usage error rather than a host or domain decision.
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ClassifyOnceAsync(ModelEvaluationsClient client, ClassificationRequest request,
        CancellationToken ct)
    {
        var receipt = await client.ClassifyAsync(request.Input, request.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(receipt, JsonOptions);
    }

    private static async Task<string> ReviewOnceAsync(ModelEvaluationsClient client, AcceptanceRequest request,
        CancellationToken ct)
    {
        var receipt = await client.ReviewAsync(request.Input, request.Options, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(receipt, JsonOptions);
    }

    private static async Task WriteUsageAsync(TextWriter error) =>
        await error.WriteLineAsync(Usage).ConfigureAwait(false);
}
